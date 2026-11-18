using System.Globalization;
using System.Text;
using Arisu.Storage;
using Arisu.Utilities;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Arisu.Services;

public interface ILevelingCardService
{
    Task<MemoryStream> RenderAsync(
        SocketGuildUser user,
        string guildName,
        LevelingRank? rank,
        bool profile,
        CancellationToken cancellationToken = default);
}

public sealed class LevelingCardService : ILevelingCardService, IDisposable
{
    private const int CardWidth = 960;
    private const int RankHeight = 360;
    private const int ProfileHeight = 460;
    private const int MaximumAvatarBytes = 2 * 1024 * 1024;
    private const long MaximumAvatarPixels = 1_048_576;
    private const string BackgroundRelativePath = "Assets/Images/member-event-background.png";
    private const string FontRelativePath = "Assets/Fonts/MochiyPopOne-Regular.ttf";
    private static readonly SKColor Accent = new(147, 197, 253);
    private static readonly SKColor Muted = new(203, 213, 225);

    private readonly HttpClient _httpClient;
    private readonly ILogger<LevelingCardService> _logger;
    private readonly SKImage? _background;
    private readonly SKTypeface? _typeface;

    public LevelingCardService(
        HttpClient httpClient,
        IHostEnvironment environment,
        ILogger<LevelingCardService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _background = LoadBackground(Path.Combine(environment.ContentRootPath, BackgroundRelativePath));
        _typeface = LoadTypeface(Path.Combine(environment.ContentRootPath, FontRelativePath));
    }

