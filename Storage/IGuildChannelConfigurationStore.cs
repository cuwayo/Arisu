namespace Arisu.Storage;

public interface IGuildChannelConfigurationStore
{
    Task<ulong?> GetAuditChannelIdAsync(
        ulong guildId,
        CancellationToken cancellationToken = default);

    Task SetAuditChannelIdAsync(
        ulong guildId,
        ulong channelId,
        CancellationToken cancellationToken = default);

    Task RemoveAuditChannelIdAsync(
        ulong guildId,
        CancellationToken cancellationToken = default);

    Task<ulong?> GetMemberLogChannelIdAsync(
        ulong guildId,
        CancellationToken cancellationToken = default);

    Task SetMemberLogChannelIdAsync(
        ulong guildId,
        ulong channelId,
        CancellationToken cancellationToken = default);

    Task RemoveMemberLogChannelIdAsync(
        ulong guildId,
        CancellationToken cancellationToken = default);

    Task<ulong?> GetEventChannelIdAsync(
        ulong guildId,
        CancellationToken cancellationToken = default);

    Task SetEventChannelIdAsync(
        ulong guildId,
        ulong channelId,
        CancellationToken cancellationToken = default);

    Task RemoveEventChannelIdAsync(
        ulong guildId,
        CancellationToken cancellationToken = default);

    Task<string?> GetEventBackgroundObjectKeyAsync(
        ulong guildId,
        CancellationToken cancellationToken = default);

    Task SetEventBackgroundObjectKeyAsync(
        ulong guildId,
        string objectKey,
        CancellationToken cancellationToken = default);

    Task RemoveEventBackgroundObjectKeyAsync(
        ulong guildId,
        CancellationToken cancellationToken = default);

    Task<WarningIssueResult> AddWarningAsync(
        ulong guildId,
        ulong memberId,
        ulong moderatorId,
        string reason,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default);

    Task<WarningHistory> GetWarningsAsync(
        ulong guildId,
        ulong memberId,
        int maximumCount = 8,
        CancellationToken cancellationToken = default);

    Task<WarningEscalation?> GetWarningEscalationAsync(
        ulong guildId,
        CancellationToken cancellationToken = default);

    Task SetWarningEscalationAsync(
        ulong guildId,
        WarningEscalation escalation,
        CancellationToken cancellationToken = default);

    Task RemoveWarningEscalationAsync(
        ulong guildId,
        CancellationToken cancellationToken = default);

    Task RemoveWarningEscalationApplicationAsync(
        ulong guildId,
        ulong memberId,
        CancellationToken cancellationToken = default);
}

public sealed record MemberWarning(
    long Id,
    ulong GuildId,
    ulong MemberId,
    ulong ModeratorId,
    string Reason,
    DateTimeOffset CreatedAt);

public sealed record WarningEscalation(int Threshold, int TimeoutMinutes);

public sealed record WarningHistory(int TotalCount, IReadOnlyList<MemberWarning> RecentWarnings);

public sealed record WarningIssueResult(
    MemberWarning Warning,
    int WarningCount,
    WarningEscalation? Escalation,
    bool ShouldApplyEscalation);
