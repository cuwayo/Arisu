using System.Globalization;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Arisu.Storage;

public sealed class PostgresModerationHistoryStore : IModerationHistoryStore, IDisposable
{
    private const long SchemaInitializationLock = 0x4172697375486973;
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<PostgresModerationHistoryStore> _logger;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private volatile bool _initialized;

    public PostgresModerationHistoryStore(
        NpgsqlDataSource dataSource,
        ILogger<PostgresModerationHistoryStore> logger)
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
            await using (var command = new NpgsqlCommand(
                "SELECT pg_advisory_xact_lock(@schemaLock);", connection, transaction))
            {
                command.Parameters.AddWithValue("schemaLock", SchemaInitializationLock);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var command = new NpgsqlCommand("""
                CREATE TABLE IF NOT EXISTS ModerationCases (
                    CaseId BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    GuildId TEXT NOT NULL,
                    TargetId TEXT NOT NULL,
                    ModeratorId TEXT NOT NULL,
                    Action TEXT NOT NULL CHECK (char_length(Action) BETWEEN 1 AND 100),
                    Reason TEXT NOT NULL CHECK (char_length(Reason) BETWEEN 1 AND 400),
                    Details TEXT NULL,
                    CreatedAtUnixSeconds BIGINT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_ModerationCases_Guild_Target
                    ON ModerationCases (GuildId, TargetId, CaseId DESC);
                """, connection, transaction))
            {
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            _initialized = true;
            _logger.LogInformation("PostgreSQL moderation history store initialized.");
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    public async Task RecordCaseAsync(
        ulong guildId,
        ulong targetId,
        ulong moderatorId,
        string action,
        string reason,
        string? details,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(action.Length, 100);
        ValidateReason(reason);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO ModerationCases
                (GuildId, TargetId, ModeratorId, Action, Reason, Details, CreatedAtUnixSeconds)
            VALUES (@guildId, @targetId, @moderatorId, @action, @reason, @details, @createdAtUnixSeconds);
            """, connection);
        command.Parameters.AddWithValue("guildId", ToDatabaseValue(guildId));
        command.Parameters.AddWithValue("targetId", ToDatabaseValue(targetId));
        command.Parameters.AddWithValue("moderatorId", ToDatabaseValue(moderatorId));
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("reason", reason);
        command.Parameters.AddWithValue("details", NpgsqlDbType.Text, (object?)details ?? DBNull.Value);
        command.Parameters.AddWithValue("createdAtUnixSeconds", createdAt.ToUnixTimeSeconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ModerationCase>> GetCasesAsync(
        ulong guildId,
        ulong targetId,
        int limit = 8,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        limit = Math.Min(limit, 100);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT CaseId, ModeratorId, Action, Reason, Details, CreatedAtUnixSeconds
            FROM ModerationCases
            WHERE GuildId = @guildId AND TargetId = @targetId
            ORDER BY CaseId DESC
            LIMIT @limit;
            """, connection);
        command.Parameters.AddWithValue("guildId", ToDatabaseValue(guildId));
        command.Parameters.AddWithValue("targetId", ToDatabaseValue(targetId));
        command.Parameters.AddWithValue("limit", limit);
        var cases = new List<ModerationCase>(limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            cases.Add(new ModerationCase(
                reader.GetInt64(0), guildId, targetId, ParseSnowflake(reader.GetString(1)),
                reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(5))));
        }

        return cases;
    }

    public async Task<MemberWarning?> GetWarningAsync(
        ulong guildId,
        long warningId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(warningId);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT WarningId, GuildId, MemberId, ModeratorId, Reason, CreatedAtUnixSeconds
            FROM MemberWarnings WHERE GuildId = @guildId AND WarningId = @warningId;
            """, connection);
        command.Parameters.AddWithValue("guildId", ToDatabaseValue(guildId));
        command.Parameters.AddWithValue("warningId", warningId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadWarning(reader) : null;
    }

    public Task<MemberWarning?> RemoveWarningAsync(
        ulong guildId,
        long warningId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(warningId);
        return ExecuteWarningWriteAsync<MemberWarning?>(guildId, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("""
                DELETE FROM MemberWarnings WHERE GuildId = @guildId AND WarningId = @warningId
                RETURNING WarningId, GuildId, MemberId, ModeratorId, Reason, CreatedAtUnixSeconds;
                """, connection, transaction);
            command.Parameters.AddWithValue("guildId", ToDatabaseValue(guildId));
            command.Parameters.AddWithValue("warningId", warningId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadWarning(reader) : null;
        }, cancellationToken);
    }

    public Task<int> ClearWarningsAsync(
        ulong guildId,
        ulong memberId,
        CancellationToken cancellationToken = default) =>
        ExecuteWarningWriteAsync(guildId, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("""
                DELETE FROM MemberWarnings WHERE GuildId = @guildId AND MemberId = @memberId;
                """, connection, transaction);
            command.Parameters.AddWithValue("guildId", ToDatabaseValue(guildId));
            command.Parameters.AddWithValue("memberId", ToDatabaseValue(memberId));
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    public Task<WarningReasonChange?> UpdateWarningReasonAsync(
        ulong guildId,
        long warningId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(warningId);
        ValidateReason(reason);
        return ExecuteWarningWriteAsync<WarningReasonChange?>(guildId, async (connection, transaction) =>
        {
            MemberWarning warning;
            await using (var command = new NpgsqlCommand("""
                SELECT WarningId, GuildId, MemberId, ModeratorId, Reason, CreatedAtUnixSeconds
                FROM MemberWarnings WHERE GuildId = @guildId AND WarningId = @warningId
                FOR UPDATE;
                """, connection, transaction))
            {
                command.Parameters.AddWithValue("guildId", ToDatabaseValue(guildId));
                command.Parameters.AddWithValue("warningId", warningId);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                {
                    return null;
                }

                warning = ReadWarning(reader);
            }

            await using (var command = new NpgsqlCommand("""
                UPDATE MemberWarnings SET Reason = @reason
                WHERE GuildId = @guildId AND WarningId = @warningId;
                """, connection, transaction))
            {
                command.Parameters.AddWithValue("guildId", ToDatabaseValue(guildId));
                command.Parameters.AddWithValue("warningId", warningId);
                command.Parameters.AddWithValue("reason", reason);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            return new WarningReasonChange(warning with { Reason = reason }, warning.Reason);
        }, cancellationToken);
    }

    public void Dispose() => _initializationGate.Dispose();

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        return await _dataSource.OpenConnectionAsync(cancellationToken);
    }

    private async Task<T> ExecuteWarningWriteAsync<T>(
        ulong guildId,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> writeAsync,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // Match the guild store lock so additions, removals and policy changes serialize.
        await using (var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(@guildHigh, @guildLow);", connection, transaction))
        {
            command.Parameters.AddWithValue("guildHigh", unchecked((int)(guildId >> 32)));
            command.Parameters.AddWithValue("guildLow", unchecked((int)guildId));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var result = await writeAsync(connection, transaction);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static MemberWarning ReadWarning(NpgsqlDataReader reader) => new(
        reader.GetInt64(0), ParseSnowflake(reader.GetString(1)), ParseSnowflake(reader.GetString(2)),
        ParseSnowflake(reader.GetString(3)), reader.GetString(4),
        DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(5)));

    private static string ToDatabaseValue(ulong value) => value.ToString(CultureInfo.InvariantCulture);

    private static ulong ParseSnowflake(string value) => ulong.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);

    private static void ValidateReason(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(reason.Length, 400);
    }
}
