using Arisu.Configuration;
using Microsoft.Extensions.Options;

namespace Arisu.Services;

public enum ImageGenerationRequestRejection
{
    None,
    AlreadyRunning,
    CoolingDown,
    AtCapacity
}

public sealed record ImageGenerationRequestAdmission(
    ImageGenerationRequestLease? Lease,
    ImageGenerationRequestRejection Rejection,
    TimeSpan RetryAfter)
{
    public bool IsAccepted => Lease is not null;
}

public interface IImageGenerationRequestLimiter
{
    ImageGenerationRequestAdmission TryAcquire(ulong userId);
}

public sealed class ImageGenerationRequestLease : IDisposable
{
    private ImageGenerationRequestLimiter? _limiter;
    private readonly ulong _userId;

    internal ImageGenerationRequestLease(ImageGenerationRequestLimiter limiter, ulong userId)
    {
        _limiter = limiter;
        _userId = userId;
    }

    public void Dispose()
    {
        var limiter = Interlocked.Exchange(ref _limiter, null);
        limiter?.Release(_userId);
    }
}

public sealed class ImageGenerationRequestLimiter : IImageGenerationRequestLimiter
{
    private readonly SemaphoreSlim _availableSlots;
    private readonly TimeSpan _userCooldown;
    private readonly object _sync = new();
    private readonly HashSet<ulong> _activeUserIds = [];
    private readonly Dictionary<ulong, DateTimeOffset> _nextAllowedAtByUser = [];

    public ImageGenerationRequestLimiter(IOptions<ComfyUiOptions> options)
    {
        var value = options.Value;
        _availableSlots = new SemaphoreSlim(
            value.MaximumConcurrentRequests,
            value.MaximumConcurrentRequests);
        _userCooldown = TimeSpan.FromSeconds(value.UserCooldownSeconds);
    }

    public ImageGenerationRequestAdmission TryAcquire(ulong userId)
    {
        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            RemoveExpiredCooldowns(now);

            if (_activeUserIds.Contains(userId))
            {
                return new ImageGenerationRequestAdmission(
                    null,
                    ImageGenerationRequestRejection.AlreadyRunning,
                    TimeSpan.Zero);
            }

            if (_nextAllowedAtByUser.TryGetValue(userId, out var nextAllowedAt))
            {
                return new ImageGenerationRequestAdmission(
                    null,
                    ImageGenerationRequestRejection.CoolingDown,
                    nextAllowedAt - now);
            }

            if (!_availableSlots.Wait(0))
            {
                return new ImageGenerationRequestAdmission(
                    null,
                    ImageGenerationRequestRejection.AtCapacity,
                    TimeSpan.Zero);
            }

            _activeUserIds.Add(userId);
            return new ImageGenerationRequestAdmission(
                new ImageGenerationRequestLease(this, userId),
                ImageGenerationRequestRejection.None,
                TimeSpan.Zero);
        }
    }

    internal void Release(ulong userId)
    {
        lock (_sync)
        {
            if (!_activeUserIds.Remove(userId))
            {
                return;
            }

            _nextAllowedAtByUser[userId] = DateTimeOffset.UtcNow + _userCooldown;
        }

        _availableSlots.Release();
    }

    private void RemoveExpiredCooldowns(DateTimeOffset now)
    {
        foreach (var userId in _nextAllowedAtByUser
                     .Where(pair => pair.Value <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _nextAllowedAtByUser.Remove(userId);
        }
    }
}
