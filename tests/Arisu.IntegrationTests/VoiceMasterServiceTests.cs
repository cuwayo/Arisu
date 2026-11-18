using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Arisu.Services;
using Arisu.Storage;
using Discord;
using Discord.Net.Rest;
using Discord.Rest;
using Discord.WebSocket;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Arisu.IntegrationTests;

public sealed class VoiceMasterServiceTests
{
    [Theory]
    [InlineData(VoiceMasterAction.Rename)]
    [InlineData(VoiceMasterAction.Limit)]
    [InlineData(VoiceMasterAction.Lock)]
    [InlineData(VoiceMasterAction.Unlock)]
    [InlineData(VoiceMasterAction.Hide)]
    [InlineData(VoiceMasterAction.Show)]
    [InlineData(VoiceMasterAction.Permit)]
    [InlineData(VoiceMasterAction.Reject)]
    [InlineData(VoiceMasterAction.Transfer)]
    public async Task NonownerCannotCustomizeOrTransferRoom(VoiceMasterAction action)
    {
        using var fixture = new RoomFixture();
        var member = fixture.CreateMember(30, fixture.Channel);
        var target = fixture.CreateMember(40, fixture.Channel);
        fixture.UserPages.Add([member, target]);

        var result = await fixture.Service.UpdateRoomAsync(member, action, name: "New name", limit: 5, target: target);

        Assert.Contains("Only this room's owner", result);
        fixture.AssertNoChanges();
    }

    [Fact]
    public async Task ClaimSucceedsWhenOwnerHasLeftAndPreservesMemberPermissions()
    {
        using var fixture = new RoomFixture();
        var member = fixture.CreateMember(30, fixture.Channel);
        fixture.UserPages.Add([member]);
        fixture.MemberOverwrite = ExistingPermissions();

        var result = await fixture.Service.UpdateRoomAsync(member, VoiceMasterAction.Claim);

        Assert.Equal("You now own this room.", result);
        AssertOwnershipChange(fixture, member);
    }

