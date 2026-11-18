using Microsoft.Extensions.Logging;

namespace Arisu.Services;

public enum LogChannelKind
{
    Audit,
    Member
}

public sealed class LogChannelCache
{
    private const int MaximumEntries = 1024;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(10);
    private readonly object _sync = new();
    private readonly Dictionary<(ulong GuildId, LogChannelKind Kind), Entry> _entries = [];
    private readonly LinkedList<(ulong GuildId, LogChannelKind Kind)> _recency = [];
    private readonly ILogger<LogChannelCache> _logger;
    private readonly TimeProvider _timeProvider;
    private TaskCompletionSource _capacityChanged = NewSignal();

    public LogChannelCache(ILogger<LogChannelCache> logger, TimeProvider? timeProvider = null)
    {
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ulong?> GetAsync(
        ulong guildId,
        LogChannelKind kind,
        Func<CancellationToken, Task<ulong?>> load,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(load);
        var key = (guildId, kind);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task? capacityChanged = null;
            Refresh? refresh = null;
            Entry? entry;
            var startRefresh = false;

            lock (_sync)
            {
                if (!_entries.TryGetValue(key, out entry))
                {
                    if (!MakeRoom())
                    {
                        capacityChanged = _capacityChanged.Task;
                    }
                    else
                    {
                        entry = AddEntry(key);
                    }
                }

                if (entry is not null)
                {
                    Touch(entry);
                    if (_timeProvider.GetUtcNow() < entry.RefreshAfter)
                    {
                        return entry.HasSnapshot ? entry.ChannelId : null;
                    }

                    refresh = entry.Refresh;
                    if (refresh is null)
                    {
                        refresh = new Refresh(
                            new CancellationTokenSource(RefreshTimeout, _timeProvider), entry.Version);
                        entry.Refresh = refresh;
                        startRefresh = true;
                    }

                    refresh.Waiters++;
                }
            }

            if (capacityChanged is not null)
            {
                await capacityChanged.WaitAsync(cancellationToken);
                continue;
            }

            if (startRefresh)
            {
                // The refresh has its own deadline; one canceled caller must not cancel other readers.
                _ = RefreshAsync(key, entry!, refresh!, load);
            }

            try
            {
                return await refresh!.Completion.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                lock (_sync)
                {
                    refresh!.Waiters--;
                    if (refresh.Waiters == 0 && !refresh.Completion.Task.IsCompleted)
                    {
                        // Cancel database work when the last reader leaves (including host shutdown).
                        if (ReferenceEquals(entry!.Refresh, refresh))
                        {
                            entry.Refresh = null;
                            SignalCapacityChanged();
                        }

                        refresh.Cancellation.Cancel();
                    }
                }
            }
        }
    }

    public void Set(ulong guildId, LogChannelKind kind, ulong? channelId)
    {
        lock (_sync)
        {
            var key = (guildId, kind);
            if (!_entries.TryGetValue(key, out var entry))
            {
                if (!MakeRoom())
                {
                    // Every slot has an active reader. A subsequent read can load the committed value.
                    return;
                }

                entry = AddEntry(key);
            }

            entry.HasSnapshot = true;
            entry.ChannelId = channelId;
            entry.RefreshAfter = _timeProvider.GetUtcNow() + CacheLifetime;
            entry.Version++;
            Touch(entry);
        }
    }

    private async Task RefreshAsync(
        (ulong GuildId, LogChannelKind Kind) key,
        Entry entry,
        Refresh refresh,
        Func<CancellationToken, Task<ulong?>> load)
    {
        ulong? channelId = null;
        Exception? failure = null;
        try
        {
            // WaitAsync also bounds the refresh when a loader does not honor cancellation.
            channelId = await load(refresh.Cancellation.Token).WaitAsync(refresh.Cancellation.Token);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        lock (_sync)
        {
            if (ReferenceEquals(entry.Refresh, refresh))
            {
                entry.Refresh = null;
                if (entry.Version == refresh.Version)
                {
                    if (failure is null)
                    {
                        entry.HasSnapshot = true;
                        entry.ChannelId = channelId;
                        entry.RefreshAfter = _timeProvider.GetUtcNow() + CacheLifetime;
                    }
                    else
                    {
                        entry.RefreshAfter = _timeProvider.GetUtcNow() + FailureCooldown;
                        _logger.LogWarning(
                            failure,
                            "Could not refresh {ChannelKind} log channel for guild {GuildId}; retrying in {RetrySeconds} seconds. Cached configuration is retained.",
                            key.Kind,
                            key.GuildId,
                            FailureCooldown.TotalSeconds);
                    }
                }

                SignalCapacityChanged();
            }

            refresh.Completion.TrySetResult(entry.HasSnapshot ? entry.ChannelId : null);
            refresh.Cancellation.Dispose();
        }
    }

    private Entry AddEntry((ulong GuildId, LogChannelKind Kind) key)
    {
        var entry = new Entry(_recency.AddLast(key));
        _entries.Add(key, entry);
        return entry;
    }

    private bool MakeRoom()
    {
        if (_entries.Count < MaximumEntries)
        {
            return true;
        }

        // Active refreshes stay resident so that concurrent callers always share their read.
        for (var node = _recency.First; node is not null; node = node.Next)
        {
            if (_entries[node.Value].Refresh is not null)
            {
                continue;
            }

            _entries.Remove(node.Value);
            _recency.Remove(node);
            return true;
        }

        return false;
    }

    private void Touch(Entry entry)
    {
        _recency.Remove(entry.Node);
        _recency.AddLast(entry.Node);
    }

    private void SignalCapacityChanged()
    {
        var signal = _capacityChanged;
        _capacityChanged = NewSignal();
        signal.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Entry(LinkedListNode<(ulong GuildId, LogChannelKind Kind)> node)
    {
        public LinkedListNode<(ulong GuildId, LogChannelKind Kind)> Node { get; } = node;
        public bool HasSnapshot { get; set; }
        public ulong? ChannelId { get; set; }
        public DateTimeOffset RefreshAfter { get; set; }
        public long Version { get; set; }
        public Refresh? Refresh { get; set; }
    }

    private sealed class Refresh(CancellationTokenSource cancellation, long version)
    {
        public long Version { get; } = version;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public TaskCompletionSource<ulong?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Waiters { get; set; }
    }
}
