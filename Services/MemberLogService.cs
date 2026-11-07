using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Arisu.Services;

public sealed class MemberLogService : IHostedService
{
    private const int MaximumMessageContentLength = 800;
    private const int MaximumEditedMessageContentLength = 350;

    private readonly DiscordSocketClient _client;
    private readonly IMemberLogChannelService _memberLogChannelService;
    private readonly ILogger<MemberLogService> _logger;
    private bool _started;

    public MemberLogService(
        DiscordSocketClient client,
        IMemberLogChannelService memberLogChannelService,
        ILogger<MemberLogService> logger)
    {
        _client = client;
        _memberLogChannelService = memberLogChannelService;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _client.UserJoined += HandleUserJoinedAsync;
        _client.UserLeft += HandleUserLeftAsync;
        _client.UserVoiceStateUpdated += HandleUserVoiceStateUpdatedAsync;
        _client.MessageDeleted += HandleMessageDeletedAsync;
        _client.MessagesBulkDeleted += HandleMessagesBulkDeletedAsync;
        _client.MessageUpdated += HandleMessageUpdatedAsync;
        _client.UserUpdated += HandleUserUpdatedAsync;
        _client.GuildMemberUpdated += HandleGuildMemberUpdatedAsync;
        _started = true;

        _logger.LogInformation(
            "Member logging subscribed to member, voice, message, and avatar events.");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_started)
        {
            return Task.CompletedTask;
        }

        _client.UserJoined -= HandleUserJoinedAsync;
        _client.UserLeft -= HandleUserLeftAsync;
        _client.UserVoiceStateUpdated -= HandleUserVoiceStateUpdatedAsync;
        _client.MessageDeleted -= HandleMessageDeletedAsync;
        _client.MessagesBulkDeleted -= HandleMessagesBulkDeletedAsync;
        _client.MessageUpdated -= HandleMessageUpdatedAsync;
        _client.UserUpdated -= HandleUserUpdatedAsync;
        _client.GuildMemberUpdated -= HandleGuildMemberUpdatedAsync;
        _started = false;