    [Fact]
    public async Task ClaimFailsWhenOwnerIsConnectedOnLaterPage()
    {
        using var fixture = new RoomFixture();
        var member = fixture.CreateMember(30, fixture.Channel);
        fixture.UserPages.Add([member]);
        fixture.UserPages.Add([fixture.Owner]);

        var result = await fixture.Service.UpdateRoomAsync(member, VoiceMasterAction.Claim);

        Assert.Contains("The owner is still in this room", result);
        fixture.AssertNoChanges();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClaimSucceedsWhenVisibleOwnerIsNotConnectedToRoom(bool connectedElsewhere)
    {
        using var fixture = new RoomFixture();
        var member = fixture.CreateMember(30, fixture.Channel);
        var owner = fixture.CreateMember(fixture.Owner.Id, connectedElsewhere ? fixture.OtherChannel : null);
        fixture.UserPages.Add([member]);
        fixture.UserPages.Add([owner]);
        fixture.MemberOverwrite = ExistingPermissions();

        var result = await fixture.Service.UpdateRoomAsync(member, VoiceMasterAction.Claim);

        Assert.Equal("You now own this room.", result);
        AssertOwnershipChange(fixture, member);
    }

    [Fact]
    public async Task OwnerCannotClaimOwnRoomAgain()
    {
        using var fixture = new RoomFixture();
        fixture.UserPages.Add([fixture.Owner]);

        var result = await fixture.Service.UpdateRoomAsync(fixture.Owner, VoiceMasterAction.Claim);

        Assert.Equal("You already own this room.", result);
        fixture.AssertNoChanges();
    }

    [Fact]
    public async Task TransferSucceedsForConnectedMemberOnLaterPageAndPreservesPermissions()
    {
        using var fixture = new RoomFixture();
        var target = fixture.CreateMember(30, fixture.Channel);
        fixture.UserPages.Add([fixture.Owner]);
        fixture.UserPages.Add([target]);
        fixture.MemberOverwrite = ExistingPermissions();

        var result = await fixture.Service.UpdateRoomAsync(fixture.Owner, VoiceMasterAction.Transfer, target: target);

        Assert.Equal($"Room ownership transferred to <@{target.Id}>.", result);
        AssertOwnershipChange(fixture, target);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransferFailsForVisibleMemberNotConnectedToRoom(bool connectedElsewhere)
    {
        using var fixture = new RoomFixture();
        var target = fixture.CreateMember(30, connectedElsewhere ? fixture.OtherChannel : null);
        fixture.UserPages.Add([fixture.Owner]);
        fixture.UserPages.Add([target]);

        var result = await fixture.Service.UpdateRoomAsync(fixture.Owner, VoiceMasterAction.Transfer, target: target);

        Assert.Equal("The new owner must be connected to your room.", result);
        fixture.AssertNoChanges();
    }

    [Fact]
    public async Task TransferFailsForMemberAbsentFromChannelPages()
    {
        using var fixture = new RoomFixture();
        var target = fixture.CreateMember(30, null);
        fixture.UserPages.Add([fixture.Owner]);

        var result = await fixture.Service.UpdateRoomAsync(fixture.Owner, VoiceMasterAction.Transfer, target: target);

        Assert.Equal("The new owner must be connected to your room.", result);
        fixture.AssertNoChanges();
    }

    [Theory]
    [InlineData(VoiceMasterAction.Lock, PermValue.Deny, PermValue.Allow)]
    [InlineData(VoiceMasterAction.Unlock, PermValue.Allow, PermValue.Allow)]
    [InlineData(VoiceMasterAction.Hide, PermValue.Deny, PermValue.Deny)]
    [InlineData(VoiceMasterAction.Show, PermValue.Deny, PermValue.Allow)]
    public async Task RoomAccessUpdatesPreserveUnrelatedRolePermissions(
        VoiceMasterAction action, PermValue expectedConnect, PermValue expectedView)
    {
        using var fixture = new RoomFixture();
        fixture.UserPages.Add([fixture.Owner]);
        fixture.RoleOverwrite = ExistingPermissions();

        await fixture.Service.UpdateRoomAsync(fixture.Owner, action);

        var update = Assert.Single(fixture.PermissionUpdates);
        Assert.Same(fixture.EveryoneRole, update.Target);
        Assert.Equal(expectedConnect, update.Permissions.Connect);
        Assert.Equal(expectedView, update.Permissions.ViewChannel);
        AssertUnrelatedPermissionsPreserved(update.Permissions);
        Assert.Empty(fixture.SavedRooms);
    }

    [Theory]
    [InlineData(VoiceMasterAction.Hide, VoiceMasterAction.Lock, PermValue.Deny, PermValue.Deny)]
    [InlineData(VoiceMasterAction.Lock, VoiceMasterAction.Hide, PermValue.Deny, PermValue.Deny)]
    [InlineData(VoiceMasterAction.Hide, VoiceMasterAction.Unlock, PermValue.Allow, PermValue.Deny)]
    [InlineData(VoiceMasterAction.Lock, VoiceMasterAction.Show, PermValue.Deny, PermValue.Allow)]
    public async Task ConsecutiveAccessUpdatesUseRestStateBeforeGatewayOverwriteArrives(
        VoiceMasterAction first, VoiceMasterAction second, PermValue expectedConnect, PermValue expectedView)
    {
        using var fixture = new RoomFixture();
        fixture.UserPages.Add([fixture.Owner]);
        fixture.RoleOverwrite = fixture.CachedRoleOverwrite = ExistingPermissions();

        await fixture.Service.UpdateRoomAsync(fixture.Owner, first);
        await fixture.Service.UpdateRoomAsync(fixture.Owner, second);

        Assert.Equal(2, fixture.PermissionUpdates.Count);
        var update = fixture.PermissionUpdates[1];
        Assert.Equal(expectedConnect, update.Permissions.Connect);
        Assert.Equal(expectedView, update.Permissions.ViewChannel);
        AssertUnrelatedPermissionsPreserved(update.Permissions);
        Assert.Equal(ExistingPermissions(), fixture.CachedRoleOverwrite);
        Assert.Equal(["GET", "PUT", "GET", "PUT"], fixture.Requests.Select(request => request.Method).ToArray());
    }

    [Theory]
    [InlineData(VoiceMasterAction.Permit, PermValue.Allow)]
    [InlineData(VoiceMasterAction.Reject, PermValue.Deny)]
    public async Task MemberAccessUpdatesPreserveUnrelatedPermissions(VoiceMasterAction action, PermValue expected)
    {
        using var fixture = new RoomFixture();
        var target = fixture.CreateMember(30, null);
        fixture.UserPages.Add([fixture.Owner]);
        fixture.MemberOverwrite = ExistingPermissions();

        await fixture.Service.UpdateRoomAsync(fixture.Owner, action, target: target);

        var update = Assert.Single(fixture.PermissionUpdates);
        Assert.Same(target, update.Target);
        Assert.Equal(expected, update.Permissions.Connect);
        Assert.Equal(expected, update.Permissions.ViewChannel);
        AssertUnrelatedPermissionsPreserved(update.Permissions);
        Assert.Empty(fixture.SavedRooms);
        Assert.Empty(fixture.MemberUpdates);
    }

    [Theory]
    [InlineData(GuildPermission.Administrator)]
    [InlineData(GuildPermission.ManageGuild)]
    [InlineData(GuildPermission.ManageChannels)]
    public async Task RejectCannotDenyAccessOrDisconnectPrivilegedMembers(GuildPermission permission)
    {
        using var fixture = new RoomFixture();
        var target = fixture.CreateMember(30, fixture.Channel, new GuildPermissions((ulong)permission));
        fixture.UserPages.Add([fixture.Owner, target]);

        var result = await fixture.Service.UpdateRoomAsync(fixture.Owner, VoiceMasterAction.Reject, target: target);

        Assert.Equal("Server owners, administrators, and channel moderators cannot be rejected.", result);
        fixture.AssertNoChanges();
    }

    [Fact]
    public async Task RejectCannotDenyAccessOrDisconnectServerOwner()
    {
        using var fixture = new RoomFixture();
        var target = fixture.CreateMember(RoomFixture.ServerOwnerId, fixture.Channel);
        fixture.UserPages.Add([fixture.Owner, target]);

        var result = await fixture.Service.UpdateRoomAsync(fixture.Owner, VoiceMasterAction.Reject, target: target);

        Assert.Equal("Server owners, administrators, and channel moderators cannot be rejected.", result);
        fixture.AssertNoChanges();
    }

    [Fact]
    public async Task RejectDisconnectsConnectedOrdinaryMemberAfterDenyingAccess()
    {
        using var fixture = new RoomFixture();
        var target = fixture.CreateMember(30, fixture.Channel);
        fixture.UserPages.Add([fixture.Owner, target]);

        var result = await fixture.Service.UpdateRoomAsync(fixture.Owner, VoiceMasterAction.Reject, target: target);

        Assert.Equal($"<@{target.Id}> can no longer see or join this room.", result);
        var overwrite = Assert.Single(fixture.PermissionUpdates);
        Assert.Same(target, overwrite.Target);
        Assert.Equal(PermValue.Deny, overwrite.Permissions.Connect);
        Assert.Equal(PermValue.Deny, overwrite.Permissions.ViewChannel);
        var disconnect = Assert.Single(fixture.MemberUpdates);
        Assert.Same(target, disconnect.Member);
        Assert.True(disconnect.Properties.Channel.IsSpecified);
        Assert.Null(disconnect.Properties.Channel.Value);
        Assert.Empty(fixture.SavedRooms);
    }

    private static OverwritePermissions ExistingPermissions() => new(
        viewChannel: PermValue.Allow,
        connect: PermValue.Deny,
        sendMessages: PermValue.Deny,
        attachFiles: PermValue.Allow,
        speak: PermValue.Deny,
        manageChannel: PermValue.Allow);

    private static void AssertUnrelatedPermissionsPreserved(OverwritePermissions permissions)
    {
        Assert.Equal(PermValue.Deny, permissions.SendMessages);
        Assert.Equal(PermValue.Allow, permissions.AttachFiles);
        Assert.Equal(PermValue.Deny, permissions.Speak);
        Assert.Equal(PermValue.Allow, permissions.ManageChannel);
        Assert.Equal(PermValue.Inherit, permissions.MoveMembers);
    }

    private static void AssertOwnershipChange(RoomFixture fixture, IGuildUser newOwner)
    {
        var saved = Assert.Single(fixture.SavedRooms);
        Assert.Equal(new VoiceMasterRoom(RoomFixture.GuildId, fixture.Channel.Id, newOwner.Id), saved);
        var update = Assert.Single(fixture.PermissionUpdates);
        Assert.Same(newOwner, update.Target);
        Assert.Equal(PermValue.Allow, update.Permissions.Connect);
        Assert.Equal(PermValue.Allow, update.Permissions.ViewChannel);
        AssertUnrelatedPermissionsPreserved(update.Permissions);
        Assert.Empty(fixture.MemberUpdates);
    }

    private sealed class RoomFixture : IDisposable
    {
        public const ulong GuildId = 100;
        public const ulong ServerOwnerId = 999;
        private readonly DiscordSocketClient _client;

        public RoomFixture()
        {
            var transport = Fake<IRestClient>((method, arguments) => method.Name switch
            {
                "SetHeader" or "SetCancelToken" or "Dispose" => null,
                "SendAsync" => Task.FromResult(HandleRequest((string)arguments[0]!,
                    (string)arguments[1]!, method.GetParameters()[2].ParameterType == typeof(string)
                        ? (string?)arguments[2] : null)),
                _ => throw Unexpected(method)
            });
            _client = new DiscordSocketClient(new DiscordSocketConfig { RestClientProvider = _ => transport });
            // Enable only the offline REST transport, without login or a gateway connection.
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var api = typeof(BaseDiscordClient).GetProperty("ApiClient", flags)!.GetValue(_client)!;
            typeof(BaseDiscordClient).Assembly.GetType("Discord.API.DiscordRestApiClient")!
                .GetProperty("LoginState", flags)!.SetValue(api, LoginState.LoggedIn);
            EveryoneRole = Fake<IRole>((method, _) => method.Name switch
            {
                "get_Id" => GuildId,
                _ => throw Unexpected(method)
            });
            OtherChannel = Fake<IVoiceChannel>((method, _) => method.Name switch
            {
                "get_Id" => 201UL,
                _ => throw Unexpected(method)
            });
            Channel = Fake<IVoiceChannel>(HandleChannel);
            var bot = Fake<IGuildUser>((method, _) => method.Name switch
            {
                "GetPermissions" => new ChannelPermissions(viewChannel: true, connect: true,
                    manageChannel: true, manageRoles: true, moveMembers: true),
                _ => throw Unexpected(method)
            });
            Guild = Fake<IGuild>((method, _) => method.Name switch
            {
                "get_Id" => GuildId,
                "get_OwnerId" => ServerOwnerId,
                "get_EveryoneRole" => EveryoneRole,
                "GetCurrentUserAsync" => Task.FromResult(bot),
                _ => throw Unexpected(method)
            });
            Owner = CreateMember(20, Channel);
            var store = Fake<IVoiceMasterStore>((method, arguments) =>
            {
                switch (method.Name)
                {
                    case "GetRoomAsync":
                        Assert.Equal(GuildId, arguments[0]);
                        Assert.Equal(Channel.Id, arguments[1]);
                        return Task.FromResult<VoiceMasterRoom?>(new(GuildId, Channel.Id, Owner.Id));
                    case "SaveRoomAsync":
                        SavedRooms.Add((VoiceMasterRoom)arguments[0]!);
                        return Task.CompletedTask;
                    default:
                        throw Unexpected(method);
                }
            });
            Service = new VoiceMasterService(_client, store, NullLogger<VoiceMasterService>.Instance);
        }

        public IVoiceMasterService Service { get; }
        public IGuild Guild { get; }
        public IVoiceChannel Channel { get; }
        public IVoiceChannel OtherChannel { get; }
        public IRole EveryoneRole { get; }
        public IGuildUser Owner { get; }
        public List<IReadOnlyCollection<IGuildUser>> UserPages { get; } = [];
        public OverwritePermissions? RoleOverwrite { get; set; }
        public OverwritePermissions? MemberOverwrite { get; set; }
        public OverwritePermissions? CachedRoleOverwrite { get; set; }
        public OverwritePermissions? CachedMemberOverwrite { get; set; }
        public List<(string Method, string Path)> Requests { get; } = [];
        private Dictionary<ulong, IGuildUser> Members { get; } = [];
        public List<(object Target, OverwritePermissions Permissions)> PermissionUpdates { get; } = [];
        public List<(IGuildUser Member, GuildUserProperties Properties)> MemberUpdates { get; } = [];
        public List<VoiceMasterRoom> SavedRooms { get; } = [];

        public IGuildUser CreateMember(ulong id, IVoiceChannel? channel, GuildPermissions permissions = default)
        {
            IGuildUser? member = null;
            member = Fake<IGuildUser>((method, arguments) =>
            {
                switch (method.Name)
                {
                    case "get_Id": return id;
                    case "get_GuildId": return GuildId;
                    case "get_Guild": return Guild;
                    case "get_VoiceChannel": return channel;
                    case "get_IsBot": return false;
                    case "get_GuildPermissions": return permissions;
                    case "ModifyAsync":
                        Assert.NotEmpty(PermissionUpdates);
                        var properties = new GuildUserProperties();
                        ((Action<GuildUserProperties>)arguments[0]!)(properties);
                        MemberUpdates.Add((member!, properties));
                        return Task.CompletedTask;
                    default: throw Unexpected(method);
                }
            });
            Members[id] = member;
            return member;
        }

        public void AssertNoChanges()
        {
            Assert.Empty(PermissionUpdates);
            Assert.Empty(MemberUpdates);
            Assert.Empty(SavedRooms);
        }

        private object? HandleChannel(MethodInfo method, object?[] arguments)
        {
            switch (method.Name)
            {
                case "get_Id": return 200UL;
                case "get_GuildId": return GuildId;
                case "GetUsersAsync": return ReadUserPagesAsync();
                case "GetPermissionOverwrite":
                    return arguments[0] is IRole ? CachedRoleOverwrite : CachedMemberOverwrite;
                case "AddPermissionOverwriteAsync":
                    // A socket write must not refresh the delayed gateway snapshot.
                    PermissionUpdates.Add((arguments[0]!, (OverwritePermissions)arguments[1]!));
                    return Task.CompletedTask;
                default: throw Unexpected(method);
            }
        }

        private RestResponse HandleRequest(string method, string path, string? body)
        {
            Requests.Add((method, path));
            if (method == "GET")
            {
                Assert.EndsWith("channels/200", path);
                var overwrites = new List<object>();
                if (RoleOverwrite is { } role)
                {
                    overwrites.Add(new
                    {
                        id = GuildId.ToString(),
                        type = 0,
                        allow = role.AllowValue.ToString(),
                        deny = role.DenyValue.ToString()
                    });
                }
                if (MemberOverwrite is { } member)
                {
                    foreach (var id in Members.Keys)
                    {
                        overwrites.Add(new
                        {
                            id = id.ToString(),
                            type = 1,
                            allow = member.AllowValue.ToString(),
                            deny = member.DenyValue.ToString()
                        });
                    }
                }
                var json = JsonSerializer.Serialize(new
                {
                    id = "200",
                    guild_id = "100",
                    type = 2,
                    name = "Room",
                    position = 0,
                    permission_overwrites = overwrites,
                    bitrate = 64000,
                    user_limit = 0
                });
                return new RestResponse(HttpStatusCode.OK, [], new MemoryStream(Encoding.UTF8.GetBytes(json)));
            }
            Assert.Equal("PUT", method);
            var targetId = ulong.Parse(path[(path.LastIndexOf('/') + 1)..]);
            using var payload = JsonDocument.Parse(body!);
            var permissions = new OverwritePermissions(
                ulong.Parse(payload.RootElement.GetProperty("allow").GetString()!),
                ulong.Parse(payload.RootElement.GetProperty("deny").GetString()!));
            var isRole = payload.RootElement.GetProperty("type").GetInt32() == 0;
            if (isRole)
            {
                Assert.Equal(GuildId, targetId);
                RoleOverwrite = permissions;
            }
            else
            {
                MemberOverwrite = permissions;
            }
            PermissionUpdates.Add((isRole ? EveryoneRole : Members[targetId], permissions));
            return new RestResponse(HttpStatusCode.NoContent, [], new MemoryStream());
        }

        private async IAsyncEnumerable<IReadOnlyCollection<IGuildUser>> ReadUserPagesAsync()
        {
            foreach (var page in UserPages)
            {
                await Task.Yield();
                yield return page;
            }
        }

        public void Dispose()
        {
            ((IDisposable)Service).Dispose();
            _client.Dispose();
        }
    }

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
