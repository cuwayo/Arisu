using System.Data;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Arisu.Storage;

public sealed class PostgresGuildChannelConfigurationStore : IGuildChannelConfigurationStore, IDisposable
{
    private const long SchemaInitializationLock = 0x4172697375536368;
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<PostgresGuildChannelConfigurationStore> _logger;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private volatile bool _initialized;

    public PostgresGuildChannelConfigurationStore(
        NpgsqlDataSource dataSource,
        ILogger<PostgresGuildChannelConfigurationStore> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
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
            await using (var lockCommand = new NpgsqlCommand(
                "SELECT pg_advisory_xact_lock(@schemaLock);", connection, transaction))
            {
                lockCommand.Parameters.AddWithValue("schemaLock", SchemaInitializationLock);
                await lockCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var versionCommand = new NpgsqlCommand("""
                CREATE TABLE IF NOT EXISTS DatabaseSchemaVersions (
                    Version INTEGER NOT NULL PRIMARY KEY CHECK (Version > 0)
                );
                SELECT COALESCE(MAX(Version), 0) FROM DatabaseSchemaVersions;
                """, connection, transaction))
            {
                var version = Convert.ToInt32(
                    await versionCommand.ExecuteScalarAsync(cancellationToken),
                    CultureInfo.InvariantCulture);
                if (version > 1)
                {
                    throw new InvalidOperationException(
                        $"Database schema version {version} is newer than the supported version 1.");
                }
            }

            await using (var schemaCommand = new NpgsqlCommand("""
                CREATE TABLE IF NOT EXISTS GuildChannelSettings (
                    GuildId TEXT NOT NULL PRIMARY KEY,
                    AuditChannelId TEXT NULL,
                    MemberLogChannelId TEXT NULL,
                    EventChannelId TEXT NULL,
                    EventBackgroundObjectKey TEXT NULL
                );

                CREATE TABLE IF NOT EXISTS GuildWarningSettings (
                    GuildId TEXT NOT NULL PRIMARY KEY,
                    TimeoutThreshold INTEGER NOT NULL CHECK (TimeoutThreshold > 0),
                    TimeoutMinutes INTEGER NOT NULL CHECK (TimeoutMinutes BETWEEN 1 AND 40320)
                );

                CREATE TABLE IF NOT EXISTS WarningEscalationApplications (
                    GuildId TEXT NOT NULL,
                    MemberId TEXT NOT NULL,
                    PRIMARY KEY (GuildId, MemberId)
                );

                CREATE TABLE IF NOT EXISTS MemberWarnings (
                    WarningId BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    GuildId TEXT NOT NULL,
                    MemberId TEXT NOT NULL,
                    ModeratorId TEXT NOT NULL,
                    Reason TEXT NOT NULL CHECK (
                        char_length(Reason) BETWEEN 1 AND 400 AND Reason ~ '[^[:space:]]'
                    ),
                    CreatedAtUnixSeconds BIGINT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS IX_MemberWarnings_Guild_Member
                    ON MemberWarnings (GuildId, MemberId, WarningId DESC);

                INSERT INTO DatabaseSchemaVersions (Version) VALUES (1)
                    ON CONFLICT (Version) DO NOTHING;
                """, connection, transaction))
            {
                await schemaCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            _initialized = true;
            _logger.LogInformation("PostgreSQL guild channel configuration store initialized at schema version 1.");
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    public Task<ulong?> GetAuditChannelIdAsync(
        ulong guildId,
        CancellationToken cancellationToken = default) =>
        GetChannelIdAsync(guildId, Setting.AuditChannelId, cancellationToken);

    public Task SetAuditChannelIdAsync(
        ulong guildId,
        ulong channelId,
        CancellationToken cancellationToken = default) =>
        SetSettingAsync(guildId, Setting.AuditChannelId, ToDatabaseValue(channelId), cancellationToken);

    public Task RemoveAuditChannelIdAsync(
        ulong guildId,
        CancellationToken cancellationToken = default) =>
        RemoveSettingAsync(guildId, Setting.AuditChannelId, cancellationToken);

    public Task<ulong?> GetMemberLogChannelIdAsync(
        ulong guildId,
        CancellationToken cancellationToken = default) =>
        GetChannelIdAsync(guildId, Setting.MemberLogChannelId, cancellationToken);

    public Task SetMemberLogChannelIdAsync(
        ulong guildId,
        ulong channelId,
        CancellationToken cancellationToken = default) =>
        SetSettingAsync(guildId, Setting.MemberLogChannelId, ToDatabaseValue(channelId), cancellationToken);

    public Task RemoveMemberLogChannelIdAsync(
        ulong guildId,
        CancellationToken cancellationToken = default) =>
        RemoveSettingAsync(guildId, Setting.MemberLogChannelId, cancellationToken);

    public Task<ulong?> GetEventChannelIdAsync(
        ulong guildId,
        CancellationToken cancellationToken = default) =>
        GetChannelIdAsync(guildId, Setting.EventChannelId, cancellationToken);

    public Task SetEventChannelIdAsync(
        ulong guildId,
        ulong channelId,
        CancellationToken cancellationToken = default) =>
        SetSettingAsync(guildId, Setting.EventChannelId, ToDatabaseValue(channelId), cancellationToken);

    public Task RemoveEventChannelIdAsync(
        ulong guildId,
        CancellationToken cancellationToken = default) =>
        RemoveSettingAsync(guildId, Setting.EventChannelId, cancellationToken);

    public Task<string?> GetEventBackgroundObjectKeyAsync(
        ulong guildId,
        CancellationToken cancellationToken = default) =>
        GetSettingAsync(guildId, Setting.EventBackgroundObjectKey, cancellationToken);

    public Task SetEventBackgroundObjectKeyAsync(
        ulong guildId,
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
        return SetSettingAsync(guildId, Setting.EventBackgroundObjectKey, objectKey, cancellationToken);
    }

    public Task RemoveEventBackgroundObjectKeyAsync(
        ulong guildId,
        CancellationToken cancellationToken = default) =>
        RemoveSettingAsync(guildId, Setting.EventBackgroundObjectKey, cancellationToken);

    public async Task<WarningIssueResult> AddWarningAsync(
        ulong guildId,
        ulong memberId,
        ulong moderatorId,
        string reason,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(reason.Length, 400);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockGuildAsync(connection, transaction, guildId, cancellationToken);

        long warningId;
        await using (var command = CreateGuildCommand(connection, transaction, guildId, """
            INSERT INTO MemberWarnings (GuildId, MemberId, ModeratorId, Reason, CreatedAtUnixSeconds)
            VALUES (@guildId, @memberId, @moderatorId, @reason, @createdAtUnixSeconds)
            RETURNING WarningId;
            """))
        {
            command.Parameters.AddWithValue("memberId", ToDatabaseValue(memberId));
            command.Parameters.AddWithValue("moderatorId", ToDatabaseValue(moderatorId));
            command.Parameters.AddWithValue("reason", reason);
            command.Parameters.AddWithValue("createdAtUnixSeconds", createdAt.ToUnixTimeSeconds());
            warningId = Convert.ToInt64(
                await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }

        var warningCount = await CountWarningsAsync(connection, transaction, guildId, memberId, cancellationToken);
        var escalation = await ReadWarningEscalationAsync(connection, transaction, guildId, cancellationToken);
        var shouldApplyEscalation = false;
        if (escalation is not null && warningCount >= escalation.Threshold)
        {
            await using var command = CreateGuildCommand(connection, transaction, guildId, """
                INSERT INTO WarningEscalationApplications (GuildId, MemberId)
                VALUES (@guildId, @memberId)
                ON CONFLICT (GuildId, MemberId) DO NOTHING;
                """);
            command.Parameters.AddWithValue("memberId", ToDatabaseValue(memberId));
            shouldApplyEscalation = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        }

        await transaction.CommitAsync(cancellationToken);
        return new WarningIssueResult(
            new MemberWarning(warningId, guildId, memberId, moderatorId, reason, createdAt),
            warningCount,
            escalation,
            shouldApplyEscalation);
    }

    public async Task<WarningHistory> GetWarningsAsync(
        ulong guildId,
        ulong memberId,
        int maximumCount = 8,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCount);
        maximumCount = Math.Min(maximumCount, 100);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken);
        var totalCount = await CountWarningsAsync(connection, transaction, guildId, memberId, cancellationToken);
        var warnings = new List<MemberWarning>(Math.Min(totalCount, maximumCount));
        await using (var command = CreateGuildCommand(connection, transaction, guildId, """
            SELECT WarningId, ModeratorId, Reason, CreatedAtUnixSeconds
            FROM MemberWarnings
            WHERE GuildId = @guildId AND MemberId = @memberId
            ORDER BY WarningId DESC
            LIMIT @maximumCount;
            """))
        {
            command.Parameters.AddWithValue("memberId", ToDatabaseValue(memberId));
            command.Parameters.AddWithValue("maximumCount", maximumCount);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                warnings.Add(new MemberWarning(
                    reader.GetInt64(0),
                    guildId,
                    memberId,
                    ParseSnowflake(reader.GetString(1), "ModeratorId"),
                    reader.GetString(2),
                    DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3))));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new WarningHistory(totalCount, warnings);
    }