        return Task.CompletedTask;
    }

    private Task HandleUserJoinedAsync(SocketGuildUser member)
    {
        _logger.LogInformation(
            "Member {MemberId} joined guild {GuildId}.",
            member.Id,
            member.Guild.Id);

        return _memberLogChannelService.PublishAsync(
            new MemberLogEntry(
                member.Guild.Id,
                "Member joined",
                member.Id,
                "The member joined the server.",
                member.GetDisplayAvatarUrl(ImageFormat.Png, 256),
                DateTimeOffset.UtcNow));
    }

    private Task HandleUserLeftAsync(SocketGuild guild, SocketUser user)
    {
        _logger.LogInformation(
            "Member {MemberId} left guild {GuildId}.",
            user.Id,
            guild.Id);

        return _memberLogChannelService.PublishAsync(
            new MemberLogEntry(
                guild.Id,
                "Member left",
                user.Id,
                "The member left the server.",
                user.GetDisplayAvatarUrl(ImageFormat.Png, 256),
                DateTimeOffset.UtcNow));
    }

    private Task HandleUserVoiceStateUpdatedAsync(
        SocketUser user,
        SocketVoiceState before,
        SocketVoiceState after)
    {
        if (user is not SocketGuildUser member ||
            CreateVoiceStateEntry(member, before, after) is not { } entry)
        {
            return Task.CompletedTask;
        }

        _logger.LogInformation(
            "Member {MemberId} had voice event {Event} in guild {GuildId}.",
            member.Id,
            entry.Event,
            member.Guild.Id);

        return _memberLogChannelService.PublishAsync(entry);
    }

    private Task HandleMessageDeletedAsync(
        Cacheable<IMessage, ulong> cachedMessage,
        Cacheable<IMessageChannel, ulong> cachedChannel)
    {
        if (!TryGetGuild(cachedChannel, out var guild))
        {
            return Task.CompletedTask;
        }

        var entry = CreateMessageDeletedEntry(cachedMessage, cachedChannel.Id, guild.Id);

        _logger.LogInformation(
            "Message {MessageId} deleted in guild {GuildId}.",
            cachedMessage.Id,
            guild.Id);

        return _memberLogChannelService.PublishAsync(entry);
    }

    private Task HandleMessagesBulkDeletedAsync(
        IReadOnlyCollection<Cacheable<IMessage, ulong>> cachedMessages,
        Cacheable<IMessageChannel, ulong> cachedChannel)
    {
        if (!TryGetGuild(cachedChannel, out var guild))
        {
            return Task.CompletedTask;
        }

        var cachedMessageCount = cachedMessages.Count(message => message.HasValue);
        var entry = new MemberLogEntry(
            guild.Id,
            "Messages bulk deleted",
            null,
            $"Channel: <#{cachedChannel.Id}>\n" +
            $"Messages deleted: {cachedMessages.Count}\n" +
            $"Cached messages: {cachedMessageCount}",
            null,
            DateTimeOffset.UtcNow);

        _logger.LogInformation(
            "{MessageCount} messages deleted in bulk in guild {GuildId}.",
            cachedMessages.Count,
            guild.Id);

        return _memberLogChannelService.PublishAsync(entry);
    }

    private Task HandleMessageUpdatedAsync(
        Cacheable<IMessage, ulong> before,
        SocketMessage after,
        ISocketMessageChannel channel)
    {
        if (!TryGetGuild(channel, out var guild) ||
            (before.HasValue && before.Value.Content == after.Content))
        {
            return Task.CompletedTask;
        }

        var entry = CreateMessageUpdatedEntry(before, after, channel.Id, guild.Id);

        _logger.LogInformation(
            "Message {MessageId} edited in guild {GuildId}.",
            after.Id,
            guild.Id);

        return _memberLogChannelService.PublishAsync(entry);
    }

    private Task HandleUserUpdatedAsync(SocketUser before, SocketUser after)
    {
        if (before.AvatarId == after.AvatarId || after is not SocketGuildUser member)
        {
            return Task.CompletedTask;
        }

        _logger.LogInformation(
            "Member {MemberId} changed their avatar in guild {GuildId}.",
            member.Id,
            member.Guild.Id);

        return _memberLogChannelService.PublishAsync(
            new MemberLogEntry(
                member.Guild.Id,
                "Avatar changed",
                member.Id,
                "The member changed their Discord avatar.",
                GetGlobalAvatarUrl(member),
                DateTimeOffset.UtcNow));
    }

    private Task HandleGuildMemberUpdatedAsync(
        Cacheable<SocketGuildUser, ulong> before,
        SocketGuildUser after)
    {
        if (!before.HasValue || before.Value.GuildAvatarId == after.GuildAvatarId)
        {
            return Task.CompletedTask;
        }

        _logger.LogInformation(
            "Member {MemberId} changed their server avatar in guild {GuildId}.",
            after.Id,
            after.Guild.Id);

        return _memberLogChannelService.PublishAsync(
            new MemberLogEntry(
                after.Guild.Id,
                "Server avatar changed",
                after.Id,
                "The member changed their server-specific avatar.",
                after.GetDisplayAvatarUrl(ImageFormat.Png, 256),
                DateTimeOffset.UtcNow));
    }

    private static bool TryGetGuild(ISocketMessageChannel channel, out SocketGuild guild)
    {
        if (channel is not SocketGuildChannel guildChannel)
        {
            guild = default!;
            return false;
        }

        guild = guildChannel.Guild;
        return true;
    }

    private bool TryGetGuild(
        Cacheable<IMessageChannel, ulong> cachedChannel,
        out SocketGuild guild)
    {
        var guildChannel = cachedChannel.Value as SocketGuildChannel
            ?? _client.GetChannel(cachedChannel.Id) as SocketGuildChannel;

        if (guildChannel is null)
        {
            guild = default!;
            return false;
        }

        guild = guildChannel.Guild;
        return true;
    }

    private static MemberLogEntry CreateMessageDeletedEntry(
        Cacheable<IMessage, ulong> cachedMessage,
        ulong channelId,
        ulong guildId)
    {
        if (!cachedMessage.HasValue)
        {
            return new MemberLogEntry(
                guildId,
                "Message deleted",
                null,
                $"Channel: <#{channelId}>\n" +
                $"Message ID: `{cachedMessage.Id}`\n" +
                "The message was not in cache.",
                null,
                DateTimeOffset.UtcNow);
        }

        var message = cachedMessage.Value;
        return new MemberLogEntry(
            guildId,
            "Message deleted",
            message.Author.Id,
            $"Channel: <#{channelId}>\n" +
            $"Message ID: `{message.Id}`\n" +
            $"Content: {FormatMessageContent(message.Content)}",
            message.Author.GetDisplayAvatarUrl(ImageFormat.Png, 256),
            DateTimeOffset.UtcNow);
    }

    private static MemberLogEntry? CreateVoiceStateEntry(
        SocketGuildUser member,
        SocketVoiceState before,
        SocketVoiceState after)
    {
        if (before.VoiceChannelId != after.VoiceChannelId)
        {
            var (eventName, details) = (before.VoiceChannelId, after.VoiceChannelId) switch
            {
                (null, { } joinedChannelId) => (
                    "Voice channel joined",
                    $"Channel: <#{joinedChannelId}>"),
                ({ } leftChannelId, null) => (
                    "Voice channel left",
                    $"Channel: <#{leftChannelId}>"),
                ({ } previousChannelId, { } currentChannelId) => (
                    "Voice channel moved",
                    $"From: <#{previousChannelId}>\nTo: <#{currentChannelId}>"),
                _ => throw new InvalidOperationException("Unexpected voice channel state.")
            };

            return CreateVoiceEntry(member, eventName, details);
        }

        var changes = new List<string>();
        AddVoiceStateChange(changes, before.IsMuted, after.IsMuted, "server-muted", "server-unmuted");
        AddVoiceStateChange(
            changes,
            before.IsDeafened,
            after.IsDeafened,
            "server-deafened",
            "server-undeafened");
        AddVoiceStateChange(changes, before.IsSelfMuted, after.IsSelfMuted, "self-muted", "self-unmuted");
        AddVoiceStateChange(
            changes,
            before.IsSelfDeafened,
            after.IsSelfDeafened,
            "self-deafened",
            "self-undeafened");
        AddVoiceStateChange(
            changes,
            before.IsStreaming,
            after.IsStreaming,
            "started streaming",
            "stopped streaming");
        AddVoiceStateChange(
            changes,
            before.IsVideoing,
            after.IsVideoing,
            "started video",
            "stopped video");
        AddVoiceStateChange(changes, before.IsSuppressed, after.IsSuppressed, "suppressed", "unsuppressed");

        if (before.RequestToSpeakTimestamp != after.RequestToSpeakTimestamp)
        {
            changes.Add(
                after.RequestToSpeakTimestamp is null
                    ? "cancelled request to speak"
                    : "requested to speak");
        }

        return changes.Count == 0
            ? null
            : CreateVoiceEntry(
                member,
                "Voice state changed",
                $"Channel: {FormatVoiceChannel(after.VoiceChannelId)}\n" +
                $"Changes: {string.Join(", ", changes)}.");
    }

    private static MemberLogEntry CreateVoiceEntry(
        SocketGuildUser member,
        string eventName,
        string details) =>
        new(
            member.Guild.Id,
            eventName,
            member.Id,
            details,
            member.GetDisplayAvatarUrl(ImageFormat.Png, 256),
            DateTimeOffset.UtcNow);

    private static void AddVoiceStateChange(
        ICollection<string> changes,
        bool before,
        bool after,
        string enabledEvent,
        string disabledEvent)
    {
        if (before != after)
        {
            changes.Add(after ? enabledEvent : disabledEvent);
        }
    }

    private static string FormatVoiceChannel(ulong? channelId) =>
        channelId is { } value ? $"<#{value}>" : "Unknown voice channel";

    private static MemberLogEntry CreateMessageUpdatedEntry(
        Cacheable<IMessage, ulong> before,
        SocketMessage after,
        ulong channelId,
        ulong guildId)
    {
        var previousContent = before.HasValue
            ? FormatMessageContent(
                before.Value.Content,
                MaximumEditedMessageContentLength)
            : "The previous message content was not cached.";

        return new MemberLogEntry(
            guildId,
            "Message edited",
            after.Author.Id,
            $"Channel: <#{channelId}>\n" +
            $"Message ID: `{after.Id}`\n" +
            $"Previous content: {previousContent}\n" +
            $"New content: {FormatMessageContent(after.Content, MaximumEditedMessageContentLength)}",
            after.Author.GetDisplayAvatarUrl(ImageFormat.Png, 256),
            DateTimeOffset.UtcNow);
    }

    private static string FormatMessageContent(string content)
        => FormatMessageContent(content, MaximumMessageContentLength);

    private static string FormatMessageContent(string content, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return "No text content was cached.";
        }

        return content.Length <= maximumLength
            ? content
            : $"{content[..maximumLength]}…";
    }

    private static string GetGlobalAvatarUrl(SocketGuildUser member) =>
        member.GetAvatarUrl(ImageFormat.Png, 256) ?? member.GetDefaultAvatarUrl();
}
