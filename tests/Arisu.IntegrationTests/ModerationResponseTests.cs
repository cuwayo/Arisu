using System.Net;
using System.Reflection;
using System.Text.Json;
using Arisu.Modules;
using Arisu.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Arisu.IntegrationTests;

public sealed class ModerationResponseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnbanPublishesOneFinalSuccessResponseWithHistorySaveOutcome(bool historySaved)
    {
        using var fixture = new OfflineInteractionFixture();
        fixture.RestCallback = request =>
        {
            if (!request.Endpoint.Contains("guilds/100/bans/500", StringComparison.Ordinal))
            {
                return null;
            }

            return request.Method switch
            {
                "GET" => OfflineInteractionFixture.JsonResponse(
                    """{"reason":"Original ban","user":{"id":"500","username":"offline-user","discriminator":"0000","avatar":null}}"""),
                "DELETE" => OfflineInteractionFixture.JsonResponse("", HttpStatusCode.NoContent),
                _ => throw new InvalidOperationException($"Unexpected ban request: {request.Method}")
            };
        };
        var history = DispatchProxy.Create<IModerationHistoryStore, HistoryStoreProxy>();
        var historyProxy = (HistoryStoreProxy)(object)history;
        historyProxy.HistorySaved = historySaved;
        var module = new ModerationModule(history, NullLogger<ModerationModule>.Instance);
        fixture.Attach(module);

        await module.UnbanAsync("500", "Appeal accepted");

        Assert.Contains(fixture.Requests, request => request.Method == "DELETE" &&
            request.Endpoint.Contains("guilds/100/bans/500", StringComparison.Ordinal));
        Assert.Equal(1, historyProxy.RecordCount);
        var response = Assert.Single(fixture.Requests, request =>
            request.Endpoint.Contains("webhooks/", StringComparison.Ordinal));
        Assert.Equal("PATCH", response.Method);
        using var payload = JsonDocument.Parse(response.Json!);
        var content = payload.RootElement.GetProperty("content").GetString()!;
        Assert.StartsWith("Unbanned <@500>.", content);
        Assert.Equal(!historySaved, content.Contains("moderation history could not be saved", StringComparison.Ordinal));
        var mentionParsing = payload.RootElement.GetProperty("allowed_mentions").GetProperty("parse");
        Assert.True(mentionParsing.ValueKind == JsonValueKind.Null || mentionParsing.GetArrayLength() == 0);
    }

    [Theory]
    [InlineData("Kicked <@200>.")]
    [InlineData("Banned <@200>.")]
    [InlineData("Unbanned <@200>.")]
    [InlineData("Timed out <@200> for 00:10:00. Sent a direct message.")]
    [InlineData("Removed the timeout for <@200>.")]
    [InlineData("Changed <@200>'s nickname.")]
    public void FailedHistorySaveRemainsInTheFinalSuccessResponse(string success)
    {
        var result = FormatResponse(success, historySaved: false);

        Assert.StartsWith(success, result);
        Assert.Contains("moderation history could not be saved", result);
    }

    [Fact]
    public void SuccessfulHistorySaveKeepsTheOriginalSuccessResponse()
    {
        const string success = "Kicked <@200>.";

        Assert.Equal(success, FormatResponse(success, historySaved: true));
    }

    public class HistoryStoreProxy : DispatchProxy
    {
        public bool HistorySaved { get; set; }
        public int RecordCount { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Assert.Equal(nameof(IModerationHistoryStore.RecordCaseAsync), targetMethod!.Name);
            Assert.Equal(OfflineInteractionFixture.GuildId, args![0]);
            Assert.Equal(500UL, args[1]);
            Assert.Equal(OfflineInteractionFixture.ModeratorId, args[2]);
            Assert.Equal("Unban", args[3]);
            Assert.Equal("Appeal accepted", args[4]);
            RecordCount++;
            return HistorySaved
                ? Task.CompletedTask
                : Task.FromException(new IOException("Simulated history-save failure."));
        }
    }

    private static string FormatResponse(string success, bool historySaved) =>
        (string)typeof(ModerationModule).GetMethod("WithHistoryNotice", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [success, historySaved])!;
}
