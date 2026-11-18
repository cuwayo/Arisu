using Arisu.Storage;
using Xunit;

namespace Arisu.IntegrationTests;

public sealed class PostgresGuildChannelConfigurationStoreTests
{
    private static readonly DateTimeOffset WarningTime =
        new(2026, 11, 17, 12, 34, 56, TimeSpan.FromHours(9));

    [Fact]
    public async Task InitializationIsIdempotentAndAllSettingsSurviveAnotherStoreInstanceAsync()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var store = database.CreateStore();
        var anotherStore = database.CreateStore();
        await Task.WhenAll(
            store.InitializeAsync(),
            store.InitializeAsync(),
            anotherStore.InitializeAsync());
        const ulong guildId = ulong.MaxValue;
        const string background = "guilds/18446744073709551615/背景 'night'.png";

        await AssertSettingsAsync(store, guildId, null, null, null, null);
        Assert.Null(await store.GetWarningEscalationAsync(guildId));
        var emptyHistory = await store.GetWarningsAsync(guildId, ulong.MaxValue);
        Assert.Equal(0, emptyHistory.TotalCount);
        Assert.Empty(emptyHistory.RecentWarnings);

        await store.SetAuditChannelIdAsync(guildId, ulong.MaxValue);
        await store.SetMemberLogChannelIdAsync(guildId, ulong.MaxValue - 1);
        await store.SetEventChannelIdAsync(guildId, ulong.MaxValue - 2);
        await store.SetEventBackgroundObjectKeyAsync(guildId, background);
        await anotherStore.InitializeAsync();

        await AssertSettingsAsync(
            anotherStore, guildId, ulong.MaxValue, ulong.MaxValue - 1, ulong.MaxValue - 2, background);

        await anotherStore.SetAuditChannelIdAsync(guildId, 123);
        await anotherStore.SetMemberLogChannelIdAsync(guildId, 456);
        await anotherStore.SetEventChannelIdAsync(guildId, 789);
        await anotherStore.SetEventBackgroundObjectKeyAsync(guildId, "replacement.png");

