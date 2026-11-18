using Arisu.Storage;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace Arisu.Services;

public interface IAuditChannelService
{
    Task<bool> SetAuditChannelAsync(ulong guildId, ulong channelId);

    Task<bool> RemoveAuditChannelAsync(ulong guildId);

    Task WriteAsync(ModerationAuditEntry entry, CancellationToken cancellationToken = default);
}

public sealed record ModerationAuditEntry(
    ulong GuildId,
    string Action,
    ulong? ActorId,
    string? Target,
    string Reason,
    string? Details,
    DateTimeOffset OccurredAt);

public sealed class AuditChannelService : IAuditChannelService
{
    private readonly DiscordSocketClient _client;
    private readonly ILogger<AuditChannelService> _logger;
    private readonly IGuildChannelConfigurationStore _channelConfigurationStore;
    private readonly LogChannelCache _cache;

    public AuditChannelService(
        DiscordSocketClient client,
        IGuildChannelConfigurationStore channelConfigurationStore,
        LogChannelCache cache,
        ILogger<AuditChannelService> logger)
    {
        _client = client;
        _channelConfigurationStore = channelConfigurationStore;
        _cache = cache;
        _logger = logger;
    }

    public async Task<bool> SetAuditChannelAsync(ulong guildId, ulong channelId)
    {
        try
        {
            await LogChannelConfigurationWriter.WriteAsync(
                guildId,
                LogChannelKind.Audit,
                channelId,
                _cache,
                () => _channelConfigurationStore.SetAuditChannelIdAsync(guildId, channelId));
            return true;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Could not save audit channel {ChannelId} for guild {GuildId}.",
                channelId,
                guildId);
            return false;
        }
    }

    public async Task WriteAsync(
        ModerationAuditEntry entry,
        CancellationToken cancellationToken = default)
    {
        await WriteAsync(entry.GuildId, BuildEmbed(entry), cancellationToken);
    }

    public async Task<bool> RemoveAuditChannelAsync(ulong guildId)
    {
        try
        {
            await LogChannelConfigurationWriter.WriteAsync(
                guildId,
                LogChannelKind.Audit,
                null,
                _cache,
                () => _channelConfigurationStore.RemoveAuditChannelIdAsync(guildId));
            return true;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Could not remove the audit channel for guild {GuildId}.",
                guildId);
            return false;
        }
    }

    private async Task WriteAsync(ulong guildId, Embed embed, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var channelId = await _cache.GetAsync(
            guildId,
            LogChannelKind.Audit,
            token => _channelConfigurationStore.GetAuditChannelIdAsync(guildId, token),
            cancellationToken);
        if (channelId is null)
        {
            return;
        }

        if (_client.GetChannel(channelId.Value) is not ITextChannel channel)
        {
            _logger.LogWarning(
                "Audit channel {ChannelId} for guild {GuildId} is unavailable or is not a text channel.",
                channelId,
                guildId);
            return;
        }

        try
        {
            await channel.SendMessageAsync(
                embed: embed,
                allowedMentions: AllowedMentions.None,
                options: new RequestOptions { CancelToken = cancellationToken });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Could not write audit entry to channel {ChannelId} in guild {GuildId}.",
                channelId,
                guildId);
        }
    }

    private static Embed BuildEmbed(ModerationAuditEntry entry)
    {
        var builder = new EmbedBuilder()
            .WithTitle("Server audit log")
            .WithColor(GetActionColor(entry.Action))
            .WithTimestamp(entry.OccurredAt)
            .AddField("Action", entry.Action, inline: true)
            .AddField(
                "Actor",
                entry.ActorId is { } actorId ? $"<@{actorId}>" : "Unknown",
                inline: true)
            .AddField("Reason", entry.Reason);

        if (!string.IsNullOrWhiteSpace(entry.Target))
        {
            builder.AddField("Target", entry.Target, inline: true);
        }

        if (!string.IsNullOrWhiteSpace(entry.Details))
        {
            builder.AddField("Details", entry.Details);
        }

        return builder.Build();
    }

    private static Color GetActionColor(string action) => action switch
    {
        "Ban" => new Color(237, 66, 69),
        "Unban" => new Color(87, 242, 135),
        "Kick" => new Color(254, 231, 92),
        "Timeout" => new Color(88, 101, 242),
        "Timeout removed" => new Color(87, 242, 135),
        _ when action.EndsWith("Created", StringComparison.Ordinal) => new Color(87, 242, 135),
        _ when action.EndsWith("Deleted", StringComparison.Ordinal) => new Color(237, 66, 69),
        _ when action.EndsWith("Updated", StringComparison.Ordinal) => new Color(88, 101, 242),
        _ => new Color(148, 155, 164)
    };
}
