namespace Arisu.Services;

internal static class LogChannelConfigurationWriter
{
    private const int StripesPerKind = 128;
    private static readonly SemaphoreSlim[] AuditWrites = CreateStripes();
    private static readonly SemaphoreSlim[] MemberWrites = CreateStripes();

    public static async Task WriteAsync(
        ulong guildId,
        LogChannelKind kind,
        ulong? channelId,
        LogChannelCache cache,
        Func<Task> persist)
    {
        // Bounded, process-wide stripes also coordinate separate service instances.
        var stripes = kind == LogChannelKind.Audit ? AuditWrites : MemberWrites;
        var stripe = stripes[(int)((guildId ^ (guildId >> 32)) % StripesPerKind)];
        await stripe.WaitAsync();
        try
        {
            // Keep the committed write and its cache publication in the same critical section.
            await persist();
            cache.Set(guildId, kind, channelId);
        }
        finally
        {
            stripe.Release();
        }
    }

    private static SemaphoreSlim[] CreateStripes() =>
        Enumerable.Range(0, StripesPerKind).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
}
