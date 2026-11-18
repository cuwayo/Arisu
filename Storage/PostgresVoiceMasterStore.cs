using System.Globalization;
using Npgsql;

namespace Arisu.Storage;

public sealed class PostgresVoiceMasterStore : IVoiceMasterStore, IDisposable
{
    private const long SchemaInitializationLock = 0x4172697375564D73;
    private readonly NpgsqlDataSource _dataSource;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private volatile bool _initialized;

    public PostgresVoiceMasterStore(NpgsqlDataSource dataSource)
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
                CREATE TABLE IF NOT EXISTS VoiceMasterConfigurations (
                    GuildId TEXT PRIMARY KEY,
                    LobbyChannelId TEXT NOT NULL,
                    CategoryId TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS VoiceMasterRooms (
                    GuildId TEXT NOT NULL,
                    ChannelId TEXT NOT NULL,
                    OwnerId TEXT NOT NULL,
                    PRIMARY KEY (GuildId, ChannelId)
                );
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

    public async Task<VoiceMasterConfiguration?> GetConfigurationAsync(
        ulong guildId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT LobbyChannelId, CategoryId FROM VoiceMasterConfigurations
            WHERE GuildId = @guildId;
            """, connection);
        command.Parameters.AddWithValue("guildId", ToDatabaseValue(guildId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new VoiceMasterConfiguration(guildId,
                ParseSnowflake(reader.GetString(0)), ParseSnowflake(reader.GetString(1)))
            : null;
    }

    public async Task SetConfigurationAsync(
        VoiceMasterConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO VoiceMasterConfigurations (GuildId, LobbyChannelId, CategoryId)
            VALUES (@guildId, @lobbyChannelId, @categoryId)
            ON CONFLICT (GuildId) DO UPDATE
            SET LobbyChannelId = EXCLUDED.LobbyChannelId, CategoryId = EXCLUDED.CategoryId;
            """, connection);
        command.Parameters.AddWithValue("guildId", ToDatabaseValue(configuration.GuildId));
        command.Parameters.AddWithValue("lobbyChannelId", ToDatabaseValue(configuration.LobbyChannelId));
        command.Parameters.AddWithValue("categoryId", ToDatabaseValue(configuration.CategoryId));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RemoveConfigurationAsync(ulong guildId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            DELETE FROM VoiceMasterConfigurations WHERE GuildId = @guildId;
            """, connection);
        command.Parameters.AddWithValue("guildId", ToDatabaseValue(guildId));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<VoiceMasterRoom>> GetRoomsAsync(
        ulong guildId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT ChannelId, OwnerId FROM VoiceMasterRooms
            WHERE GuildId = @guildId
            ORDER BY ChannelId;
            """, connection);
        command.Parameters.AddWithValue("guildId", ToDatabaseValue(guildId));
        var rooms = new List<VoiceMasterRoom>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rooms.Add(new VoiceMasterRoom(guildId,
                ParseSnowflake(reader.GetString(0)), ParseSnowflake(reader.GetString(1))));
        }

        return rooms;
    }

    public async Task<VoiceMasterRoom?> GetRoomAsync(
        ulong guildId,
        ulong channelId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT OwnerId FROM VoiceMasterRooms
            WHERE GuildId = @guildId AND ChannelId = @channelId;
            """, connection);
        command.Parameters.AddWithValue("guildId", ToDatabaseValue(guildId));
        command.Parameters.AddWithValue("channelId", ToDatabaseValue(channelId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new VoiceMasterRoom(guildId, channelId, ParseSnowflake(reader.GetString(0)))
            : null;
    }

    public async Task SaveRoomAsync(VoiceMasterRoom room, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO VoiceMasterRooms (GuildId, ChannelId, OwnerId)
            VALUES (@guildId, @channelId, @ownerId)
            ON CONFLICT (GuildId, ChannelId) DO UPDATE SET OwnerId = EXCLUDED.OwnerId;
            """, connection);
        command.Parameters.AddWithValue("guildId", ToDatabaseValue(room.GuildId));
        command.Parameters.AddWithValue("channelId", ToDatabaseValue(room.ChannelId));
        command.Parameters.AddWithValue("ownerId", ToDatabaseValue(room.OwnerId));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RemoveRoomAsync(
        ulong guildId,
        ulong channelId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            DELETE FROM VoiceMasterRooms WHERE GuildId = @guildId AND ChannelId = @channelId;
            """, connection);
        command.Parameters.AddWithValue("guildId", ToDatabaseValue(guildId));
        command.Parameters.AddWithValue("channelId", ToDatabaseValue(channelId));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public void Dispose() => _initializationGate.Dispose();

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        return await _dataSource.OpenConnectionAsync(cancellationToken);
    }

    private static string ToDatabaseValue(ulong value) => value.ToString(CultureInfo.InvariantCulture);

    private static ulong ParseSnowflake(string value) => ulong.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
}
