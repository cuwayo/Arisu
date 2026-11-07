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
}

public sealed class SqliteGuildChannelConfigurationStore : IGuildChannelConfigurationStore, IDisposable
{
    private const string AuditChannelColumn = "AuditChannelId";
    private const string MemberLogChannelColumn = "MemberLogChannelId";
    private const string EventChannelColumn = "EventChannelId";
    private const string EventBackgroundObjectKeyColumn = "EventBackgroundObjectKey";
    private const string LegacyEventBackgroundImageColumn = "EventBackgroundImage";

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

    private static string ToDatabaseValue(ulong value) =>
        value.ToString(CultureInfo.InvariantCulture);
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