    public async Task<WarningEscalation?> GetWarningEscalationAsync(
        ulong guildId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await ReadWarningEscalationAsync(connection, null, guildId, cancellationToken);
    }

    public Task SetWarningEscalationAsync(
        ulong guildId,
        WarningEscalation escalation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(escalation);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(escalation.Threshold);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(escalation.TimeoutMinutes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(escalation.TimeoutMinutes, 40320);

        return ExecuteGuildWriteAsync(guildId, async (connection, transaction) =>
        {
            await using var command = CreateGuildCommand(connection, transaction, guildId, """
                INSERT INTO GuildWarningSettings (GuildId, TimeoutThreshold, TimeoutMinutes)
                VALUES (@guildId, @threshold, @timeoutMinutes)
                ON CONFLICT (GuildId) DO UPDATE
                SET TimeoutThreshold = excluded.TimeoutThreshold,
                    TimeoutMinutes = excluded.TimeoutMinutes;
                DELETE FROM WarningEscalationApplications WHERE GuildId = @guildId;
                """);
            command.Parameters.AddWithValue("threshold", escalation.Threshold);
            command.Parameters.AddWithValue("timeoutMinutes", escalation.TimeoutMinutes);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);
    }

    public Task RemoveWarningEscalationAsync(
        ulong guildId,
        CancellationToken cancellationToken = default) =>
        ExecuteGuildWriteAsync(guildId, async (connection, transaction) =>
        {
            await using var command = CreateGuildCommand(connection, transaction, guildId, """
                DELETE FROM GuildWarningSettings WHERE GuildId = @guildId;
                DELETE FROM WarningEscalationApplications WHERE GuildId = @guildId;
                """);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    public Task RemoveWarningEscalationApplicationAsync(
        ulong guildId,
        ulong memberId,
        CancellationToken cancellationToken = default) =>
        ExecuteGuildWriteAsync(guildId, async (connection, transaction) =>
        {
            await using var command = CreateGuildCommand(connection, transaction, guildId, """
                DELETE FROM WarningEscalationApplications
                WHERE GuildId = @guildId AND MemberId = @memberId;
                """);
            command.Parameters.AddWithValue("memberId", ToDatabaseValue(memberId));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    public void Dispose() => _initializationGate.Dispose();

    private async Task<ulong?> GetChannelIdAsync(
        ulong guildId,
        Setting setting,
        CancellationToken cancellationToken)
    {
        var value = await GetSettingAsync(guildId, setting, cancellationToken);
        return value is null ? null : ParseSnowflake(value, GetColumnName(setting));
    }

    private async Task<string?> GetSettingAsync(
        ulong guildId,
        Setting setting,
        CancellationToken cancellationToken)
    {
        var column = GetColumnName(setting);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = CreateGuildCommand(connection, null, guildId, $"""
            SELECT {column} FROM GuildChannelSettings WHERE GuildId = @guildId;
            """);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value switch
        {
            null or DBNull => null,
            string text when !string.IsNullOrWhiteSpace(text) => text,
            _ => throw new InvalidOperationException($"The {column} value for guild {guildId} is not valid.")
        };
    }

    private Task SetSettingAsync(
        ulong guildId,
        Setting setting,
        string value,
        CancellationToken cancellationToken)
    {
        var column = GetColumnName(setting);
        return ExecuteGuildWriteAsync(guildId, async (connection, transaction) =>
        {
            await using var command = CreateGuildCommand(connection, transaction, guildId, $"""
                INSERT INTO GuildChannelSettings (GuildId, {column})
                VALUES (@guildId, @value)
                ON CONFLICT (GuildId) DO UPDATE SET {column} = excluded.{column};
                """);
            command.Parameters.AddWithValue("value", value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);
    }

    private Task RemoveSettingAsync(
        ulong guildId,
        Setting setting,
        CancellationToken cancellationToken)
    {
        var column = GetColumnName(setting);
        return ExecuteGuildWriteAsync(guildId, async (connection, transaction) =>
        {
            await using var command = CreateGuildCommand(connection, transaction, guildId, $"""
                UPDATE GuildChannelSettings SET {column} = NULL WHERE GuildId = @guildId;
                DELETE FROM GuildChannelSettings
                WHERE GuildId = @guildId
                    AND AuditChannelId IS NULL
                    AND MemberLogChannelId IS NULL
                    AND EventChannelId IS NULL
                    AND EventBackgroundObjectKey IS NULL;
                """);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        return await _dataSource.OpenConnectionAsync(cancellationToken);
    }

    private async Task ExecuteGuildWriteAsync(
        ulong guildId,
        Func<NpgsqlConnection, NpgsqlTransaction, Task> writeAsync,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockGuildAsync(connection, transaction, guildId, cancellationToken);
        await writeAsync(connection, transaction);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task LockGuildAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ulong guildId,
        CancellationToken cancellationToken)
    {
        // Preserve all 64 snowflake bits in the two-integer lock namespace, which
        // PostgreSQL keeps separate from the bigint schema-initialization lock.
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(@guildHigh, @guildLow);", connection, transaction);
        command.Parameters.AddWithValue("guildHigh", unchecked((int)(guildId >> 32)));
        command.Parameters.AddWithValue("guildLow", unchecked((int)guildId));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> CountWarningsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ulong guildId,
        ulong memberId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateGuildCommand(connection, transaction, guildId, """
            SELECT COUNT(*) FROM MemberWarnings
            WHERE GuildId = @guildId AND MemberId = @memberId;
            """);
        command.Parameters.AddWithValue("memberId", ToDatabaseValue(memberId));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task<WarningEscalation?> ReadWarningEscalationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        ulong guildId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateGuildCommand(connection, transaction, guildId, """
            SELECT TimeoutThreshold, TimeoutMinutes FROM GuildWarningSettings WHERE GuildId = @guildId;
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new WarningEscalation(reader.GetInt32(0), reader.GetInt32(1))
            : null;
    }

    private static NpgsqlCommand CreateGuildCommand(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        ulong guildId,
        string sql)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("guildId", ToDatabaseValue(guildId));
        return command;
    }

    private static string GetColumnName(Setting setting) => setting switch
    {
        Setting.AuditChannelId => "AuditChannelId",
        Setting.MemberLogChannelId => "MemberLogChannelId",
        Setting.EventChannelId => "EventChannelId",
        Setting.EventBackgroundObjectKey => "EventBackgroundObjectKey",
        _ => throw new ArgumentOutOfRangeException(nameof(setting))
    };

    private static string ToDatabaseValue(ulong value) => value.ToString(CultureInfo.InvariantCulture);

    private static ulong ParseSnowflake(string value, string column)
    {
        if (ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var snowflake))
        {
            return snowflake;
        }

        throw new InvalidOperationException($"The {column} value is not a valid Discord snowflake.");
    }

    private enum Setting
    {
        AuditChannelId,
        MemberLogChannelId,
        EventChannelId,
        EventBackgroundObjectKey
    }
}
