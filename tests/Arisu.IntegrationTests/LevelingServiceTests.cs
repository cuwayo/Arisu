using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Reflection;
using Arisu.Services;
using Arisu.Storage;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Arisu.IntegrationTests;

public sealed class LevelingServiceTests
{
    [Fact]
    public async Task GatewayCallbackSnapshotsRolesAndMessageTimeWithoutWaitingForStorage()
    {
        using var fixture = new LevelingFixture();
        var entered = NewSignal();
        var release = NewSignal();
        var processed = NewSignal();
        var attempts = new ConcurrentQueue<AwardAttempt>();
        fixture.AwardHandler = async attempt =>
        {
            attempts.Enqueue(attempt);
            if (attempts.Count == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(attempt.Token);
            }
            else
            {
                processed.TrySetResult();
            }
            return null;
        };
        await fixture.Service.StartAsync(default);
        try
        {
            await fixture.DeliverAsync(fixture.CreateMessage());
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var message = fixture.CreateMessage();
            var timestamp = message.Timestamp;
            var roles = fixture.Member.Roles.Select(role => role.Id).Append(fixture.Guild.Id).Distinct().ToArray();
            var delivery = fixture.DeliverAsync(message);
            Assert.True(delivery.IsCompletedSuccessfully);
            Assert.Single(attempts);
            SetField(fixture.Member, "_roleIds", ImmutableArray<ulong>.Empty);
            SetField(message, "_timestampTicks", DateTimeOffset.UtcNow.UtcTicks);
            release.TrySetResult();
            await processed.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var attempt = attempts.Last();
            Assert.Equal(OfflineInteractionFixture.GuildId, attempt.GuildId);
            Assert.Equal(fixture.Member.Id, attempt.UserId);
            Assert.Equal(600UL, attempt.ChannelId);
            Assert.Equal(timestamp, attempt.Timestamp);
            Assert.Equal(TimeSpan.Zero, attempt.Timestamp.Offset);
            Assert.Equal(roles, attempt.RoleIds);
            Assert.Contains(OfflineInteractionFixture.GuildId, attempt.RoleIds);
            Assert.All(attempts, captured => Assert.InRange(captured.Xp, 15, 25));
            Assert.Equal(0, fixture.SettingsReads);
        }
        finally
        {
            await fixture.Service.StopAsync(default);
        }
    }

