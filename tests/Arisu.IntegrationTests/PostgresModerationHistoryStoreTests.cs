using Arisu.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Arisu.IntegrationTests;

public sealed class PostgresModerationHistoryStoreTests
{
    private static readonly DateTimeOffset WarningTime =
        new(2026, 11, 17, 12, 34, 56, TimeSpan.FromHours(9));

    [Fact]
    public async Task WarningLookupRequiresItsGuildAndDoesNotChangeThePersistedWarningAsync()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var warnings = database.CreateStore();
        await warnings.InitializeAsync();
        using var history = CreateHistoryStore(database);
        const ulong guildId = ulong.MaxValue;
        var issued = await warnings.AddWarningAsync(guildId, ulong.MaxValue - 1, ulong.MaxValue - 2,
            "Original 'quoted' 日本語\nreason", WarningTime);
        var other = await warnings.AddWarningAsync(guildId - 1, issued.Warning.MemberId, 300,
            "Other guild", WarningTime);

        Assert.Equal(issued.Warning, await history.GetWarningAsync(guildId, issued.Warning.Id));
        Assert.Null(await history.GetWarningAsync(guildId - 1, issued.Warning.Id));
        Assert.Null(await history.GetWarningAsync(guildId, other.Warning.Id));
        Assert.Null(await history.GetWarningAsync(guildId, long.MaxValue));
        Assert.Equal(issued.Warning, Assert.Single((await warnings.GetWarningsAsync(
            guildId, issued.Warning.MemberId)).RecentWarnings));
        Assert.Equal(other.Warning, Assert.Single((await warnings.GetWarningsAsync(
            guildId - 1, issued.Warning.MemberId)).RecentWarnings));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task WarningLookupRejectsNonpositiveIdsWithoutOpeningTheDatabaseAsync(long warningId)
    {
        await using var dataSource = NpgsqlDataSource.Create("Host=localhost;Database=unused;Username=unused");
        using var history = new PostgresModerationHistoryStore(
            dataSource, NullLogger<PostgresModerationHistoryStore>.Instance);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => history.GetWarningAsync(100, warningId));
    }

    [Fact]
    public async Task RemoveWarningRequiresItsGuildAndReturnsThePersistedWarningAsync()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var warnings = database.CreateStore();
        await warnings.InitializeAsync();
        using var history = CreateHistoryStore(database);
        const ulong guildId = ulong.MaxValue;
        var issued = await warnings.AddWarningAsync(guildId, ulong.MaxValue - 1, ulong.MaxValue - 2,
            "Original 'quoted' 日本語\nreason", WarningTime);
        var other = await warnings.AddWarningAsync(guildId - 1, issued.Warning.MemberId, 300,
            "Other guild", WarningTime);

        Assert.Null(await history.RemoveWarningAsync(guildId - 1, issued.Warning.Id));
        Assert.Equal(issued.Warning, Assert.Single((await warnings.GetWarningsAsync(
            guildId, issued.Warning.MemberId)).RecentWarnings));
        Assert.Equal(issued.Warning, await history.RemoveWarningAsync(guildId, issued.Warning.Id));
        Assert.Null(await history.RemoveWarningAsync(guildId, issued.Warning.Id));
        Assert.Equal(0, (await warnings.GetWarningsAsync(guildId, issued.Warning.MemberId)).TotalCount);
        Assert.Equal(other.Warning, Assert.Single((await warnings.GetWarningsAsync(
            guildId - 1, issued.Warning.MemberId)).RecentWarnings));
    }

    [Fact]
    public async Task EditWarningRequiresItsGuildAndPreservesItsIdentityAndPreviousReasonAsync()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var warnings = database.CreateStore();
        await warnings.InitializeAsync();
        using var history = CreateHistoryStore(database);
        var issued = await warnings.AddWarningAsync(100, 200, ulong.MaxValue, "Original reason", WarningTime);
        var other = await warnings.AddWarningAsync(101, 200, 300, "Other guild", WarningTime);
        var replacement = new string('あ', 400);

        Assert.Null(await history.UpdateWarningReasonAsync(101, issued.Warning.Id, replacement));
        Assert.Equal(issued.Warning, Assert.Single((await warnings.GetWarningsAsync(100, 200)).RecentWarnings));
        var change = await history.UpdateWarningReasonAsync(100, issued.Warning.Id, replacement);

        Assert.NotNull(change);
        Assert.Equal(issued.Warning.Reason, change.PreviousReason);
        Assert.Equal(issued.Warning with { Reason = replacement }, change.Warning);
        Assert.Equal(change.Warning, Assert.Single((await warnings.GetWarningsAsync(100, 200)).RecentWarnings));
        var secondChange = await history.UpdateWarningReasonAsync(100, issued.Warning.Id, "Edited again");
        Assert.NotNull(secondChange);
        Assert.Equal(replacement, secondChange.PreviousReason);
        Assert.Equal("Edited again", secondChange.Warning.Reason);
        Assert.Null(await history.UpdateWarningReasonAsync(100, long.MaxValue, "Missing warning"));
        Assert.Equal(other.Warning, Assert.Single((await warnings.GetWarningsAsync(101, 200)).RecentWarnings));
    }

    [Fact]
    public async Task ClearWarningsDeletesOnlyTheRequestedMemberInTheRequestedGuildAsync()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var warnings = database.CreateStore();
        await warnings.InitializeAsync();
        using var history = CreateHistoryStore(database);
        await warnings.AddWarningAsync(100, 200, 300, "First warning", WarningTime);
        await warnings.AddWarningAsync(100, 200, 300, "Second warning", WarningTime);
        var otherMember = await warnings.AddWarningAsync(100, 201, 300, "Other member", WarningTime);
        var otherGuild = await warnings.AddWarningAsync(101, 200, 300, "Other guild", WarningTime);

        Assert.Equal(2, await history.ClearWarningsAsync(100, 200));
        Assert.Equal(0, await history.ClearWarningsAsync(100, 200));
        var cleared = await warnings.GetWarningsAsync(100, 200);
        Assert.Equal(0, cleared.TotalCount);
        Assert.Empty(cleared.RecentWarnings);
        Assert.Equal(otherMember.Warning, Assert.Single((await warnings.GetWarningsAsync(100, 201)).RecentWarnings));
        Assert.Equal(otherGuild.Warning, Assert.Single((await warnings.GetWarningsAsync(101, 200)).RecentWarnings));
    }

    [Fact]
    public async Task RemovingAndClearingWarningsPreservesTheAppliedEscalationUntilThePolicyChangesAsync()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var warnings = database.CreateStore();
        await warnings.InitializeAsync();
        using var history = CreateHistoryStore(database);
        var policy = new WarningEscalation(2, 10);
        await warnings.SetWarningEscalationAsync(100, policy);
        var first = await warnings.AddWarningAsync(100, 200, 300, "Below threshold", WarningTime);
        Assert.False(first.ShouldApplyEscalation);
        var crossing = await warnings.AddWarningAsync(100, 200, 300, "Threshold reached", WarningTime);
        Assert.True(crossing.ShouldApplyEscalation);

        Assert.Equal(crossing.Warning, await history.RemoveWarningAsync(100, crossing.Warning.Id));
        var afterRemoval = await warnings.AddWarningAsync(100, 200, 300, "Threshold reached again", WarningTime);
        Assert.Equal(2, afterRemoval.WarningCount);
        Assert.False(afterRemoval.ShouldApplyEscalation);
        Assert.Equal(2, await history.ClearWarningsAsync(100, 200));
        for (var index = 1; index <= 3; index++)
        {
            var afterClear = await warnings.AddWarningAsync(100, 200, 300, $"After clear {index}", WarningTime);
            Assert.Equal(index, afterClear.WarningCount);
            Assert.Equal(policy, afterClear.Escalation);
            Assert.False(afterClear.ShouldApplyEscalation);
        }

        await warnings.SetWarningEscalationAsync(100, new WarningEscalation(2, 20));
        Assert.True((await warnings.AddWarningAsync(100, 200, 300, "New policy", WarningTime)).ShouldApplyEscalation);
    }

    [Fact]
    public async Task CasesReturnTheMostRecentEightCapAtOneHundredAndRoundTripBoundaryValuesAsync()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var warnings = database.CreateStore();
        await warnings.InitializeAsync();
        using var writer = CreateHistoryStore(database);
        const ulong guildId = ulong.MaxValue;
        const ulong targetId = ulong.MaxValue - 1;
        const ulong moderatorId = ulong.MaxValue - 2;
        const string details = "Details: 'quoted' 日本語\nsecond line";
        var maximumReason = new string('あ', 400);
        var maximumAction = new string('x', 100);
        Assert.Empty(await writer.GetCasesAsync(guildId, targetId));
        for (var index = 0; index < 105; index++)
        {
            await writer.RecordCaseAsync(guildId, targetId, moderatorId,
                index == 104 ? maximumAction : "warn",
                index == 104 ? maximumReason : $"Case {index}",
                index % 2 == 0 ? details : null,
                index == 104 ? DateTimeOffset.MaxValue : WarningTime.AddSeconds(index));
        }

        using var reader = CreateHistoryStore(database);
        var recent = await reader.GetCasesAsync(guildId, targetId);
        var capped = await reader.GetCasesAsync(guildId, targetId, int.MaxValue);
        var bounded = await reader.GetCasesAsync(guildId, targetId, 3);
        Assert.Equal(8, recent.Count);
        Assert.Equal(100, capped.Count);
        Assert.Equal(capped.Take(8), recent);
        Assert.Equal(capped.Take(3), bounded);
        Assert.Equal(capped.Select(item => item.Id).OrderDescending(), capped.Select(item => item.Id));
        Assert.Equal(100, capped.Select(item => item.Id).Distinct().Count());
        Assert.All(capped, item =>
        {
            Assert.True(item.Id > 0);
            Assert.Equal(guildId, item.GuildId);
            Assert.Equal(targetId, item.TargetId);
            Assert.Equal(moderatorId, item.ModeratorId);
            Assert.Equal(TimeSpan.Zero, item.CreatedAt.Offset);
        });
        Assert.Equal(maximumReason, recent[0].Reason);
        Assert.Equal(maximumAction, recent[0].Action);
        Assert.Equal(details, recent[0].Details);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.MaxValue.ToUnixTimeSeconds()), recent[0].CreatedAt);
        for (var position = 1; position < capped.Count; position++)
        {
            var index = 104 - position;
            Assert.Equal($"Case {index}", capped[position].Reason);
            Assert.Equal("warn", capped[position].Action);
            Assert.Equal(index % 2 == 0 ? details : null, capped[position].Details);
            Assert.Equal(WarningTime.AddSeconds(index), capped[position].CreatedAt);
        }
    }

    [Fact]
    public async Task CasesAreIsolatedByBothGuildAndTargetAsync()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var warnings = database.CreateStore();
        await warnings.InitializeAsync();
        using var history = CreateHistoryStore(database);
        await history.RecordCaseAsync(100, 200, 300, "warn", "Requested member", null, WarningTime);
        await history.RecordCaseAsync(100, 201, 300, "ban", "Other member", "details", WarningTime);
        await history.RecordCaseAsync(101, 200, 300, "kick", "Other guild", null, WarningTime);

        Assert.Equal("Requested member", Assert.Single(await history.GetCasesAsync(100, 200)).Reason);
        Assert.Equal("Other member", Assert.Single(await history.GetCasesAsync(100, 201)).Reason);
        Assert.Equal("Other guild", Assert.Single(await history.GetCasesAsync(101, 200)).Reason);
        Assert.Empty(await history.GetCasesAsync(101, 201));
    }

    [Fact]
    public async Task ConcurrentAddRemoveEditAndClearWaitForTheSameGuildLockAsync()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var warnings = database.CreateStore();
        await warnings.InitializeAsync();
        using var history = CreateHistoryStore(database);
        await history.InitializeAsync();
        var guildId = BitConverter.ToUInt64(Guid.NewGuid().ToByteArray());
        var removed = await warnings.AddWarningAsync(guildId, 202, 300, "Remove me", WarningTime);
        var edited = await warnings.AddWarningAsync(guildId, 203, 300, "Edit me", WarningTime);
        await warnings.AddWarningAsync(guildId, 204, 300, "Clear me", WarningTime);
        await warnings.AddWarningAsync(guildId, 204, 300, "Clear me too", WarningTime);
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(@guildHigh, @guildLow);", connection, transaction))
        {
            command.Parameters.AddWithValue("guildHigh", unchecked((int)(guildId >> 32)));
            command.Parameters.AddWithValue("guildLow", unchecked((int)guildId));
            await command.ExecuteNonQueryAsync();
        }

        var addTask = warnings.AddWarningAsync(guildId, 201, 300, "Concurrent addition", WarningTime);
        var removeTask = history.RemoveWarningAsync(guildId, removed.Warning.Id);
        var editTask = history.UpdateWarningReasonAsync(guildId, edited.Warning.Id, "Concurrent edit");
        var clearTask = history.ClearWarningsAsync(guildId, 204);
        var allWaiting = false;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var check = database.DataSource.CreateCommand("""
                SELECT COUNT(*) FROM pg_locks
                WHERE locktype = 'advisory' AND NOT granted AND objsubid = 2
                    AND classid::bigint = @guildHigh AND objid::bigint = @guildLow
                    AND database = (SELECT oid FROM pg_database WHERE datname = current_database());
                """);
            check.Parameters.AddWithValue("guildHigh", (long)(uint)(guildId >> 32));
            check.Parameters.AddWithValue("guildLow", (long)(uint)guildId);
            while (!timeout.IsCancellationRequested)
            {
                if ((long)(await check.ExecuteScalarAsync())! == 4)
                {
                    allWaiting = true;
                    break;
                }

                if (addTask.IsCompleted || removeTask.IsCompleted || editTask.IsCompleted || clearTask.IsCompleted)
                {
                    break;
                }

                await Task.Delay(20);
            }
        }
        finally
        {
            await transaction.RollbackAsync();
            await Task.WhenAll(addTask, removeTask, editTask, clearTask).WaitAsync(TimeSpan.FromSeconds(15));
        }

        var added = await addTask;
        var removedWarning = await removeTask;
        var change = await editTask;
        var clearedCount = await clearTask;
        Assert.True(allWaiting, "Add, remove, edit, and clear must all wait for the guild advisory lock.");
        Assert.Equal(1, added.WarningCount);
        Assert.Equal(removed.Warning, removedWarning);
        Assert.NotNull(change);
        Assert.Equal("Edit me", change.PreviousReason);
        Assert.Equal(edited.Warning with { Reason = "Concurrent edit" }, change.Warning);
        Assert.Equal(2, clearedCount);
        Assert.Equal(added.Warning, Assert.Single((await warnings.GetWarningsAsync(guildId, 201)).RecentWarnings));
        Assert.Equal(0, (await warnings.GetWarningsAsync(guildId, 202)).TotalCount);
        Assert.Equal(change.Warning, Assert.Single((await warnings.GetWarningsAsync(guildId, 203)).RecentWarnings));
        Assert.Equal(0, (await warnings.GetWarningsAsync(guildId, 204)).TotalCount);
    }

    private static PostgresModerationHistoryStore CreateHistoryStore(PostgresTestDatabase database) =>
        new(database.DataSource, NullLogger<PostgresModerationHistoryStore>.Instance);
}
