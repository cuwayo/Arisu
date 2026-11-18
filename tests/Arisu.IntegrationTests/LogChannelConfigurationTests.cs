using System.Collections.Concurrent;
using System.Reflection;
using Arisu.Services;
using Arisu.Storage;
using Discord.WebSocket;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Arisu.IntegrationTests;

public sealed class LogChannelConfigurationTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(LogChannelKind.Audit)]
    [InlineData(LogChannelKind.Member)]
    public async Task RemoveWaitsForCommittedSetterToPublishAcrossServiceInstancesAsync(LogChannelKind kind)
    {
        using var fixture = new ConfigurationFixture();
        var setter = fixture.SetAsync(1, kind, 123);
        await fixture.Store.FirstSetCommitted.Task.WaitAsync(TestTimeout);
        Task<bool>? remover = null;
        try
        {
            Assert.Equal((ulong?)123, fixture.Store.GetChannelId(1, kind));
            Assert.False(setter.IsCompleted);
            remover = fixture.RemoveAsync(1, kind);

            Assert.False(remover.IsCompleted);
            Assert.Equal(0, fixture.Store.RemoveCount);
        }
        finally
        {
            fixture.Store.ReleaseFirstSet.TrySetResult();
            await Task.WhenAll(setter, remover ?? Task.FromResult(false)).WaitAsync(TestTimeout);
        }

        Assert.True(await setter);
        Assert.True(await remover!);
        Assert.Equal(1, fixture.Store.RemoveCount);
        Assert.Null(fixture.Store.GetChannelId(1, kind));
        Assert.Null(await fixture.GetCachedChannelIdAsync(1, kind));
    }

    [Theory]
    [InlineData(LogChannelKind.Audit)]
    [InlineData(LogChannelKind.Member)]
    public async Task LaterSetterWaitsForCommittedSetterAndPublishesFinalRoutingAsync(LogChannelKind kind)
    {
        using var fixture = new ConfigurationFixture();
        var first = fixture.SetAsync(1, kind, 123);
        await fixture.Store.FirstSetCommitted.Task.WaitAsync(TestTimeout);
        Task<bool>? second = null;
        try
        {
            second = fixture.SetAsync(1, kind, 456, useSecondInstance: true);

            Assert.False(second.IsCompleted);
            Assert.Equal(1, fixture.Store.SetCount);
            Assert.Equal((ulong?)123, fixture.Store.GetChannelId(1, kind));
        }
        finally
        {
            fixture.Store.ReleaseFirstSet.TrySetResult();
            await Task.WhenAll(first, second ?? Task.FromResult(false)).WaitAsync(TestTimeout);
        }

        Assert.True(await first);
        Assert.True(await second!);
        Assert.Equal(2, fixture.Store.SetCount);
        Assert.Equal((ulong?)456, fixture.Store.GetChannelId(1, kind));
        Assert.Equal((ulong?)456, await fixture.GetCachedChannelIdAsync(1, kind));
    }

    [Theory]
    [InlineData(1UL, LogChannelKind.Member)]
    [InlineData(2UL, LogChannelKind.Audit)]
    public async Task PendingSetterDoesNotBlockAnotherKindOrNoncollidingGuildAsync(
        ulong otherGuildId,
        LogChannelKind otherKind)
    {
        using var fixture = new ConfigurationFixture();
        var first = fixture.SetAsync(1, LogChannelKind.Audit, 123);
        await fixture.Store.FirstSetCommitted.Task.WaitAsync(TestTimeout);
        Task<bool>? other = null;
        try
        {
            other = fixture.SetAsync(otherGuildId, otherKind, 456);
            Assert.True(await other.WaitAsync(TestTimeout));
            Assert.False(first.IsCompleted);
            Assert.Equal((ulong?)456, await fixture.GetCachedChannelIdAsync(otherGuildId, otherKind));
        }
        finally
        {
            fixture.Store.ReleaseFirstSet.TrySetResult();
            await Task.WhenAll(first, other ?? Task.FromResult(false)).WaitAsync(TestTimeout);
        }

        Assert.True(await first);
    }

    [Theory]
    [InlineData(LogChannelKind.Audit, false)]
    [InlineData(LogChannelKind.Audit, true)]
    [InlineData(LogChannelKind.Member, false)]
    [InlineData(LogChannelKind.Member, true)]
    public async Task FailedWriteRetainsCacheAndReleasesCoordinationAsync(LogChannelKind kind, bool remove)
    {
        using var fixture = new ConfigurationFixture();
        fixture.Store.DelayFirstSet = false;
        fixture.Store.Seed(1, kind, 123);
        fixture.Cache.Set(1, kind, 123);
        fixture.Store.FailNextWrite = true;

        var result = remove
            ? await fixture.RemoveAsync(1, kind)
            : await fixture.SetAsync(1, kind, 456);

        Assert.False(result);
        Assert.Equal((ulong?)123, fixture.Store.GetChannelId(1, kind));
        Assert.Equal((ulong?)123, await fixture.GetCachedChannelIdAsync(1, kind));
        Assert.True(await fixture.SetAsync(1, kind, 789, useSecondInstance: true).WaitAsync(TestTimeout));
        Assert.Equal((ulong?)789, fixture.Store.GetChannelId(1, kind));
        Assert.Equal((ulong?)789, await fixture.GetCachedChannelIdAsync(1, kind));
        Assert.True(await fixture.RemoveAsync(1, kind).WaitAsync(TestTimeout));
        Assert.Null(fixture.Store.GetChannelId(1, kind));
        Assert.Null(await fixture.GetCachedChannelIdAsync(1, kind));
    }

    private sealed class ConfigurationFixture : IDisposable
    {
        private readonly DiscordSocketClient _client = new();
        private readonly AuditChannelService[] _auditServices;
        private readonly MemberLogChannelService[] _memberServices;

        public ConfigurationFixture()
        {
            var store = DispatchProxy.Create<IGuildChannelConfigurationStore, DelayedConfigurationStore>();
            Store = (DelayedConfigurationStore)(object)store;
            _auditServices = Enumerable.Range(0, 2).Select(_ => new AuditChannelService(
                _client, store, Cache, NullLogger<AuditChannelService>.Instance)).ToArray();
            _memberServices = Enumerable.Range(0, 2).Select(_ => new MemberLogChannelService(
                _client, store, Cache, NullLogger<MemberLogChannelService>.Instance)).ToArray();
        }

        public DelayedConfigurationStore Store { get; }
        public LogChannelCache Cache { get; } = new(NullLogger<LogChannelCache>.Instance);

        public Task<bool> SetAsync(ulong guildId, LogChannelKind kind, ulong channelId, bool useSecondInstance = false)
        {
            var index = useSecondInstance ? 1 : 0;
            return kind == LogChannelKind.Audit
                ? _auditServices[index].SetAuditChannelAsync(guildId, channelId)
                : _memberServices[index].SetMemberLogChannelAsync(guildId, channelId);
        }

        public Task<bool> RemoveAsync(ulong guildId, LogChannelKind kind) => kind == LogChannelKind.Audit
            ? _auditServices[1].RemoveAuditChannelAsync(guildId)
            : _memberServices[1].RemoveMemberLogChannelAsync(guildId);

        public async Task<ulong?> GetCachedChannelIdAsync(ulong guildId, LogChannelKind kind)
        {
            var loads = 0;
            var value = await Cache.GetAsync(guildId, kind, _ =>
            {
                loads++;
                return Task.FromResult<ulong?>(999);
            });
            Assert.Equal(0, loads);
            return value;
        }

        public void Dispose() => _client.Dispose();
    }

    public class DelayedConfigurationStore : DispatchProxy
    {
        private readonly ConcurrentDictionary<(ulong GuildId, LogChannelKind Kind), ulong> _channels = new();
        private int _setCount;
        private int _removeCount;

        public TaskCompletionSource FirstSetCommitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstSet { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool DelayFirstSet { get; set; } = true;
        public bool FailNextWrite { get; set; }
        public int SetCount => Volatile.Read(ref _setCount);
        public int RemoveCount => Volatile.Read(ref _removeCount);

        public void Seed(ulong guildId, LogChannelKind kind, ulong channelId) => _channels[(guildId, kind)] = channelId;

        public ulong? GetChannelId(ulong guildId, LogChannelKind kind) =>
            _channels.TryGetValue((guildId, kind), out var channelId) ? channelId : null;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var method = targetMethod!.Name;
            var kind = method switch
            {
                nameof(IGuildChannelConfigurationStore.GetAuditChannelIdAsync)
                    or nameof(IGuildChannelConfigurationStore.SetAuditChannelIdAsync)
                    or nameof(IGuildChannelConfigurationStore.RemoveAuditChannelIdAsync) => LogChannelKind.Audit,
                nameof(IGuildChannelConfigurationStore.GetMemberLogChannelIdAsync)
                    or nameof(IGuildChannelConfigurationStore.SetMemberLogChannelIdAsync)
                    or nameof(IGuildChannelConfigurationStore.RemoveMemberLogChannelIdAsync) => LogChannelKind.Member,
                _ => throw new NotSupportedException(method)
            };
            var guildId = (ulong)args![0]!;
            if (method.StartsWith("Get", StringComparison.Ordinal))
            {
                return Task.FromResult(GetChannelId(guildId, kind));
            }

            if (FailNextWrite)
            {
                FailNextWrite = false;
                return Task.FromException(new InvalidOperationException("Database unavailable"));
            }

            if (method.StartsWith("Remove", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _removeCount);
                _channels.TryRemove((guildId, kind), out _);
                return Task.CompletedTask;
            }

            var count = Interlocked.Increment(ref _setCount);
            _channels[(guildId, kind)] = (ulong)args[1]!;
            if (DelayFirstSet && count == 1)
            {
                // The database has committed, but the service cannot publish until this task completes.
                FirstSetCommitted.TrySetResult();
                return ReleaseFirstSet.Task;
            }

            return Task.CompletedTask;
        }
    }
}
