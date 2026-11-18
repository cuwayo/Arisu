using System.Collections.Concurrent;
using System.Threading.Channels;
using Arisu.Storage;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Arisu.Services;

public enum VoiceMasterAction
{
    Rename, Limit, Lock, Unlock, Hide, Show, Permit, Reject, Transfer, Claim
}

public interface IVoiceMasterService
{
    Task<string> ConfigureAsync(IGuild guild, IVoiceChannel lobby, ICategoryChannel category);
    Task<string> DisableAsync(IGuild guild);
    Task<string> UpdateRoomAsync(IGuildUser user, VoiceMasterAction action, string? name = null,
        int limit = 0, IGuildUser? target = null);
}

public sealed class VoiceMasterService : BackgroundService, IVoiceMasterService
{
    private readonly DiscordSocketClient _client;
    private readonly IVoiceMasterStore _store;
    private readonly ILogger<VoiceMasterService> _logger;
    private readonly ConcurrentDictionary<ulong, SemaphoreSlim> _guildLocks = new();
    private readonly ConcurrentDictionary<ulong, byte> _pendingGuilds = new();
    private readonly ConcurrentDictionary<ulong, SocketGuild> _availableGuilds = new();
    private readonly ConcurrentDictionary<(ulong Guild, ulong Member), DateTimeOffset> _creationAttempts = new();
    private readonly ConcurrentDictionary<ulong, DateTimeOffset> _pendingMoves = new();
    private readonly Channel<ulong> _queue = Channel.CreateUnbounded<ulong>(new UnboundedChannelOptions
    {
        SingleReader = true,
        AllowSynchronousContinuations = false
    });

    public VoiceMasterService(DiscordSocketClient client, IVoiceMasterStore store,
        ILogger<VoiceMasterService> logger)
    {
        _client = client;
        _store = store;
        _logger = logger;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _client.UserVoiceStateUpdated += OnVoiceStateUpdatedAsync;
        _client.GuildAvailable += OnGuildAvailableAsync;
        _client.GuildUnavailable += OnGuildUnavailableAsync;
        _client.Disconnected += OnDisconnectedAsync;
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _client.UserVoiceStateUpdated -= OnVoiceStateUpdatedAsync;
        _client.GuildAvailable -= OnGuildAvailableAsync;
        _client.GuildUnavailable -= OnGuildUnavailableAsync;
        _client.Disconnected -= OnDisconnectedAsync;
        _availableGuilds.Clear();
        await base.StopAsync(cancellationToken);
    }

    private Task OnVoiceStateUpdatedAsync(SocketUser user, SocketVoiceState before, SocketVoiceState after)
    {
        if (user is SocketGuildUser member && before.VoiceChannel?.Id != after.VoiceChannel?.Id)
        {
            Enqueue(member.Guild.Id);
        }

        return Task.CompletedTask;
    }

    private Task OnGuildAvailableAsync(SocketGuild guild)
    {
        _availableGuilds[guild.Id] = guild;
        Enqueue(guild.Id);
        return Task.CompletedTask;
    }

    private Task OnGuildUnavailableAsync(SocketGuild guild)
    {
        _availableGuilds.TryRemove(guild.Id, out _);
        return Task.CompletedTask;
    }

    private Task OnDisconnectedAsync(Exception exception)
    {
        _availableGuilds.Clear();
        return Task.CompletedTask;
    }

    private bool IsReady(SocketGuild guild) =>
        _client.ConnectionState == ConnectionState.Connected &&
        _availableGuilds.TryGetValue(guild.Id, out var available) && ReferenceEquals(guild, available);

