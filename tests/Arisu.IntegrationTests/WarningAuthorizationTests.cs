using System.Net;
using System.Reflection;
using System.Text.Json;
using Arisu.Modules;
using Arisu.Services;
using Arisu.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Arisu.IntegrationTests;

public sealed class WarningAuthorizationTests
{
    [Theory]
    [InlineData("delete", OfflineInteractionFixture.ModeratorId)]
    [InlineData("delete", OfflineInteractionFixture.OwnerId)]
    [InlineData("delete", OfflineInteractionFixture.BotId)]
    [InlineData("reason", OfflineInteractionFixture.ModeratorId)]
    [InlineData("reason", OfflineInteractionFixture.OwnerId)]
    [InlineData("reason", OfflineInteractionFixture.BotId)]
    [InlineData("clear", OfflineInteractionFixture.ModeratorId)]
    [InlineData("clear", OfflineInteractionFixture.OwnerId)]
    [InlineData("clear", OfflineInteractionFixture.BotId)]
    public async Task WarningMutationsRejectSelfOwnerAndBotBeforeWritingAsync(string action, ulong targetId)
    {
        using var fixture = new OfflineInteractionFixture();
        var target = fixture.CreateMember(targetId, 1);
        var history = new HistoryFixture(targetId);
        var module = CreateModule(fixture, history);

        await MutateAsync(module, action, target);

        Assert.Empty(history.Mutations);
        Assert.Empty(history.Cases);
        Assert.DoesNotContain(fixture.Requests, request => request.Endpoint.Contains("/members/", StringComparison.Ordinal));
        Assert.Contains("cannot moderate", LastContent(fixture));
    }

    [Theory]
    [InlineData("delete", 10)]
    [InlineData("delete", 11)]
    [InlineData("reason", 10)]
    [InlineData("reason", 11)]
    [InlineData("clear", 10)]
    [InlineData("clear", 11)]
    public async Task WarningMutationsRejectEqualAndHigherModeratorRolesBeforeWritingAsync(string action, int hierarchy)
    {
        using var fixture = new OfflineInteractionFixture();
        var target = fixture.CreateMember(500, hierarchy);
        var history = new HistoryFixture(target.Id);
        var module = CreateModule(fixture, history);

        await MutateAsync(module, action, target);

        Assert.Empty(history.Mutations);
        Assert.Empty(history.Cases);
        Assert.Contains("below your highest role", LastContent(fixture));
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("reason")]
    [InlineData("clear")]
    public async Task WarningMutationsAllowLowerModeratorRoleEvenAboveBotRoleAsync(string action)
    {
        using var fixture = new OfflineInteractionFixture(botHierarchy: 1);
        var target = fixture.CreateMember(500, 5);
        var history = new HistoryFixture(target.Id);
        var module = CreateModule(fixture, history);

        await MutateAsync(module, action, target);

        Assert.Single(history.Mutations);
        Assert.Single(history.Cases);
        if (action != "clear")
        {
            Assert.Equal(new[] { "lookup", action }, history.Operations);
        }
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("reason")]
    [InlineData("clear")]
    public async Task WarningMutationsAllowServerOwnerAboveAnyMemberRoleAsync(string action)
    {
        using var fixture = new OfflineInteractionFixture(moderatorIsOwner: true);
        var target = fixture.CreateMember(500, 20);
        var history = new HistoryFixture(target.Id);
        var module = CreateModule(fixture, history);

        await MutateAsync(module, action, target);

        Assert.Single(history.Mutations);
    }