        await AssertSettingsAsync(store, guildId, 123, 456, 789, "replacement.png");
    }

    [Fact]
    public async Task RemovingSettingsPreservesOtherColumnsAndDeletesTheEmptyRowAsync()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var store = database.CreateStore();
        const ulong guildId = 100;
        await store.SetAuditChannelIdAsync(guildId, 101);
        await store.SetMemberLogChannelIdAsync(guildId, 102);
        await store.SetEventChannelIdAsync(guildId, 103);
        await store.SetEventBackgroundObjectKeyAsync(guildId, "background.png");
        await store.SetWarningEscalationAsync(guildId, new WarningEscalation(2, 10));
        await store.AddWarningAsync(guildId, 200, 300, "Keep warning history", WarningTime);

        await store.RemoveAuditChannelIdAsync(guildId);
        await AssertSettingsAsync(store, guildId, null, 102, 103, "background.png");
        await store.RemoveMemberLogChannelIdAsync(guildId);
        await AssertSettingsAsync(store, guildId, null, null, 103, "background.png");
        await store.RemoveEventChannelIdAsync(guildId);
        await AssertSettingsAsync(store, guildId, null, null, null, "background.png");
        await store.RemoveEventBackgroundObjectKeyAsync(guildId);
        await AssertSettingsAsync(store, guildId, null, null, null, null);

        await using (var command = database.DataSource.CreateCommand("SELECT COUNT(*) FROM GuildChannelSettings;"))
        {
            Assert.Equal(0L, await command.ExecuteScalarAsync());
        }

        // Removing absent settings is idempotent and does not remove moderation data.
        await store.RemoveAuditChannelIdAsync(guildId);
        await store.RemoveMemberLogChannelIdAsync(guildId);
        await store.RemoveEventChannelIdAsync(guildId);
        await store.RemoveEventBackgroundObjectKeyAsync(guildId);
        Assert.Equal(new WarningEscalation(2, 10), await store.GetWarningEscalationAsync(guildId));
        Assert.Equal(1, (await store.GetWarningsAsync(guildId, 200)).TotalCount);

        await store.SetMemberLogChannelIdAsync(guildId, 104);
        await AssertSettingsAsync(store, guildId, null, 104, null, null);
    }

    [Fact]
    public async Task WarningHistoryBoundsRecentResultsButPreservesTotalsSnowflakesAndTimestampsAsync()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var store = database.CreateStore();
        const ulong guildId = ulong.MaxValue;
        const ulong memberId = ulong.MaxValue - 1;
        const ulong moderatorId = ulong.MaxValue - 2;
        var issued = new List<MemberWarning>();
        for (var index = 0; index < 12; index++)
        {
            // Decreasing dates verify that history follows issue order, not event time.
            var createdAt = index == 11
                ? new DateTimeOffset(1969, 12, 31, 23, 59, 59, TimeSpan.Zero)
                : WarningTime.AddSeconds(-index);
            var result = await store.AddWarningAsync(
                guildId, memberId, moderatorId, $"Warning {index}: 'quoted' 日本語\nsecond line", createdAt);
            Assert.Equal(index + 1, result.WarningCount);
            Assert.Null(result.Escalation);
            Assert.False(result.ShouldApplyEscalation);
            Assert.True(result.Warning.Id > 0);
            Assert.Equal(guildId, result.Warning.GuildId);
            Assert.Equal(memberId, result.Warning.MemberId);
            Assert.Equal(moderatorId, result.Warning.ModeratorId);
            Assert.Equal(createdAt, result.Warning.CreatedAt);
            issued.Add(result.Warning);
        }

        var reader = database.CreateStore();
        var bounded = await reader.GetWarningsAsync(guildId, memberId, maximumCount: 3);
        var defaultHistory = await reader.GetWarningsAsync(guildId, memberId);
        var fullHistory = await reader.GetWarningsAsync(guildId, memberId, maximumCount: 100);
        Assert.Equal(12, bounded.TotalCount);
        Assert.Equal(12, defaultHistory.TotalCount);
        Assert.Equal(12, fullHistory.TotalCount);
        Assert.Equal(issued.AsEnumerable().Reverse().Take(3), bounded.RecentWarnings);
        Assert.Equal(issued.AsEnumerable().Reverse().Take(8), defaultHistory.RecentWarnings);
        Assert.Equal(issued.AsEnumerable().Reverse(), fullHistory.RecentWarnings);
        Assert.All(fullHistory.RecentWarnings, warning => Assert.Equal(TimeSpan.Zero, warning.CreatedAt.Offset));
        Assert.Equal(12, issued.Select(warning => warning.Id).Distinct().Count());
    }

    [Fact]
    public async Task ConcurrentWarningsReturnEveryCountAndCrossTheEscalationThresholdExactlyOnceAsync()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var stores = new[] { database.CreateStore(), database.CreateStore() };
        const ulong guildId = 100;
        const ulong memberId = 200;
        const int warningCount = 24;
        var policy = new WarningEscalation(8, 30);
        await stores[0].SetWarningEscalationAsync(guildId, policy);

        var results = await RunConcurrentlyAsync(Enumerable.Range(0, warningCount)
            .Select<int, Func<Task<WarningIssueResult>>>(index => () => stores[index % stores.Length].AddWarningAsync(
                guildId, memberId, (ulong)(300 + index), $"Concurrent warning {index}", WarningTime)));

        Assert.Equal(Enumerable.Range(1, warningCount), results.Select(result => result.WarningCount).Order());
        Assert.Equal(warningCount, results.Select(result => result.Warning.Id).Distinct().Count());
        Assert.All(results, result => Assert.Equal(policy, result.Escalation));
        var crossing = Assert.Single(results, result => result.ShouldApplyEscalation);
        Assert.Equal(policy.Threshold, crossing.WarningCount);
        var history = await stores[1].GetWarningsAsync(guildId, memberId, maximumCount: warningCount);
        Assert.Equal(warningCount, history.TotalCount);
        Assert.Equal(
            results.Select(result => result.Warning.Id).OrderDescending(),
            history.RecentWarnings.Select(warning => warning.Id));

        var later = await stores[1].AddWarningAsync(guildId, memberId, 300, "Already escalated", WarningTime);
        Assert.Equal(warningCount + 1, later.WarningCount);
        Assert.False(later.ShouldApplyEscalation);
    }

    [Fact]
    public async Task SettingOrRemovingThePolicyResetsApplicationsWithoutDeletingHistoryAsync()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var store = database.CreateStore();
        const ulong guildId = 100;
        ulong[] members = [200, 201];
        var firstPolicy = new WarningEscalation(1, 5);
        await store.SetWarningEscalationAsync(guildId, firstPolicy);
        foreach (var memberId in members)
        {
            Assert.True((await store.AddWarningAsync(guildId, memberId, 300, "Initial escalation", WarningTime))
                .ShouldApplyEscalation);
        }

        var updatedPolicy = new WarningEscalation(2, 15);
        await store.SetWarningEscalationAsync(guildId, updatedPolicy);
        Assert.Equal(updatedPolicy, await store.GetWarningEscalationAsync(guildId));
        foreach (var memberId in members)
        {
            var reapplied = await store.AddWarningAsync(guildId, memberId, 300, "Updated policy", WarningTime);
            Assert.True(reapplied.ShouldApplyEscalation);
            Assert.Equal(updatedPolicy, reapplied.Escalation);
            Assert.Equal(2, reapplied.WarningCount);
            Assert.False((await store.AddWarningAsync(guildId, memberId, 300, "Already applied", WarningTime))
                .ShouldApplyEscalation);
        }

        // Saving an unchanged policy also resets the application markers.
        await store.SetWarningEscalationAsync(guildId, updatedPolicy);
        Assert.True((await store.AddWarningAsync(guildId, members[0], 300, "Saved again", WarningTime))
            .ShouldApplyEscalation);

        await store.RemoveWarningEscalationAsync(guildId);
        await store.RemoveWarningEscalationAsync(guildId);
        Assert.Null(await store.GetWarningEscalationAsync(guildId));
        var disabled = await store.AddWarningAsync(guildId, members[0], 300, "Policy disabled", WarningTime);
        Assert.Null(disabled.Escalation);
        Assert.False(disabled.ShouldApplyEscalation);
        Assert.Equal(5, disabled.WarningCount);

        await store.SetWarningEscalationAsync(guildId, firstPolicy);
        Assert.True((await store.AddWarningAsync(guildId, members[0], 300, "Policy reenabled", WarningTime))
            .ShouldApplyEscalation);
        Assert.Equal(6, (await store.GetWarningsAsync(guildId, members[0])).TotalCount);
        Assert.Equal(3, (await store.GetWarningsAsync(guildId, members[1])).TotalCount);
    }

    [Fact]
    public async Task RemovingAnApplicationAllowsOneRetryForOnlyTheSelectedMemberAsync()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var stores = new[] { database.CreateStore(), database.CreateStore() };
        const ulong guildId = 100;
        const ulong memberId = 200;
        const ulong otherMemberId = 201;
        await stores[0].SetWarningEscalationAsync(guildId, new WarningEscalation(1, 10));
        Assert.True((await stores[0].AddWarningAsync(guildId, memberId, 300, "First attempt", WarningTime))
            .ShouldApplyEscalation);
        Assert.True((await stores[0].AddWarningAsync(guildId, otherMemberId, 300, "Other member", WarningTime))
            .ShouldApplyEscalation);

        await stores[1].RemoveWarningEscalationApplicationAsync(guildId, memberId);
        await stores[1].RemoveWarningEscalationApplicationAsync(guildId, memberId);
        var retries = await RunConcurrentlyAsync(Enumerable.Range(0, 8)
            .Select<int, Func<Task<WarningIssueResult>>>(index => () => stores[index % stores.Length].AddWarningAsync(
                guildId, memberId, 300, $"Retry {index}", WarningTime)));

        Assert.Single(retries, result => result.ShouldApplyEscalation);
        Assert.Equal(Enumerable.Range(2, 8), retries.Select(result => result.WarningCount).Order());
        Assert.False((await stores[1].AddWarningAsync(guildId, otherMemberId, 300, "Still applied", WarningTime))
            .ShouldApplyEscalation);
        Assert.Equal(9, (await stores[0].GetWarningsAsync(guildId, memberId)).TotalCount);
    }

    [Fact]
    public async Task GuildsIsolateSettingsPoliciesWarningsAndApplicationMarkersAsync()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var store = database.CreateStore();
        const ulong firstGuild = 100;
        const ulong secondGuild = 101;
        const ulong memberId = 200;
        await store.SetAuditChannelIdAsync(firstGuild, 301);
        await store.SetMemberLogChannelIdAsync(firstGuild, 302);
        await store.SetEventChannelIdAsync(firstGuild, 303);
        await store.SetEventBackgroundObjectKeyAsync(firstGuild, "first.png");
        await store.SetAuditChannelIdAsync(secondGuild, 401);
        await store.SetMemberLogChannelIdAsync(secondGuild, 402);
        await store.SetEventChannelIdAsync(secondGuild, 403);
        await store.SetEventBackgroundObjectKeyAsync(secondGuild, "second.png");
        await store.SetWarningEscalationAsync(firstGuild, new WarningEscalation(1, 5));
        await store.SetWarningEscalationAsync(secondGuild, new WarningEscalation(2, 20));

        Assert.True((await store.AddWarningAsync(firstGuild, memberId, 300, "First guild", WarningTime))
            .ShouldApplyEscalation);
        Assert.False((await store.AddWarningAsync(secondGuild, memberId, 300, "Second guild, first", WarningTime))
            .ShouldApplyEscalation);
        Assert.True((await store.AddWarningAsync(secondGuild, memberId, 300, "Second guild, second", WarningTime))
            .ShouldApplyEscalation);

        await store.RemoveAuditChannelIdAsync(firstGuild);
        await store.RemoveMemberLogChannelIdAsync(firstGuild);
        await store.RemoveEventChannelIdAsync(firstGuild);
        await store.RemoveEventBackgroundObjectKeyAsync(firstGuild);
        await store.RemoveWarningEscalationApplicationAsync(firstGuild, memberId);
        await store.SetWarningEscalationAsync(firstGuild, new WarningEscalation(3, 30));
        await store.RemoveWarningEscalationAsync(firstGuild);

        await AssertSettingsAsync(store, secondGuild, 401, 402, 403, "second.png");
        Assert.Equal(new WarningEscalation(2, 20), await store.GetWarningEscalationAsync(secondGuild));
        var next = await store.AddWarningAsync(secondGuild, memberId, 300, "Second guild, third", WarningTime);
        Assert.Equal(3, next.WarningCount);
        Assert.False(next.ShouldApplyEscalation);
        var firstHistory = await store.GetWarningsAsync(firstGuild, memberId);
        Assert.Equal("First guild", Assert.Single(firstHistory.RecentWarnings).Reason);
        Assert.Equal(1, firstHistory.TotalCount);
        Assert.Equal(3, (await store.GetWarningsAsync(secondGuild, memberId)).TotalCount);
        Assert.Empty((await store.GetWarningsAsync(secondGuild, memberId + 1)).RecentWarnings);
    }

    [Fact]
    public async Task ConcurrentSettersAndRemovalsPreserveIndependentSettingsAsync()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var stores = new[] { database.CreateStore(), database.CreateStore() };
        for (ulong guildId = 100; guildId < 112; guildId++)
        {
            // Race independent upserts against the same initially absent row.
            await RunConcurrentlyAsync([
                () => stores[0].SetAuditChannelIdAsync(guildId, 301),
                () => stores[1].SetMemberLogChannelIdAsync(guildId, 302),
                () => stores[0].SetEventChannelIdAsync(guildId, 303),
                () => stores[1].SetEventBackgroundObjectKeyAsync(guildId, "initial.png")
            ]);
            await AssertSettingsAsync(stores[0], guildId, 301, 302, 303, "initial.png");

            await RunConcurrentlyAsync([
                () => stores[0].RemoveAuditChannelIdAsync(guildId),
                () => stores[1].RemoveMemberLogChannelIdAsync(guildId),
                () => stores[0].SetEventChannelIdAsync(guildId, 403),
                () => stores[1].SetEventBackgroundObjectKeyAsync(guildId, "updated.png")
            ]);
            await AssertSettingsAsync(stores[1], guildId, null, null, 403, "updated.png");

            // The last old setting can be removed while a different setting is inserted.
            await stores[0].RemoveEventBackgroundObjectKeyAsync(guildId);
            await RunConcurrentlyAsync([
                () => stores[0].RemoveEventChannelIdAsync(guildId),
                () => stores[1].SetAuditChannelIdAsync(guildId, 501)
            ]);
            await AssertSettingsAsync(stores[0], guildId, 501, null, null, null);

        }
    }

    [Fact]
    public async Task InitializationRejectsFutureSchemaVersionsBeforeCreatingApplicationTablesAsync()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using (var command = database.DataSource.CreateCommand("""
            CREATE TABLE DatabaseSchemaVersions (Version INTEGER PRIMARY KEY);
            INSERT INTO DatabaseSchemaVersions VALUES (2);
            """))
        {
            await command.ExecuteNonQueryAsync();
        }

        var store = database.CreateStore();
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.InitializeAsync());
        await using var check = database.DataSource.CreateCommand("SELECT to_regclass('guildchannelsettings')::text;");
        Assert.Equal(DBNull.Value, await check.ExecuteScalarAsync());
    }

    [Fact]
    public async Task FailedInitializationRollsBackAndCanBeRetriedAfterRepairAsync()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using (var command = database.DataSource.CreateCommand("""
            CREATE TABLE MemberWarnings (UnexpectedColumn INTEGER);
            """))
        {
            await command.ExecuteNonQueryAsync();
        }

        var store = database.CreateStore();
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => store.InitializeAsync());
        await using (var check = database.DataSource.CreateCommand("""
            SELECT to_regclass('databaseschemaversions'), to_regclass('guildchannelsettings');
            """))
        {
            await using var reader = await check.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.IsDBNull(0));
            Assert.True(reader.IsDBNull(1));
        }

        await using (var repair = database.DataSource.CreateCommand("DROP TABLE MemberWarnings;"))
        {
            await repair.ExecuteNonQueryAsync();
        }

        await store.InitializeAsync();
        await store.SetAuditChannelIdAsync(100, 101);
        Assert.Equal(101UL, await store.GetAuditChannelIdAsync(100));
    }

    private static async Task AssertSettingsAsync(
        IGuildChannelConfigurationStore store,
        ulong guildId,
        ulong? auditChannelId,
        ulong? memberLogChannelId,
        ulong? eventChannelId,
        string? backgroundObjectKey)
    {
        Assert.Equal(auditChannelId, await store.GetAuditChannelIdAsync(guildId));
        Assert.Equal(memberLogChannelId, await store.GetMemberLogChannelIdAsync(guildId));
        Assert.Equal(eventChannelId, await store.GetEventChannelIdAsync(guildId));
        Assert.Equal(backgroundObjectKey, await store.GetEventBackgroundObjectKeyAsync(guildId));
    }

    private static async Task<T[]> RunConcurrentlyAsync<T>(IEnumerable<Func<Task<T>>> operations)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = operations.Select(async operation =>
        {
            await start.Task;
            return await operation();
        }).ToArray();
        start.SetResult();
        return await Task.WhenAll(tasks);
    }

    private static async Task RunConcurrentlyAsync(IEnumerable<Func<Task>> operations)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = operations.Select(async operation =>
        {
            await start.Task;
            await operation();
        }).ToArray();
        start.SetResult();
        await Task.WhenAll(tasks);
    }
}
