using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using Arisu.Modules;
using Arisu.Services;
using Arisu.Storage;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Arisu.IntegrationTests;

public sealed class LevelingSettingsAuthorizationTests
{
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Theory]
    [InlineData(GuildPermission.ManageGuild, false)]
    [InlineData(GuildPermission.ManageRoles, false)]
    [InlineData(GuildPermission.ManageGuild | GuildPermission.ManageRoles, true)]
    [InlineData(GuildPermission.Administrator, true)]
    public async Task RewardCommandPreconditionsRequireBothManageGuildAndManageRolesAsync(
        GuildPermission callerPermissions, bool allowed)
    {
        using var fixture = new AuthorizationFixture(callerPermissions: callerPermissions);
        using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(fixture.Store)
            .AddSingleton(fixture.Lifetime)
            .AddSingleton(fixture.AuditService)
            .AddSingleton(Proxy<IMemberLogChannelService>((method, _) =>
                throw new InvalidOperationException($"Unexpected {method.Name}")))
            .AddSingleton(Proxy<IGuildEventBackgroundService>((method, _) =>
                throw new InvalidOperationException($"Unexpected {method.Name}")))
            .AddSingleton(Proxy<IGuildEventChannelService>((method, _) =>
                throw new InvalidOperationException($"Unexpected {method.Name}")))
            .AddSingleton(Proxy<IGuildChannelConfigurationStore>((method, _) =>
                throw new InvalidOperationException($"Unexpected {method.Name}")))
            .BuildServiceProvider();
        using var commands = new InteractionService(fixture.Discord.Client);
        await commands.AddModuleAsync<SettingsModule>(services);
        var command = Assert.Single(commands.SlashCommands,
            command => command.Name.EndsWith("reward-add", StringComparison.Ordinal));

        var result = await command.CheckPreconditionsAsync(fixture.Discord.Context, services);

        Assert.Equal(allowed, result.IsSuccess);
        Assert.Empty(fixture.RewardWrites);
    }

    [Fact]
    public async Task RewardValidationRejectsCallerWithoutManageRolesEvenWhenInvokedDirectlyAsync()
    {
        using var fixture = new AuthorizationFixture(callerPermissions: GuildPermission.ManageGuild);
        Assert.True(fixture.Discord.Moderator.GuildPermissions.ManageGuild);
        Assert.False(fixture.Discord.Moderator.GuildPermissions.ManageRoles);

        await fixture.Module.RewardAddAsync(5, fixture.RewardRole);

        AssertRejected(fixture, "You need Manage Roles");
    }

    [Theory]
    [InlineData(GuildPermission.CreateGuildExpressions)]
    [InlineData(GuildPermission.CreateEvents)]
    [InlineData(GuildPermission.ManageRoles)]
    [InlineData(GuildPermission.ManageMessages)]
    [InlineData(GuildPermission.Administrator)]
    public async Task PrivilegedRewardRolePermissionsAreRejectedBeforeSavingAsync(GuildPermission permission)
    {
        using var fixture = new AuthorizationFixture();
        SetPermissions(fixture.RewardRole, permission | GuildPermission.ViewChannel);

        await fixture.Module.RewardAddAsync(5, fixture.RewardRole);

        AssertRejected(fixture, "without moderation or server management permissions");
    }

    [Theory]
    [InlineData(ChannelPermission.ManageMessages, false)]
    [InlineData(ChannelPermission.ManageRoles, false)]
    [InlineData(ChannelPermission.CreateEvents, false)]
    [InlineData(ChannelPermission.CreateEvents, true)]
    [InlineData(ChannelPermission.ManageMessages, true)]
    public async Task UnsafeRewardRoleChannelAndCategoryAllowsAreRejectedBeforeSavingAsync(
        ChannelPermission permission, bool category)
    {
        using var fixture = new AuthorizationFixture();
        fixture.AddChannelOverwrite(fixture.RewardRole.Id, new OverwritePermissions((ulong)permission, 0), category);

        await fixture.Module.RewardAddAsync(5, fixture.RewardRole);

        AssertRejected(fixture, "without moderation or server management permissions");
    }

    [Theory]
    [InlineData(5, 5)]
    [InlineData(5, 6)]
    public async Task RewardRolesAtOrAboveBotHierarchyAreRejectedBeforeSavingAsync(int botHierarchy, int rolePosition)
    {
        using var fixture = new AuthorizationFixture(botHierarchy: botHierarchy);
        SetPosition(fixture.RewardRole, rolePosition);

        await fixture.Module.RewardAddAsync(5, fixture.RewardRole);

        AssertRejected(fixture, "below my highest role");
    }

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    public async Task RewardRolesAtOrAboveCallerHierarchyAreRejectedBeforeSavingAsync(int rolePosition)
    {
        using var fixture = new AuthorizationFixture();
        SetPosition(fixture.RewardRole, rolePosition);

        await fixture.Module.RewardAddAsync(5, fixture.RewardRole);

        AssertRejected(fixture, "below your highest role");
    }

    [Fact]
    public async Task BotWithoutManageRolesCannotSaveRewardAsync()
    {
        using var fixture = new AuthorizationFixture();
        SetPermissions(fixture.Discord.Guild.GetRole(1020), GuildPermission.ViewChannel);

        await fixture.Module.RewardAddAsync(5, fixture.RewardRole);

        AssertRejected(fixture, "I need Manage Roles");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EveryoneAndManagedRewardRolesAreRejectedBeforeSavingAsync(bool managed)
    {
        using var fixture = new AuthorizationFixture();
        var role = managed ? fixture.RewardRole : fixture.Discord.Guild.EveryoneRole;
        if (managed)
        {
            typeof(SocketRole).GetProperty(nameof(SocketRole.IsManaged))!.SetValue(role, true);
        }

        await fixture.Module.RewardAddAsync(5, role);

        AssertRejected(fixture, "cannot be @everyone or managed");
    }

    [Fact]
    public async Task RewardRolesFromAnotherGuildAreRejectedBeforeSavingAsync()
    {
        using var fixture = new AuthorizationFixture();
        var otherGuild = Construct<SocketGuild>(fixture.Discord.Client, 900UL);
        var role = Construct<SocketRole>(otherGuild, 901UL);

        await fixture.Module.RewardAddAsync(5, role);

        AssertRejected(fixture, "Select a role from this server");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public async Task InvalidRewardLevelsAreRejectedBeforeSavingAsync(int level)
    {
        using var fixture = new AuthorizationFixture();

        await fixture.Module.RewardAddAsync(level, fixture.RewardRole);

        AssertRejected(fixture, "between 1 and 1000");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    public async Task SafeLowerRewardRoleSavesRequestedGuildLevelAndRoleAndWritesAuditAsync(int level)
    {
        using var fixture = new AuthorizationFixture();
        SetPermissions(fixture.RewardRole, GuildPermission.ViewChannel | GuildPermission.SendMessages);
        fixture.AddChannelOverwrite(fixture.RewardRole.Id, new OverwritePermissions(
            (ulong)(ChannelPermission.ViewChannel | ChannelPermission.SendMessages),
            (ulong)(ChannelPermission.ManageMessages | ChannelPermission.CreateEvents)));
        // Another role's privileged overwrite must not disqualify a cosmetic reward.
        fixture.AddChannelOverwrite(777, new OverwritePermissions(manageMessages: PermValue.Allow), category: true);

        await fixture.Module.RewardAddAsync(level, fixture.RewardRole);

        AssertSaved(fixture, level);
    }

    [Fact]
    public async Task OwnerCanSaveRewardAboveOwnRoleButBelowBotRoleAsync()
    {
        using var fixture = new AuthorizationFixture(moderatorIsOwner: true, callerPermissions: 0);
        SetPosition(fixture.RewardRole, 15);

        await fixture.Module.RewardAddAsync(5, fixture.RewardRole);

        AssertSaved(fixture, 5);
    }

    [Fact]
    public async Task OwnerCannotBypassBotRewardRoleHierarchyAsync()
    {
        using var fixture = new AuthorizationFixture(moderatorIsOwner: true, callerPermissions: 0);
        SetPosition(fixture.RewardRole, 20);

        await fixture.Module.RewardAddAsync(5, fixture.RewardRole);

        AssertRejected(fixture, "below my highest role");
    }

    private static void AssertRejected(AuthorizationFixture fixture, string expectedMessage)
    {
        Assert.Empty(fixture.RewardWrites);
        Assert.Empty(fixture.AuditEntries);
        Assert.Contains(expectedMessage, AssertPrivateResponse(fixture));
    }

    private static void AssertSaved(AuthorizationFixture fixture, int level)
    {
        var write = Assert.Single(fixture.RewardWrites);
        Assert.Equal(OfflineInteractionFixture.GuildId, write.GuildId);
        Assert.Equal(level, write.Level);
        Assert.Equal(fixture.RewardRole.Id, write.RoleId);
        Assert.Equal(fixture.StoppingToken, write.Token);
        var audit = Assert.Single(fixture.AuditEntries);
        Assert.Equal(OfflineInteractionFixture.GuildId, audit.GuildId);
        Assert.Equal(fixture.Discord.Moderator.Id, audit.ActorId);
        Assert.Equal("Leveling settings changed", audit.Action);
        Assert.Contains($"level {level}: role {fixture.RewardRole.Id}", audit.Details);
        Assert.Contains($"Level {level} will award <@&{fixture.RewardRole.Id}>", AssertPrivateResponse(fixture));
    }

    private static string AssertPrivateResponse(AuthorizationFixture fixture)
    {
        Assert.Equal(2, fixture.Discord.Requests.Count);
        var defer = fixture.Discord.Requests[0];
        Assert.Equal("POST", defer.Method);
        Assert.Contains("/callback", defer.Endpoint);
        using var initial = JsonDocument.Parse(defer.Json!);
        Assert.Equal((int)InteractionResponseType.DeferredChannelMessageWithSource,
            initial.RootElement.GetProperty("type").GetInt32());
        Assert.Equal((int)MessageFlags.Ephemeral,
            initial.RootElement.GetProperty("data").GetProperty("flags").GetInt32());
        var edit = fixture.Discord.Requests[1];
        Assert.Equal("PATCH", edit.Method);
        Assert.Contains("/messages/@original", edit.Endpoint);
        using var response = JsonDocument.Parse(edit.Json!);
        return response.RootElement.GetProperty("content").GetString()!;
    }

    private sealed class AuthorizationFixture : IDisposable
    {
        private readonly CancellationTokenSource _stopping = new();
        private readonly ConcurrentDictionary<ulong, SocketGuildChannel> _channels = new();

        public AuthorizationFixture(
            GuildPermission callerPermissions = GuildPermission.ManageGuild | GuildPermission.ManageRoles,
            bool moderatorIsOwner = false,
            int botHierarchy = 20)
        {
            Discord = new OfflineInteractionFixture(moderatorIsOwner, botHierarchy);
            var guild = Discord.Guild;
            var roles = (ConcurrentDictionary<ulong, SocketRole>)typeof(SocketGuild)
                .GetField("_roles", InstanceMembers)!.GetValue(guild)!;
            roles[guild.Id] = Construct<SocketRole>(guild, guild.Id);
            SetPermissions(guild.GetRole(1010), callerPermissions);
            SetPermissions(guild.GetRole(1000UL + (ulong)botHierarchy), GuildPermission.ManageRoles);
            var state = typeof(DiscordSocketClient).GetProperty("State", InstanceMembers)!.GetValue(Discord.Client)!;
            state.GetType().GetMethod("AddGuild", InstanceMembers)!.Invoke(state, [guild]);
            typeof(SocketGuild).GetField("_channels", InstanceMembers)!.SetValue(guild, _channels);
            RewardRole = Construct<SocketRole>(guild, 501UL);
            SetPosition(RewardRole, 5);
            roles[RewardRole.Id] = RewardRole;
            Store = Proxy<ILevelingStore>((method, args) =>
            {
                Assert.Equal(nameof(ILevelingStore.SetRewardAsync), method.Name);
                RewardWrites.Add(((ulong)args[0]!, (int)args[1]!, (ulong)args[2]!, (CancellationToken)args[3]!));
                return Task.CompletedTask;
            });
            Lifetime = Proxy<IHostApplicationLifetime>((method, _) =>
            {
                Assert.Equal("get_ApplicationStopping", method.Name);
                return StoppingToken;
            });
            AuditService = Proxy<IAuditChannelService>((method, args) =>
            {
                Assert.Equal(nameof(IAuditChannelService.WriteAsync), method.Name);
                Assert.Equal(StoppingToken, (CancellationToken)args[1]!);
                AuditEntries.Add((ModerationAuditEntry)args[0]!);
                return Task.CompletedTask;
            });
            Module = new SettingsModule.LevelingSettingsModule(Store,
                NullLogger<SettingsModule.LevelingSettingsModule>.Instance, Lifetime, AuditService);
            Discord.Attach(Module);
        }

        public OfflineInteractionFixture Discord { get; }
        public SocketRole RewardRole { get; }
        public SettingsModule.LevelingSettingsModule Module { get; }
        public ILevelingStore Store { get; }
        public IHostApplicationLifetime Lifetime { get; }
        public IAuditChannelService AuditService { get; }
        public CancellationToken StoppingToken => _stopping.Token;
        public List<(ulong GuildId, int Level, ulong RoleId, CancellationToken Token)> RewardWrites { get; } = [];
        public List<ModerationAuditEntry> AuditEntries { get; } = [];

        public void AddChannelOverwrite(ulong roleId, OverwritePermissions permissions, bool category = false)
        {
            var id = 600UL + (ulong)_channels.Count;
            SocketGuildChannel channel = category
                ? Construct<SocketCategoryChannel>(Discord.Client, id, Discord.Guild)
                : Construct<SocketTextChannel>(Discord.Client, id, Discord.Guild);
            typeof(SocketGuildChannel).GetField("_overwrites", InstanceMembers)!.SetValue(channel,
                ImmutableArray.Create(new Overwrite(roleId, PermissionTarget.Role, permissions)));
            _channels[id] = channel;
        }

        public void Dispose()
        {
            Discord.Dispose();
            _stopping.Dispose();
        }
    }

    private static void SetPermissions(SocketRole role, GuildPermission permissions) =>
        typeof(SocketRole).GetProperty(nameof(SocketRole.Permissions))!
            .SetValue(role, new GuildPermissions((ulong)permissions));

    private static void SetPosition(SocketRole role, int position) =>
        typeof(SocketRole).GetProperty(nameof(SocketRole.Position))!.SetValue(role, position);

    private static T Construct<T>(params object[] arguments) => (T)typeof(T).GetConstructors(InstanceMembers)
        .Single(constructor => constructor.GetParameters().Length == arguments.Length &&
            constructor.GetParameters().Select((parameter, index) => parameter.ParameterType.IsInstanceOfType(arguments[index])).All(match => match))
        .Invoke(arguments);

    private static T Proxy<T>(Func<MethodInfo, object?[], object?> invoke) where T : class
    {
        var proxy = DispatchProxy.Create<T, TestProxy>();
        ((TestProxy)(object)proxy).Callback = invoke;
        return proxy;
    }

    public class TestProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Callback { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Callback(targetMethod!, args!);
    }
}
