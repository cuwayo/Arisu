using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Arisu.Services;
using Discord;
using Discord.Net.Rest;
using Discord.Rest;
using Discord.WebSocket;
using Npgsql;
using Xunit;

namespace Arisu.IntegrationTests;

public sealed class ChannelLockRecoveryRegressionTests
{
    [Fact]
    public async Task AdmissionIsSharedAcrossInstancesAndReleasedAfterConnectionFailure()
    {
        var dataSource = NpgsqlDataSource.Create("Host=localhost;Database=unused;Username=unused;Maximum Pool Size=2");
        await dataSource.DisposeAsync();
        using var discord = new OfflineDiscord();
        var first = new ChannelLockService(dataSource, discord.Client);
        var second = new ChannelLockService(dataSource, discord.Client);
        var field = typeof(ChannelLockService).GetField("_admissionGate", InstanceMembers)!;
        var gate = (SemaphoreSlim)field.GetValue(first)!;
        Assert.Same(gate, field.GetValue(second));
        await gate.WaitAsync();
        Task<ChannelLockResult>? operation = null;
        try
        {
            operation = second.LockAsync(discord.Channel, "test");
            // A disposed data source fails immediately if admission occurs after opening it.
            Assert.False(operation.IsCompleted);
        }
        finally
        {
            gate.Release();
        }

        await Assert.ThrowsAsync<ObjectDisposedException>(() => operation!.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, gate.CurrentCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => first.UnlockAsync(discord.Channel, "test"));
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task DifferentDataSourcesDoNotShareAdmission()
    {
        await using var firstSource = NpgsqlDataSource.Create("Host=localhost;Database=unused;Username=unused");
        await using var secondSource = NpgsqlDataSource.Create("Host=localhost;Database=unused;Username=unused");
        using var discord = new OfflineDiscord();
        var field = typeof(ChannelLockService).GetField("_admissionGate", InstanceMembers)!;

        Assert.NotSame(
            field.GetValue(new ChannelLockService(firstSource, discord.Client)),
            field.GetValue(new ChannelLockService(secondSource, discord.Client)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PoolOfTwoCompletesConcurrentRequestsAcrossServiceInstances(bool sameChannel)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var dataSource = CreateSmallPool(database);
        using var discord = new OfflineDiscord();
        var first = new ChannelLockService(dataSource, discord.Client);
        var second = new ChannelLockService(dataSource, discord.Client);
        // Hold the first channel's advisory lock outside the tested two-connection pool.
        await using var blocker = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(200);", blocker, transaction))
        {
            await command.ExecuteNonQueryAsync();
        }

        var firstOperation = first.LockAsync(discord.Channel, "first");
        Task<ChannelLockResult>? secondOperation = null;
        try
        {
            await WaitForAdvisoryLockWaiterAsync(blocker, transaction, dataSource);
            secondOperation = second.LockAsync(sameChannel ? discord.Channel : discord.OtherChannel, "second");
            // Give the second request time to reach connection acquisition if it was admitted.
            await Task.Delay(150);
        }
        finally
        {
            await transaction.RollbackAsync();
        }

        var results = await Task.WhenAll(firstOperation, secondOperation!).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ChannelLockResult.Locked, results[0]);
        Assert.Equal(sameChannel ? ChannelLockResult.AlreadyLocked : ChannelLockResult.Locked, results[1]);
        Assert.Equal(sameChannel ? 1 : 2, discord.PermissionWrites);
    }

    [Theory]
    [InlineData(PermValue.Allow, true)]
    [InlineData(PermValue.Inherit, true)]
    [InlineData(PermValue.Inherit, false)]
    public async Task FailedDenyCanRetryWithoutReplacingRecoverySnapshot(PermValue original, bool hadOverwrite)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var dataSource = CreateSmallPool(database);
        using var discord = new OfflineDiscord
        {
            Overwrite = hadOverwrite ? new OverwritePermissions(sendMessages: original) : null,
            FailNextWrite = true
        };
        var service = new ChannelLockService(dataSource, discord.Client);

        await Assert.ThrowsAsync<IOException>(() => service.LockAsync(discord.Channel, "first"));
        Assert.Equal(new ChannelLockSnapshot(original, hadOverwrite), await ReadSnapshotAsync(dataSource));
        discord.Overwrite = (discord.Overwrite ?? default).Modify(attachFiles: PermValue.Allow);

        Assert.Equal(ChannelLockResult.Locked, await service.LockAsync(discord.Channel, "retry"));
        Assert.Equal(2, discord.PermissionWrites);
        Assert.Equal(PermValue.Deny, discord.Overwrite!.Value.SendMessages);
        Assert.Equal(PermValue.Allow, discord.Overwrite.Value.AttachFiles);
        Assert.Equal(new ChannelLockSnapshot(original, hadOverwrite), await ReadSnapshotAsync(dataSource));
        Assert.Equal(ChannelLockResult.AlreadyLocked, await service.LockAsync(discord.Channel, "again"));
        Assert.Equal(2, discord.PermissionWrites);

        Assert.Equal(ChannelLockResult.Unlocked, await service.UnlockAsync(discord.Channel, "restore"));
        Assert.Equal(original, discord.Overwrite!.Value.SendMessages);
        Assert.Equal(PermValue.Allow, discord.Overwrite.Value.AttachFiles);
        Assert.Null(await ReadSnapshotAsync(dataSource));
    }

    [Theory]
    [InlineData(PermValue.Allow, PermValue.Inherit, false)]
    [InlineData(PermValue.Inherit, PermValue.Allow, false)]
    [InlineData(PermValue.Allow, PermValue.Inherit, true)]
    public async Task FailedDenyRetryRefusesUnexpectedSendPermissionChange(
        PermValue original, PermValue changed, bool changeOnSecondFetch)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var dataSource = CreateSmallPool(database);
        using var discord = new OfflineDiscord
        {
            Overwrite = new OverwritePermissions(sendMessages: original),
            FailNextWrite = true
        };
        var service = new ChannelLockService(dataSource, discord.Client);
        await Assert.ThrowsAsync<IOException>(() => service.LockAsync(discord.Channel, "first"));
        if (changeOnSecondFetch)
        {
            var changeAt = discord.ChannelReads + 2;
            discord.BeforeChannelRead = () =>
            {
                if (discord.ChannelReads == changeAt)
                {
                    discord.Overwrite = new OverwritePermissions(sendMessages: changed);
                }
            };
        }
        else
        {
            discord.Overwrite = new OverwritePermissions(sendMessages: changed);
        }

        Assert.Equal(ChannelLockResult.PermissionsChanged, await service.LockAsync(discord.Channel, "retry"));
        Assert.Equal(1, discord.PermissionWrites);
        Assert.Equal(changed, discord.Overwrite!.Value.SendMessages);
        Assert.Equal(new ChannelLockSnapshot(original, true), await ReadSnapshotAsync(dataSource));
    }

