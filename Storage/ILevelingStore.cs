namespace Arisu.Storage;

public interface ILevelingStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<LevelingSettings> GetSettingsAsync(ulong guildId, CancellationToken cancellationToken = default);
    Task SetEnabledAsync(ulong guildId, bool enabled, CancellationToken cancellationToken = default);
    Task SetCooldownAsync(ulong guildId, int seconds, CancellationToken cancellationToken = default);
    Task SetChannelExcludedAsync(ulong guildId, ulong channelId, bool excluded, CancellationToken cancellationToken = default);
    Task SetRoleExcludedAsync(ulong guildId, ulong roleId, bool excluded, CancellationToken cancellationToken = default);
    Task SetRewardAsync(ulong guildId, int level, ulong roleId, CancellationToken cancellationToken = default);
    Task RemoveRewardAsync(ulong guildId, int level, CancellationToken cancellationToken = default);
    // Callers must supply the trusted Discord message timestamp, not user input
    // or processing time. This keeps queued messages inside the durable cooldown.
    Task<LevelingAward?> TryAwardAsync(ulong guildId, ulong userId, ulong channelId, IReadOnlyCollection<ulong> roleIds, int xp, DateTimeOffset timestamp, CancellationToken cancellationToken = default);
    Task<LevelingRank?> GetRankAsync(ulong guildId, ulong userId, CancellationToken cancellationToken = default);
    Task<LevelingLeaderboard> GetLeaderboardAsync(ulong guildId, int page, CancellationToken cancellationToken = default);
}
