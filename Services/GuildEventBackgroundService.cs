using Arisu.Storage;
using Discord;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Arisu.Services;

public interface IGuildEventBackgroundService
{
    Task<GuildEventBackgroundUpdateResult> SetAsync(
        ulong guildId,
        IAttachment attachment,
        CancellationToken cancellationToken = default);

    Task<bool> ResetAsync(
        ulong guildId,
        CancellationToken cancellationToken = default);

    Task<byte[]?> GetImageAsync(
        ulong guildId,
        CancellationToken cancellationToken = default);
}

public sealed record GuildEventBackgroundUpdateResult(
    bool Succeeded,
    string Message);

public sealed class GuildEventBackgroundService : IGuildEventBackgroundService
{
    private const int MaximumImageBytes = 8 * 1024 * 1024;
    private const int MaximumStoredImageBytes = 2 * 1024 * 1024;
    private const long MaximumImagePixels = 16_000_000;
    private const int CardWidth = 960;
    private const int CardHeight = 360;
    private const string SupportedImageTypes = "PNG, JPEG, or WebP";

    private static readonly HashSet<string> SupportedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/png",
        "image/webp"
    };

    private readonly IGuildChannelConfigurationStore _channelConfigurationStore;
    private readonly HttpClient _httpClient;
    private readonly ILogger<GuildEventBackgroundService> _logger;
    private readonly IObjectStorageAdapter _objectStorage;

    public GuildEventBackgroundService(
        IGuildChannelConfigurationStore channelConfigurationStore,
        HttpClient httpClient,
        ILogger<GuildEventBackgroundService> logger,
        IObjectStorageAdapter objectStorage)
    {
        _channelConfigurationStore = channelConfigurationStore;
        _httpClient = httpClient;
        _logger = logger;
        _objectStorage = objectStorage;
    }

    public async Task<GuildEventBackgroundUpdateResult> SetAsync(
        ulong guildId,
        IAttachment attachment,
        CancellationToken cancellationToken = default)
    {
        if (!_objectStorage.IsEnabled)
        {
            return new GuildEventBackgroundUpdateResult(
                false,
                "Enable object storage before setting a custom member event background.");
        }

        if (attachment.Size <= 0 || attachment.Size > MaximumImageBytes)
        {
            return new GuildEventBackgroundUpdateResult(
                false,
                "The background image must be 8 MB or smaller.");
        }

        if (!IsSupportedContentType(attachment.ContentType))
        {
            return new GuildEventBackgroundUpdateResult(
                false,
                $"Use a {SupportedImageTypes} image for the background.");
        }

        if (attachment.Width is { } width &&
            attachment.Height is { } height &&
            ((long)width * height) > MaximumImagePixels)
        {
            return new GuildEventBackgroundUpdateResult(
                false,
                "The background image has too many pixels. Use an image smaller than 16 megapixels.");
        }

        if (!Uri.TryCreate(attachment.Url, UriKind.Absolute, out var imageUri) ||
            imageUri.Scheme != Uri.UriSchemeHttps)
        {
            return new GuildEventBackgroundUpdateResult(
                false,
                "The uploaded image does not have a valid HTTPS URL.");
        }

        try
        {
            var sourceImage = await DownloadImageAsync(imageUri, cancellationToken);
            EnsureSupportedImageSignature(sourceImage);
            var normalizedImage = NormalizeImage(sourceImage);
            var previousObjectKey = await _channelConfigurationStore
                .GetEventBackgroundObjectKeyAsync(guildId, cancellationToken);
            var objectKey = CreateObjectKey(guildId);

            await using (var imageStream = new MemoryStream(normalizedImage, writable: false))
            {
                await _objectStorage.UploadAsync(
                    objectKey,
                    imageStream,
                    "image/png",
                    cancellationToken);
            }

            try
            {
                await _channelConfigurationStore.SetEventBackgroundObjectKeyAsync(
                    guildId,
                    objectKey,
                    cancellationToken);
            }
            catch
            {
                await DeleteObjectAsync(objectKey, cancellationToken);
                throw;
            }

            if (!string.IsNullOrWhiteSpace(previousObjectKey))
            {
                await DeleteObjectAsync(previousObjectKey, cancellationToken);
            }

            _logger.LogInformation(
                "Saved member event background image {ObjectKey} for guild {GuildId}.",
                objectKey,
                guildId);

            return new GuildEventBackgroundUpdateResult(
                true,
                "The member event card background was updated.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            return new GuildEventBackgroundUpdateResult(
                false,
                $"Use a valid {SupportedImageTypes} image that is 8 MB or smaller.");
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Could not save member event background image for guild {GuildId}.",
                guildId);

            return new GuildEventBackgroundUpdateResult(
                false,
                "I could not use that image. Upload a valid image that is 8 MB or smaller.");
        }
    }

    public async Task<bool> ResetAsync(
        ulong guildId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var objectKey = await _channelConfigurationStore.GetEventBackgroundObjectKeyAsync(
                guildId,
                cancellationToken);
            if (objectKey is not null && !_objectStorage.IsEnabled)
            {
                return false;
            }

            await _channelConfigurationStore.RemoveEventBackgroundObjectKeyAsync(
                guildId,
                cancellationToken);

            if (objectKey is not null)
            {
                await DeleteObjectAsync(objectKey, cancellationToken);
            }

            _logger.LogInformation(
                "Reset member event background image for guild {GuildId}.",
                guildId);

            return true;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Could not reset member event background image for guild {GuildId}.",
                guildId);
            return false;
        }
    }

    public async Task<byte[]?> GetImageAsync(
        ulong guildId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!_objectStorage.IsEnabled)
            {
                return null;
            }

            var objectKey = await _channelConfigurationStore.GetEventBackgroundObjectKeyAsync(
                guildId,
                cancellationToken);
            if (objectKey is null)
            {
                return null;
            }

            var image = await _objectStorage.DownloadAsync(objectKey, cancellationToken);
            if (image.Length > MaximumStoredImageBytes)
            {
                throw new InvalidDataException(
                    $"Custom member event background {objectKey} is larger than {MaximumStoredImageBytes} bytes.");
            }

            return image;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Could not load member event background image for guild {GuildId}.",
                guildId);
            return null;
        }
    }

    private async Task<byte[]> DownloadImageAsync(
        Uri imageUri,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            imageUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is > MaximumImageBytes)
        {
            throw new InvalidDataException(
                $"The uploaded image is larger than {MaximumImageBytes} bytes.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new MemoryStream();
        var buffer = new byte[81_920];

        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return destination.ToArray();
            }

            if (destination.Length + read > MaximumImageBytes)
            {
                throw new InvalidDataException(
                    $"The uploaded image is larger than {MaximumImageBytes} bytes.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private async Task DeleteObjectAsync(string objectKey, CancellationToken cancellationToken)
    {
        try
        {
            await _objectStorage.DeleteAsync(objectKey, cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Could not remove member event background object {ObjectKey}.",
                objectKey);
        }
    }

    private static string CreateObjectKey(ulong guildId) =>
        $"member-event-backgrounds/{guildId}/{Guid.NewGuid():N}.png";

    private static bool IsSupportedContentType(string? contentType) =>
        contentType is not null && SupportedContentTypes.Contains(contentType);

    private static void EnsureSupportedImageSignature(ReadOnlySpan<byte> image)
    {
        if (HasPngSignature(image) || HasJpegSignature(image) || HasWebpSignature(image))
        {
            return;
        }

        throw new InvalidDataException(
            $"The uploaded file is not a supported {SupportedImageTypes} image.");
    }

    private static bool HasPngSignature(ReadOnlySpan<byte> image) =>
        image.Length >= 8 &&
        image[0] == 0x89 &&
        image[1] == 0x50 &&
        image[2] == 0x4E &&
        image[3] == 0x47 &&
        image[4] == 0x0D &&
        image[5] == 0x0A &&
        image[6] == 0x1A &&
        image[7] == 0x0A;

    private static bool HasJpegSignature(ReadOnlySpan<byte> image) =>
        image.Length >= 3 &&
        image[0] == 0xFF &&
        image[1] == 0xD8 &&
        image[2] == 0xFF;

    private static bool HasWebpSignature(ReadOnlySpan<byte> image) =>
        image.Length >= 12 &&
        image[0] == (byte)'R' &&
        image[1] == (byte)'I' &&
        image[2] == (byte)'F' &&
        image[3] == (byte)'F' &&
        image[8] == (byte)'W' &&
        image[9] == (byte)'E' &&
        image[10] == (byte)'B' &&
        image[11] == (byte)'P';

    private static byte[] NormalizeImage(byte[] sourceImage)
    {
        using var image = SKImage.FromEncodedData(sourceImage)
            ?? throw new InvalidDataException("The uploaded file is not a supported image.");

        if ((long)image.Width * image.Height > MaximumImagePixels)
        {
            throw new InvalidDataException("The uploaded image has too many pixels.");
        }

        using var surface = SKSurface.Create(
            new SKImageInfo(CardWidth, CardHeight, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("Could not create an event card background.");

        DrawCoverImage(surface.Canvas, image);

        using var normalizedImage = surface.Snapshot();
        using var normalizedData = normalizedImage.Encode(SKEncodedImageFormat.Png, quality: 100);
        using var stream = new MemoryStream();
        normalizedData.SaveTo(stream);
        return stream.ToArray();
    }

    private static void DrawCoverImage(SKCanvas canvas, SKImage image)
    {
        var scale = Math.Max(
            CardWidth / (float)image.Width,
            CardHeight / (float)image.Height);
        var width = image.Width * scale;
        var height = image.Height * scale;
        var destination = new SKRect(
            (CardWidth - width) / 2F,
            (CardHeight - height) / 2F,
            (CardWidth + width) / 2F,
            (CardHeight + height) / 2F);
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);

        canvas.DrawImage(image, destination, sampling);
    }
}
