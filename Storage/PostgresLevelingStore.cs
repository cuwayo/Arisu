using System.Globalization;
using Npgsql;

namespace Arisu.Storage;

public sealed class PostgresLevelingStore : ILevelingStore, IDisposable
{
    private const long SchemaInitializationLock = 0x41726973754C7673;
    private readonly NpgsqlDataSource _dataSource;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private volatile bool _initialized;

    public PostgresLevelingStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using (var command = new NpgsqlCommand(
                "SELECT pg_advisory_xact_lock(@schemaLock);", connection, transaction))
            {
                command.Parameters.AddWithValue("schemaLock", SchemaInitializationLock);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var command = new NpgsqlCommand("""
                CREATE TABLE IF NOT EXISTS LevelingSettings (
                    GuildId TEXT PRIMARY KEY,
                    Enabled BOOLEAN NOT NULL DEFAULT FALSE,
                    CooldownSeconds INTEGER NOT NULL DEFAULT 60 CHECK (CooldownSeconds BETWEEN 15 AND 3600),
                    ExcludedChannelIds TEXT[] NOT NULL DEFAULT '{}',
                    ExcludedRoleIds TEXT[] NOT NULL DEFAULT '{}'
                );
                CREATE TABLE IF NOT EXISTS LevelingRewards (
                    GuildId TEXT NOT NULL,
                    Level INTEGER NOT NULL CHECK (Level BETWEEN 1 AND 1000),
                    RoleId TEXT NOT NULL,
                    PRIMARY KEY (GuildId, Level)
                );
                CREATE TABLE IF NOT EXISTS LevelingMembers (
                    GuildId TEXT NOT NULL,
                    UserId TEXT NOT NULL,
                    TotalXp BIGINT NOT NULL CHECK (TotalXp >= 0),
                    MessageCount BIGINT NOT NULL CHECK (MessageCount > 0),
                    LastAwardedAt TIMESTAMPTZ NOT NULL,
                    PRIMARY KEY (GuildId, UserId)
                );
                CREATE INDEX IF NOT EXISTS IX_LevelingMembers_Leaderboard
                    ON LevelingMembers (GuildId, TotalXp DESC, length(UserId), UserId COLLATE "C");
                """, connection, transaction))
            {
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    public async Task<LevelingSettings> GetSettingsAsync(ulong guildId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var enabled = false;
        var cooldown = 60;
        ulong[] channels = [];
        ulong[] roles = [];
        await using (var command = new NpgsqlCommand("""
            SELECT Enabled, CooldownSeconds, ExcludedChannelIds, ExcludedRoleIds
            FROM LevelingSettings WHERE GuildId = @guildId;
            """, connection))
        {
            command.Parameters.AddWithValue("guildId", ToDatabaseValue(guildId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                enabled = reader.GetBoolean(0);
                cooldown = reader.GetInt32(1);
                channels = reader.GetFieldValue<string[]>(2).Select(ParseSnowflake).Order().ToArray();
                roles = reader.GetFieldValue<string[]>(3).Select(ParseSnowflake).Order().ToArray();
            }
        }

        var rewards = new List<LevelRoleReward>();
        await using (var command = new NpgsqlCommand("""
            SELECT Level, RoleId FROM LevelingRewards WHERE GuildId = @guildId ORDER BY Level;
            """, connection))
        {
            command.Parameters.AddWithValue("guildId", ToDatabaseValue(guildId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rewards.Add(new LevelRoleReward(reader.GetInt32(0), ParseSnowflake(reader.GetString(1))));
            }
        }

        return new LevelingSettings(guildId, enabled, cooldown, channels, roles, rewards);
    }

    public Task SetEnabledAsync(ulong guildId, bool enabled, CancellationToken cancellationToken = default)
    {
        return UpdateSettingAsync(guildId, "Enabled", enabled, cancellationToken);
    }

    public Task SetCooldownAsync(ulong guildId, int seconds, CancellationToken cancellationToken = default)
    {
        ValidateRange(seconds, 15, 3600, nameof(seconds));
        return UpdateSettingAsync(guildId, "CooldownSeconds", seconds, cancellationToken);
    }

    public Task SetChannelExcludedAsync(ulong guildId, ulong channelId, bool excluded, CancellationToken cancellationToken = default)
    {
        return UpdateExclusionAsync(guildId, "ExcludedChannelIds", channelId, excluded, cancellationToken);
    }

    public Task SetRoleExcludedAsync(ulong guildId, ulong roleId, bool excluded, CancellationToken cancellationToken = default)
    {
        return UpdateExclusionAsync(guildId, "ExcludedRoleIds", roleId, excluded, cancellationToken);
    }

    public async Task SetRewardAsync(ulong guildId, int level, ulong roleId, CancellationToken cancellationToken = default)
    {
        ValidateRange(level, 1, 1000, nameof(level));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO LevelingRewards (GuildId, Level, RoleId) VALUES (@guildId, @level, @roleId)
            ON CONFLICT (GuildId, Level) DO UPDATE SET RoleId = EXCLUDED.RoleId;
            """, connection);
        command.Parameters.AddWithValue("guildId", ToDatabaseValue(guildId));
        command.Parameters.AddWithValue("level", level);
        command.Parameters.AddWithValue("roleId", ToDatabaseValue(roleId));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RemoveRewardAsync(ulong guildId, int level, CancellationToken cancellationToken = default)
    {
        ValidateRange(level, 1, 1000, nameof(level));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "DELETE FROM LevelingRewards WHERE GuildId = @guildId AND Level = @level;", connection);
        command.Parameters.AddWithValue("guildId", ToDatabaseValue(guildId));
        command.Parameters.AddWithValue("level", level);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<LevelingAward?> TryAwardAsync(
        ulong guildId,
        ulong userId,
        ulong channelId,
        IReadOnlyCollection<ulong> roleIds,
        int xp,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken = default)
    {
        ValidateRange(xp, 15, 25, nameof(xp));
        ArgumentNullException.ThrowIfNull(roleIds);
        if (timestamp == DateTimeOffset.MinValue || timestamp == DateTimeOffset.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(timestamp), "A finite message timestamp is required.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        // The conditional upsert holds the member row lock, including when multiple
        // processes try to award the first message. No in-memory cooldown is needed.
        await using var command = new NpgsqlCommand("""
            INSERT INTO LevelingMembers AS member (GuildId, UserId, TotalXp, MessageCount, LastAwardedAt)
            SELECT @guildId, @userId, @xp, 1, @timestamp
            FROM LevelingSettings AS settings
            WHERE settings.GuildId = @guildId AND settings.Enabled
                AND NOT (@channelId = ANY(settings.ExcludedChannelIds))
                AND NOT (settings.ExcludedRoleIds && @roleIds)
            ON CONFLICT (GuildId, UserId) DO UPDATE
            SET TotalXp = member.TotalXp + EXCLUDED.TotalXp,
                MessageCount = member.MessageCount + 1,
                LastAwardedAt = EXCLUDED.LastAwardedAt
            WHERE member.TotalXp <= 9223372036854775807 - @xp
                AND member.MessageCount < 9223372036854775807
                AND member.LastAwardedAt <= @timestamp - (
                SELECT make_interval(secs => CooldownSeconds)
                FROM LevelingSettings WHERE GuildId = @guildId)
            RETURNING TotalXp, MessageCount, LastAwardedAt;
            """, connection);
        command.Parameters.AddWithValue("guildId", ToDatabaseValue(guildId));
        command.Parameters.AddWithValue("userId", ToDatabaseValue(userId));
        command.Parameters.AddWithValue("channelId", ToDatabaseValue(channelId));
        command.Parameters.AddWithValue("roleIds", roleIds.Select(ToDatabaseValue).ToArray());
        command.Parameters.AddWithValue("xp", (long)xp);
        command.Parameters.AddWithValue("timestamp", timestamp.ToUniversalTime());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var member = new LevelingMember(guildId, userId, reader.GetInt64(0), reader.GetInt64(1),
            reader.GetFieldValue<DateTimeOffset>(2));
        return new LevelingAward(member, member.TotalXp - xp);
    }

    public async Task<LevelingRank?> GetRankAsync(ulong guildId, ulong userId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT member.TotalXp, member.MessageCount, member.LastAwardedAt,
                1 + (SELECT count(*) FROM LevelingMembers AS other
                    WHERE other.GuildId = member.GuildId AND (
                        other.TotalXp > member.TotalXp OR (other.TotalXp = member.TotalXp AND (
                            length(other.UserId) < length(member.UserId) OR (
                                length(other.UserId) = length(member.UserId)
                                AND other.UserId COLLATE "C" < member.UserId COLLATE "C"))))) AS Position
            FROM LevelingMembers AS member WHERE member.GuildId = @guildId AND member.UserId = @userId;
            """, connection);
        command.Parameters.AddWithValue("guildId", ToDatabaseValue(guildId));
        command.Parameters.AddWithValue("userId", ToDatabaseValue(userId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new LevelingRank(new LevelingMember(guildId, userId, reader.GetInt64(0), reader.GetInt64(1),
                reader.GetFieldValue<DateTimeOffset>(2)), reader.GetInt64(3))
            : null;
    }

    public async Task<LevelingLeaderboard> GetLeaderboardAsync(ulong guildId, int page, CancellationToken cancellationToken = default)
    {
        ValidateRange(page, 1, 1000, nameof(page));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT totals.TotalMembers, entries.UserId, entries.TotalXp, entries.MessageCount, entries.LastAwardedAt
            FROM (SELECT count(*) AS TotalMembers FROM LevelingMembers WHERE GuildId = @guildId) AS totals
            LEFT JOIN (
                SELECT UserId, TotalXp, MessageCount, LastAwardedAt FROM LevelingMembers
                WHERE GuildId = @guildId
                ORDER BY TotalXp DESC, length(UserId), UserId COLLATE "C"
                LIMIT 10 OFFSET @offset
            ) AS entries ON TRUE
            ORDER BY entries.TotalXp DESC, length(entries.UserId), entries.UserId COLLATE "C";
            """, connection);
        command.Parameters.AddWithValue("guildId", ToDatabaseValue(guildId));
        command.Parameters.AddWithValue("offset", (page - 1) * 10);
        var entries = new List<LevelingRank>();
        var total = 0L;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            total = reader.GetInt64(0);
            if (!reader.IsDBNull(1))
            {
                entries.Add(new LevelingRank(new LevelingMember(guildId, ParseSnowflake(reader.GetString(1)),
                    reader.GetInt64(2), reader.GetInt64(3), reader.GetFieldValue<DateTimeOffset>(4)),
                    (page - 1) * 10L + entries.Count + 1));
            }
        }

        return new LevelingLeaderboard(entries, total);
    }

    public void Dispose() => _initializationGate.Dispose();

    private async Task UpdateSettingAsync(ulong guildId, string column, object value, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        // Column names are private constants supplied only by the methods above.
        await using var command = new NpgsqlCommand($"""
            INSERT INTO LevelingSettings (GuildId, {column}) VALUES (@guildId, @value)
            ON CONFLICT (GuildId) DO UPDATE SET {column} = EXCLUDED.{column};
            """, connection);
        command.Parameters.AddWithValue("guildId", ToDatabaseValue(guildId));
        command.Parameters.AddWithValue("value", value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task UpdateExclusionAsync(ulong guildId, string column, ulong id, bool excluded, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var expression = excluded
            ? $"array_append(settings.{column}, @id)"
            : $"array_remove(settings.{column}, @id)";
        await using var command = new NpgsqlCommand($"""
            INSERT INTO LevelingSettings AS settings (GuildId, {column}) VALUES (@guildId, @initial)
            ON CONFLICT (GuildId) DO UPDATE SET {column} = {expression}
            WHERE @excluded <> (@id = ANY(settings.{column}));
            """, connection);
        command.Parameters.AddWithValue("guildId", ToDatabaseValue(guildId));
        command.Parameters.AddWithValue("id", ToDatabaseValue(id));
        command.Parameters.AddWithValue("initial", excluded ? new[] { ToDatabaseValue(id) } : Array.Empty<string>());
        command.Parameters.AddWithValue("excluded", excluded);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        return await _dataSource.OpenConnectionAsync(cancellationToken);
    }

    private static void ValidateRange(int value, int minimum, int maximum, string name)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(name, value, $"Must be between {minimum} and {maximum}.");
        }
    }

    private static string ToDatabaseValue(ulong value) => value.ToString(CultureInfo.InvariantCulture);
    private static ulong ParseSnowflake(string value) => ulong.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
}