    [Theory]
    [InlineData("delete", 5, true)]
    [InlineData("reason", 5, true)]
    [InlineData("clear", 5, true)]
    [InlineData("delete", 10, false)]
    [InlineData("reason", 11, false)]
    [InlineData("clear", 11, false)]
    public async Task WarningMutationsDownloadUncachedMembersAndCheckRestMemberHierarchyAsync(
        string action, int hierarchy, bool allowed)
    {
        using var fixture = new OfflineInteractionFixture();
        var target = fixture.CreateMember(500, hierarchy, cached: false);
        fixture.RestCallback = request => request.Endpoint.EndsWith("/members/500", StringComparison.Ordinal)
            ? OfflineInteractionFixture.MemberResponse(500, hierarchy) : null;
        var history = new HistoryFixture(target.Id);
        var module = CreateModule(fixture, history);

        await MutateAsync(module, action, target);

        Assert.Single(fixture.Requests, request => request.Endpoint.EndsWith("/members/500", StringComparison.Ordinal));
        Assert.Equal(allowed ? 1 : 0, history.Mutations.Count);
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("reason")]
    [InlineData("clear")]
    public async Task WarningMutationsAllowGenuinelyDepartedMembersAsync(string action)
    {
        using var fixture = new OfflineInteractionFixture();
        var target = fixture.CreateMember(500, 20, cached: false);
        fixture.RestCallback = request => request.Endpoint.EndsWith("/members/500", StringComparison.Ordinal)
            ? OfflineInteractionFixture.JsonResponse("{\"code\":10007,\"message\":\"Unknown Member\"}", HttpStatusCode.NotFound) : null;
        var history = new HistoryFixture(target.Id);
        var module = CreateModule(fixture, history);

        await MutateAsync(module, action, target);

        Assert.Single(history.Mutations);
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("reason")]
    [InlineData("clear")]
    public async Task WarningMutationsFailClosedOnMembershipLookupErrorsAsync(string action)
    {
        using var fixture = new OfflineInteractionFixture();
        var target = fixture.CreateMember(500, 1, cached: false);
        fixture.RestCallback = request => request.Endpoint.EndsWith("/members/500", StringComparison.Ordinal)
            ? OfflineInteractionFixture.JsonResponse("{\"code\":50013,\"message\":\"Missing Permissions\"}", HttpStatusCode.Forbidden) : null;
        var history = new HistoryFixture(target.Id);
        var module = CreateModule(fixture, history);

        await MutateAsync(module, action, target);

        Assert.Empty(history.Mutations);
        Assert.Contains("could not verify", LastContent(fixture));
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("reason")]
    public async Task MissingGuildScopedWarningDoesNotResolveMembersOrMutateAsync(string action)
    {
        using var fixture = new OfflineInteractionFixture();
        var target = fixture.CreateMember(500, 1);
        var history = new HistoryFixture(target.Id) { Missing = true };
        var module = CreateModule(fixture, history);

        await MutateAsync(module, action, target);

        Assert.Equal(new[] { "lookup" }, history.Operations);
        Assert.Equal(OfflineInteractionFixture.GuildId, history.LookupGuildId);
        Assert.Empty(history.Mutations);
        Assert.Contains("not found in this server", LastContent(fixture));
    }

    [Theory]
    [InlineData("delete", HttpStatusCode.Forbidden)]
    [InlineData("reason", HttpStatusCode.Forbidden)]
    [InlineData("clear", HttpStatusCode.Forbidden)]
    [InlineData("delete", HttpStatusCode.NotFound)]
    [InlineData("reason", HttpStatusCode.NotFound)]
    [InlineData("clear", HttpStatusCode.NotFound)]
    public async Task WarningMutationsFailClosedWhenModeratorMembershipCannotBeVerifiedAsync(
        string action, HttpStatusCode status)
    {
        using var fixture = new OfflineInteractionFixture();
        var target = fixture.CreateMember(500, 1);
        fixture.RemoveCachedMember(fixture.Moderator.Id);
        fixture.RestCallback = request => request.Endpoint.EndsWith("/members/200", StringComparison.Ordinal)
            ? OfflineInteractionFixture.JsonResponse("{\"code\":10007,\"message\":\"Lookup failed\"}", status) : null;
        var history = new HistoryFixture(target.Id);
        var module = CreateModule(fixture, history);

        await MutateAsync(module, action, target);

        Assert.Empty(history.Mutations);
        Assert.Contains("could not verify your server role", LastContent(fixture));
    }

