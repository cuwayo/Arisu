using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Discord;
using Discord.Interactions;
using Discord.Net.Rest;
using Discord.WebSocket;

namespace Arisu.IntegrationTests;

internal sealed class OfflineInteractionFixture : IDisposable
{
    public const ulong GuildId = 100;
    public const ulong ModeratorId = 200;
    public const ulong BotId = 300;
    public const ulong OwnerId = 400;
    private readonly ConcurrentDictionary<ulong, SocketGuildUser> _members = new();
    private readonly ConcurrentDictionary<ulong, SocketRole> _roles = new();

    public DiscordSocketClient Client { get; }
    public SocketGuild Guild { get; }
    public SocketGuildUser Moderator { get; }
    public SocketCommandBase Interaction { get; }
    public SocketInteractionContext Context { get; }
    public List<OfflineRestRequest> Requests { get; } = [];
    public Func<OfflineRestRequest, RestResponse?>? RestCallback { get; set; }

    public OfflineInteractionFixture(bool moderatorIsOwner = false, int botHierarchy = 1)
    {
        Client = new DiscordSocketClient(new DiscordSocketConfig
        {
            RestClientProvider = _ => new OfflineRestClient(this),
            ResponseInternalTimeCheck = false
        });
        var apiClient = typeof(Discord.Rest.BaseDiscordClient).GetProperty("ApiClient", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(Client)!;
        SetProperty(apiClient, "LoginState", LoginState.LoggedIn);
        Guild = Construct<SocketGuild>(Client, GuildId);
        SetProperty(Guild, nameof(Guild.OwnerId), OwnerId);
        SetField(Guild, "_members", _members);
        SetField(Guild, "_roles", _roles);
        Moderator = CreateMember(moderatorIsOwner ? OwnerId : ModeratorId, 10);
        var bot = CreateMember(BotId, botHierarchy);
        var globalUser = typeof(SocketGuildUser).GetProperty("GlobalUser", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(bot)!;
        var self = Construct<SocketSelfUser>(Client, globalUser);
        typeof(BaseSocketClient).GetProperty(nameof(Client.CurrentUser),
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)!.SetValue(Client, self);

        // Build only the state used by command execution. No gateway or login is needed.
        Interaction = (SocketCommandBase)RuntimeHelpers.GetUninitializedObject(typeof(SocketCommandBase));
        SetProperty(Interaction, "Discord", Client);
        SetProperty(Interaction, nameof(Interaction.Id), SnowflakeUtils.ToSnowflake(DateTimeOffset.UtcNow));
        SetProperty(Interaction, nameof(Interaction.User), Moderator);
        SetProperty(Interaction, nameof(Interaction.Token), "offline-interaction");
        SetProperty(Interaction, nameof(Interaction.CreatedAt), DateTimeOffset.UtcNow);
        SetProperty(Interaction, nameof(Interaction.ApplicationId), BotId);
        SetProperty(Interaction, nameof(Interaction.GuildId), GuildId);
        SetField(Interaction, "_lock", Activator.CreateInstance(typeof(Lock))!);
        Context = new SocketInteractionContext(Client, Interaction);
    }

    public SocketGuildUser CreateMember(ulong id, int hierarchy, bool cached = true)
    {
        var roleId = 1000UL + (ulong)hierarchy;
        if (!_roles.ContainsKey(roleId))
        {
            var role = Construct<SocketRole>(Guild, roleId);
            SetProperty(role, nameof(role.Position), hierarchy);
            _roles[roleId] = role;
        }

        var globalType = typeof(SocketGuildUser).Assembly.GetType("Discord.WebSocket.SocketGlobalUser")!;
        var global = Construct(globalType, Client, id);
        var member = Construct<SocketGuildUser>(Guild, global);
        SetField(member, "_roleIds", System.Collections.Immutable.ImmutableArray.Create(roleId));
        if (cached)
        {
            _members[id] = member;
        }

        return member;
    }

    public void RemoveCachedMember(ulong id) => _members.TryRemove(id, out _);

    public void Attach(IInteractionModuleBase module) => module.SetContext(Context);

    public static RestResponse JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status, [], new MemoryStream(Encoding.UTF8.GetBytes(json)));

    public static RestResponse MemberResponse(ulong id, int hierarchy) => JsonResponse(JsonSerializer.Serialize(new
    {
        user = new { id = id.ToString(), username = "offline-member", discriminator = "0000", avatar = (string?)null },
        roles = new[] { (1000UL + (ulong)hierarchy).ToString() },
        joined_at = "2026-01-01T00:00:00Z",
        deaf = false,
        mute = false,
        flags = 0
    }));

    public void Dispose() => Client.Dispose();

    private RestResponse Respond(OfflineRestRequest request)
    {
        Requests.Add(request);
        if (RestCallback?.Invoke(request) is { } response)
        {
            return response;
        }

        if (request.Method == "POST" && request.Endpoint.Contains("/callback", StringComparison.Ordinal))
        {
            return JsonResponse("", HttpStatusCode.NoContent);
        }

        if (request.Endpoint.Contains("webhooks/", StringComparison.Ordinal))
        {
            using var payload = JsonDocument.Parse(request.Json ?? "{}");
            var content = payload.RootElement.TryGetProperty("content", out var value)
                ? value.GetString() ?? string.Empty : string.Empty;
            return JsonResponse(JsonSerializer.Serialize(new
            {
                id = "500",
                channel_id = "600",
                content,
                author = new { id = BotId.ToString(), username = "Arisu", discriminator = "0000", avatar = (string?)null, bot = true },
                timestamp = "2026-11-18T00:00:00Z",
                edited_timestamp = (string?)null,
                tts = false,
                mention_everyone = false,
                mentions = Array.Empty<object>(),
                mention_roles = Array.Empty<object>(),
                attachments = Array.Empty<object>(),
                embeds = Array.Empty<object>(),
                type = 0,
                flags = 64
            }));
        }

        throw new InvalidOperationException($"Unexpected offline REST request: {request.Method} {request.Endpoint}");
    }

    private static T Construct<T>(params object[] arguments) => (T)Construct(typeof(T), arguments);

    private static object Construct(Type type, params object[] arguments) =>
        type.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Single(constructor => constructor.GetParameters().Length == arguments.Length &&
                constructor.GetParameters().Select((parameter, index) => parameter.ParameterType.IsInstanceOfType(arguments[index])).All(match => match))
            .Invoke(arguments);

    private static void SetProperty(object target, string name, object value)
    {
        for (var type = target.GetType(); type is not null; type = type.BaseType)
        {
            var field = type.GetField($"<{name}>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (field is not null)
            {
                field.SetValue(target, value);
                return;
            }
        }

        throw new InvalidOperationException($"No backing field for {name}.");
    }

    private static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);

    private sealed class OfflineRestClient(OfflineInteractionFixture fixture) : IRestClient
    {
        public void Dispose() { }
        public void SetHeader(string key, string value) { }
        public void SetCancelToken(CancellationToken cancelToken) { }

        public Task<RestResponse> SendAsync(string method, string endpoint, CancellationToken cancelToken,
            bool headerOnly = false, string? reason = null,
            IEnumerable<KeyValuePair<string, IEnumerable<string>>>? requestHeaders = null) =>
            Task.FromResult(fixture.Respond(new(method, endpoint, null)));

        public Task<RestResponse> SendAsync(string method, string endpoint, string json, CancellationToken cancelToken,
            bool headerOnly = false, string? reason = null,
            IEnumerable<KeyValuePair<string, IEnumerable<string>>>? requestHeaders = null) =>
            Task.FromResult(fixture.Respond(new(method, endpoint, json)));

        public Task<RestResponse> SendAsync(string method, string endpoint, IReadOnlyDictionary<string, object> multipartParams,
            CancellationToken cancelToken, bool headerOnly = false, string? reason = null,
            IEnumerable<KeyValuePair<string, IEnumerable<string>>>? requestHeaders = null)
        {
            // Discord.Net uses multipart when clearing attachments, even without files to upload.
            if (multipartParams.Count != 1 || !multipartParams.TryGetValue("payload_json", out var payload))
            {
                throw new InvalidOperationException("Offline tests do not upload files.");
            }

            return Task.FromResult(fixture.Respond(new(method, endpoint, (string)payload)));
        }
    }
}

internal sealed record OfflineRestRequest(string Method, string Endpoint, string? Json);
