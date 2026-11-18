using System.Net;
using System.Reflection;
using Arisu.Services;
using Arisu.Storage;
using Arisu.Utilities;
using Discord.WebSocket;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;

namespace Arisu.IntegrationTests;

public sealed class LevelingCardServiceTests
{
    [Theory]
    [InlineData(false, 360)]
    [InlineData(true, 460)]
    public async Task RenderAsyncReturnsPngForUnrankedMemberWithLongLabels(bool profile, int expectedHeight)
    {
        using var fixture = new OfflineInteractionFixture();
        SetDisplayName(fixture.Moderator, string.Concat(Enumerable.Repeat("日本語🙂very-long-name ", 20)));
        using var httpClient = new HttpClient(new AvatarHandler(() => new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var service = CreateService(httpClient);

        await using var stream = await service.RenderAsync(
            fixture.Moderator, new string('W', 200), null, profile, CancellationToken.None);

        AssertPng(stream, expectedHeight);
    }

    [Fact]
    public async Task RenderAsyncUsesDownloadedDiscordAvatar()
    {
        using var fixture = new OfflineInteractionFixture();
        SetDisplayName(fixture.Moderator, "Member");
        var avatar = CreateAvatar(256, 256);
        var handler = new AvatarHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(avatar)
        });
        using var httpClient = new HttpClient(handler);
        using var service = CreateService(httpClient);
        var rank = new LevelingRank(new LevelingMember(100, 200, 450, 12, DateTimeOffset.UtcNow), 2);

        await using var stream = await service.RenderAsync(fixture.Moderator, "Test server", rank, true);

        Assert.Equal(1, handler.Requests);
        var url = new Uri(handler.LastUrl!);
        Assert.Equal("https", url.Scheme);
        Assert.Contains(url.Host, new[] { "cdn.discordapp.com", "media.discordapp.net" });
        using var image = SKBitmap.Decode(stream);
        Assert.Equal(SKColors.Red, image.GetPixel(70, 207));
    }