    [Theory]
    [InlineData("delete", 0)]
    [InlineData("delete", -1)]
    [InlineData("reason", 0)]
    [InlineData("reason", -1)]
    public async Task InvalidWarningIdsAreRejectedBeforeLookupOrMutationAsync(string action, long warningId)
    {
        using var fixture = new OfflineInteractionFixture();
        var history = new HistoryFixture(500);
        var module = CreateModule(fixture, history);

        if (action == "delete")
        {
            await module.DeleteWarningAsync(warningId);
        }
        else
        {
            await module.ReasonAsync(warningId, "Updated reason");
        }

        Assert.Empty(history.Operations);
        Assert.Empty(history.Mutations);
        Assert.Contains("positive number", LastContent(fixture));
    }

    [Fact]
    public async Task UnconfirmedClearDoesNotMutateAsync()
    {
        using var fixture = new OfflineInteractionFixture();
        var target = fixture.CreateMember(500, 1);
        var history = new HistoryFixture(target.Id);
        var module = CreateModule(fixture, history);

        await module.ClearWarningsAsync(target, confirmed: false);

        Assert.Empty(history.Mutations);
        Assert.Contains("confirmed set to true", LastContent(fixture));
    }

    private static WarningModule CreateModule(OfflineInteractionFixture fixture, HistoryFixture history)
    {
        var module = new WarningModule(
            Proxy<IAuditChannelService>((_, _) => Task.CompletedTask),
            Proxy<IGuildChannelConfigurationStore>((method, _) => throw new InvalidOperationException($"Unexpected {method.Name}")),
            history.Store, NullLogger<WarningModule>.Instance);
        fixture.Attach(module);
        return module;
    }

    private static Task MutateAsync(WarningModule module, string action, Discord.WebSocket.SocketGuildUser member) =>
        action switch
        {
            "delete" => module.DeleteWarningAsync(1),
            "reason" => module.ReasonAsync(1, "Updated reason"),
            "clear" => module.ClearWarningsAsync(member, confirmed: true),
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };

    private static string LastContent(OfflineInteractionFixture fixture) =>
        JsonDocument.Parse(fixture.Requests.Last(request => request.Method == "PATCH").Json!).RootElement
            .GetProperty("content").GetString()!;

    private sealed class HistoryFixture
    {
        public IModerationHistoryStore Store { get; }
        public List<string> Operations { get; } = [];
        public List<string> Mutations { get; } = [];
        public List<object?[]> Cases { get; } = [];
        public ulong? LookupGuildId { get; private set; }
        public bool Missing { get; init; }

        public HistoryFixture(ulong targetId)
        {
            var warning = new MemberWarning(1, OfflineInteractionFixture.GuildId, targetId, 600, "Original reason", DateTimeOffset.UtcNow);
            Store = Proxy<IModerationHistoryStore>((method, args) =>
            {
                switch (method.Name)
                {
                    case nameof(IModerationHistoryStore.GetWarningAsync):
                        Operations.Add("lookup");
                        LookupGuildId = (ulong)args[0]!;
                        return Task.FromResult(Missing ? null : warning);
                    case nameof(IModerationHistoryStore.RemoveWarningAsync):
                        Operations.Add("delete");
                        Mutations.Add("delete");
                        return Task.FromResult<MemberWarning?>(warning);
                    case nameof(IModerationHistoryStore.ClearWarningsAsync):
                        Mutations.Add("clear");
                        return Task.FromResult(1);
                    case nameof(IModerationHistoryStore.UpdateWarningReasonAsync):
                        Operations.Add("reason");
                        Mutations.Add("reason");
                        return Task.FromResult<WarningReasonChange?>(new(warning with { Reason = (string)args[2]! }, warning.Reason));
                    case nameof(IModerationHistoryStore.RecordCaseAsync):
                        Cases.Add(args);
                        return Task.CompletedTask;
                    default:
                        throw new InvalidOperationException($"Unexpected {method.Name}");
                }
            });
        }
    }

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
