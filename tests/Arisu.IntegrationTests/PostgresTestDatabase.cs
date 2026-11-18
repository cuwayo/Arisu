using Arisu.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Arisu.IntegrationTests;

internal sealed class PostgresTestDatabase : IAsyncDisposable
{
    private readonly string _adminConnectionString;
    private readonly string _schemaName;
    private readonly List<PostgresGuildChannelConfigurationStore> _stores = [];

    private PostgresTestDatabase(string adminConnectionString, string schemaName, NpgsqlDataSource dataSource)
    {
        _adminConnectionString = adminConnectionString;
        _schemaName = schemaName;
        DataSource = dataSource;
    }

    public NpgsqlDataSource DataSource { get; }

    public static async Task<PostgresTestDatabase> CreateAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ARISU_TEST_DATABASE_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "PostgreSQL integration tests require ARISU_TEST_DATABASE_CONNECTION_STRING. " +
                "Set it to a fresh, isolated, disposable PostgreSQL database. " +
                "Tests create and DROP their own schemas with CASCADE. " +
                "See tests/Arisu.IntegrationTests/README.md.");
        }

        var schemaName = $"arisu_test_{Guid.NewGuid():N}";
        var adminBuilder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            SearchPath = "pg_catalog"
        };
        var testBuilder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            // Do not fall back to public: every store object must belong to this test.
            SearchPath = schemaName
        };
        var dataSource = NpgsqlDataSource.Create(testBuilder.ConnectionString);
        try
        {
            await using var connection = new NpgsqlConnection(adminBuilder.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            // The identifier contains only a fixed prefix and a generated hexadecimal GUID.
            command.CommandText = $"CREATE SCHEMA \"{schemaName}\";";
            await command.ExecuteNonQueryAsync();
            return new PostgresTestDatabase(adminBuilder.ConnectionString, schemaName, dataSource);
        }
        catch
        {
            await dataSource.DisposeAsync();
            throw;
        }
    }

    public PostgresGuildChannelConfigurationStore CreateStore()
    {
        var store = new PostgresGuildChannelConfigurationStore(
            DataSource,
            NullLogger<PostgresGuildChannelConfigurationStore>.Instance);
        _stores.Add(store);
        return store;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var store in _stores)
        {
            store.Dispose();
        }

        // Close the schema-specific pool before dropping its objects.
        await DataSource.DisposeAsync();
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP SCHEMA \"{_schemaName}\" CASCADE;";
        await command.ExecuteNonQueryAsync();
    }
}