    [Fact]
    public async Task FullQueueDropsNewestMessagesAndShutdownCancelsWithoutDraining()
    {
        using var fixture = new LevelingFixture();
        var entered = NewSignal();
        var cancelled = NewSignal();
        var attempts = 0;
        fixture.AwardHandler = async attempt =>
        {
            Interlocked.Increment(ref attempts);
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, attempt.Token);
            }
            catch (OperationCanceledException) when (attempt.Token.IsCancellationRequested)
            {
                cancelled.TrySetResult();
                throw;
            }
            return null;
        };
        await fixture.Service.StartAsync(default);
        await fixture.DeliverAsync(fixture.CreateMessage());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var index = 0; index < 1024; index++)
        {
            Assert.True(fixture.DeliverAsync(fixture.CreateMessage()).IsCompletedSuccessfully);
        }
        Assert.Empty(fixture.Logger.Warnings);
        for (var index = 0; index < 9; index++)
        {
            Assert.True(fixture.DeliverAsync(fixture.CreateMessage()).IsCompletedSuccessfully);
        }
        Assert.Equal([1L, 2L, 4L, 8L], fixture.Logger.Warnings.Select(warning => warning.DroppedCount));
        await fixture.Service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.DeliverAsync(fixture.CreateMessage());
        Assert.Equal(1, attempts);
        Assert.Equal(0, fixture.SettingsReads);
        Assert.DoesNotContain(GetMessageSubscribers(fixture.Client), subscriber => subscriber.Target == fixture.Service);
    }

    [Theory]
    [InlineData(MessageSource.Bot)]
    [InlineData(MessageSource.Webhook)]
    [InlineData(MessageSource.System)]
    public async Task NonuserSourcesAreIgnored(MessageSource source)
    {
        using var fixture = new LevelingFixture();
        await fixture.Service.StartAsync(default);
        try
        {
            await fixture.DeliverAsync(fixture.CreateMessage(source));
            Assert.Equal(0, PendingCount(fixture.Service));
            Assert.Equal(0, fixture.AwardCalls);
        }
        finally
        {
            await fixture.Service.StopAsync(default);
        }
    }

    [Fact]
    public async Task BotAuthorsAndNonguildChannelsAreIgnoredEvenWithUserSource()
    {
        using var fixture = new LevelingFixture();
        await fixture.Service.StartAsync(default);
        try
        {
            typeof(SocketGuildUser).GetProperty(nameof(SocketGuildUser.IsBot))!.SetValue(fixture.Member, true);
            await fixture.DeliverAsync(fixture.CreateMessage());
            typeof(SocketGuildUser).GetProperty(nameof(SocketGuildUser.IsBot))!.SetValue(fixture.Member, false);
            var message = fixture.CreateMessage();
            SetField(message, "<Channel>k__BackingField", null);
            await fixture.DeliverAsync(message);
            Assert.Equal(0, PendingCount(fixture.Service));
            Assert.Equal(0, fixture.AwardCalls);
        }
        finally
        {
            await fixture.Service.StopAsync(default);
        }
    }

    [Fact]
    public async Task AcceptedMessagesRetryAllMissingEligibleRewardsAfterPersistingXp()
    {
        using var fixture = new LevelingFixture();
        fixture.AddReward(1, 501, 2);
        fixture.AddReward(2, 502, 3);
        fixture.AddReward(3, 503, 4);
        fixture.AwardHandler = _ => Task.FromResult<LevelingAward?>(fixture.Award(450, 430));
        fixture.RestCallback = request =>
        {
            Assert.Equal(1, fixture.AwardCalls);
            Assert.Equal(1, fixture.SettingsReads);
            Assert.Equal("PUT", request.Method);
            return request.Endpoint.EndsWith("/501", StringComparison.Ordinal)
                ? OfflineInteractionFixture.JsonResponse("{\"code\":50013,\"message\":\"Missing Permissions\"}", HttpStatusCode.Forbidden)
                : OfflineInteractionFixture.JsonResponse("", HttpStatusCode.NoContent);
        };

        await fixture.ProcessAsync();

        Assert.Equal(2, fixture.Requests.Count);
        Assert.EndsWith("/501", fixture.Requests[0].Endpoint);
        Assert.EndsWith("/502", fixture.Requests[1].Endpoint);
        Assert.Single(fixture.Logger.Warnings);
        // The gateway cache now contains the successful reward, but not the failed one.
        SetField(fixture.Member, "_roleIds", ImmutableArray.Create(1001UL, 502UL));
        fixture.RestCallback = request => OfflineInteractionFixture.JsonResponse("", HttpStatusCode.NoContent);
        await fixture.ProcessAsync();

        Assert.Equal(3, fixture.Requests.Count);
        Assert.EndsWith("/501", fixture.Requests[2].Endpoint);
        Assert.DoesNotContain(fixture.Requests, request => request.Endpoint.EndsWith("/503", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnsafeManagedEveryoneMissingAndUnassignableRewardsAreSkipped()
    {
        using var fixture = new LevelingFixture();
        fixture.AddReward(1, OfflineInteractionFixture.GuildId, 0);
        fixture.AddReward(1, 501, 1, managed: true);
        fixture.AddReward(1, 502, 20);
        fixture.AddReward(1, 503, 21);
        fixture.AddReward(1, 504, 1, permissions: GuildPermission.Administrator);
        fixture.AddReward(1, 505, 1, permissions: GuildPermission.ManageMessages);
        fixture.AddReward(1, 506, 1);
        fixture.AddReward(1, 507, 1);
        SetField(fixture.Member, "_roleIds", ImmutableArray.Create(1001UL, 506UL));
        fixture.Rewards.Add(new LevelRoleReward(1, 999));
        fixture.AwardHandler = _ => Task.FromResult<LevelingAward?>(fixture.Award(100, 80));
        fixture.RestCallback = _ => OfflineInteractionFixture.JsonResponse("", HttpStatusCode.NoContent);

        await fixture.ProcessAsync();

        var request = Assert.Single(fixture.Requests);
        Assert.EndsWith("/507", request.Endpoint);
    }

    [Fact]
    public async Task RoleWithUnsafeChannelOverwriteIsSkippedButDeniedPermissionIsSafe()
    {
        using var fixture = new LevelingFixture();
        fixture.AddReward(1, 501, 1);
        fixture.AddReward(1, 502, 2);
        SetField(fixture.Channel, "_overwrites", ImmutableArray.Create(
            new Overwrite(501, PermissionTarget.Role, new OverwritePermissions(manageMessages: PermValue.Allow)),
            new Overwrite(502, PermissionTarget.Role, new OverwritePermissions(manageMessages: PermValue.Deny))));
        fixture.AwardHandler = _ => Task.FromResult<LevelingAward?>(fixture.Award(100, 80));
        fixture.RestCallback = _ => OfflineInteractionFixture.JsonResponse("", HttpStatusCode.NoContent);

        await fixture.ProcessAsync();

        var request = Assert.Single(fixture.Requests);
        Assert.EndsWith("/502", request.Endpoint);
    }

    [Theory]
    [InlineData("disconnected")]
    [InlineData("unavailable")]
    [InlineData("missing member")]
    [InlineData("missing bot permission")]
    public async Task ChangedGuildOrMemberStatePreventsRewardRequests(string change)
    {
        using var fixture = new LevelingFixture();
        fixture.AddReward(1, 501, 1);
        fixture.AwardHandler = async _ =>
        {
            // The state changes while persistence is in flight.
            switch (change)
            {
                case "disconnected":
                    await fixture.InvokeAsync("OnDisconnectedAsync", new IOException("Offline disconnect"));
                    break;
                case "unavailable":
                    await fixture.InvokeAsync("OnGuildUnavailableAsync", fixture.Guild);
                    break;
                case "missing member":
                    fixture.RemoveMember();
                    break;
                case "missing bot permission":
                    fixture.Guild.GetRole(1020).GetType().GetProperty(nameof(SocketRole.Permissions))!
                        .SetValue(fixture.Guild.GetRole(1020), GuildPermissions.None);
                    break;
            }
            return fixture.Award(100, 80);
        };

        await fixture.ProcessAsync();

        Assert.Equal(1, fixture.AwardCalls);
        Assert.Equal(1, fixture.SettingsReads);
        Assert.Empty(fixture.Requests);
    }

    [Fact]
    public async Task DisablingLevelingDuringAnAwardPreventsRewardRequests()
    {
        using var fixture = new LevelingFixture();
        fixture.AddReward(1, 501, 1);
        fixture.AwardHandler = _ =>
        {
            fixture.Enabled = false;
            return Task.FromResult<LevelingAward?>(fixture.Award(100, 80));
        };

        await fixture.ProcessAsync();

        Assert.Equal(1, fixture.AwardCalls);
        Assert.Equal(1, fixture.SettingsReads);
        Assert.Empty(fixture.Requests);
    }

    [Fact]
    public async Task RoleRequestCancellationPropagatesInsteadOfTryingAnotherReward()
    {
        using var fixture = new LevelingFixture();
        fixture.AddReward(1, 501, 1);
        fixture.AddReward(1, 502, 2);
        fixture.AwardHandler = _ => Task.FromResult<LevelingAward?>(fixture.Award(100, 80));
        fixture.RestCallback = _ => throw new OperationCanceledException();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.ProcessAsync());

        Assert.Single(fixture.Requests);
        Assert.Empty(fixture.Logger.Warnings);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static IEnumerable<Delegate> GetMessageSubscribers(DiscordSocketClient client)
    {
        var eventField = typeof(BaseSocketClient).GetField("_messageReceivedEvent", InstanceMembers)!;
        var asyncEvent = eventField.GetValue(client)!;
        return (IEnumerable<Delegate>)asyncEvent.GetType().GetProperty("Subscriptions", InstanceMembers)!.GetValue(asyncEvent)!;
    }

    private static int PendingCount(LevelingService service)
    {
        var queue = typeof(LevelingService).GetField("_pending", InstanceMembers)!.GetValue(service)!;
        var reader = queue.GetType().GetProperty("Reader")!.GetValue(queue)!;
        return (int)reader.GetType().GetProperty("Count")!.GetValue(reader)!;
    }

    private static void SetField(object target, string name, object? value)
    {
        for (var type = target.GetType(); type is not null; type = type.BaseType)
        {
            if (type.GetField(name, InstanceMembers | BindingFlags.DeclaredOnly) is { } field)
            {
                field.SetValue(target, value);
                return;
            }
        }
        throw new InvalidOperationException($"No field {name}.");
    }

    private static T Construct<T>(params object[] args) => (T)Activator.CreateInstance(typeof(T), InstanceMembers,
        binder: null, args: args, culture: null)!;

    private sealed class LevelingFixture : IDisposable
    {
        private readonly OfflineInteractionFixture _discord = new(botHierarchy: 20);

        public LevelingFixture()
        {
            AddRole(OfflineInteractionFixture.GuildId, 0);
            typeof(SocketRole).GetProperty(nameof(SocketRole.Permissions))!.SetValue(
                Guild.GetRole(1020), new GuildPermissions(manageRoles: true));
            var state = typeof(DiscordSocketClient).GetProperty("State", InstanceMembers)!.GetValue(Client)!;
            state.GetType().GetMethod("AddGuild", InstanceMembers)!.Invoke(state, [Guild]);
            var connection = typeof(DiscordSocketClient).GetField("_connection", InstanceMembers)!.GetValue(Client)!;
            connection.GetType().GetProperty("State", InstanceMembers)!.SetValue(connection, ConnectionState.Connected);
            Channel = Construct<SocketTextChannel>(Client, 600UL, Guild);
            SetField(Channel, "_overwrites", ImmutableArray<Overwrite>.Empty);
            SetField(Guild, "_channels", new ConcurrentDictionary<ulong, SocketGuildChannel>
            {
                [Channel.Id] = Channel
            });
            Member = _discord.CreateMember(700, 1);
            var store = DispatchProxy.Create<ILevelingStore, StoreProxy>();
            ((StoreProxy)(object)store).Handler = (method, args) =>
            {
                switch (method.Name)
                {
                    case nameof(ILevelingStore.TryAwardAsync):
                        AwardCalls++;
                        return AwardHandler(new AwardAttempt((ulong)args[0]!, (ulong)args[1]!,
                            (ulong)args[2]!, (IReadOnlyCollection<ulong>)args[3]!, (int)args[4]!,
                            (DateTimeOffset)args[5]!, (CancellationToken)args[6]!));
                    case nameof(ILevelingStore.GetSettingsAsync):
                        SettingsReads++;
                        return Task.FromResult(new LevelingSettings(Guild.Id, Enabled, 60, [], [], Rewards.ToArray()));
                    default:
                        throw new InvalidOperationException($"Unexpected store method: {method.Name}");
                }
            };
            Service = new LevelingService(Client, store, Logger);
            InvokeAsync("OnGuildAvailableAsync", Guild).GetAwaiter().GetResult();
        }

        public DiscordSocketClient Client => _discord.Client;
        public SocketGuild Guild => _discord.Guild;
        public SocketGuildUser Member { get; }
        public SocketTextChannel Channel { get; }
        public LevelingService Service { get; }
        public WarningLogger Logger { get; } = new();
        public int AwardCalls { get; private set; }
        public int SettingsReads { get; private set; }
        public bool Enabled { get; set; } = true;
        public List<LevelRoleReward> Rewards { get; } = [];
        public List<OfflineRestRequest> Requests => _discord.Requests;
        public Func<OfflineRestRequest, Discord.Net.Rest.RestResponse?>? RestCallback
        {
            set => _discord.RestCallback = value;
        }
        public Func<AwardAttempt, Task<LevelingAward?>> AwardHandler { get; set; } = _ => Task.FromResult<LevelingAward?>(null);

        public void AddReward(int level, ulong roleId, int position, bool managed = false,
            GuildPermission permissions = 0)
        {
            var role = AddRole(roleId, position);
            typeof(SocketRole).GetProperty(nameof(SocketRole.IsManaged))!.SetValue(role, managed);
            typeof(SocketRole).GetProperty(nameof(SocketRole.Permissions))!.SetValue(role, new GuildPermissions((ulong)permissions));
            Rewards.Add(new LevelRoleReward(level, roleId));
        }

        private SocketRole AddRole(ulong id, int position)
        {
            var role = Construct<SocketRole>(Guild, id);
            typeof(SocketRole).GetProperty(nameof(SocketRole.Position))!.SetValue(role, position);
            var roles = (ConcurrentDictionary<ulong, SocketRole>)typeof(SocketGuild).GetField("_roles", InstanceMembers)!.GetValue(Guild)!;
            roles[id] = role;
            return role;
        }

        public SocketUserMessage CreateMessage(MessageSource source = MessageSource.User)
        {
            var message = Construct<SocketUserMessage>(Client, 800UL, Channel, Member, source);
            SetField(message, "_timestampTicks", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks);
            return message;
        }

        public Task DeliverAsync(SocketMessage message) => InvokeAsync("OnMessageReceivedAsync", message);

        public Task InvokeAsync(string name, params object[] args) =>
            (Task)typeof(LevelingService).GetMethod(name, InstanceMembers)!.Invoke(Service, args)!;

        public LevelingAward Award(long totalXp, long previousXp) =>
            new(new LevelingMember(Guild.Id, Member.Id, totalXp, 10, DateTimeOffset.UtcNow), previousXp);

        public Task ProcessAsync()
        {
            var pendingType = typeof(LevelingService).GetNestedType("PendingMessage", BindingFlags.NonPublic)!;
            var pending = Activator.CreateInstance(pendingType, InstanceMembers, binder: null,
                args: [Guild.Id, Member.Id, Channel.Id, Member.Roles.Select(role => role.Id).ToImmutableArray(), CreateMessage().Timestamp], culture: null)!;
            return InvokeAsync("ProcessMessageAsync", pending, CancellationToken.None);
        }

        public void RemoveMember() => _discord.RemoveCachedMember(Member.Id);

        public void Dispose()
        {
            Service.Dispose();
            _discord.Dispose();
        }
    }

    private sealed record AwardAttempt(ulong GuildId, ulong UserId, ulong ChannelId,
        IReadOnlyCollection<ulong> RoleIds, int Xp, DateTimeOffset Timestamp, CancellationToken Token);

    private sealed class WarningLogger : ILogger<LevelingService>
    {
        public ConcurrentQueue<(string Message, long? DroppedCount)> Warnings { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                var values = (IEnumerable<KeyValuePair<string, object?>>)(object)state!;
                var dropped = values.FirstOrDefault(value => value.Key == "DroppedCount").Value;
                Warnings.Enqueue((formatter(state, exception), dropped as long?));
            }
        }
    }

    public class StoreProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args ?? []);
    }
}
