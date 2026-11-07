using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Arisu.Services;

public interface IGuildEventCardService
{
    Task<MemoryStream> CreateAsync(
        GuildEventNotification notification,
        CancellationToken cancellationToken = default);
}

public sealed class GuildEventCardService : IGuildEventCardService, IDisposable
{
    private const int CardWidth = 960;
    private const int CardHeight = 360;
    private const int AvatarSize = 160;
    private const int MaximumAvatarBytes = 2 * 1024 * 1024;
    private const string BackgroundRelativePath = "Assets/Images/member-event-background.png";
    private const string FontRelativePath = "Assets/Fonts/MochiyPopOne-Regular.ttf";

    private readonly SKImage? _background;
    private readonly IGuildEventBackgroundService _eventBackgroundService;
    private readonly HttpClient _httpClient;
    private readonly ILogger<GuildEventCardService> _logger;
    private readonly SKTypeface? _typeface;

    public GuildEventCardService(
        IGuildEventBackgroundService eventBackgroundService,
        HttpClient httpClient,
        IHostEnvironment environment,
        ILogger<GuildEventCardService> logger)
    {
        _eventBackgroundService = eventBackgroundService;
        _httpClient = httpClient;
        _logger = logger;
        _background = LoadBackground(
            Path.Combine(environment.ContentRootPath, BackgroundRelativePath));
        _typeface = LoadTypeface(
            Path.Combine(environment.ContentRootPath, FontRelativePath));
    }

    public async Task<MemoryStream> CreateAsync(
        GuildEventNotification notification,
        CancellationToken cancellationToken = default)
    {
        var style = GetStyle(notification.Event);
        using var avatar = await DownloadAvatarAsync(notification, cancellationToken);
        using var customBackground = await LoadCustomBackgroundAsync(
            notification.GuildId,
            cancellationToken);
        using var surface = SKSurface.Create(
            new SKImageInfo(CardWidth, CardHeight, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("Could not create a server event card surface.");

        DrawCard(
            surface.Canvas,
            notification,
            style,
            avatar,
            customBackground ?? _background);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, quality: 100);
        var stream = new MemoryStream();

        try
        {
            data.SaveTo(stream);
            stream.Position = 0;
            return stream;
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    private async Task<SKImage?> DownloadAvatarAsync(
        GuildEventNotification notification,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(
                notification.AvatarUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength is > MaximumAvatarBytes)
            {
                _logger.LogWarning(
                    "Avatar for member {MemberId} is larger than {MaximumAvatarBytes} bytes.",
                    notification.MemberId,
                    MaximumAvatarBytes);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var avatarBytes = await ReadAvatarBytesAsync(stream, cancellationToken);
            return SKImage.FromEncodedData(avatarBytes)
                ?? throw new InvalidDataException("Discord returned an invalid avatar image.");
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Could not download the avatar for member {MemberId}; using the fallback avatar.",
                notification.MemberId);
            return null;
        }
    }

    private async Task<SKImage?> LoadCustomBackgroundAsync(
        ulong guildId,
        CancellationToken cancellationToken)
    {
        var imageData = await _eventBackgroundService.GetImageAsync(guildId, cancellationToken);
        if (imageData is null)
        {
            return null;
        }

        var image = SKImage.FromEncodedData(imageData);
        if (image is null)
        {
            _logger.LogWarning(
                "Custom member event background for guild {GuildId} is invalid; using the default background.",
                guildId);
        }

        return image;
    }

    private static async Task<byte[]> ReadAvatarBytesAsync(
        Stream source,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81_920];
        var bytesRead = 0;

        await using var destination = new MemoryStream();
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return destination.ToArray();
            }

            bytesRead += read;
            if (bytesRead > MaximumAvatarBytes)
            {
                throw new InvalidDataException(
                    $"Discord returned an avatar larger than {MaximumAvatarBytes} bytes.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    public void Dispose()
    {
        _background?.Dispose();
        _typeface?.Dispose();
    }

    private void DrawCard(
        SKCanvas canvas,
        GuildEventNotification notification,
        CardStyle style,
        SKImage? avatar,
        SKImage? background)
    {
        var centerX = CardWidth / 2F;
        const float avatarCenterY = 169;

        canvas.Clear(style.Background);
        if (background is not null)
        {
            DrawBackground(canvas, background);
        }

        using var accentPaint = CreatePaint(style.Accent);
        using var borderPaint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 4
        };

        DrawCenteredText(
            canvas,
            style.Heading,
            36,
            SKFontStyleWeight.Bold,
            new SKPoint(centerX, 45),
            SKColors.White);

        canvas.DrawCircle(centerX, avatarCenterY, (AvatarSize / 2F) + 7, accentPaint);
        if (avatar is null)
        {
            DrawFallbackAvatar(canvas, notification.MemberName, centerX, avatarCenterY);
        }
        else
        {
            DrawAvatar(canvas, avatar, centerX, avatarCenterY);
        }

        canvas.DrawCircle(centerX, avatarCenterY, (AvatarSize / 2F) + 7, borderPaint);

        DrawCenteredText(
            canvas,
            Truncate(notification.MemberName, 42),
            30,
            SKFontStyleWeight.Bold,
            new SKPoint(centerX, 285),
            SKColors.White);
        DrawCenteredText(
            canvas,
            Truncate(notification.Details, 64),
            19,
            SKFontStyleWeight.Normal,
            new SKPoint(centerX, 325),
            new SKColor(226, 232, 240));
    }

    private static void DrawAvatar(
        SKCanvas canvas,
        SKImage avatar,
        float centerX,
        float centerY)
    {
        var destination = new SKRect(
            centerX - (AvatarSize / 2F),
            centerY - (AvatarSize / 2F),
            centerX + (AvatarSize / 2F),
            centerY + (AvatarSize / 2F));
        var scale = Math.Max(AvatarSize / (float)avatar.Width, AvatarSize / (float)avatar.Height);
        var sourceWidth = AvatarSize / scale;
        var sourceHeight = AvatarSize / scale;
        var source = new SKRect(
            (avatar.Width - sourceWidth) / 2F,
            (avatar.Height - sourceHeight) / 2F,
            (avatar.Width + sourceWidth) / 2F,
            (avatar.Height + sourceHeight) / 2F);

        using var clip = new SKPath();
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);

        clip.AddCircle(centerX, centerY, AvatarSize / 2F);
        canvas.Save();
        canvas.ClipPath(clip, SKClipOperation.Intersect, antialias: true);
        canvas.DrawImage(avatar, source, destination, sampling);
        canvas.Restore();
    }

    private static void DrawBackground(SKCanvas canvas, SKImage background)
    {
        var scale = Math.Max(
            CardWidth / (float)background.Width,
            CardHeight / (float)background.Height);
        var width = background.Width * scale;
        var height = background.Height * scale;
        var destination = new SKRect(
            (CardWidth - width) / 2F,
            (CardHeight - height) / 2F,
            (CardWidth + width) / 2F,
            (CardHeight + height) / 2F);
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);

        canvas.DrawImage(background, destination, sampling);
    }

