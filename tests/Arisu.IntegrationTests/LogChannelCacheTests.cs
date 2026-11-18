using Arisu.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Arisu.IntegrationTests;

public sealed class LogChannelCacheTests
{
    [Fact]
    public async Task ConcurrentReadsShareOneSlowRefreshAsync()
    {
        var cache = CreateCache();
        var result = NewResult();
        var reads = 0;
        Task<ulong?> LoadAsync(CancellationToken token)
        {
            Interlocked.Increment(ref reads);
            return result.Task;
        }

        var callers = Enumerable.Range(0, 50)
            .Select(_ => cache.GetAsync(1, LogChannelKind.Audit, LoadAsync))
            .ToArray();

        Assert.Equal(1, reads);
        Assert.All(callers, caller => Assert.False(caller.IsCompleted));
        result.SetResult(123);
        Assert.All(await Task.WhenAll(callers), value => Assert.Equal((ulong?)123, value));
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task UnconfiguredChannelsAreCachedSeparatelyForEachKindAsync()
    {
        var cache = CreateCache();
        var reads = 0;
        Task<ulong?> LoadAsync(CancellationToken token)
        {
            reads++;
            return Task.FromResult<ulong?>(null);
        }

        Assert.Null(await cache.GetAsync(1, LogChannelKind.Audit, LoadAsync));
        Assert.Null(await cache.GetAsync(1, LogChannelKind.Audit, LoadAsync));
        Assert.Null(await cache.GetAsync(1, LogChannelKind.Member, LoadAsync));
        Assert.Null(await cache.GetAsync(1, LogChannelKind.Member, LoadAsync));
        Assert.Equal(2, reads);
    }

    [Theory]
    [InlineData(456UL)]
    [InlineData(null)]
    public async Task SetWinsAgainstAnOlderRefreshAsync(ulong? updatedChannelId)
    {
        var cache = CreateCache();
        var oldRead = NewResult();
        var pending = cache.GetAsync(1, LogChannelKind.Audit, _ => oldRead.Task);

        cache.Set(1, LogChannelKind.Audit, updatedChannelId);
        oldRead.SetResult(123);

        Assert.Equal(updatedChannelId, await pending);
        Assert.Equal(updatedChannelId, await cache.GetAsync(
            1, LogChannelKind.Audit, _ => throw new InvalidOperationException("Unexpected reload")));
    }

    [Fact]
    public async Task SuccessfulSnapshotExpiresAfterFiveMinutesAsync()
    {
        var clock = new ManualTimeProvider();
        var cache = new LogChannelCache(NullLogger<LogChannelCache>.Instance, clock);
        cache.Set(1, LogChannelKind.Audit, 123);
        var reads = 0;
        Task<ulong?> LoadAsync(CancellationToken token)
        {
            reads++;
            return Task.FromResult<ulong?>(456);
        }

        clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        Assert.Equal((ulong?)123, await cache.GetAsync(1, LogChannelKind.Audit, LoadAsync));
        Assert.Equal(0, reads);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal((ulong?)456, await cache.GetAsync(1, LogChannelKind.Audit, LoadAsync));
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task SetWinsAgainstAFailedOlderRefreshWithoutCooldownAsync()
    {
        var clock = new ManualTimeProvider();
        var logger = new RecordingLogger();
        var cache = new LogChannelCache(logger, clock);
        var oldRead = NewResult();
        var pending = cache.GetAsync(1, LogChannelKind.Audit, _ => oldRead.Task);

        cache.Set(1, LogChannelKind.Audit, 456);
        oldRead.SetException(new InvalidOperationException("Old read failed"));
        Assert.Equal((ulong?)456, await pending);
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal((ulong?)456, await cache.GetAsync(
            1, LogChannelKind.Audit, _ => throw new InvalidOperationException("Unexpected reload")));
        Assert.Equal(0, logger.WarningCount);
    }

    [Fact]
    public async Task FailedInitialReadUsesCooldownAndThenRecoversAsync()
    {
        var clock = new ManualTimeProvider();
        var logger = new RecordingLogger();
        var cache = new LogChannelCache(logger, clock);
        var reads = 0;
        Task<ulong?> LoadAsync(CancellationToken token)
        {
            reads++;
            return reads == 1
                ? Task.FromException<ulong?>(new InvalidOperationException("Database unavailable"))
                : Task.FromResult<ulong?>(123);
        }

        Assert.Null(await cache.GetAsync(1, LogChannelKind.Audit, LoadAsync));
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Null(await cache.GetAsync(1, LogChannelKind.Audit, LoadAsync));
        Assert.Equal(1, reads);
        Assert.Equal(1, logger.WarningCount);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal((ulong?)123, await cache.GetAsync(1, LogChannelKind.Audit, LoadAsync));
        Assert.Equal(2, reads);
        Assert.Equal(1, logger.WarningCount);
    }

    [Theory]
    [InlineData(123UL)]
    [InlineData(null)]
    public async Task FailedRefreshPreservesSuccessfulSnapshotUntilRecoveryAsync(ulong? originalChannelId)
    {
        var clock = new ManualTimeProvider();
        var logger = new RecordingLogger();
        var cache = new LogChannelCache(logger, clock);
        cache.Set(1, LogChannelKind.Audit, originalChannelId);
        clock.Advance(TimeSpan.FromMinutes(5));
        var reads = 0;
        Task<ulong?> LoadAsync(CancellationToken token)
        {
            reads++;
            return reads == 1
                ? Task.FromException<ulong?>(new InvalidOperationException("Database unavailable"))
                : Task.FromResult<ulong?>(456);
        }

        Assert.Equal(originalChannelId, await cache.GetAsync(1, LogChannelKind.Audit, LoadAsync));
        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(originalChannelId, await cache.GetAsync(1, LogChannelKind.Audit, LoadAsync));
        }

        Assert.Equal(1, reads);
        Assert.Equal(1, logger.WarningCount);
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal((ulong?)456, await cache.GetAsync(1, LogChannelKind.Audit, LoadAsync));
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task CancelingOneReaderDoesNotCancelTheSharedRefreshAsync()
    {
        var cache = CreateCache();
        using var callerCancellation = new CancellationTokenSource();
        var result = NewResult();
        CancellationToken databaseToken = default;
        var first = cache.GetAsync(1, LogChannelKind.Audit, token =>
        {
            databaseToken = token;
            return result.Task;
        }, callerCancellation.Token);
        var second = cache.GetAsync(1, LogChannelKind.Audit, _ => throw new InvalidOperationException());

        callerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(databaseToken.IsCancellationRequested);
        result.SetResult(123);
        Assert.Equal((ulong?)123, await second);
    }

    [Fact]
    public async Task CancelingTheLastReaderCancelsDatabaseWorkAndAllowsANewReadAsync()
    {
        var logger = new RecordingLogger();
        var cache = new LogChannelCache(logger);
        using var callerCancellation = new CancellationTokenSource();
        CancellationToken databaseToken = default;
        var pending = cache.GetAsync(1, LogChannelKind.Audit, token =>
        {
            databaseToken = token;
            return NewResult().Task;
        }, callerCancellation.Token);

        callerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(databaseToken.IsCancellationRequested);
        Assert.Equal((ulong?)456, await cache.GetAsync(
            1, LogChannelKind.Audit, _ => Task.FromResult<ulong?>(456)));
        Assert.Equal(0, logger.WarningCount);
    }

    [Fact]
    public async Task RefreshTimesOutAfterTenSecondsEvenIfLoaderIgnoresCancellationAsync()
    {
        var clock = new ManualTimeProvider();
        var logger = new RecordingLogger();
        var cache = new LogChannelCache(logger, clock);
        var reads = 0;
        var pending = cache.GetAsync(1, LogChannelKind.Audit, _ =>
        {
            reads++;
            return NewResult().Task;
        });

        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, logger.WarningCount);
        Assert.Null(await cache.GetAsync(1, LogChannelKind.Audit, _ =>
        {
            reads++;
            return Task.FromResult<ulong?>(123);
        }));
        Assert.Equal(1, reads);
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal((ulong?)123, await cache.GetAsync(
            1, LogChannelKind.Audit, _ => Task.FromResult<ulong?>(123)));
    }

    [Fact]
    public async Task CapacityEvictsOldEntriesWithoutEvictingActiveRefreshesAsync()
    {
        var cache = CreateCache();
        var slowResult = NewResult();
        var pending = cache.GetAsync(1, LogChannelKind.Audit, _ => slowResult.Task);
        for (ulong guildId = 2; guildId <= 1025; guildId++)
        {
            cache.Set(guildId, LogChannelKind.Audit, guildId);
        }

        var shared = cache.GetAsync(1, LogChannelKind.Audit, _ => throw new InvalidOperationException());
        slowResult.SetResult(123);
        Assert.Equal((ulong?)123, await pending);
        Assert.Equal((ulong?)123, await shared);

        var reloaded = false;
        Assert.Equal((ulong?)456, await cache.GetAsync(2, LogChannelKind.Audit, _ =>
        {
            reloaded = true;
            return Task.FromResult<ulong?>(456);
        }));
        Assert.True(reloaded);
    }

    [Fact]
    public async Task FullCacheWaitsForActiveRefreshCapacityAndSupportsCancellationAsync()
    {
        var cache = CreateCache();
        var results = Enumerable.Range(0, 1024).Select(_ => NewResult()).ToArray();
        var pending = results.Select((result, index) => cache.GetAsync(
            (ulong)index, LogChannelKind.Audit, _ => result.Task)).ToArray();
        using var cancellation = new CancellationTokenSource();
        var canceled = cache.GetAsync(2048, LogChannelKind.Audit,
            _ => throw new InvalidOperationException("No slot available"), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);

        var waiting = cache.GetAsync(1024, LogChannelKind.Audit, _ => Task.FromResult<ulong?>(456));
        Assert.False(waiting.IsCompleted);
        results[0].SetResult(123);
        Assert.Equal((ulong?)123, await pending[0]);
        Assert.Equal((ulong?)456, await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
        foreach (var result in results.Skip(1))
        {
            result.SetResult(123);
        }

        await Task.WhenAll(pending);
    }

    private static LogChannelCache CreateCache() => new(NullLogger<LogChannelCache>.Instance);

    private static TaskCompletionSource<ulong?> NewResult() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class RecordingLogger : ILogger<LogChannelCache>
    {
        private int _warningCount;
        public int WarningCount => Volatile.Read(ref _warningCount);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Interlocked.Increment(ref _warningCount);
            }
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;
        private readonly List<ManualTimer> _timers = [];

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, dueTime);
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan elapsed)
        {
            _utcNow += elapsed;
            foreach (var timer in _timers.ToArray())
            {
                timer.FireIfDue();
            }
        }

        private sealed class ManualTimer(
            ManualTimeProvider clock,
            TimerCallback callback,
            object? state,
            TimeSpan dueTime) : ITimer
        {
            private DateTimeOffset _dueAt = clock.GetUtcNow() + dueTime;
            private bool _disposed;

            public bool Change(TimeSpan newDueTime, TimeSpan period)
            {
                _dueAt = newDueTime == Timeout.InfiniteTimeSpan
                    ? DateTimeOffset.MaxValue
                    : clock.GetUtcNow() + newDueTime;
                return !_disposed;
            }

            public void FireIfDue()
            {
                if (!_disposed && clock.GetUtcNow() >= _dueAt)
                {
                    _dueAt = DateTimeOffset.MaxValue;
                    callback(state);
                }
            }

            public void Dispose() => _disposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