    private static NpgsqlDataSource CreateSmallPool(PostgresTestDatabase database) =>
        NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(database.DataSource.ConnectionString)
        {
            Pooling = true,
            MaxPoolSize = 2,
            Timeout = 3,
            CommandTimeout = 3,
            ApplicationName = $"arisu_lock_regression_{Guid.NewGuid():N}"
        }.ConnectionString);

    private static async Task WaitForAdvisoryLockWaiterAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, NpgsqlDataSource dataSource)
    {
        var applicationName = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString).ApplicationName;
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (
                SELECT 1 FROM pg_stat_activity
                WHERE application_name = @name AND wait_event = 'advisory'
            );
            """, connection, transaction);
        command.Parameters.AddWithValue("name", applicationName!);
        // Avoid a cached activity snapshot in the blocker's open transaction.
        await using var clear = new NpgsqlCommand("SELECT pg_stat_clear_snapshot();", connection, transaction);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            await clear.ExecuteNonQueryAsync(timeout.Token);
            if ((bool)(await command.ExecuteScalarAsync(timeout.Token))!)
            {
                return;
            }

            await Task.Delay(20, timeout.Token);
        }
    }

    private static async Task<ChannelLockSnapshot?> ReadSnapshotAsync(NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT OriginalSendMessages, HadOverwrite FROM ChannelModerationLocks
            WHERE GuildId = '100' AND ChannelId = '200';
            """);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? new ChannelLockSnapshot((PermValue)reader.GetInt32(0), reader.GetBoolean(1))
            : null;
    }

    private sealed class OfflineDiscord : IDisposable
    {
        public OfflineDiscord()
        {
            var transport = Fake<IRestClient>((method, arguments) => method.Name switch
            {
                "SetHeader" or "SetCancelToken" or "Dispose" => null,
                "SendAsync" => SendAsync(arguments),
                _ => throw Unexpected(method)
            });
            Client = new DiscordSocketClient(new DiscordSocketConfig
            {
                RestClientProvider = _ => transport,
                DefaultRetryMode = RetryMode.AlwaysFail
            });
            // Mark only the local API client logged in; no gateway, login, or HTTP is used.
            var api = typeof(BaseDiscordClient).GetProperty("ApiClient", InstanceMembers)!.GetValue(Client)!;
            typeof(BaseDiscordClient).Assembly.GetType("Discord.API.DiscordRestApiClient")!
                .GetProperty("LoginState", InstanceMembers)!.SetValue(api, LoginState.LoggedIn);
            var everyone = Fake<IRole>((method, _) => method.Name switch
            {
                "get_Id" => 100UL,
                _ => throw Unexpected(method)
            });
            var guild = Fake<IGuild>((method, _) => method.Name switch
            {
                "get_EveryoneRole" => everyone,
                _ => throw Unexpected(method)
            });
            Channel = CreateChannel(200, guild);
            OtherChannel = CreateChannel(201, guild);
        }

        public DiscordSocketClient Client { get; }
        public ITextChannel Channel { get; }
        public ITextChannel OtherChannel { get; }
        public OverwritePermissions? Overwrite { get; set; }
        public bool FailNextWrite { get; set; }
        public int PermissionWrites { get; private set; }
        public int ChannelReads { get; private set; }
        public Action? BeforeChannelRead { get; set; }

        private Task<RestResponse> SendAsync(object?[] arguments)
        {
            var method = (string)arguments[0]!;
            var path = (string)arguments[1]!;
            if (method == "GET")
            {
                ChannelReads++;
                BeforeChannelRead?.Invoke();
                var channelId = path.EndsWith("201", StringComparison.Ordinal) ? "201" : "200";
                var overwrites = Overwrite is { } permissions
                    ? new[] { new { id = "100", type = 0, allow = permissions.AllowValue.ToString(), deny = permissions.DenyValue.ToString() } }
                    : [];
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    id = channelId,
                    guild_id = "100",
                    type = 0,
                    name = "test",
                    position = 0,
                    permission_overwrites = overwrites
                })));
            }

            if (method == "PUT")
            {
                PermissionWrites++;
                if (FailNextWrite)
                {
                    FailNextWrite = false;
                    return Task.FromException<RestResponse>(new IOException("Simulated Discord deny failure."));
                }

                var payload = arguments.OfType<string>().First(value => value.StartsWith('{'));
                using var json = JsonDocument.Parse(payload);
                Overwrite = new OverwritePermissions(
                    ulong.Parse(json.RootElement.GetProperty("allow").ToString()),
                    ulong.Parse(json.RootElement.GetProperty("deny").ToString()));
                return Task.FromResult(JsonResponse(HttpStatusCode.NoContent, ""));
            }

            if (method == "DELETE")
            {
                PermissionWrites++;
                Overwrite = null;
                return Task.FromResult(JsonResponse(HttpStatusCode.NoContent, ""));
            }

            throw new InvalidOperationException($"Unexpected offline request: {method} {path}");
        }

        private static ITextChannel CreateChannel(ulong id, IGuild guild) =>
            Fake<ITextChannel>((method, _) => method.Name switch
            {
                "get_Id" => id,
                "get_GuildId" => 100UL,
                "get_Guild" => guild,
                _ => throw Unexpected(method)
            });

        public void Dispose() => Client.Dispose();
    }

    private static RestResponse JsonResponse(HttpStatusCode status, string json) =>
        new(status, [], new MemoryStream(Encoding.UTF8.GetBytes(json)));

    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static T Fake<T>(Func<MethodInfo, object?[], object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, LockInterfaceProxy>();
        ((LockInterfaceProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    private static Exception Unexpected(MethodInfo method) =>
        new InvalidOperationException($"Unexpected offline fake call: {method.DeclaringType?.Name}.{method.Name}");

    public class LockInterfaceProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Handler { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            Handler(targetMethod!, args ?? []);
    }
}