    private void DrawFallbackAvatar(
        SKCanvas canvas,
        string memberName,
        float centerX,
        float centerY)
    {
        var initial = string.IsNullOrWhiteSpace(memberName)
            ? "?"
            : char.ToUpperInvariant(memberName.Trim()[0]).ToString();

        DrawCenteredText(
            canvas,
            initial,
            72,
            SKFontStyleWeight.Bold,
            new SKPoint(centerX, centerY),
            SKColors.White);
    }

    private void DrawCenteredText(
        SKCanvas canvas,
        string text,
        float textSize,
        SKFontStyleWeight fontWeight,
        SKPoint center,
        SKColor color)
    {
        using var font = new SKFont(_typeface ?? SKTypeface.Default, textSize)
        {
            Embolden = fontWeight >= SKFontStyleWeight.Bold,
            Subpixel = true
        };
        using var paint = new SKPaint
        {
            Color = color,
            IsAntialias = true
        };
        var metrics = font.Metrics;
        var baseline = center.Y - ((metrics.Ascent + metrics.Descent) / 2F);

        canvas.DrawText(text, center.X, baseline, SKTextAlign.Center, font, paint);
    }

    private SKTypeface? LoadTypeface(string fontPath)
    {
        if (!File.Exists(fontPath))
        {
            _logger.LogError(
                "Card font was not found at {FontPath}; using the SkiaSharp default font.",
                fontPath);
            return null;
        }

        var typeface = SKTypeface.FromFile(fontPath);
        if (typeface is null)
        {
            _logger.LogError(
                "Card font at {FontPath} could not be loaded; using the SkiaSharp default font.",
                fontPath);
        }

        return typeface;
    }

    private SKImage? LoadBackground(string backgroundPath)
    {
        if (!File.Exists(backgroundPath))
        {
            _logger.LogError(
                "Card background was not found at {BackgroundPath}; using event colors only.",
                backgroundPath);
            return null;
        }

        var background = SKImage.FromEncodedData(backgroundPath);
        if (background is null)
        {
            _logger.LogError(
                "Card background at {BackgroundPath} could not be loaded; using event colors only.",
                backgroundPath);
        }

        return background;
    }

    private static SKPaint CreatePaint(SKColor color) =>
        new()
        {
            Color = color,
            IsAntialias = true
        };

    private static CardStyle GetStyle(string eventName) => eventName switch
    {
        "Member joined" => new CardStyle(
            "WELCOME",
            new SKColor(74, 222, 128),
            new SKColor(15, 81, 50)),
        "Member left" => new CardStyle(
            "GOODBYE",
            new SKColor(251, 113, 133),
            new SKColor(112, 26, 117)),
        "Server boost" => new CardStyle(
            "THANK YOU",
            new SKColor(244, 114, 182),
            new SKColor(88, 28, 135)),
        _ => new CardStyle(
            "SERVER EVENT",
            new SKColor(148, 163, 184),
            new SKColor(30, 41, 59))
    };

    private static string Truncate(string text, int maximumLength) =>
        text.Length <= maximumLength
            ? text
            : $"{text[..(maximumLength - 1)]}…";

    private sealed record CardStyle(
        string Heading,
        SKColor Accent,
        SKColor Background);
}
