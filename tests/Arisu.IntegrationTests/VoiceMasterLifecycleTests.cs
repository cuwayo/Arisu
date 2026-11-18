using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Arisu.Services;
using Arisu.Storage;
using Discord;
using Discord.Net.Rest;
using Discord.Rest;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Arisu.IntegrationTests;

public sealed class VoiceMasterLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LosingAvailabilitySkipsQueuedWorkUntilGuildAvailableReturns(bool disconnected)
    {
        using var fixture = new LifecycleFixture();
        await fixture.InvokeAsync("OnGuildAvailableAsync", fixture.Guild);
        Assert.True(fixture.IsReady(fixture.Guild));

        if (disconnected)
        {
            await fixture.InvokeAsync("OnDisconnectedAsync", new IOException("Gateway disconnected"));
        }
        else
        {
            await fixture.InvokeAsync("OnGuildUnavailableAsync", fixture.Guild);
        }

        // A connected socket alone must not restore readiness before a fresh guild snapshot.
        Assert.Equal(ConnectionState.Connected, fixture.Client.ConnectionState);
        Assert.False(fixture.IsReady(fixture.Guild));
        await fixture.ProcessQueuedAsync();
        await fixture.ReconcileAsync(fixture.Guild);

        Assert.Empty(fixture.StoreCalls);
        Assert.Empty(fixture.Requests);
        Assert.Equal(fixture.Room, Assert.Single(fixture.Rooms));

        fixture.AllowReads = true;
        fixture.Rooms.Clear();
        await fixture.InvokeAsync("OnGuildAvailableAsync", fixture.Guild);
        await fixture.ReconcileAsync(fixture.Guild);

        Assert.True(fixture.IsReady(fixture.Guild));
        Assert.Equal(["GetRoomsAsync", "GetConfigurationAsync"], fixture.StoreCalls.ToArray());
        Assert.Empty(fixture.Requests);
    }

    [Theory]
    [InlineData(ConnectionState.Disconnected)]
    [InlineData(ConnectionState.Connecting)]
    [InlineData(ConnectionState.Disconnecting)]
    public async Task GuildAvailableCannotReconcileWhileSocketIsNotConnected(ConnectionState state)
    {
        using var fixture = new LifecycleFixture();
        await fixture.InvokeAsync("OnGuildAvailableAsync", fixture.Guild);
        fixture.SetConnectionState(state);

        await fixture.ProcessQueuedAsync();
        await fixture.ReconcileAsync(fixture.Guild);

        Assert.False(fixture.IsReady(fixture.Guild));
        Assert.Empty(fixture.StoreCalls);
        Assert.Empty(fixture.Requests);
        Assert.Equal(fixture.Room, Assert.Single(fixture.Rooms));
    }

    [Fact]
    public async Task NewGuildSnapshotDoesNotMakePreviousSnapshotReady()
    {
        using var fixture = new LifecycleFixture();
        var previous = fixture.Guild;
        await fixture.InvokeAsync("OnGuildAvailableAsync", previous);
        var replacement = fixture.CreateGuild(previous.Id);
        await fixture.InvokeAsync("OnGuildAvailableAsync", replacement);

        await fixture.ReconcileAsync(previous);

        Assert.False(fixture.IsReady(previous));
        Assert.Empty(fixture.StoreCalls);
        Assert.Empty(fixture.Requests);

        fixture.AllowReads = true;
        fixture.Rooms.Clear();
        await fixture.ProcessQueuedAsync();

        Assert.True(fixture.IsReady(replacement));
        Assert.Equal(["GetRoomsAsync", "GetConfigurationAsync"], fixture.StoreCalls.ToArray());
    }

    [Fact]
    public async Task PendingMoveRetainsUncachedRoomWithoutRestConfirmationDuringGracePeriod()
    {
        using var fixture = new LifecycleFixture { AllowReads = true };
        var movedAt = DateTimeOffset.UtcNow;
        fixture.PendingMoves[fixture.Room.ChannelId] = movedAt;
        await fixture.InvokeAsync("OnGuildAvailableAsync", fixture.Guild);
        Assert.Null(fixture.Guild.GetVoiceChannel(fixture.Room.ChannelId));

        await fixture.ProcessQueuedAsync();

        Assert.Equal(fixture.Room, Assert.Single(fixture.Rooms));
        Assert.Equal(movedAt, fixture.PendingMoves[fixture.Room.ChannelId]);
        Assert.Equal(["GetRoomsAsync", "GetConfigurationAsync"], fixture.StoreCalls.ToArray());
        Assert.Empty(fixture.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UncachedRoomSurvivesWhenRestConfirmsItStillExists(bool expiredMove)
    {
        using var fixture = new LifecycleFixture { AllowReads = true };
        if (expiredMove)
        {
            fixture.PendingMoves[fixture.Room.ChannelId] = DateTimeOffset.UtcNow.AddMinutes(-1);
        }
        fixture.Response = () => JsonResponse(HttpStatusCode.OK,
            "{\"id\":\"200\",\"guild_id\":\"100\",\"type\":2,\"name\":\"Room\",\"position\":0,\"permission_overwrites\":[],\"bitrate\":64000,\"user_limit\":0}");
        await fixture.InvokeAsync("OnGuildAvailableAsync", fixture.Guild);

        await fixture.ProcessQueuedAsync();

        AssertChannelLookup(fixture);
        Assert.Equal(fixture.Room, Assert.Single(fixture.Rooms));
        Assert.Equal(["GetRoomsAsync", "GetConfigurationAsync"], fixture.StoreCalls.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UncachedRoomIsRemovedOnlyAfterRestConfirmsUnknownChannel(bool expiredMove)
    {
        using var fixture = new LifecycleFixture { AllowReads = true, AllowRemoval = true };
        if (expiredMove)
        {
            fixture.PendingMoves[fixture.Room.ChannelId] = DateTimeOffset.UtcNow.AddMinutes(-1);
        }
        fixture.Response = () => JsonResponse(HttpStatusCode.NotFound,
            "{\"code\":10003,\"message\":\"Unknown Channel\"}");
        await fixture.InvokeAsync("OnGuildAvailableAsync", fixture.Guild);

        await fixture.ProcessQueuedAsync();

        AssertChannelLookup(fixture);
        Assert.Empty(fixture.Rooms);
        Assert.Equal(["GetRoomsAsync", "RemoveRoomAsync", "GetConfigurationAsync"], fixture.StoreCalls.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonadministratorCreatesRoomWithInheritedCategoryPermissions(bool categoryOnlyPermissions)
    {
        using var fixture = new LifecycleFixture { AllowReads = true, AllowSave = true };
        fixture.Rooms.Clear();
        fixture.PopulateCreationGuild(categoryOnlyPermissions);
        var member = fixture.Guild.GetUser(300);
        var lobby = fixture.Guild.GetVoiceChannel(201);
        var category = fixture.Guild.GetCategoryChannel(202);
        Assert.False(fixture.Guild.CurrentUser.GuildPermissions.Administrator);
        Assert.Equal(!categoryOnlyPermissions, fixture.Guild.CurrentUser.GuildPermissions.ManageRoles);
        Assert.True(fixture.Guild.CurrentUser.GetPermissions(category).ManageRoles);
        await fixture.InvokeAsync("OnGuildAvailableAsync", fixture.Guild);
        fixture.RequestResponse = (method, path, body) =>
        {
            if (method == "POST")
            {
                Assert.EndsWith("guilds/100/channels", path);
                using var payload = JsonDocument.Parse(body!);
                Assert.Equal("202", payload.RootElement.GetProperty("parent_id").GetString());
                // Even copied Manage Roles or category-only bits must not be sent on create.
                Assert.False(payload.RootElement.TryGetProperty("permission_overwrites", out _));
                return JsonResponse(HttpStatusCode.OK, fixture.CreatedChannelJson);
            }
            if (method == "PUT")
            {
                Assert.Contains("channels/200/permissions/", path);
                return JsonResponse(HttpStatusCode.NoContent, "");
            }
            Assert.Equal("PATCH", method);
            Assert.EndsWith("guilds/100/members/300", path);
            using var move = JsonDocument.Parse(body!);
            Assert.Equal("200", move.RootElement.GetProperty("channel_id").GetString());
            return JsonResponse(HttpStatusCode.OK,
                "{\"user\":{\"id\":\"300\",\"username\":\"Owner\",\"discriminator\":\"0\"},\"roles\":[]}");
        };

        await fixture.InvokeAsync("CreateRoomAsync", member, lobby, category, CancellationToken.None);

        Assert.Equal(fixture.Room, Assert.Single(fixture.Rooms));
        Assert.Equal(["POST", "PUT", "PUT", "PATCH"], fixture.Requests.Select(request => request.Method).ToArray());
        var updates = fixture.RequestBodies.Where(request => request.Method == "PUT").ToArray();
        Assert.Equal(2, updates.Length);
        using var botPayload = JsonDocument.Parse(updates[0].Body!);
        var botPermissions = ReadPermissions(botPayload.RootElement);
        Assert.Equal(PermValue.Allow, botPermissions.ManageRoles);
        Assert.Equal(PermValue.Allow, botPermissions.ViewChannel);
        Assert.Equal(PermValue.Allow, botPermissions.Connect);
        Assert.Equal(PermValue.Deny, botPermissions.Speak);
        using var ownerPayload = JsonDocument.Parse(updates[1].Body!);
        var ownerPermissions = ReadPermissions(ownerPayload.RootElement);
        Assert.Equal(PermValue.Allow, ownerPermissions.ViewChannel);
        Assert.Equal(PermValue.Allow, ownerPermissions.Connect);
        Assert.Equal(PermValue.Deny, ownerPermissions.SendMessages);
        Assert.Equal(PermValue.Deny, ownerPermissions.ManageRoles);
    }

    private static OverwritePermissions ReadPermissions(JsonElement payload) => new(
        ulong.Parse(payload.GetProperty("allow").GetString()!),
        ulong.Parse(payload.GetProperty("deny").GetString()!));

    private static void AssertChannelLookup(LifecycleFixture fixture)
    {
        var request = Assert.Single(fixture.Requests);
        Assert.Equal("GET", request.Method);
        Assert.EndsWith("channels/200", request.Path);
    }

    private static RestResponse JsonResponse(HttpStatusCode status, string json) =>
        new(status, [], new MemoryStream(Encoding.UTF8.GetBytes(json)));

    private sealed class LifecycleFixture : IDisposable
    {
        public LifecycleFixture()
        {
            var transport = Fake<IRestClient>((method, arguments) =>
            {
                switch (method.Name)
                {
                    case "SetHeader":
                    case "SetCancelToken":
                    case "Dispose":
                        return null;
                    case "SendAsync":
                        var methodName = (string)arguments[0]!;
                        var path = (string)arguments[1]!;
                        var body = method.GetParameters()[2].ParameterType == typeof(string)
                            ? (string?)arguments[2] : null;
                        Requests.Enqueue((methodName, path));
                        RequestBodies.Enqueue((methodName, path, body));
                        if (RequestResponse is not null)
                        {
                            return Task.FromResult(RequestResponse(methodName, path, body));
                        }
                        return Response is null
                            ? Task.FromException<RestResponse>(Unexpected(method))
                            : Task.FromResult(Response());
                    default:
                        throw Unexpected(method);
                }
            });
            Client = new DiscordSocketClient(new DiscordSocketConfig
            {
                RestClientProvider = _ => transport
            });
            SetConnectionState(ConnectionState.Connected);
            // Mark only the local API client logged in; no login, gateway, or real HTTP is used.
            var api = typeof(BaseDiscordClient).GetProperty("ApiClient", InstanceMembers)!.GetValue(Client)!;
            typeof(BaseDiscordClient).Assembly.GetType("Discord.API.DiscordRestApiClient")!
                .GetProperty("LoginState", InstanceMembers)!.SetValue(api, LoginState.LoggedIn);
            Guild = CreateGuild(100);
            Room = new VoiceMasterRoom(Guild.Id, 200, 300);
            Rooms.Add(Room);
            var store = Fake<IVoiceMasterStore>((method, arguments) =>
            {
                // Record even rejected calls: the service catches failures in queue processing.
                StoreCalls.Enqueue(method.Name);
                if (method.Name != "SaveRoomAsync")
                {
                    Assert.Equal(Guild.Id, arguments[0]);
                }
                switch (method.Name)
                {
                    case "GetRoomsAsync" when AllowReads:
                        return Task.FromResult<IReadOnlyList<VoiceMasterRoom>>(Rooms.ToArray());
                    case "GetConfigurationAsync" when AllowReads:
                        return Task.FromResult<VoiceMasterConfiguration?>(null);
                    case "SaveRoomAsync" when AllowSave:
                        Rooms.Add((VoiceMasterRoom)arguments[0]!);
                        return Task.CompletedTask;
                    case "RemoveRoomAsync" when AllowRemoval:
                        Assert.Equal(Room.ChannelId, arguments[1]);
                        Assert.NotEmpty(Requests);
                        Rooms.Remove(Room);
                        return Task.CompletedTask;
                    default:
                        throw Unexpected(method);
                }
            });
            Service = new VoiceMasterService(Client, store, Logger);
        }

        private FailureLogger Logger { get; } = new();
        public DiscordSocketClient Client { get; }
        public SocketGuild Guild { get; }
        public VoiceMasterService Service { get; }
        public VoiceMasterRoom Room { get; }
        public List<VoiceMasterRoom> Rooms { get; } = [];
        public ConcurrentQueue<string> StoreCalls { get; } = new();
        public ConcurrentQueue<(string Method, string Path)> Requests { get; } = new();
        public bool AllowReads { get; set; }
        public bool AllowRemoval { get; set; }
        public bool AllowSave { get; set; }
        public Func<RestResponse>? Response { get; set; }
        public Func<string, string, string?, RestResponse>? RequestResponse { get; set; }
        public ConcurrentQueue<(string Method, string Path, string? Body)> RequestBodies { get; } = new();
        public string CreatedChannelJson { get; private set; } = "";
        public ConcurrentDictionary<ulong, DateTimeOffset> PendingMoves =>
            (ConcurrentDictionary<ulong, DateTimeOffset>)typeof(VoiceMasterService)
                .GetField("_pendingMoves", InstanceMembers)!.GetValue(Service)!;

        public SocketGuild CreateGuild(ulong id)
        {
            // Discord.Net exposes no public constructor for an offline socket guild.
            var guild = (SocketGuild)Activator.CreateInstance(typeof(SocketGuild), InstanceMembers,
                binder: null, args: [Client, id], culture: null)!;
            var state = typeof(DiscordSocketClient).GetProperty("State", InstanceMembers)!.GetValue(Client)!;
            state.GetType().GetMethod("AddGuild", InstanceMembers)!.Invoke(state, [guild]);
            return guild;
        }

        public void PopulateCreationGuild(bool categoryOnlyPermissions)
        {
            var management = new OverwritePermissions(viewChannel: PermValue.Allow, connect: PermValue.Allow,
                manageChannel: PermValue.Allow, manageRoles: PermValue.Allow, moveMembers: PermValue.Allow,
                speak: PermValue.Deny);
            var owner = new OverwritePermissions(viewChannel: PermValue.Deny, connect: PermValue.Deny,
                sendMessages: PermValue.Deny, manageRoles: PermValue.Deny);
            var everyone = new OverwritePermissions(viewChannel: PermValue.Deny, connect: PermValue.Deny);
            object[] overwrites =
            [
                new { id = "100", type = 0, allow = everyone.AllowValue.ToString(), deny = everyone.DenyValue.ToString() },
                new { id = "400", type = 1, allow = management.AllowValue.ToString(), deny = management.DenyValue.ToString() },
                new { id = "300", type = 1, allow = owner.AllowValue.ToString(), deny = owner.DenyValue.ToString() },
                new { id = "101", type = 0, allow = ((ulong)GuildPermission.ManageRoles).ToString(), deny = "0" }
            ];
            CreatedChannelJson = JsonSerializer.Serialize(new
            {
                id = "200",
                guild_id = "100",
                type = 2,
                name = "Owner's room",
                position = 0,
                parent_id = "202",
                permission_overwrites = overwrites,
                bitrate = 64000,
                user_limit = 0
            });
            var permissions = (ulong)(GuildPermission.ViewChannel | GuildPermission.Connect |
                GuildPermission.ManageChannels | GuildPermission.SendMessages);
            if (!categoryOnlyPermissions)
            {
                permissions |= (ulong)(GuildPermission.ManageRoles | GuildPermission.MoveMembers);
            }
            var guildJson = JsonSerializer.Serialize(new
            {
                id = "100",
                name = "Guild",
                owner_id = "999",
                roles = new object[]
                {
                    new { id = "100", name = "@everyone", permissions = "0", position = 0 },
                    new { id = "101", name = "Moderator", permissions = "0", position = 1 },
                    new { id = "102", name = "Bot", permissions = permissions.ToString(), position = 2 }
                },
                channels = new object[]
                {
                    new { id = "201", type = 2, name = "Lobby", position = 0,
                        permission_overwrites = overwrites, bitrate = 64000, user_limit = 0 },
                    new { id = "202", type = 4, name = "Rooms", position = 0, permission_overwrites = overwrites }
                },
                members = new object[]
                {
                    new { user = new { id = "400", username = "Bot", discriminator = "0", bot = true }, roles = new[] { "102" } },
                    new { user = new { id = "300", username = "Owner", discriminator = "0", bot = false }, roles = Array.Empty<string>() }
                },
                member_count = 2,
                presences = Array.Empty<object>(),
                threads = Array.Empty<object>(),
                voice_states = new[] { new { user_id = "300", channel_id = "201", session_id = "offline" } },
                guild_scheduled_events = Array.Empty<object>(),
                features = Array.Empty<string>()
            });
            var state = typeof(DiscordSocketClient).GetProperty("State", InstanceMembers)!.GetValue(Client)!;
            var serializer = (Newtonsoft.Json.JsonSerializer)typeof(DiscordSocketClient)
                .GetField("_serializer", InstanceMembers)!.GetValue(Client)!;
            object Model(Type type, string json)
            {
                using var reader = new Newtonsoft.Json.JsonTextReader(new StringReader(json));
                return serializer.Deserialize(reader, type)!;
            }
            var userType = typeof(BaseDiscordClient).Assembly.GetType("Discord.API.User")!;
            var self = (SocketSelfUser)typeof(SocketSelfUser).GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [Client, state, Model(userType,
                    "{\"id\":\"400\",\"username\":\"Bot\",\"discriminator\":\"0\",\"bot\":true}")])!;
            typeof(BaseSocketClient).GetProperty("CurrentUser", InstanceMembers | BindingFlags.DeclaredOnly)!.SetValue(Client, self);
            var extendedType = typeof(DiscordSocketClient).Assembly.GetType("Discord.API.Gateway.ExtendedGuild")!;
            typeof(SocketGuild).GetMethod("Update", InstanceMembers, binder: null,
                types: [state.GetType(), extendedType], modifiers: null)!.Invoke(Guild, [state, Model(extendedType, guildJson)]);
        }

        public void SetConnectionState(ConnectionState state)
        {
            var connection = typeof(DiscordSocketClient).GetField("_connection", InstanceMembers)!.GetValue(Client)!;
            connection.GetType().GetProperty("State", InstanceMembers)!.SetValue(connection, state);
        }

        public bool IsReady(SocketGuild guild) =>
            (bool)typeof(VoiceMasterService).GetMethod("IsReady", InstanceMembers)!.Invoke(Service, [guild])!;

        public Task InvokeAsync(string method, params object[] arguments) =>
            (Task)typeof(VoiceMasterService).GetMethod(method, InstanceMembers)!.Invoke(Service, arguments)!;

        public Task ReconcileAsync(SocketGuild guild) => InvokeAsync("ReconcileAsync", guild, CancellationToken.None);

        public Task ProcessQueuedAsync()
        {
            var queue = (Channel<ulong>)typeof(VoiceMasterService).GetField("_queue", InstanceMembers)!.GetValue(Service)!;
            Assert.True(queue.Writer.TryComplete());
            return InvokeAsync("ProcessQueueAsync", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        }

        public void Dispose()
        {
            Service.Dispose();
            Client.Dispose();
            Assert.Empty(Logger.Failures);
        }
    }

    private sealed class FailureLogger : ILogger<VoiceMasterService>
    {
        public ConcurrentQueue<Exception> Failures { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                Failures.Enqueue(exception ?? new InvalidOperationException(formatter(state, exception)));
            }
        }
    }

    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static T Fake<T>(Func<MethodInfo, object?[], object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, InterfaceProxy>();
        ((InterfaceProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    private static Exception Unexpected(MethodInfo method) =>
        new InvalidOperationException($"Unexpected offline fake call: {method.DeclaringType?.Name}.{method.Name}");

    public class InterfaceProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Handler { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            Handler(targetMethod!, args ?? []);
    }
}
