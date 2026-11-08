using System.Globalization;
using Arisu.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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

    Task<IReadOnlyList<MemberWarning>> GetWarningsAsync(
        ulong guildId,
        ulong memberId,
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

public sealed record WarningIssueResult(
    MemberWarning Warning,
    int WarningCount,
    WarningEscalation? Escalation,
    bool ShouldApplyEscalation);

public sealed class SqliteGuildChannelConfigurationStore : IGuildChannelConfigurationStore, IDisposable
{
    private const string AuditChannelColumn = "AuditChannelId";
    private const string MemberLogChannelColumn = "MemberLogChannelId";
    private const string EventChannelColumn = "EventChannelId";
    private const string EventBackgroundObjectKeyColumn = "EventBackgroundObjectKey";
    private const string LegacyEventBackgroundImageColumn = "EventBackgroundImage";
    private const string GuildWarningSettingsTable = "GuildWarningSettings";
    private const string WarningEscalationApplicationsTable = "WarningEscalationApplications";
    private const string MemberWarningsTable = "MemberWarnings";

    private readonly string _connectionString;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly ILogger<SqliteGuildChannelConfigurationStore> _logger;
    private bool _initialized;

    public SqliteGuildChannelConfigurationStore(
        IHostEnvironment environment,
        IOptions<DatabaseOptions> options,
        ILogger<SqliteGuildChannelConfigurationStore> logger)
    {
        _logger = logger;

        var databasePath = Path.GetFullPath(options.Value.Path, environment.ContentRootPath);

        _connectionString = new SqliteConnectionStringBuilder
        {
            Cache = SqliteCacheMode.Shared,
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
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

            var databasePath = new SqliteConnectionStringBuilder(_connectionString).DataSource;
            var directoryPath = Path.GetDirectoryName(databasePath)
                ?? throw new InvalidOperationException("The SQLite database path must include a directory.");

            Directory.CreateDirectory(directoryPath);

            await using var connection = await OpenConnectionAsync(cancellationToken);
            await CreateSchemaAsync(connection, cancellationToken);

            _initialized = true;
            _logger.LogInformation("SQLite guild channel configuration store initialized.");
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    public Task<ulong?> GetAuditChannelIdAsync(
        ulong guildId,
        CancellationToken cancellationToken = default) =>
        GetChannelIdAsync(guildId, AuditChannelColumn, cancellationToken);

    public Task SetAuditChannelIdAsync(
        ulong guildId,
        ulong channelId,
        CancellationToken cancellationToken = default) =>
        SetChannelIdAsync(guildId, channelId, AuditChannelColumn, cancellationToken);

    public Task RemoveAuditChannelIdAsync(
        ulong guildId,
        CancellationToken cancellationToken = default) =>
        RemoveChannelIdAsync(guildId, AuditChannelColumn, cancellationToken);

    public Task<ulong?> GetMemberLogChannelIdAsync(
        ulong guildId,
        CancellationToken cancellationToken = default) =>
        GetChannelIdAsync(guildId, MemberLogChannelColumn, cancellationToken);

    public Task SetMemberLogChannelIdAsync(
        ulong guildId,
        ulong channelId,
        CancellationToken cancellationToken = default) =>
        SetChannelIdAsync(guildId, channelId, MemberLogChannelColumn, cancellationToken);

    public Task RemoveMemberLogChannelIdAsync(
        ulong guildId,
        CancellationToken cancellationToken = default) =>
        RemoveChannelIdAsync(guildId, MemberLogChannelColumn, cancellationToken);

    public Task<ulong?> GetEventChannelIdAsync(
        ulong guildId,
        CancellationToken cancellationToken = default) =>
        GetChannelIdAsync(guildId, EventChannelColumn, cancellationToken);

    public Task SetEventChannelIdAsync(
        ulong guildId,
        ulong channelId,
        CancellationToken cancellationToken = default) =>
        SetChannelIdAsync(guildId, channelId, EventChannelColumn, cancellationToken);

    public Task RemoveEventChannelIdAsync(
        ulong guildId,
        CancellationToken cancellationToken = default) =>
        RemoveChannelIdAsync(guildId, EventChannelColumn, cancellationToken);

    public async Task<string?> GetEventBackgroundObjectKeyAsync(
        ulong guildId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {EventBackgroundObjectKeyColumn}
            FROM GuildChannelSettings
            WHERE GuildId = $guildId;
            """;
        command.Parameters.AddWithValue("$guildId", ToDatabaseValue(guildId));

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result switch
        {
            null or DBNull => null,
            string objectKey when !string.IsNullOrWhiteSpace(objectKey) => objectKey,
            _ => throw new InvalidOperationException(
                $"The {EventBackgroundObjectKeyColumn} value for guild {guildId} is not valid.")
        };
    }

    public async Task SetEventBackgroundObjectKeyAsync(
        ulong guildId,
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

        await InitializeAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO GuildChannelSettings (GuildId, {EventBackgroundObjectKeyColumn})
            VALUES ($guildId, $objectKey)
            ON CONFLICT (GuildId) DO UPDATE
            SET {EventBackgroundObjectKeyColumn} = excluded.{EventBackgroundObjectKeyColumn};
            """;
        command.Parameters.AddWithValue("$guildId", ToDatabaseValue(guildId));
        command.Parameters.AddWithValue("$objectKey", objectKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task RemoveEventBackgroundObjectKeyAsync(
        ulong guildId,
        CancellationToken cancellationToken = default) =>
        RemoveSettingAsync(guildId, EventBackgroundObjectKeyColumn, cancellationToken);

    public async Task<WarningIssueResult> AddWarningAsync(
        ulong guildId,
        ulong memberId,
        ulong moderatorId,
        string reason,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        await InitializeAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        long warningId;
        await using (var insertCommand = connection.CreateCommand())
        {
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = $"""
                INSERT INTO {MemberWarningsTable}
                    (GuildId, MemberId, ModeratorId, Reason, CreatedAtUnixSeconds)
                VALUES
                    ($guildId, $memberId, $moderatorId, $reason, $createdAtUnixSeconds);
                SELECT last_insert_rowid();
                """;
            insertCommand.Parameters.AddWithValue("$guildId", ToDatabaseValue(guildId));
            insertCommand.Parameters.AddWithValue("$memberId", ToDatabaseValue(memberId));
            insertCommand.Parameters.AddWithValue("$moderatorId", ToDatabaseValue(moderatorId));
            insertCommand.Parameters.AddWithValue("$reason", reason);
            insertCommand.Parameters.AddWithValue("$createdAtUnixSeconds", createdAt.ToUnixTimeSeconds());
            warningId = Convert.ToInt64(
                await insertCommand.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);
        }

        var warningCount = 0;
        await using (var countCommand = connection.CreateCommand())
        {
            countCommand.Transaction = transaction;
            countCommand.CommandText = $"""
                SELECT COUNT(*)
                FROM {MemberWarningsTable}
                WHERE GuildId = $guildId
                    AND MemberId = $memberId;
                """;
            countCommand.Parameters.AddWithValue("$guildId", ToDatabaseValue(guildId));
            countCommand.Parameters.AddWithValue("$memberId", ToDatabaseValue(memberId));
            warningCount = Convert.ToInt32(
                await countCommand.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);
        }

        WarningEscalation? escalation = null;
        await using (var settingsCommand = connection.CreateCommand())
        {
            settingsCommand.Transaction = transaction;
            settingsCommand.CommandText = $"""
                SELECT TimeoutThreshold, TimeoutMinutes
                FROM {GuildWarningSettingsTable}
                WHERE GuildId = $guildId;
                """;
            settingsCommand.Parameters.AddWithValue("$guildId", ToDatabaseValue(guildId));

            await using var reader = await settingsCommand.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                escalation = new WarningEscalation(reader.GetInt32(0), reader.GetInt32(1));
            }
        }

        var shouldApplyEscalation = false;
        if (escalation is not null && warningCount >= escalation.Threshold)
        {
            await using var escalationCommand = connection.CreateCommand();
            escalationCommand.Transaction = transaction;
            escalationCommand.CommandText = $"""
                INSERT INTO {WarningEscalationApplicationsTable} (GuildId, MemberId)
                SELECT $guildId, $memberId
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM {WarningEscalationApplicationsTable}
                    WHERE GuildId = $guildId
                        AND MemberId = $memberId
                );
                """;
            escalationCommand.Parameters.AddWithValue("$guildId", ToDatabaseValue(guildId));
            escalationCommand.Parameters.AddWithValue("$memberId", ToDatabaseValue(memberId));
            shouldApplyEscalation =
                await escalationCommand.ExecuteNonQueryAsync(cancellationToken) == 1;
        }

        await transaction.CommitAsync(cancellationToken);

        return new WarningIssueResult(
            new MemberWarning(warningId, guildId, memberId, moderatorId, reason, createdAt),
            warningCount,
            escalation,
            shouldApplyEscalation);
    }

    public async Task<IReadOnlyList<MemberWarning>> GetWarningsAsync(
        ulong guildId,
        ulong memberId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT WarningId, ModeratorId, Reason, CreatedAtUnixSeconds
            FROM {MemberWarningsTable}
            WHERE GuildId = $guildId
                AND MemberId = $memberId
            ORDER BY WarningId DESC;
            """;
        command.Parameters.AddWithValue("$guildId", ToDatabaseValue(guildId));
        command.Parameters.AddWithValue("$memberId", ToDatabaseValue(memberId));

        var warnings = new List<MemberWarning>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            warnings.Add(
                new MemberWarning(
                    reader.GetInt64(0),
                    guildId,
                    memberId,
                    ParseSnowflake(reader.GetString(1), "ModeratorId"),
                    reader.GetString(2),
                    DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3))));
        }

        return warnings;
    }

    public async Task<WarningEscalation?> GetWarningEscalationAsync(
        ulong guildId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT TimeoutThreshold, TimeoutMinutes
            FROM {GuildWarningSettingsTable}
            WHERE GuildId = $guildId;
            """;
        command.Parameters.AddWithValue("$guildId", ToDatabaseValue(guildId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new WarningEscalation(reader.GetInt32(0), reader.GetInt32(1))
            : null;
    }

    public async Task SetWarningEscalationAsync(
        ulong guildId,
        WarningEscalation escalation,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(escalation.Threshold);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(escalation.TimeoutMinutes);

        await InitializeAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"""
                INSERT INTO {GuildWarningSettingsTable} (GuildId, TimeoutThreshold, TimeoutMinutes)
                VALUES ($guildId, $threshold, $timeoutMinutes)
                ON CONFLICT (GuildId) DO UPDATE
                SET TimeoutThreshold = excluded.TimeoutThreshold,
                    TimeoutMinutes = excluded.TimeoutMinutes;
                """;
            command.Parameters.AddWithValue("$guildId", ToDatabaseValue(guildId));
            command.Parameters.AddWithValue("$threshold", escalation.Threshold);
            command.Parameters.AddWithValue("$timeoutMinutes", escalation.TimeoutMinutes);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await ClearWarningEscalationApplicationsAsync(connection, transaction, guildId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RemoveWarningEscalationAsync(
        ulong guildId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"""
                DELETE FROM {GuildWarningSettingsTable}
                WHERE GuildId = $guildId;
                """;
            command.Parameters.AddWithValue("$guildId", ToDatabaseValue(guildId));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await ClearWarningEscalationApplicationsAsync(connection, transaction, guildId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RemoveWarningEscalationApplicationAsync(
        ulong guildId,
        ulong memberId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            DELETE FROM {WarningEscalationApplicationsTable}
            WHERE GuildId = $guildId
                AND MemberId = $memberId;
            """;
        command.Parameters.AddWithValue("$guildId", ToDatabaseValue(guildId));
        command.Parameters.AddWithValue("$memberId", ToDatabaseValue(memberId));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public void Dispose() => _initializationGate.Dispose();

    private async Task<ulong?> GetChannelIdAsync(
        ulong guildId,
        string channelColumn,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {channelColumn}
            FROM GuildChannelSettings
            WHERE GuildId = $guildId;
            """;
        command.Parameters.AddWithValue("$guildId", ToDatabaseValue(guildId));

        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is null || result is DBNull)
        {
            return null;
        }

        var value = Convert.ToString(result, CultureInfo.InvariantCulture);
        if (ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var channelId))
        {
            return channelId;
        }

        throw new InvalidOperationException(
            $"The {channelColumn} value for guild {guildId} is not a valid Discord snowflake.");
    }

    private async Task SetChannelIdAsync(
        ulong guildId,
        ulong channelId,
        string channelColumn,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await UpsertChannelIdAsync(connection, guildId, channelId, channelColumn, cancellationToken);
    }

    private Task RemoveChannelIdAsync(
        ulong guildId,
        string channelColumn,
        CancellationToken cancellationToken)
        => RemoveSettingAsync(guildId, channelColumn, cancellationToken);

    private async Task RemoveSettingAsync(
        ulong guildId,
        string settingColumn,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (var clearCommand = connection.CreateCommand())
        {
            clearCommand.Transaction = transaction;
            clearCommand.CommandText = $"""
                UPDATE GuildChannelSettings
                SET {settingColumn} = NULL
                WHERE GuildId = $guildId;
                """;
            clearCommand.Parameters.AddWithValue("$guildId", ToDatabaseValue(guildId));
            await clearCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = """
                DELETE FROM GuildChannelSettings
                WHERE GuildId = $guildId
                    AND AuditChannelId IS NULL
                    AND MemberLogChannelId IS NULL
                    AND EventChannelId IS NULL
                    AND EventBackgroundObjectKey IS NULL;
                """;
            deleteCommand.Parameters.AddWithValue("$guildId", ToDatabaseValue(guildId));
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task CreateSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS GuildChannelSettings (
                GuildId TEXT NOT NULL PRIMARY KEY,
                AuditChannelId TEXT NULL,
                MemberLogChannelId TEXT NULL,
                EventChannelId TEXT NULL,
                EventBackgroundObjectKey TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS GuildWarningSettings (
                GuildId TEXT NOT NULL PRIMARY KEY,
                TimeoutThreshold INTEGER NOT NULL,
                TimeoutMinutes INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS WarningEscalationApplications (
                GuildId TEXT NOT NULL,
                MemberId TEXT NOT NULL,
                PRIMARY KEY (GuildId, MemberId)
            );

            CREATE TABLE IF NOT EXISTS MemberWarnings (
                WarningId INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                GuildId TEXT NOT NULL,
                MemberId TEXT NOT NULL,
                ModeratorId TEXT NOT NULL,
                Reason TEXT NOT NULL,
                CreatedAtUnixSeconds INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_MemberWarnings_Guild_Member
            ON MemberWarnings (GuildId, MemberId, WarningId DESC);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureColumnAsync(
            connection,
            MemberLogChannelColumn,
            "TEXT NULL",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            EventBackgroundObjectKeyColumn,
            "TEXT NULL",
            cancellationToken);
        await ClearLegacyEventBackgroundImageAsync(connection, cancellationToken);
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string columnName,
        string columnType,
        CancellationToken cancellationToken)
    {
        if (await HasColumnAsync(connection, columnName, cancellationToken))
        {
            return;
        }

        await using var alterCommand = connection.CreateCommand();
        alterCommand.CommandText = $"""
            ALTER TABLE GuildChannelSettings
            ADD COLUMN {columnName} {columnType};
            """;
        await alterCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ClearLegacyEventBackgroundImageAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await HasColumnAsync(
                connection,
                LegacyEventBackgroundImageColumn,
                cancellationToken))
        {
            return;
        }

        await using var clearCommand = connection.CreateCommand();
        clearCommand.CommandText = $"""
            UPDATE GuildChannelSettings
            SET {LegacyEventBackgroundImageColumn} = NULL
            WHERE {LegacyEventBackgroundImageColumn} IS NOT NULL;
            """;
        await clearCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> HasColumnAsync(
        SqliteConnection connection,
        string columnName,
        CancellationToken cancellationToken)
    {
        await using var columnCommand = connection.CreateCommand();
        columnCommand.CommandText = "PRAGMA table_info(GuildChannelSettings);";

        await using var reader = await columnCommand.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(
                    reader.GetString(1),
                    columnName,
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task UpsertChannelIdAsync(
        SqliteConnection connection,
        ulong guildId,
        ulong channelId,
        string channelColumn,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO GuildChannelSettings (GuildId, {channelColumn})
            VALUES ($guildId, $channelId)
            ON CONFLICT (GuildId) DO UPDATE
            SET {channelColumn} = excluded.{channelColumn};
            """;
        command.Parameters.AddWithValue("$guildId", ToDatabaseValue(guildId));
        command.Parameters.AddWithValue("$channelId", ToDatabaseValue(channelId));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ClearWarningEscalationApplicationsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ulong guildId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            DELETE FROM {WarningEscalationApplicationsTable}
            WHERE GuildId = $guildId;
            """;
        command.Parameters.AddWithValue("$guildId", ToDatabaseValue(guildId));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string ToDatabaseValue(ulong value) =>
        value.ToString(CultureInfo.InvariantCulture);

    private static ulong ParseSnowflake(string value, string columnName)
    {
        if (ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var snowflake))
        {
            return snowflake;
        }

        throw new InvalidOperationException(
            $"The {columnName} value in {MemberWarningsTable} is not a valid Discord snowflake.");
    }
}

public sealed class DatabaseInitializationService : IHostedService
{
    private readonly SqliteGuildChannelConfigurationStore _store;

    public DatabaseInitializationService(SqliteGuildChannelConfigurationStore store)
    {
        _store = store;
    }

    public Task StartAsync(CancellationToken cancellationToken) =>
        _store.InitializeAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
