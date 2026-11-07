using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Arisu.Services;

public sealed class ModerationAuditLogService : IHostedService
{
    private const string NoReasonProvided = "No reason provided.";
    private const int AuditLogActorLookupLimit = 1;

    private readonly IAuditChannelService _auditChannelService;
    private readonly DiscordSocketClient _client;
    private readonly ILogger<ModerationAuditLogService> _logger;
    private bool _started;

    public ModerationAuditLogService(
        IAuditChannelService auditChannelService,
        DiscordSocketClient client,
        ILogger<ModerationAuditLogService> logger)
    {
        _auditChannelService = auditChannelService;
        _client = client;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _client.AuditLogCreated += HandleAuditLogCreatedAsync;
        _started = true;

        _logger.LogInformation(
            "Moderation audit logging subscribed to Discord audit log events.");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_started)
        {
            return Task.CompletedTask;
        }

        _client.AuditLogCreated -= HandleAuditLogCreatedAsync;
        _started = false;

        return Task.CompletedTask;
    }

    private async Task HandleAuditLogCreatedAsync(
        SocketAuditLogEntry auditLogEntry,
        SocketGuild guild)
    {
        var actorId = auditLogEntry.User?.Id
            ?? await ResolveActorIdAsync(auditLogEntry, guild);
        var moderationEntry = CreateAuditEntry(auditLogEntry, guild.Id, actorId);

        _logger.LogInformation(
            "Received {Action} audit log entry for target {Target} in guild {GuildId}.",
            moderationEntry.Action,
            moderationEntry.Target ?? "unknown",
            moderationEntry.GuildId);

        await _auditChannelService.WriteAsync(moderationEntry);
    }

    private static ModerationAuditEntry CreateAuditEntry(
        SocketAuditLogEntry auditLogEntry,
        ulong guildId,
        ulong? actorId)
    {
        var details = CreateDetails(auditLogEntry);
        var action = GetActionName(auditLogEntry.Action);

        if (auditLogEntry.Data is SocketMemberUpdateAuditLogData memberUpdate)
        {
            if (memberUpdate.After.TimedOutUntil is { } timedOutUntil)
            {
                action = "Timeout";
                details = new AuditEntryDetails(
                    MemberTarget(memberUpdate.Target.Id),
                    $"Timeout ends <t:{timedOutUntil.ToUnixTimeSeconds()}:F>.");
            }
            else if (memberUpdate.Before.TimedOutUntil is not null)
            {
                action = "Timeout removed";
                details = new AuditEntryDetails(
                    MemberTarget(memberUpdate.Target.Id),
                    details.Details);
            }
        }

        return new ModerationAuditEntry(
            guildId,
            action,
            actorId,
            details.Target,
            string.IsNullOrWhiteSpace(auditLogEntry.Reason)
                ? NoReasonProvided
                : auditLogEntry.Reason,
            details.Details,
            auditLogEntry.CreatedAt);
    }

    private async Task<ulong?> ResolveActorIdAsync(
        SocketAuditLogEntry auditLogEntry,
        SocketGuild guild)
    {
        try
        {
            await foreach (var entries in guild.GetAuditLogsAsync(
                               AuditLogActorLookupLimit,
                               beforeId: auditLogEntry.Id + 1))
            {
                return entries
                    .FirstOrDefault(entry => entry.Id == auditLogEntry.Id)
                    ?.User
                    ?.Id;
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Could not resolve the actor for audit log entry {AuditLogEntryId} in guild {GuildId}.",
                auditLogEntry.Id,
                guild.Id);
        }

        return null;
    }

    private static AuditEntryDetails CreateDetails(SocketAuditLogEntry auditLogEntry) =>
        auditLogEntry.Data switch
        {
            SocketKickAuditLogData data =>
                new AuditEntryDetails(MemberTarget(data.Target.Id), null),
            SocketBanAuditLogData data =>
                new AuditEntryDetails(MemberTarget(data.Target.Id), null),
            SocketUnbanAuditLogData data =>
                new AuditEntryDetails(MemberTarget(data.Target.Id), null),
            SocketMemberUpdateAuditLogData data =>
                new AuditEntryDetails(MemberTarget(data.Target.Id), "Member settings changed."),
            SocketMemberRoleAuditLogData data =>
                new AuditEntryDetails(
                    MemberTarget(data.Target.Id),
                    $"Roles changed: {data.Roles.Count}."),
            SocketChannelCreateAuditLogData data =>
                new AuditEntryDetails(
                    ChannelTarget(data.ChannelName, data.ChannelId),
                    $"Channel type: {data.ChannelType}."),
            SocketChannelDeleteAuditLogData data =>
                new AuditEntryDetails(
                    ChannelTarget(data.ChannelName, data.ChannelId),
                    $"Channel type: {data.ChannelType}."),
            SocketChannelUpdateAuditLogData data =>
                new AuditEntryDetails(
                    ChannelTarget(data.ChannelId),
                    "Channel settings changed."),
            SocketMemberMoveAuditLogData data =>
                new AuditEntryDetails(
                    null,
                    $"Destination channel: <#{data.ChannelId}>\n" +
                    $"Members moved: {data.MemberCount}."),
            SocketMemberDisconnectAuditLogData data =>
                new AuditEntryDetails(
                    null,
                    $"Members disconnected: {data.MemberCount}."),
            SocketMessageDeleteAuditLogData data =>
                new AuditEntryDetails(
                    data.Target.HasValue ? MemberTarget(data.Target.Id) : null,
                    $"Channel: <#{data.ChannelId}>\n" +
                    $"Messages deleted: {data.MessageCount}."),
            SocketMessageBulkDeleteAuditLogData data =>
                new AuditEntryDetails(
                    null,
                    $"Channel: <#{data.ChannelId}>\n" +
                    $"Messages deleted: {data.MessageCount}."),
            _ => new AuditEntryDetails(
                null,
                auditLogEntry.Data is { } data
                    ? $"Discord audit data: {data.GetType().Name}."
                    : "Discord did not provide audit details.")
        };

    private static string GetActionName(ActionType action)
    {
        var actionName = action.ToString();
        var builder = new System.Text.StringBuilder(actionName.Length + 4);

        foreach (var character in actionName)
        {
            if (char.IsUpper(character) && builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static string ChannelTarget(string channelName, ulong channelId) =>
        $"#{channelName} (`{channelId}`)";

    private static string ChannelTarget(ulong channelId) => $"<#{channelId}>";

    private static string MemberTarget(ulong memberId) => $"<@{memberId}>";

    private sealed record AuditEntryDetails(string? Target, string? Details);
}