    private void Enqueue(ulong guildId)
    {
        if (_pendingGuilds.TryAdd(guildId, 0))
        {
            _queue.Writer.TryWrite(guildId);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.WhenAll(ProcessQueueAsync(stoppingToken), ScheduleReconciliationAsync(stoppingToken));
    }

    private async Task ScheduleReconciliationAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            foreach (var guild in _availableGuilds.Values)
            {
                Enqueue(guild.Id);
            }

            var cutoff = DateTimeOffset.UtcNow.AddMinutes(-1);
            foreach (var attempt in _creationAttempts.Where(pair => pair.Value < cutoff))
            {
                _creationAttempts.TryRemove(attempt.Key, out _);
            }
        }
    }

    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        await Parallel.ForEachAsync(_queue.Reader.ReadAllAsync(cancellationToken), new ParallelOptions
        {
            MaxDegreeOfParallelism = 4,
            CancellationToken = cancellationToken
        }, async (guildId, token) =>
        {
            var gate = GetGate(guildId);
            await gate.WaitAsync(token);
            _pendingGuilds.TryRemove(guildId, out _);
            try
            {
                if (_client.GetGuild(guildId) is { } guild && IsReady(guild))
                {
                    await ReconcileAsync(guild, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Could not reconcile temporary voice rooms in guild {GuildId}.", guildId);
            }
            finally
            {
                gate.Release();
            }
        });
    }

    public async Task<string> ConfigureAsync(IGuild guild, IVoiceChannel lobby, ICategoryChannel category)
    {
        if (lobby.GuildId != guild.Id || category.GuildId != guild.Id || lobby is IStageChannel)
        {
            return "Choose a regular voice lobby and a category in this server.";
        }

        var bot = await guild.GetCurrentUserAsync();
        if (!CanManage(bot, lobby) || !CanManage(bot, category))
        {
            return "I need View Channel, Connect, Manage Channels, Manage Permissions, and Move Members in the lobby and destination category.";
        }

        var gate = GetGate(guild.Id);
        await gate.WaitAsync();
        try
        {
            if (await _store.GetRoomAsync(guild.Id, lobby.Id) is not null)
            {
                return "A temporary room cannot be used as the join-to-create lobby.";
            }

            await _store.SetConfigurationAsync(new VoiceMasterConfiguration(guild.Id, lobby.Id, category.Id));
            Enqueue(guild.Id);
            return $"Join <#{lobby.Id}> to create a voice room in <#{category.Id}>. Use /voicemaster commands to customize your room.";
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<string> DisableAsync(IGuild guild)
    {
        var gate = GetGate(guild.Id);
        await gate.WaitAsync();
        try
        {
            await _store.RemoveConfigurationAsync(guild.Id);
            return "Join-to-create is disabled. Existing temporary rooms remain customizable and will be deleted when empty.";
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<string> UpdateRoomAsync(IGuildUser user, VoiceMasterAction action, string? name = null,
        int limit = 0, IGuildUser? target = null)
    {
        var gate = GetGate(user.GuildId);
        await gate.WaitAsync();
        try
        {
            var channel = user.VoiceChannel;
            if (channel is null || await _store.GetRoomAsync(user.GuildId, channel.Id) is not { } room)
            {
                return "Join a temporary room created by Arisu first.";
            }

            var members = (await channel.GetUsersAsync().FlattenAsync())
                .Where(member => member.VoiceChannel?.Id == channel.Id).ToArray();
            if (action == VoiceMasterAction.Claim)
            {
                if (room.OwnerId == user.Id)
                {
                    return "You already own this room.";
                }
                if (members.Any(member => member.Id == room.OwnerId))
                {
                    return "The owner is still in this room. Ask them to transfer ownership.";
                }
            }
            else if (room.OwnerId != user.Id)
            {
                return "Only this room's owner can customize it. Use /voicemaster claim if the owner has left.";
            }

            var bot = await user.Guild.GetCurrentUserAsync();
            if (!CanManage(bot, channel))
            {
                return "I need View Channel, Connect, Manage Channels, Manage Permissions, and Move Members in this room.";
            }

            var options = new RequestOptions { AuditLogReason = $"VoiceMaster {action} by {user.Id}" };
            if (action is VoiceMasterAction.Lock or VoiceMasterAction.Unlock or VoiceMasterAction.Hide or
                VoiceMasterAction.Show or VoiceMasterAction.Permit or VoiceMasterAction.Reject or
                VoiceMasterAction.Transfer or VoiceMasterAction.Claim)
            {
                // REST permission writes can finish before CHANNEL_UPDATE refreshes the socket cache.
                // Keep the membership snapshot above; REST channels do not carry live voice state.
                if (await _client.Rest.GetChannelAsync(channel.Id, options) is not IVoiceChannel freshChannel ||
                    freshChannel.GuildId != user.GuildId)
                {
                    return "This temporary room no longer exists.";
                }
                channel = freshChannel;
            }
            switch (action)
            {
                case VoiceMasterAction.Rename:
                    name = name?.Trim();
                    if (string.IsNullOrEmpty(name) || name.Length > 100 || name.Any(char.IsControl))
                    {
                        return "Use a room name of 1–100 characters without line breaks or control characters.";
                    }
                    await channel.ModifyAsync(properties => properties.Name = name, options);
                    return "Room name updated.";
                case VoiceMasterAction.Limit:
                    if (limit is < 0 or > 99)
                    {
                        return "The user limit must be 0–99; 0 means unlimited.";
                    }
                    await channel.ModifyAsync(properties => properties.UserLimit = limit, options);
                    return limit == 0 ? "Room user limit removed." : $"Room user limit set to {limit}.";
                case VoiceMasterAction.Lock:
                case VoiceMasterAction.Unlock:
                case VoiceMasterAction.Hide:
                case VoiceMasterAction.Show:
                    var role = user.Guild.EveryoneRole;
                    var overwrite = channel.GetPermissionOverwrite(role) ?? OverwritePermissions.InheritAll;
                    overwrite = action switch
                    {
                        VoiceMasterAction.Lock => overwrite.Modify(connect: PermValue.Deny),
                        VoiceMasterAction.Unlock => overwrite.Modify(connect: PermValue.Allow),
                        VoiceMasterAction.Hide => overwrite.Modify(viewChannel: PermValue.Deny),
                        _ => overwrite.Modify(viewChannel: PermValue.Allow)
                    };
                    await channel.AddPermissionOverwriteAsync(role, overwrite, options);
                    return action switch
                    {
                        VoiceMasterAction.Lock => "Room locked. Permitted members and roles with explicit access can still join.",
                        VoiceMasterAction.Unlock => "Room unlocked.",
                        VoiceMasterAction.Hide => "Room hidden. Permitted members and roles with explicit access can still see it.",
                        _ => "Room is visible again."
                    };
                case VoiceMasterAction.Permit:
                case VoiceMasterAction.Reject:
                case VoiceMasterAction.Transfer:
                    if (target is null || target.GuildId != user.GuildId || target.Id == user.Id || target.IsBot)
                    {
                        return "Choose another human member of this server.";
                    }
                    if (action == VoiceMasterAction.Transfer)
                    {
                        if (!members.Any(member => member.Id == target.Id))
                        {
                            return "The new owner must be connected to your room.";
                        }
                        await AllowMemberAsync(channel, target, options);
                        await _store.SaveRoomAsync(room with { OwnerId = target.Id });
                        return $"Room ownership transferred to <@{target.Id}>.";
                    }
                    if (action == VoiceMasterAction.Reject &&
                        (target.Id == user.Guild.OwnerId || target.GuildPermissions.Administrator ||
                         target.GuildPermissions.ManageGuild || target.GuildPermissions.ManageChannels))
                    {
                        return "Server owners, administrators, and channel moderators cannot be rejected.";
                    }
                    var memberOverwrite = channel.GetPermissionOverwrite(target) ?? OverwritePermissions.InheritAll;
                    var access = action == VoiceMasterAction.Permit ? PermValue.Allow : PermValue.Deny;
                    await channel.AddPermissionOverwriteAsync(target,
                        memberOverwrite.Modify(viewChannel: access, connect: access), options);
                    if (action == VoiceMasterAction.Reject && target.VoiceChannel?.Id == channel.Id)
                    {
                        await target.ModifyAsync(properties => properties.Channel = null, options);
                    }
                    return action == VoiceMasterAction.Permit
                        ? $"<@{target.Id}> can now see and join this room."
                        : $"<@{target.Id}> can no longer see or join this room.";
                case VoiceMasterAction.Claim:
                    await AllowMemberAsync(channel, user, options);
                    await _store.SaveRoomAsync(room with { OwnerId = user.Id });
                    return "You now own this room.";
                default:
                    throw new ArgumentOutOfRangeException(nameof(action));
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task ReconcileAsync(SocketGuild guild, CancellationToken cancellationToken)
    {
        if (!IsReady(guild))
        {
            return;
        }
        var rooms = await _store.GetRoomsAsync(guild.Id, cancellationToken);
        var options = new RequestOptions { CancelToken = cancellationToken, AuditLogReason = "VoiceMaster empty room cleanup" };
        foreach (var room in rooms)
        {
            if (!IsReady(guild))
            {
                return;
            }
            try
            {
                // GuildAvailable means the gateway's channel and voice-state snapshots are ready.
                var channel = guild.GetVoiceChannel(room.ChannelId);
                if (channel is null)
                {
                    if (_pendingMoves.TryGetValue(room.ChannelId, out var createdAt) &&
                        DateTimeOffset.UtcNow - createdAt < TimeSpan.FromSeconds(15))
                    {
                        continue;
                    }
                    // A cache miss does not prove deletion: CHANNEL_CREATE can lag behind REST.
                    var remoteChannel = await _client.Rest.GetChannelAsync(room.ChannelId, options);
                    if (remoteChannel is not null)
                    {
                        continue;
                    }
                    await _store.RemoveRoomAsync(guild.Id, room.ChannelId, cancellationToken);
                    _pendingMoves.TryRemove(room.ChannelId, out _);
                }
                else if (channel.ConnectedUsers.Any())
                {
                    _pendingMoves.TryRemove(channel.Id, out _);
                }
                else if (!_pendingMoves.TryGetValue(channel.Id, out var movedAt) ||
                         DateTimeOffset.UtcNow - movedAt >= TimeSpan.FromSeconds(15))
                {
                    // REST moves can complete before their gateway voice-state update arrives.
                    await channel.DeleteAsync(options);
                    await _store.RemoveRoomAsync(guild.Id, room.ChannelId, cancellationToken);
                    _pendingMoves.TryRemove(channel.Id, out _);
                }
            }
            catch (HttpException exception) when (exception.DiscordCode == DiscordErrorCode.UnknownChannel)
            {
                await _store.RemoveRoomAsync(guild.Id, room.ChannelId, cancellationToken);
                _pendingMoves.TryRemove(room.ChannelId, out _);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "Could not clean up temporary voice channel {ChannelId} in guild {GuildId}.", room.ChannelId, guild.Id);
            }
        }

        var configuration = await _store.GetConfigurationAsync(guild.Id, cancellationToken);
        if (!IsReady(guild) || configuration is null || guild.GetVoiceChannel(configuration.LobbyChannelId) is not { } lobby ||
            guild.GetCategoryChannel(configuration.CategoryId) is not { } category)
        {
            return;
        }

        foreach (var member in lobby.ConnectedUsers.Where(member => !member.IsBot).ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var key = (guild.Id, member.Id);
                if (_creationAttempts.TryGetValue(key, out var previous) &&
                    DateTimeOffset.UtcNow - previous < TimeSpan.FromSeconds(15))
                {
                    continue;
                }
                _creationAttempts[key] = DateTimeOffset.UtcNow;
                await CreateRoomAsync(member, lobby, category, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "Could not create a voice room for member {MemberId} in guild {GuildId}.", member.Id, guild.Id);
            }
        }
    }

    private async Task CreateRoomAsync(SocketGuildUser member, SocketVoiceChannel lobby,
        SocketCategoryChannel category, CancellationToken cancellationToken)
    {
        if (!IsReady(member.Guild) || member.VoiceChannel?.Id != lobby.Id)
        {
            return;
        }

        var options = new RequestOptions { CancelToken = cancellationToken, AuditLogReason = $"VoiceMaster room for {member.Id}" };
        var bot = member.Guild.CurrentUser;
        if (!CanManage(bot, lobby) || !CanManage(bot, category))
        {
            throw new InvalidOperationException("VoiceMaster requires channel management, permissions, and member movement access.");
        }

        // Reuse an occupied room owned by this member rather than producing duplicate rooms.
        var existing = (await _store.GetRoomsAsync(member.Guild.Id, cancellationToken))
            .FirstOrDefault(room => room.OwnerId == member.Id && member.Guild.GetVoiceChannel(room.ChannelId) is not null);
        if (existing is not null && member.Guild.GetVoiceChannel(existing.ChannelId) is { } destination)
        {
            if (!IsReady(member.Guild) || member.VoiceChannel?.Id != lobby.Id)
            {
                return;
            }
            if (!CanManage(bot, destination))
            {
                throw new InvalidOperationException("VoiceMaster cannot manage the existing room.");
            }
            _pendingMoves[destination.Id] = DateTimeOffset.UtcNow;
            await member.ModifyAsync(properties => properties.ChannelId = existing.ChannelId, options);
            return;
        }
        if (!IsReady(member.Guild) || member.VoiceChannel?.Id != lobby.Id)
        {
            return;
        }

        var displayName = new string(member.DisplayName.Where(character => !char.IsControl(character)).ToArray());
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = "Member";
        }
        var name = $"{displayName[..Math.Min(displayName.Length, 88)]}'s room";
        // Inherit category overwrites without sending them in the create payload. That endpoint
        // restricts overwrites to guild permissions and permits Manage Roles only for administrators.
        var channel = await member.Guild.CreateVoiceChannelAsync(name,
            properties => properties.CategoryId = category.Id, options);
        try
        {
            // Persist before permission changes or moving so failures remain recoverable.
            await _store.SaveRoomAsync(new VoiceMasterRoom(member.Guild.Id, channel.Id, member.Id), cancellationToken);
            // The overwrite endpoint accepts permissions held in the category or channel.
            var botPermissions = channel.GetPermissionOverwrite(bot) ?? OverwritePermissions.InheritAll;
            await channel.AddPermissionOverwriteAsync(bot, botPermissions.Modify(
                viewChannel: PermValue.Allow, connect: PermValue.Allow, manageChannel: PermValue.Allow,
                manageRoles: PermValue.Allow, moveMembers: PermValue.Allow), options);
            await AllowMemberAsync(channel, member, options);
            if (IsReady(member.Guild) && member.VoiceChannel?.Id == lobby.Id)
            {
                _pendingMoves[channel.Id] = DateTimeOffset.UtcNow;
                await member.ModifyAsync(properties => properties.ChannelId = channel.Id, options);
            }
            else
            {
                await channel.DeleteAsync(options);
                await _store.RemoveRoomAsync(member.Guild.Id, channel.Id, cancellationToken);
            }
        }
        catch
        {
            // Keep persisted tracking if deletion fails; reconciliation retries cleanup.
            try
            {
                await channel.DeleteAsync(new RequestOptions { AuditLogReason = "VoiceMaster failed creation cleanup" });
                await _store.RemoveRoomAsync(member.Guild.Id, channel.Id);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Could not roll back temporary voice channel {ChannelId} in guild {GuildId}.", channel.Id, member.Guild.Id);
            }
            throw;
        }
    }

    private SemaphoreSlim GetGate(ulong guildId) => _guildLocks.GetOrAdd(guildId, _ => new SemaphoreSlim(1, 1));

    private static bool CanManage(IGuildUser bot, IGuildChannel channel)
    {
        var permissions = bot.GetPermissions(channel);
        return permissions.ViewChannel && permissions.ManageChannel && permissions.ManageRoles &&
               permissions.MoveMembers && (channel is ICategoryChannel || permissions.Connect);
    }

    private static Task AllowMemberAsync(IVoiceChannel channel, IGuildUser member, RequestOptions options)
    {
        var overwrite = channel.GetPermissionOverwrite(member) ?? OverwritePermissions.InheritAll;
        return channel.AddPermissionOverwriteAsync(member,
            overwrite.Modify(viewChannel: PermValue.Allow, connect: PermValue.Allow), options);
    }
}
