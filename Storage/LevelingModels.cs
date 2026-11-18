namespace Arisu.Storage;

public sealed record LevelingSettings(
    ulong GuildId,
    bool Enabled,
    int CooldownSeconds,
    IReadOnlyList<ulong> ExcludedChannelIds,
    IReadOnlyList<ulong> ExcludedRoleIds,
    IReadOnlyList<LevelRoleReward> Rewards);

public sealed record LevelRoleReward(int Level, ulong RoleId);

public sealed record LevelingMember(
    ulong GuildId,
    ulong UserId,
    long TotalXp,
    long MessageCount,
    DateTimeOffset LastAwardedAt);

public sealed record LevelingRank(LevelingMember Member, long Position);

public sealed record LevelingLeaderboard(IReadOnlyList<LevelingRank> Entries, long TotalMembers);

public sealed record LevelingAward(LevelingMember Member, long PreviousXp);
