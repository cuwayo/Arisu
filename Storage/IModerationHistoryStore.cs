namespace Arisu.Storage;

public interface IModerationHistoryStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task RecordCaseAsync(
        ulong guildId,
        ulong targetId,
        ulong moderatorId,
        string action,
        string reason,
        string? details,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ModerationCase>> GetCasesAsync(
        ulong guildId,
        ulong targetId,
        int limit = 8,
        CancellationToken cancellationToken = default);

    Task<MemberWarning?> GetWarningAsync(
        ulong guildId,
        long warningId,
        CancellationToken cancellationToken = default);

    // Warning changes preserve escalation markers: escalation applies once per
    // member for the lifetime of the current policy, even after warnings are removed.
    Task<MemberWarning?> RemoveWarningAsync(
        ulong guildId,
        long warningId,
        CancellationToken cancellationToken = default);

    Task<int> ClearWarningsAsync(
        ulong guildId,
        ulong memberId,
        CancellationToken cancellationToken = default);

    Task<WarningReasonChange?> UpdateWarningReasonAsync(
        ulong guildId,
        long warningId,
        string reason,
        CancellationToken cancellationToken = default);
}

public sealed record ModerationCase(
    long Id,
    ulong GuildId,
    ulong TargetId,
    ulong ModeratorId,
    string Action,
    string Reason,
    string? Details,
    DateTimeOffset CreatedAt);

public sealed record WarningReasonChange(MemberWarning Warning, string PreviousReason);
