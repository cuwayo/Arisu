using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Threading.Channels;
using Arisu.Storage;
using Arisu.Utilities;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Arisu.Services;

/// <summary>
/// Awards message XP outside the gateway callback. A full queue drops the newest
/// message without awarding XP; shutdown cancels work rather than draining it.
/// Register before the Discord client hosted service to subscribe before login.
/// </summary>
public sealed class LevelingService : BackgroundService
{
    private const int Capacity = 1024;
    private readonly DiscordSocketClient _client;
    private readonly ILevelingStore _store;
    private readonly ILogger<LevelingService> _logger;
    private readonly ConcurrentDictionary<ulong, SocketGuild> _availableGuilds = new();
    private readonly Channel<PendingMessage> _pending = Channel.CreateBounded<PendingMessage>(
        new BoundedChannelOptions(Capacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
    private long _dropped;
    private int _acceptingMessages;

    public LevelingService(
        DiscordSocketClient client,
        ILevelingStore store,
        ILogger<LevelingService> logger)
    {
        _client = client;
        _store = store;
        _logger = logger;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _client.MessageReceived += OnMessageReceivedAsync;
        _client.GuildAvailable += OnGuildAvailableAsync;
        _client.GuildUnavailable += OnGuildUnavailableAsync;
        _client.Disconnected += OnDisconnectedAsync;
        Volatile.Write(ref _acceptingMessages, 1);

        try
        {
            await base.StartAsync(cancellationToken);
        }
        catch
        {
            Unsubscribe();
            throw;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Unsubscribe();
        // Base StopAsync cancels the worker immediately, including in-flight I/O.
        // Complete only after requesting cancellation; queued XP is not drained.
        var stopping = base.StopAsync(cancellationToken);
        _pending.Writer.TryComplete();
        await stopping;
    }

    public override void Dispose()
    {
        Unsubscribe();
        base.Dispose();
        _pending.Writer.TryComplete();
    }

    private void Unsubscribe()
    {
        Volatile.Write(ref _acceptingMessages, 0);
        _client.MessageReceived -= OnMessageReceivedAsync;
        _client.GuildAvailable -= OnGuildAvailableAsync;
        _client.GuildUnavailable -= OnGuildUnavailableAsync;
        _client.Disconnected -= OnDisconnectedAsync;
        _availableGuilds.Clear();
    }

    private Task OnGuildAvailableAsync(SocketGuild guild)
    {
        _availableGuilds[guild.Id] = guild;
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

    private Task OnMessageReceivedAsync(SocketMessage message)
    {
        if (Volatile.Read(ref _acceptingMessages) == 0 ||
            message is not SocketUserMessage { Source: MessageSource.User } ||
            message.Author.IsBot || message.Author.IsWebhook ||
            message.Channel is not SocketGuildChannel channel ||
            message.Author is not SocketGuildUser member)
        {
            return Task.CompletedTask;
        }

        // Snapshot roles (including @everyone) and the message's UTC timestamp.
        // Processing time would turn a backlog into artificial cooldown windows.
        var pending = new PendingMessage(
            channel.Guild.Id,
            member.Id,
            channel.Id,
            member.Roles.Select(role => role.Id).Append(channel.Guild.Id).Distinct().ToImmutableArray(),
            message.Timestamp.ToUniversalTime());
        if (!_pending.Writer.TryWrite(pending))
        {
            var dropped = Interlocked.Increment(ref _dropped);
            if ((dropped & (dropped - 1)) == 0)
            {
                _logger.LogWarning(
                    "Leveling queue is full or stopping; {DroppedCount} messages were dropped without awarding XP. Latest guild {GuildId}, channel {ChannelId}.",
                    dropped,
                    pending.GuildId,
                    pending.ChannelId);
            }
        }

        return Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var pending in _pending.Reader.ReadAllAsync(stoppingToken))
            {
                // ReadAllAsync can still yield buffered items after cancellation.
                stoppingToken.ThrowIfCancellationRequested();
                try
                {
                    await ProcessMessageAsync(pending, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger.LogError(
                        exception,
                        "Could not process leveling for user {UserId} in guild {GuildId}, channel {ChannelId}.",
                        pending.UserId,
                        pending.GuildId,
                        pending.ChannelId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task ProcessMessageAsync(PendingMessage pending, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var award = await _store.TryAwardAsync(
            pending.GuildId,
            pending.UserId,
            pending.ChannelId,
            pending.RoleIds,
            Random.Shared.Next(15, 26),
            pending.Timestamp,
            cancellationToken);
        if (award is null)
        {
            return;
        }

        var settings = await _store.GetSettingsAsync(pending.GuildId, cancellationToken);
        if (!settings.Enabled)
        {
            return;
        }

        var level = LevelingProgress.GetLevel(award.Member.TotalXp);
        // Revisit every eligible reward on accepted messages, not just level-ups,
        // so a failed role grant can be retried without rolling back persisted XP.
        foreach (var roleId in settings.Rewards.Where(reward => reward.Level <= level)
            .OrderBy(reward => reward.Level).Select(reward => reward.RoleId).Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var guild = _client.GetGuild(pending.GuildId);
            if (guild is null || !IsReady(guild) ||
                guild.GetUser(pending.UserId) is not { IsBot: false, IsWebhook: false } member ||
                guild.CurrentUser is not { } bot || !bot.GuildPermissions.ManageRoles)
            {
                return;
            }

            var role = guild.GetRole(roleId);
            if (role is null || role.IsEveryone || role.IsManaged ||
                role.Position >= bot.Hierarchy || !LevelingRewardSafety.IsSafe(role.Permissions) ||
                guild.Channels.Any(channel => channel.GetPermissionOverwrite(role) is { } overwrite &&
                    !LevelingRewardSafety.IsSafe(overwrite)) ||
                member.Roles.Any(existing => existing.Id == roleId))
            {
                continue;
            }

            try
            {
                await member.AddRoleAsync(role, new RequestOptions { CancelToken = cancellationToken });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Could not grant leveling reward role {RoleId} to user {UserId} in guild {GuildId}; a later accepted message can retry it.",
                    roleId,
                    pending.UserId,
                    pending.GuildId);
            }
        }
    }

    private bool IsReady(SocketGuild guild) =>
        _client.ConnectionState == ConnectionState.Connected &&
        _availableGuilds.TryGetValue(guild.Id, out var available) && ReferenceEquals(guild, available);

    private sealed record PendingMessage(
        ulong GuildId,
        ulong UserId,
        ulong ChannelId,
        ImmutableArray<ulong> RoleIds,
        DateTimeOffset Timestamp);
}
