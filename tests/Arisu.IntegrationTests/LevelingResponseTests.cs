using System.Reflection;
using System.Text.Json;
using Arisu.Modules;
using Arisu.Services;
using Arisu.Storage;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Arisu.IntegrationTests;

public sealed class LevelingResponseTests
{
    [Theory]
    [InlineData("rank")]
    [InlineData("profile")]
    [InlineData("leaderboard")]
    public async Task DisabledLevelingRepliesPubliclyWithoutLoadingStatsOrRenderingCardsAsync(string command)
    {
        using var fixture = new OfflineInteractionFixture();
        var module = CreateModule(fixture, out var store, out var cards);
        store.Enabled = false;

        await InvokeAsync(module, command);

        using var payload = FinalResponse(fixture);
        var content = payload.RootElement.GetProperty("content").GetString()!;
        Assert.Contains("Leveling is disabled in this server.", content);
        Assert.Contains("/settings leveling enable", content);
        Assert.Contains("XP is earned only while leveling is enabled.", content);
        Assert.Equal(1, store.SettingsCalls);
        Assert.Empty(store.RankUserIds);
        Assert.Empty(store.LeaderboardPages);
        Assert.Equal(0, cards.RenderCalls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RenderingFailurePublishesOnePublicSafeTextFallbackWithTheRequestedStatsAsync(
        bool profile, bool ranked)
    {
        using var fixture = new OfflineInteractionFixture();
        SetProperty(fixture.Guild, nameof(SocketGuild.Name), "@here <@&123> Server");
        var member = fixture.CreateMember(500, 1);
        SetUsername(member, "@everyone <@500> **member**");
        var joinedAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        typeof(SocketGuildUser).GetField("_joinedAtTicks", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(member, joinedAt.UtcTicks);
        var module = CreateModule(fixture, out var store, out var cards);
        store.Rank = ranked ? CreateRank(member.Id, 12, 125, 5) : null;

        await InvokeAsync(module, profile ? "profile" : "rank", member);

        using var payload = FinalResponse(fixture);
        Assert.Equal("The image card is unavailable. Here are the same stats as text.",
            payload.RootElement.GetProperty("content").GetString());
        Assert.Equal(0, payload.RootElement.GetProperty("attachments").GetArrayLength());
        var embed = SingleEmbed(payload);
        Assert.Equal($"＠everyone ＜＠500＞ \\*\\*member\\*\\* · {(profile ? "Profile" : "Rank")}",
            embed.GetProperty("title").GetString());
        Assert.Equal("＠here ＜＠&123＞ Server", embed.GetProperty("description").GetString());
        AssertNoUnsafeMentions(embed);
        var fields = embed.GetProperty("fields").EnumerateArray().ToDictionary(
            field => field.GetProperty("name").GetString()!,
            field => field.GetProperty("value").GetString()!);
        Assert.Equal(ranked ? "1" : "0", fields["Level"]);
        Assert.Equal(ranked ? "125" : "0", fields["Total XP"]);
        Assert.Equal(ranked ? "#12" : "Unranked", fields["Server rank"]);
        Assert.Equal(ranked ? "25 / 300 XP" : "0 / 100 XP", fields["Progress to next level"]);
        if (profile)
        {
            Assert.Equal(ranked ? "5" : "0", fields["Messages awarded XP"]);
            Assert.Equal($"<t:{joinedAt.ToUnixTimeSeconds()}:D>", fields["Joined this server"]);
            Assert.Contains("not a total message count", embed.GetProperty("footer").GetProperty("text").GetString());
            Assert.Equal(6, fields.Count);
        }
        else
        {
            Assert.Equal(4, fields.Count);
        }

        Assert.Equal(1, store.SettingsCalls);
        Assert.Equal(new[] { member.Id }, store.RankUserIds);
        Assert.Empty(store.LeaderboardPages);
        Assert.Equal(1, cards.RenderCalls);
        Assert.Same(member, cards.User);
        Assert.Same(store.Rank, cards.Rank);
        Assert.Equal(profile, cards.Profile);
        Assert.Equal(fixture.Guild.Name, cards.GuildName);
    }

    [Theory]
    [InlineData("rank")]
    [InlineData("profile")]
    public async Task ImageUploadFailurePublishesOnePublicSafeTextFallbackAndDisposesTheCardAsync(string command)
    {
        using var fixture = new OfflineInteractionFixture();
        SetProperty(fixture.Interaction, nameof(SocketInteraction.AttachmentSizeLimit), 1024UL);
        var logger = new CapturingLogger();
        var module = CreateModule(fixture, out var store, out var cards, logger);
        store.Rank = CreateRank(fixture.Moderator.Id, 12, 125);
        SetUsername(fixture.Moderator, "@everyone <@200> member");
        using var stream = new MemoryStream(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        stream.Position = stream.Length;
        cards.RenderedCard = stream;

        await InvokeAsync(module, command);

        using var payload = FinalResponse(fixture);
        AssertCardFallback(payload, command);
        var exception = Assert.Single(logger.Warnings);
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal("Offline tests do not upload files.", exception.Message);
        Assert.Empty(logger.Errors);
        Assert.Equal(1, cards.RenderCalls);
        Assert.Equal(new[] { fixture.Moderator.Id }, store.RankUserIds);
        Assert.False(stream.CanRead);
    }

    [Theory]
    [InlineData("rank")]
    [InlineData("profile")]
    public async Task OversizedCardsSkipUploadingAndPublishOnePublicSafeTextFallbackAsync(string command)
    {
        using var fixture = new OfflineInteractionFixture();
        SetProperty(fixture.Interaction, nameof(SocketInteraction.AttachmentSizeLimit), 8UL);
        var logger = new CapturingLogger();
        var module = CreateModule(fixture, out var store, out var cards, logger);
        store.Rank = CreateRank(fixture.Moderator.Id, 12, 125);
        SetUsername(fixture.Moderator, "@everyone <@200> member");
        using var stream = new MemoryStream(new byte[9]);
        cards.RenderedCard = stream;

        await InvokeAsync(module, command);

        using var payload = FinalResponse(fixture);
        AssertCardFallback(payload, command);
        var exception = Assert.Single(logger.Warnings);
        Assert.IsType<InvalidDataException>(exception);
        Assert.Contains("exceeds the interaction attachment limit", exception.Message);
        Assert.Empty(logger.Errors);
        Assert.Equal(1, cards.RenderCalls);
        Assert.Equal(new[] { fixture.Moderator.Id }, store.RankUserIds);
        Assert.False(stream.CanRead);
    }

    [Theory]
    [InlineData("rank")]
    [InlineData("profile")]
    public async Task CardCommandsDefaultToTheInvokingMemberAsync(string command)
    {
        using var fixture = new OfflineInteractionFixture();
        var module = CreateModule(fixture, out var store, out var cards);

        await InvokeAsync(module, command);

        using var payload = FinalResponse(fixture);
        Assert.Equal("The image card is unavailable. Here are the same stats as text.",
            payload.RootElement.GetProperty("content").GetString());
        Assert.Equal(new[] { fixture.Moderator.Id }, store.RankUserIds);
        Assert.Same(fixture.Moderator, cards.User);
        Assert.Equal(1, cards.RenderCalls);
    }

    [Fact]
    public async Task EmptyLeaderboardExplainsHowToEarnXpAndShowsTheFirstPageAsync()
    {
        using var fixture = new OfflineInteractionFixture();
        SetProperty(fixture.Guild, nameof(SocketGuild.Name), "Offline server");
        var module = CreateModule(fixture, out var store, out var cards);

        await module.LeaderboardAsync();

        using var payload = FinalResponse(fixture);
        var embed = SingleEmbed(payload);
        Assert.Equal("Offline server · XP leaderboard", embed.GetProperty("title").GetString());
        Assert.Equal("No members have earned XP yet. Eligible messages earn XP while leveling is enabled.",
            embed.GetProperty("description").GetString());
        Assert.Equal("Page 1 of 1 · 0 ranked members", embed.GetProperty("footer").GetProperty("text").GetString());
        Assert.Equal(new[] { 1 }, store.LeaderboardPages);
        Assert.Empty(store.RankUserIds);
        Assert.Equal(0, cards.RenderCalls);
    }

    [Fact]
    public async Task LeaderboardUsesAbsoluteRanksLimitsPagesToTenAndEscapesUsernamesAsync()
    {
        using var fixture = new OfflineInteractionFixture();
        SetProperty(fixture.Guild, nameof(SocketGuild.Name), "@here <@&123> Server");
        var member = fixture.CreateMember(500, 1);
        SetUsername(member, "@everyone <@500> **member**");
        var module = CreateModule(fixture, out var store, out var cards);
        store.Leaderboard = new LevelingLeaderboard(
            Enumerable.Range(0, 11).Select(index => CreateRank(500UL + (ulong)index, 11 + index, 125)).ToArray(), 23);

        await module.LeaderboardAsync(2);

        using var payload = FinalResponse(fixture);
        var embed = SingleEmbed(payload);
        Assert.Equal("＠here ＜＠&123＞ Server · XP leaderboard", embed.GetProperty("title").GetString());
        var lines = embed.GetProperty("description").GetString()!.Split("\n\n", StringSplitOptions.None);
        Assert.Equal(10, lines.Length);
        Assert.Equal("**#11 · ＠everyone ＜＠500＞ \\*\\*member\\*\\***\nLevel 1 · 125 XP", lines[0]);
        Assert.Equal("**#20 · Member 509**\nLevel 1 · 125 XP", lines[^1]);
        Assert.Equal("Page 2 of 3 · 23 ranked members", embed.GetProperty("footer").GetProperty("text").GetString());
        AssertNoUnsafeMentions(embed);
        Assert.Equal(new[] { 2 }, store.LeaderboardPages);
        Assert.Empty(store.RankUserIds);
        Assert.Equal(0, cards.RenderCalls);
    }

    [Theory]
    [InlineData(2, 0L, "1", "0")]
    [InlineData(2, 10L, "1", "10")]
    [InlineData(3, 11L, "2", "11")]
    [InlineData(3, 20L, "2", "20")]
    public async Task EmptyOutOfRangeLeaderboardPagesAdvertiseTheLastAvailablePageAsync(
        int page, long totalMembers, string lastPage, string memberCount)
    {
        using var fixture = new OfflineInteractionFixture();
        var module = CreateModule(fixture, out var store, out var cards);
        store.Leaderboard = new LevelingLeaderboard([], totalMembers);

        await module.LeaderboardAsync(page);

        using var payload = FinalResponse(fixture);
        var embed = SingleEmbed(payload);
        Assert.Equal($"That page is empty. Choose a page from 1 to {lastPage}.",
            embed.GetProperty("description").GetString());
        Assert.Equal($"Page {page} of {lastPage} · {memberCount} ranked members",
            embed.GetProperty("footer").GetProperty("text").GetString());
        Assert.Equal(new[] { page }, store.LeaderboardPages);
        Assert.Equal(0, cards.RenderCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1001)]
    public async Task InvalidLeaderboardPagesReplyBeforeLoadingSettingsOrStatsAsync(int page)
    {
        using var fixture = new OfflineInteractionFixture();
        var module = CreateModule(fixture, out var store, out var cards);

        await module.LeaderboardAsync(page);

        using var payload = FinalResponse(fixture);
        Assert.Equal("Choose a leaderboard page from 1 to 1000.", payload.RootElement.GetProperty("content").GetString());
        Assert.Equal(0, store.SettingsCalls);
        Assert.Empty(store.LeaderboardPages);
        Assert.Empty(store.RankUserIds);
        Assert.Equal(0, cards.RenderCalls);
    }

    private static LevelingModule CreateModule(
        OfflineInteractionFixture fixture, out LevelingStoreProxy storeProxy, out LevelingCardProxy cardProxy,
        ILogger<LevelingModule>? logger = null)
    {
        SetProperty(fixture.Guild, nameof(SocketGuild.Name), fixture.Guild.Name ?? "Offline server");
        SetUsername(fixture.Moderator, "offline-member");
        var store = DispatchProxy.Create<ILevelingStore, LevelingStoreProxy>();
        storeProxy = (LevelingStoreProxy)(object)store;
        var cards = DispatchProxy.Create<ILevelingCardService, LevelingCardProxy>();
        cardProxy = (LevelingCardProxy)(object)cards;
        var module = new LevelingModule(store, cards, new OfflineLifetime(), logger ?? NullLogger<LevelingModule>.Instance);
        fixture.Attach(module);
        return module;
    }

    private static Task InvokeAsync(LevelingModule module, string command, SocketGuildUser? user = null) => command switch
    {
        "rank" => module.RankAsync(user),
        "profile" => module.ProfileAsync(user),
        "leaderboard" => module.LeaderboardAsync(),
        _ => throw new ArgumentOutOfRangeException(nameof(command))
    };

    private static LevelingRank CreateRank(ulong userId, long position, long totalXp, long messageCount = 5) =>
        new(new LevelingMember(OfflineInteractionFixture.GuildId, userId, totalXp, messageCount,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)), position);

    private static JsonDocument FinalResponse(OfflineInteractionFixture fixture)
    {
        Assert.True(fixture.Requests.Count == 2, JsonSerializer.Serialize(fixture.Requests));
        var acknowledgement = Assert.Single(fixture.Requests, request =>
            request.Endpoint.Contains("/callback", StringComparison.Ordinal));
        Assert.Equal("POST", acknowledgement.Method);
        using var deferred = JsonDocument.Parse(acknowledgement.Json!);
        Assert.Equal(5, deferred.RootElement.GetProperty("type").GetInt32());
        var flags = deferred.RootElement.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("flags", out var value) ? value.GetInt32() : 0;
        Assert.Equal(0, flags & 64);
        var response = Assert.Single(fixture.Requests, request =>
            request.Endpoint.Contains("webhooks/", StringComparison.Ordinal));
        Assert.Equal("PATCH", response.Method);
        Assert.EndsWith("/messages/@original", response.Endpoint);
        var payload = JsonDocument.Parse(response.Json!);
        var mentions = payload.RootElement.GetProperty("allowed_mentions");
        foreach (var name in new[] { "parse", "users", "roles" })
        {
            if (mentions.TryGetProperty(name, out var values))
            {
                Assert.True(values.ValueKind == JsonValueKind.Null || values.GetArrayLength() == 0);
            }
        }

        if (mentions.TryGetProperty("replied_user", out var repliedUser))
        {
            Assert.True(repliedUser.ValueKind is JsonValueKind.Null or JsonValueKind.False);
        }

        return payload;
    }

    private static void AssertCardFallback(JsonDocument payload, string command)
    {
        Assert.Equal("The image card is unavailable. Here are the same stats as text.",
            payload.RootElement.GetProperty("content").GetString());
        Assert.Equal(0, payload.RootElement.GetProperty("attachments").GetArrayLength());
        var embed = SingleEmbed(payload);
        Assert.Equal($"＠everyone ＜＠200＞ member · {(command == "profile" ? "Profile" : "Rank")}",
            embed.GetProperty("title").GetString());
        AssertNoUnsafeMentions(embed);
        var fields = embed.GetProperty("fields").EnumerateArray().ToDictionary(
            field => field.GetProperty("name").GetString()!,
            field => field.GetProperty("value").GetString()!);
        Assert.Equal("1", fields["Level"]);
        Assert.Equal("125", fields["Total XP"]);
        Assert.Equal("#12", fields["Server rank"]);
        Assert.Equal("25 / 300 XP", fields["Progress to next level"]);
    }

    private static JsonElement SingleEmbed(JsonDocument payload) =>
        Assert.Single(payload.RootElement.GetProperty("embeds").EnumerateArray());

    private static void AssertNoUnsafeMentions(JsonElement embed)
    {
        var text = embed.ToString();
        Assert.DoesNotContain("@everyone", text);
        Assert.DoesNotContain("@here", text);
        Assert.DoesNotContain("<@", text);
    }

    private static void SetUsername(SocketGuildUser member, string username)
    {
        var globalUser = typeof(SocketGuildUser).GetProperty("GlobalUser", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(member)!;
        SetProperty(globalUser, nameof(SocketGuildUser.Username), username);
    }

    private static void SetProperty(object target, string name, object value)
    {
        for (var type = target.GetType(); type is not null; type = type.BaseType)
        {
            var field = type.GetField($"<{name}>k__BackingField",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (field is not null)
            {
                field.SetValue(target, value);
                return;
            }
        }

        throw new InvalidOperationException($"No backing field for {name}.");
    }

    public class LevelingStoreProxy : DispatchProxy
    {
        public bool Enabled { get; set; } = true;
        public LevelingRank? Rank { get; set; }
        public LevelingLeaderboard Leaderboard { get; set; } = new([], 0);
        public int SettingsCalls { get; private set; }
        public List<ulong> RankUserIds { get; } = [];
        public List<int> LeaderboardPages { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Assert.Equal(OfflineInteractionFixture.GuildId, args![0]);
            Assert.Equal(CancellationToken.None, args[^1]);
            switch (targetMethod!.Name)
            {
                case nameof(ILevelingStore.GetSettingsAsync):
                    SettingsCalls++;
                    return Task.FromResult(new LevelingSettings(OfflineInteractionFixture.GuildId, Enabled, 60, [], [], []));
                case nameof(ILevelingStore.GetRankAsync):
                    RankUserIds.Add((ulong)args[1]!);
                    return Task.FromResult(Rank);
                case nameof(ILevelingStore.GetLeaderboardAsync):
                    LeaderboardPages.Add((int)args[1]!);
                    return Task.FromResult(Leaderboard);
                default:
                    throw new InvalidOperationException($"Unexpected leveling store call: {targetMethod.Name}");
            }
        }
    }

    public class LevelingCardProxy : DispatchProxy
    {
        public MemoryStream? RenderedCard { get; set; }
        public int RenderCalls { get; private set; }
        public SocketGuildUser? User { get; private set; }
        public string? GuildName { get; private set; }
        public LevelingRank? Rank { get; private set; }
        public bool Profile { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Assert.Equal(nameof(ILevelingCardService.RenderAsync), targetMethod!.Name);
            Assert.Equal(CancellationToken.None, args![4]);
            RenderCalls++;
            User = (SocketGuildUser)args[0]!;
            GuildName = (string)args[1]!;
            Rank = (LevelingRank?)args[2];
            Profile = (bool)args[3]!;
            return RenderedCard is { } stream
                ? Task.FromResult(stream)
                : Task.FromException<MemoryStream>(new IOException("Simulated leveling card rendering failure."));
        }
    }

    private sealed class CapturingLogger : ILogger<LevelingModule>
    {
        public List<Exception> Warnings { get; } = [];
        public List<Exception> Errors { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is not null)
            {
                if (logLevel == LogLevel.Warning)
                {
                    Warnings.Add(exception);
                }
                else if (logLevel == LogLevel.Error)
                {
                    Errors.Add(exception);
                }
            }
        }
    }

    private sealed class OfflineLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