    public async Task<MemoryStream> RenderAsync(
        SocketGuildUser user,
        string guildName,
        LevelingRank? rank,
        bool profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        cancellationToken.ThrowIfCancellationRequested();
        using var avatar = await DownloadAvatarAsync(user, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        using var surface = SKSurface.Create(new SKImageInfo(
            CardWidth, profile ? ProfileHeight : RankHeight, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("Could not create a leveling card surface.");
        DrawCard(surface.Canvas, user, guildName, rank, profile, avatar);
        cancellationToken.ThrowIfCancellationRequested();
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, quality: 100)
            ?? throw new InvalidOperationException("Could not encode a leveling card.");
        var stream = new MemoryStream();
        try
        {
            data.SaveTo(stream);
            cancellationToken.ThrowIfCancellationRequested();
            stream.Position = 0;
            return stream;
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    private async Task<SKImage?> DownloadAvatarAsync(SocketGuildUser user, CancellationToken cancellationToken)
    {
        try
        {
            var url = user.GetDisplayAvatarUrl(ImageFormat.Png, size: 256);
            if (!EndpointValidation.IsDiscordMediaUrl(url))
            {
                return null;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            // The shared Discord-media HttpClient is configured with automatic redirects disabled.
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaximumAvatarBytes)
            {
                throw new InvalidDataException("The avatar exceeds the allowed download size.");
            }

            await using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
            var bytes = await BoundedStreamReader.ReadAsync(source, MaximumAvatarBytes, timeout.Token);
            using var data = SKData.CreateCopy(bytes);
            using var codec = SKCodec.Create(data)
                ?? throw new InvalidDataException("Discord returned an invalid avatar image.");
            var info = codec.Info;
            if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > MaximumAvatarPixels)
            {
                throw new InvalidDataException("The avatar exceeds the allowed pixel count.");
            }

            if (codec.EncodedFormat is not (SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Webp))
            {
                throw new InvalidDataException("Discord returned an unsupported avatar format.");
            }

            return SKImage.FromEncodedData(bytes)
                ?? throw new InvalidDataException("The avatar could not be decoded.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception,
                "Could not load the leveling avatar for member {MemberId}; using an initial instead.", user.Id);
            return null;
        }
    }

    private void DrawCard(
        SKCanvas canvas,
        SocketGuildUser user,
        string guildName,
        LevelingRank? rank,
        bool profile,
        SKImage? avatar)
    {
        var height = profile ? ProfileHeight : RankHeight;
        var totalXp = rank?.Member.TotalXp ?? 0;
        var level = LevelingProgress.GetLevel(totalXp);
        var name = LevelingDisplay.Label(user.DisplayName);
        canvas.Clear(new SKColor(15, 23, 42));
        if (_background is not null)
        {
            DrawCroppedImage(canvas, _background, new SKRect(0, 0, CardWidth, height));
        }

        using var overlay = CreatePaint(new SKColor(10, 18, 35, 224));
        canvas.DrawRect(0, 0, CardWidth, height, overlay);
        using var accent = CreatePaint(Accent);
        canvas.DrawRect(0, 0, 8, height, accent);

        DrawFittedText(canvas, profile ? "SERVER PROFILE" : "SERVER RANK", 36, 56, 560, 20, Accent);
        DrawFittedText(canvas, LevelingDisplay.Label(guildName), 36, 92, 888, 19, Muted);
        DrawAvatar(canvas, avatar, name, new SKRect(36, 122, 206, 292));
        DrawFittedText(canvas, name, 244, 151, 680, 32, SKColors.White);
        DrawStat(canvas, "LEVEL", LevelingDisplay.Number(level), 244, 194, 170);
        DrawStat(canvas, "TOTAL XP", LevelingDisplay.Number(totalXp), 434, 194, 278);
        DrawStat(canvas, "SERVER RANK", rank is null ? "Unranked" : $"#{LevelingDisplay.Number(rank.Position)}",
            732, 194, 192);

        var xpIntoLevel = LevelingProgress.GetXpIntoLevel(totalXp);
        var xpForNextLevel = LevelingDisplay.XpForNextLevel(totalXp);
        var bar = new SKRect(244, 269, 924, 284);
        using var track = CreatePaint(new SKColor(51, 65, 85));
        canvas.DrawRoundRect(bar, 7, 7, track);
        var progress = (float)Math.Clamp(xpIntoLevel / (double)xpForNextLevel, 0, 1);
        if (progress > 0)
        {
            canvas.Save();
            using var clip = new SKPath();
            clip.AddRoundRect(bar, 7, 7);
            canvas.ClipPath(clip, SKClipOperation.Intersect, antialias: true);
            canvas.DrawRect(new SKRect(bar.Left, bar.Top, bar.Left + (bar.Width * progress), bar.Bottom), accent);
            canvas.Restore();
        }

        DrawFittedText(canvas,
            $"{LevelingDisplay.Number(xpIntoLevel)} / {LevelingDisplay.Number(xpForNextLevel)} XP to next level",
            244, 320, 680, 16, Muted);
        if (!profile)
        {
            return;
        }

        using var divider = CreatePaint(new SKColor(71, 85, 105));
        canvas.DrawRect(36, 348, 888, 1, divider);
        DrawStat(canvas, "MESSAGES AWARDED XP", LevelingDisplay.Number(rank?.Member.MessageCount ?? 0), 36, 380, 440);
        if (user.JoinedAt is { } joinedAt)
        {
            DrawStat(canvas, "JOINED THIS SERVER", joinedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                500, 380, 424);
        }

        DrawFittedText(canvas, "Only messages awarded XP are counted, not every message sent.",
            36, 440, 888, 12, Muted);
    }

    private void DrawStat(SKCanvas canvas, string label, string value, float x, float y, float width)
    {
        DrawFittedText(canvas, label, x, y, width, 13, Muted);
        DrawFittedText(canvas, value, x, y + 39, width, 27, SKColors.White);
    }

    private void DrawAvatar(SKCanvas canvas, SKImage? avatar, string name, SKRect destination)
    {
        var centerX = destination.MidX;
        var centerY = destination.MidY;
        var radius = destination.Width / 2;
        using var fill = CreatePaint(new SKColor(51, 65, 85));
        canvas.DrawCircle(centerX, centerY, radius + 4, fill);
        if (avatar is not null)
        {
            canvas.Save();
            using var clip = new SKPath();
            clip.AddCircle(centerX, centerY, radius);
            canvas.ClipPath(clip, SKClipOperation.Intersect, antialias: true);
            DrawCroppedImage(canvas, avatar, destination);
            canvas.Restore();
        }
        else
        {
            var initial = StringInfo.GetNextTextElement(name).ToUpperInvariant();
            DrawFittedText(canvas, initial, centerX, centerY, destination.Width - 20, 60, SKColors.White,
                centered: true);
        }

        using var border = CreatePaint(Accent);
        border.Style = SKPaintStyle.Stroke;
        border.StrokeWidth = 3;
        canvas.DrawCircle(centerX, centerY, radius + 3, border);
    }

    private static void DrawCroppedImage(SKCanvas canvas, SKImage image, SKRect destination)
    {
        var scale = Math.Max(destination.Width / image.Width, destination.Height / image.Height);
        var sourceWidth = destination.Width / scale;
        var sourceHeight = destination.Height / scale;
        var source = new SKRect(
            (image.Width - sourceWidth) / 2, (image.Height - sourceHeight) / 2,
            (image.Width + sourceWidth) / 2, (image.Height + sourceHeight) / 2);
        canvas.DrawImage(image, source, destination, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
    }

    private void DrawFittedText(
        SKCanvas canvas, string text, float x, float baseline, float maximumWidth, float size, SKColor color,
        bool centered = false)
    {
        using var font = new SKFont(_typeface ?? SKTypeface.Default, size) { Subpixel = true };
        using var paint = CreatePaint(color);
        var fallbackFonts = new Dictionary<int, SKFont>();
        var ownedFonts = new List<SKFont>();
        var ownedTypefaces = new HashSet<SKTypeface>();
        try
        {
            SKFont GetFont(Rune rune)
            {
                if (font.ContainsGlyph(rune.Value))
                {
                    return font;
                }

                if (!fallbackFonts.TryGetValue(rune.Value, out var fallback))
                {
                    var typeface = SKFontManager.Default.MatchCharacter(rune.Value);
                    fallback = font;
                    if (typeface is not null)
                    {
                        ownedTypefaces.Add(typeface);
                        fallback = new SKFont(typeface, font.Size) { Subpixel = true };
                        ownedFonts.Add(fallback);
                    }

                    fallbackFonts.Add(rune.Value, fallback);
                }

                return fallback;
            }

            List<(string Text, SKFont Font)> CreateRuns(string value)
            {
                var result = new List<(string Text, SKFont Font)>();
                var builder = new StringBuilder();
                var current = font;
                foreach (var rune in value.EnumerateRunes())
                {
                    // DrawText does not shape joined emoji. Render their component glyphs without
                    // visible boxes for joiners or variation selectors; do not split them when fitting.
                    if (rune.Value == 0x200D || rune.Value is >= 0xFE00 and <= 0xFE0F or >= 0xE0100 and <= 0xE01EF)
                    {
                        continue;
                    }

                    var next = GetFont(rune);
                    if (next != current && builder.Length > 0)
                    {
                        result.Add((builder.ToString(), current));
                        builder.Clear();
                    }

                    current = next;
                    builder.Append(rune.ToString());
                }

                if (builder.Length > 0)
                {
                    result.Add((builder.ToString(), current));
                }

                return result;
            }

            var runs = CreateRuns(text);
            float MeasureRuns(out SKRect bounds)
            {
                bounds = SKRect.Empty;
                var advance = 0f;
                foreach (var run in runs)
                {
                    var width = run.Font.MeasureText(run.Text, out var ink, paint);
                    ink.Offset(advance, 0);
                    bounds.Union(ink);
                    advance += width;
                }

                return Math.Max(advance, bounds.Right) - Math.Min(0, bounds.Left);
            }

            while (MeasureRuns(out _) > maximumWidth && font.Size > 12)
            {
                font.Size--;
                foreach (var fallback in ownedFonts)
                {
                    fallback.Size = font.Size;
                }
            }

            if (MeasureRuns(out _) > maximumWidth)
            {
                var boundaries = StringInfo.ParseCombiningCharacters(text);
                for (var index = boundaries.Length - 1; index >= 0; index--)
                {
                    runs = CreateRuns($"{text[..boundaries[index]]}…");
                    if (MeasureRuns(out _) <= maximumWidth)
                    {
                        break;
                    }
                }

                if (MeasureRuns(out _) > maximumWidth)
                {
                    runs.Clear();
                }
            }

            var fittedWidth = MeasureRuns(out var bounds);
            x -= Math.Min(0, bounds.Left);
            if (centered)
            {
                x -= fittedWidth / 2;
                baseline -= bounds.MidY;
            }

            foreach (var run in runs)
            {
                canvas.DrawText(run.Text, x, baseline, SKTextAlign.Left, run.Font, paint);
                x += run.Font.MeasureText(run.Text, paint);
            }
        }
        finally
        {
            foreach (var fallback in ownedFonts)
            {
                fallback.Dispose();
            }

            foreach (var typeface in ownedTypefaces)
            {
                typeface.Dispose();
            }
        }
    }

    private SKImage? LoadBackground(string path)
    {
        try
        {
            return SKImage.FromEncodedData(path)
                ?? throw new InvalidDataException("The bundled leveling background is invalid.");
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception,
                "Could not load the bundled leveling background at {BackgroundPath}; using a plain background.", path);
            return null;
        }
    }

    private SKTypeface? LoadTypeface(string path)
    {
        try
        {
            return SKTypeface.FromFile(path)
                ?? throw new InvalidDataException("The bundled leveling font is invalid.");
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception,
                "Could not load the bundled leveling font at {FontPath}; using the default font.", path);
            return null;
        }
    }

    private static SKPaint CreatePaint(SKColor color) => new() { Color = color, IsAntialias = true };

    public void Dispose()
    {
        _background?.Dispose();
        _typeface?.Dispose();
    }
}