    [Fact]
    public async Task RenderAsyncRejectsAvatarWithTooManyPixelsBeforeDrawing()
    {
        using var fixture = new OfflineInteractionFixture();
        SetDisplayName(fixture.Moderator, "Member");
        var avatar = CreateAvatar(2048, 1024);
        using var httpClient = new HttpClient(new AvatarHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(avatar)
        }));
        using var service = CreateService(httpClient);

        await using var stream = await service.RenderAsync(fixture.Moderator, "Test server", null, false);

        using var image = SKBitmap.Decode(stream);
        Assert.Equal(new SKColor(51, 65, 85), image.GetPixel(70, 207));
    }

    [Fact]
    public async Task RenderAsyncRejectsAvatarExceedingByteLimitWithoutContentLength()
    {
        using var fixture = new OfflineInteractionFixture();
        SetDisplayName(fixture.Moderator, "Member");
        var source = new NonSeekableStream(new byte[8 * 1024 * 1024]);
        using var httpClient = new HttpClient(new AvatarHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(source)
        }));
        using var service = CreateService(httpClient);

        await using var stream = await service.RenderAsync(fixture.Moderator, "Test server", null, false);

        AssertPng(stream, 360);
        using var image = SKBitmap.Decode(stream);
        Assert.Equal(new SKColor(51, 65, 85), image.GetPixel(70, 207));
        Assert.InRange(source.BytesRead, (2 * 1024 * 1024) + 1, (2 * 1024 * 1024) + 81_920);
        Assert.True(source.IsDisposed);
    }

    [Fact]
    public async Task RenderAsyncHonorsCancellationBeforeDownloading()
    {
        using var fixture = new OfflineInteractionFixture();
        var handler = new AvatarHandler(() => throw new InvalidOperationException("No request was expected."));
        using var httpClient = new HttpClient(handler);
        using var service = CreateService(httpClient);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RenderAsync(
            fixture.Moderator, "Test server", null, false, cancellation.Token));

        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task RenderAsyncSupportsMaximumStoredXpWithoutOverflow()
    {
        using var fixture = new OfflineInteractionFixture();
        using var httpClient = new HttpClient(new AvatarHandler(() => new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var service = CreateService(httpClient);
        var rank = new LevelingRank(new LevelingMember(100, 200, long.MaxValue, long.MaxValue, DateTimeOffset.UtcNow), long.MaxValue);

        await using var stream = await service.RenderAsync(fixture.Moderator, "Test server", rank, true);

        Assert.True(stream.Length > 0);
        Assert.True(LevelingDisplay.XpForNextLevel(long.MaxValue) > 0);
    }

    [Theory]
    [InlineData(false, 0L)]
    [InlineData(true, 0L)]
    [InlineData(false, 125L)]
    [InlineData(true, 125L)]
    [InlineData(false, long.MaxValue)]
    [InlineData(true, long.MaxValue)]
    public async Task RenderAsyncProducesFiniteProgressForZeroPartialAndMaximumXp(bool profile, long totalXp)
    {
        using var fixture = new OfflineInteractionFixture();
        SetDisplayName(fixture.Moderator, "Member");
        using var httpClient = new HttpClient(new AvatarHandler(() => new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var service = CreateService(httpClient);
        var rank = new LevelingRank(new LevelingMember(100, 200, totalXp, 12, DateTimeOffset.UtcNow), 2);

        await using var stream = await service.RenderAsync(fixture.Moderator, "Test server", rank, profile);

        AssertPng(stream, profile ? 460 : 360);
        using var image = SKBitmap.Decode(stream);
        Assert.Equal(new SKColor(51, 65, 85), image.GetPixel(923, 276));
        Assert.Equal(totalXp == 0 ? new SKColor(51, 65, 85) : new SKColor(147, 197, 253), image.GetPixel(255, 276));
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("gif")]
    [InlineData("truncated-png")]
    [InlineData("truncated-pixels")]
    public async Task RenderAsyncFallsBackForInvalidUnsupportedAndTruncatedAvatars(string kind)
    {
        using var fixture = new OfflineInteractionFixture();
        SetDisplayName(fixture.Moderator, "Member");
        var bytes = kind switch
        {
            "gif" => Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7"),
            "truncated-png" => CreateAvatar(256, 256)[..60],
            "truncated-pixels" => CreateAvatar(256, 256)[..100],
            _ => "not an image"u8.ToArray()
        };
        var source = new NonSeekableStream(bytes);
        var handler = new AvatarHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(source)
        });
        using var httpClient = new HttpClient(handler);
        using var service = CreateService(httpClient);

        await using var stream = await service.RenderAsync(fixture.Moderator, "Test server", null, false);

        AssertPng(stream, 360);
        using var image = SKBitmap.Decode(stream);
        Assert.Equal(new SKColor(51, 65, 85), image.GetPixel(70, 207));
        Assert.True(source.IsDisposed);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task RenderAsyncRejectsDeclaredOversizedAvatarBeforeReadingAndDisposesContent()
    {
        using var fixture = new OfflineInteractionFixture();
        SetDisplayName(fixture.Moderator, "Member");
        var source = new NonSeekableStream(CreateAvatar(256, 256));
        var handler = new AvatarHandler(() =>
        {
            var content = new StreamContent(source);
            content.Headers.ContentLength = (2 * 1024 * 1024) + 1;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var httpClient = new HttpClient(handler);
        using var service = CreateService(httpClient);

        await using var stream = await service.RenderAsync(fixture.Moderator, "Test server", null, false);

        AssertPng(stream, 360);
        using var image = SKBitmap.Decode(stream);
        Assert.Equal(new SKColor(51, 65, 85), image.GetPixel(70, 207));
        Assert.Equal(0, source.BytesRead);
        Assert.True(source.IsDisposed);
    }

    [Fact]
    public async Task RenderAsyncAcceptsAvatarAtPixelLimitAndDisposesDownloadedStream()
    {
        using var fixture = new OfflineInteractionFixture();
        SetDisplayName(fixture.Moderator, "Member");
        var source = new NonSeekableStream(CreateAvatar(1024, 1024));
        using var httpClient = new HttpClient(new AvatarHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(source)
        }));
        using var service = CreateService(httpClient);

        await using var stream = await service.RenderAsync(fixture.Moderator, "Test server", null, false);

        AssertPng(stream, 360);
        using var image = SKBitmap.Decode(stream);
        Assert.Equal(SKColors.Red, image.GetPixel(70, 207));
        Assert.True(source.IsDisposed);
    }

    [Theory]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task RenderAsyncFallsBackForFailedOrRedirectedAvatarWithoutAnotherRequest(HttpStatusCode status)
    {
        using var fixture = new OfflineInteractionFixture();
        SetDisplayName(fixture.Moderator, "Member");
        var source = new NonSeekableStream(CreateAvatar(256, 256));
        var handler = new AvatarHandler(() =>
        {
            var response = new HttpResponseMessage(status) { Content = new StreamContent(source) };
            response.Headers.Location = new Uri("http://127.0.0.1/private");
            return response;
        });
        using var httpClient = new HttpClient(handler);
        using var service = CreateService(httpClient);

        await using var stream = await service.RenderAsync(fixture.Moderator, "Test server", null, false);

        AssertPng(stream, 360);
        using var image = SKBitmap.Decode(stream);
        Assert.Equal(new SKColor(51, 65, 85), image.GetPixel(70, 207));
        Assert.Equal(1, handler.Requests);
        Assert.True(source.IsDisposed);
    }

    [Fact]
    public async Task RenderAsyncPropagatesCallerCancellationDuringDownload()
    {
        using var fixture = new OfflineInteractionFixture();
        using var cancellation = new CancellationTokenSource();
        var handler = new AvatarHandler(async token =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Cancellation should interrupt the request.");
        });
        using var httpClient = new HttpClient(handler);
        using var service = CreateService(httpClient);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RenderAsync(
            fixture.Moderator, "Test server", null, false, cancellation.Token));

        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task RenderAsyncPropagatesCallerCancellationDuringContentReadAndDisposesStream()
    {
        using var fixture = new OfflineInteractionFixture();
        using var cancellation = new CancellationTokenSource();
        var source = new CancelingReadStream(cancellation);
        using var httpClient = new HttpClient(new AvatarHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(source)
        }));
        using var service = CreateService(httpClient);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RenderAsync(
            fixture.Moderator, "Test server", null, false, cancellation.Token));

        Assert.True(source.IsDisposed);
    }

    [Fact]
    public async Task RenderAsyncFallsBackForDownloadTimeoutWhenCallerIsNotCanceled()
    {
        using var fixture = new OfflineInteractionFixture();
        SetDisplayName(fixture.Moderator, "Member");
        using var httpClient = new HttpClient(new AvatarHandler(() => throw new TaskCanceledException("Simulated HTTP timeout.")));
        using var service = CreateService(httpClient);

        await using var stream = await service.RenderAsync(fixture.Moderator, "Test server", null, false);

        AssertPng(stream, 360);
        using var image = SKBitmap.Decode(stream);
        Assert.Equal(new SKColor(51, 65, 85), image.GetPixel(70, 207));
    }

    [Theory]
    [InlineData(false, "rank")]
    [InlineData(true, "profile")]
    public async Task RenderAsyncSavesBundledAssetSamplesOutsideRepository(bool profile, string name)
    {
        using var fixture = new OfflineInteractionFixture();
        SetDisplayName(fixture.Moderator, "Alice · 日本語🙂");
        typeof(SocketGuildUser).GetField("_joinedAtTicks", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(fixture.Moderator, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks);
        using var httpClient = new HttpClient(new AvatarHandler(() => new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var service = CreateService(httpClient);
        var rank = new LevelingRank(new LevelingMember(100, 200, 1250, 42, DateTimeOffset.UtcNow), 7);

        await using var stream = await service.RenderAsync(fixture.Moderator, "Arisu · A place to talk", rank, profile);

        AssertPng(stream, profile ? 460 : 360);
        Directory.CreateDirectory("/tmp/opencode");
        await using var file = File.Create($"/tmp/opencode/leveling-{name}-review.png");
        await stream.CopyToAsync(file);
    }

    [Theory]
    [InlineData("Alice · 日本語", "Alice · 日本語", false)]
    [InlineData("W🙂W", "W🙂W", false)]
    [InlineData("W🙂WW", "W🙂…", true)]
    [InlineData("W🙂\u200D🙂WW", "W…", true)]
    public void DrawFittedTextPreservesPrimaryGlyphsAndUsesFallbackMeasurements(string text, string expectedText, bool truncate)
    {
        using var httpClient = new HttpClient();
        using var service = CreateService(httpClient);
        using var typeface = SKTypeface.FromFile(Path.Combine(AppContext.BaseDirectory, "Assets/Fonts/MochiyPopOne-Regular.ttf"));
        using var primary = new SKFont(typeface, truncate ? 12 : 32) { Subpixel = true };
        Assert.False(primary.ContainsGlyph(0x1F642));
        using var fallbackTypeface = SKFontManager.Default.MatchCharacter(0x1F642);
        using var fallback = new SKFont(fallbackTypeface ?? typeface, primary.Size) { Subpixel = true };
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        var pieces = expectedText.Contains("🙂", StringComparison.Ordinal)
            ? new[] { ("W", primary), ("🙂", fallback), (truncate ? "…" : "W", primary) }
            : new[] { (expectedText, primary) };
        var expectedWidth = pieces.Sum(piece => piece.Item2.MeasureText(piece.Item1, paint));
        var maximumWidth = truncate ? expectedWidth : 900;
        using var actual = SKSurface.Create(new SKImageInfo(960, 100));
        using var expected = SKSurface.Create(new SKImageInfo(960, 100));
        actual.Canvas.Clear(SKColors.Transparent);
        expected.Canvas.Clear(SKColors.Transparent);

        DrawText(service, actual.Canvas, text, maximumWidth);
        var x = 8f;
        foreach (var (piece, font) in pieces)
        {
            expected.Canvas.DrawText(piece, x, 60, SKTextAlign.Left, font, paint);
            x += font.MeasureText(piece, paint);
        }

        using var actualImage = actual.Snapshot();
        using var expectedImage = expected.Snapshot();
        using var actualPixels = SKBitmap.FromImage(actualImage);
        using var expectedPixels = SKBitmap.FromImage(expectedImage);
        Assert.Equal(expectedPixels.Bytes, actualPixels.Bytes);
    }

    [Theory]
    [InlineData("🙂\uFE0F", "🙂")]
    [InlineData("🙂\u200D🙂", "🙂🙂")]
    public void DrawFittedTextDoesNotDrawBoxesForEmojiSelectorsOrJoiners(string text, string components)
    {
        using var httpClient = new HttpClient();
        using var service = CreateService(httpClient);
        using var actual = SKSurface.Create(new SKImageInfo(960, 100));
        using var expected = SKSurface.Create(new SKImageInfo(960, 100));
        actual.Canvas.Clear(SKColors.Transparent);
        expected.Canvas.Clear(SKColors.Transparent);

        DrawText(service, actual.Canvas, text, 900);
        DrawText(service, expected.Canvas, components, 900);

        using var actualImage = actual.Snapshot();
        using var expectedImage = expected.Snapshot();
        using var actualPixels = SKBitmap.FromImage(actualImage);
        using var expectedPixels = SKBitmap.FromImage(expectedImage);
        Assert.Equal(expectedPixels.Bytes, actualPixels.Bytes);
    }

    [Fact]
    public void DrawFittedTextLeavesNoPixelsWhenEvenEllipsisCannotFit()
    {
        using var httpClient = new HttpClient();
        using var service = CreateService(httpClient);
        using var surface = SKSurface.Create(new SKImageInfo(960, 100));
        surface.Canvas.Clear(SKColors.Transparent);

        DrawText(service, surface.Canvas, "🙂", 1);

        using var image = surface.Snapshot();
        using var pixels = SKBitmap.FromImage(image);
        Assert.All(pixels.Bytes, value => Assert.Equal((byte)0, value));
    }

    [Fact]
    public void DrawFittedTextCentersFallbackAvatarInitialUsingInkBounds()
    {
        using var httpClient = new HttpClient();
        using var service = CreateService(httpClient);
        using var surface = SKSurface.Create(new SKImageInfo(200, 200));
        surface.Canvas.Clear(SKColors.Transparent);
        typeof(LevelingCardService).GetMethod("DrawFittedText", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [surface.Canvas, "🙂", 100f, 100f, 150f, 60f, SKColors.White, true]);

        using var image = surface.Snapshot();
        using var pixels = SKBitmap.FromImage(image);
        var left = pixels.Width;
        var right = -1;
        var top = pixels.Height;
        var bottom = -1;
        for (var y = 0; y < pixels.Height; y++)
        {
            for (var x = 0; x < pixels.Width; x++)
            {
                if (pixels.GetPixel(x, y).Alpha == 0)
                {
                    continue;
                }

                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }
        }

        Assert.True(right >= left);
        Assert.InRange((left + right) / 2f, 96, 104);
        Assert.InRange((top + bottom) / 2f, 96, 104);
    }

    private static void DrawText(LevelingCardService service, SKCanvas canvas, string text, float maximumWidth)
    {
        typeof(LevelingCardService).GetMethod("DrawFittedText", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [canvas, text, 8f, 60f, maximumWidth, 32f, SKColors.White, false]);
    }

    private static void AssertPng(MemoryStream stream, int expectedHeight)
    {
        Assert.Equal(0, stream.Position);
        Assert.True(stream.CanRead);
        Assert.True(stream.CanSeek);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, stream.ToArray()[..8]);
        using var image = SKBitmap.Decode(stream.ToArray());
        Assert.NotNull(image);
        Assert.Equal(960, image.Width);
        Assert.Equal(expectedHeight, image.Height);
        Assert.Equal(0, stream.Position);
    }

    private static LevelingCardService CreateService(HttpClient httpClient)
    {
        var environment = new TestEnvironment();
        Assert.True(File.Exists(Path.Combine(environment.ContentRootPath, "Assets/Images/member-event-background.png")));
        Assert.True(File.Exists(Path.Combine(environment.ContentRootPath, "Assets/Fonts/MochiyPopOne-Regular.ttf")));
        return new LevelingCardService(httpClient, environment, NullLogger<LevelingCardService>.Instance);
    }

    private static byte[] CreateAvatar(int width, int height)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        surface.Canvas.Clear(SKColors.Red);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static void SetDisplayName(SocketGuildUser user, string name)
    {
        var global = typeof(SocketGuildUser).GetProperty("GlobalUser", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(user)!;
        for (var type = global.GetType(); type is not null; type = type.BaseType)
        {
            var field = type.GetField("<Username>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field is not null)
            {
                field.SetValue(global, name);
                return;
            }
        }

        throw new InvalidOperationException("Could not set the offline member's display name.");
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Arisu.IntegrationTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class AvatarHandler : HttpMessageHandler
    {
        private readonly Func<CancellationToken, Task<HttpResponseMessage>> _response;

        public AvatarHandler(Func<HttpResponseMessage> response) : this(_ => Task.FromResult(response())) { }

        public AvatarHandler(Func<CancellationToken, Task<HttpResponseMessage>> response) => _response = response;

        public int Requests { get; private set; }
        public string? LastUrl { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            LastUrl = request.RequestUri?.AbsoluteUri;
            return _response(cancellationToken);
        }
    }

    private sealed class NonSeekableStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes, writable: false);
        public bool IsDisposed { get; private set; }
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await _inner.ReadAsync(buffer, cancellationToken);
            BytesRead += read;
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                IsDisposed = true;
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class CancelingReadStream(CancellationTokenSource cancellation) : Stream
    {
        public bool IsDisposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Cancellation should interrupt the read.");
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                IsDisposed = true;
            }

            base.Dispose(disposing);
        }
    }
}
