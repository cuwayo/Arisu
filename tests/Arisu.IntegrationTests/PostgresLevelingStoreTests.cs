using Arisu.Storage;
using Npgsql;
using Xunit;

namespace Arisu.IntegrationTests;

public sealed class PostgresLevelingStoreTests
{
    private static readonly DateTimeOffset AwardTime =
        new(2026, 11, 17, 12, 34, 56, TimeSpan.FromHours(9));

    [Fact]
    public async Task ConcurrentInitializationIsIdempotentAndSettingsSurviveAnotherStoreInstanceAsync()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var first = CreateStore(database);
        using var second = CreateStore(database);

        await RunConcurrentlyAsync([
            () => first.InitializeAsync(),
            () => second.InitializeAsync(),
            () => first.InitializeAsync()
        ]);

        const ulong guildId = ulong.MaxValue;
        await first.SetEnabledAsync(guildId, true);
        await first.SetCooldownAsync(guildId, 120);
        await first.SetChannelExcludedAsync(guildId, ulong.MaxValue, true);
        await first.SetChannelExcludedAsync(guildId, 9, true);
        await first.SetRoleExcludedAsync(guildId, ulong.MaxValue - 1, true);
        await first.SetRoleExcludedAsync(guildId, 10, true);
        await first.SetRewardAsync(guildId, 1000, ulong.MaxValue - 2);
        await first.SetRewardAsync(guildId, 1, 500);

        using var reader = CreateStore(database);
        await Task.WhenAll(first.InitializeAsync(), second.InitializeAsync(), reader.InitializeAsync());
        var settings = await reader.GetSettingsAsync(guildId);
        Assert.Equal(guildId, settings.GuildId);
        Assert.True(settings.Enabled);
        Assert.Equal(120, settings.CooldownSeconds);
        Assert.Equal(new ulong[] { 9, ulong.MaxValue }, settings.ExcludedChannelIds);
        Assert.Equal(new ulong[] { 10, ulong.MaxValue - 1 }, settings.ExcludedRoleIds);
        Assert.Equal(new[] { new LevelRoleReward(1, 500), new LevelRoleReward(1000, ulong.MaxValue - 2) },
            settings.Rewards);
    }

    [Fact]
    public async Task DefaultsAreOptInAndConfigurationDoesNotAwardXpWhileDisabledAsync()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var store = CreateStore(database);
        const ulong guildId = 100;

        var settings = await store.GetSettingsAsync(guildId);
        Assert.Equal(guildId, settings.GuildId);
        Assert.False(settings.Enabled);
        Assert.Equal(60, settings.CooldownSeconds);
        Assert.Empty(settings.ExcludedChannelIds);
        Assert.Empty(settings.ExcludedRoleIds);
        Assert.Empty(settings.Rewards);
        Assert.Null(await store.GetRankAsync(guildId, 200));
        var leaderboard = await store.GetLeaderboardAsync(guildId, 1);
        Assert.Equal(0L, leaderboard.TotalMembers);
        Assert.Empty(leaderboard.Entries);
        Assert.Null(await store.TryAwardAsync(guildId, 200, 300, [], 25, AwardTime));

        await store.SetCooldownAsync(guildId, 15);
        await store.SetChannelExcludedAsync(guildId, 301, true);
        await store.SetRoleExcludedAsync(guildId, 400, true);
        await store.SetRewardAsync(guildId, 1, 500);
        Assert.False((await store.GetSettingsAsync(guildId)).Enabled);
        Assert.Null(await store.TryAwardAsync(guildId, 200, 300, [], 25, AwardTime));
        Assert.Null(await store.GetRankAsync(guildId, 200));
        Assert.Equal(0L, (await store.GetLeaderboardAsync(guildId, 1)).TotalMembers);

        await store.SetEnabledAsync(guildId, true);
        var award = await store.TryAwardAsync(guildId, 200, 300, [], 15, AwardTime);
        Assert.NotNull(award);
        Assert.Equal(0L, award.PreviousXp);
        Assert.Equal(new LevelingMember(guildId, 200, 15, 1, AwardTime.ToUniversalTime()), award.Member);
        Assert.Equal(TimeSpan.Zero, award.Member.LastAwardedAt.Offset);
        Assert.True((await store.GetSettingsAsync(guildId)).Enabled);
    }

    [Fact]
    public async Task DisablingPreservesProgressAndRejectedMessagesDoNotSpendTheCooldownAsync()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var store = CreateStore(database);
        const ulong guildId = 100;
        await store.SetEnabledAsync(guildId, true);
        var initial = await store.TryAwardAsync(guildId, 200, 300, [], 20, AwardTime);
        Assert.NotNull(initial);

        await store.SetEnabledAsync(guildId, false);
        Assert.Null(await store.TryAwardAsync(guildId, 200, 300, [], 25, AwardTime.AddSeconds(120)));
        Assert.Null(await store.TryAwardAsync(guildId, 201, 300, [], 25, AwardTime.AddSeconds(120)));
        Assert.Equal(initial.Member, (await store.GetRankAsync(guildId, 200))?.Member);
        Assert.Null(await store.GetRankAsync(guildId, 201));

        await store.SetEnabledAsync(guildId, true);
        Assert.Null(await store.TryAwardAsync(guildId, 200, 300, [], 25, AwardTime.AddSeconds(59)));
        var next = await store.TryAwardAsync(guildId, 200, 300, [], 25, AwardTime.AddSeconds(120));
        Assert.NotNull(next);
        Assert.Equal(20L, next.PreviousXp);
        Assert.Equal(new LevelingMember(guildId, 200, 45, 2, AwardTime.AddSeconds(120).ToUniversalTime()),
            next.Member);
        Assert.Equal(1L, (await store.GetLeaderboardAsync(guildId, 1)).TotalMembers);
    }

    [Fact]
    public async Task ConcurrentAwardsAcceptExactlyOneMessagePerUserAndCooldownWindowAcrossStoresAsync()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var first = CreateStore(database);
        using var second = CreateStore(database);
        ILevelingStore[] stores = [first, second];
        const ulong guildId = ulong.MaxValue;
        const ulong userId = ulong.MaxValue - 1;
        await first.SetEnabledAsync(guildId, true);
        var totalXp = 0L;

        for (var window = 0; window < 3; window++)
        {
            var timestamp = AwardTime.AddSeconds(window * 60);
            var awards = await RunConcurrentlyAsync(Enumerable.Range(0, 24)
                .Select<int, Func<Task<LevelingAward?>>>(index => () => stores[index % stores.Length].TryAwardAsync(
                    guildId, userId, ulong.MaxValue, [], 15 + index % 11, timestamp)));

            var accepted = Assert.Single(awards, award => award is not null);
            Assert.NotNull(accepted);
            Assert.Equal(totalXp, accepted.PreviousXp);
            Assert.InRange(accepted.Member.TotalXp - totalXp, 15L, 25L);
            totalXp = accepted.Member.TotalXp;
            Assert.Equal(window + 1L, accepted.Member.MessageCount);
            Assert.Equal(timestamp.ToUniversalTime(), accepted.Member.LastAwardedAt);
            Assert.Equal(TimeSpan.Zero, accepted.Member.LastAwardedAt.Offset);
            Assert.Equal(accepted.Member, (await second.GetRankAsync(guildId, userId))?.Member);
            Assert.Null(await second.TryAwardAsync(guildId, userId, 301, [400], 25, timestamp.AddSeconds(59)));
        }

        // A member's cooldown must not block another member in the same guild.
        var other = await second.TryAwardAsync(guildId, 200, 300, [], 25, AwardTime.AddSeconds(120));
        Assert.NotNull(other);
        Assert.Equal(25L, other.Member.TotalXp);
        Assert.Equal(1L, other.Member.MessageCount);
        Assert.Equal(2L, (await first.GetLeaderboardAsync(guildId, 1)).TotalMembers);
    }

    [Fact]
    public async Task CooldownPersistsAcrossInstancesAndUsesTheSuppliedUtcInstantAtTheExactBoundaryAsync()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        const ulong guildId = 100;
        var historicalTime = new DateTimeOffset(1969, 12, 31, 23, 59, 59, TimeSpan.FromHours(-5));
        using (var writer = CreateStore(database))
        {
            await writer.SetEnabledAsync(guildId, true);
            Assert.NotNull(await writer.TryAwardAsync(guildId, 200, 300, [], 15, historicalTime));
        }

        using var reader = CreateStore(database);
        Assert.Null(await reader.TryAwardAsync(guildId, 200, 301, [400], 25,
            historicalTime.ToOffset(TimeSpan.FromHours(9))));
        Assert.Null(await reader.TryAwardAsync(guildId, 200, 300, [], 25, historicalTime.AddSeconds(-1)));
        Assert.Null(await reader.TryAwardAsync(guildId, 200, 300, [], 25,
            historicalTime.AddSeconds(60).AddMilliseconds(-1)));
        var rank = await reader.GetRankAsync(guildId, 200);
        Assert.NotNull(rank);
        Assert.Equal(new LevelingMember(guildId, 200, 15, 1, historicalTime.ToUniversalTime()), rank.Member);
        Assert.Equal(TimeSpan.Zero, rank.Member.LastAwardedAt.Offset);

        var boundary = historicalTime.AddSeconds(60).ToOffset(TimeSpan.FromHours(9));
        var accepted = await reader.TryAwardAsync(guildId, 200, 300, [], 25, boundary);
        Assert.NotNull(accepted);
        Assert.Equal(15L, accepted.PreviousXp);
        Assert.Equal(new LevelingMember(guildId, 200, 40, 2, boundary.ToUniversalTime()), accepted.Member);
        Assert.Equal(TimeSpan.Zero, accepted.Member.LastAwardedAt.Offset);
    }

    [Fact]
    public async Task CooldownUpdatesApplyImmediatelyWithoutResettingProgressOrOtherSettingsAsync()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var store = CreateStore(database);
        const ulong guildId = 100;
        await store.SetEnabledAsync(guildId, true);
        await store.SetChannelExcludedAsync(guildId, 301, true);
        await store.SetRoleExcludedAsync(guildId, 400, true);
        await store.SetRewardAsync(guildId, 1, 500);
        Assert.NotNull(await store.TryAwardAsync(guildId, 200, 300, [], 15, AwardTime));

        await store.SetCooldownAsync(guildId, 15);
        Assert.Null(await store.TryAwardAsync(guildId, 200, 300, [], 20, AwardTime.AddSeconds(14)));
        var minimum = await store.TryAwardAsync(guildId, 200, 300, [], 20, AwardTime.AddSeconds(15));
        Assert.NotNull(minimum);
        Assert.Equal(15L, minimum.PreviousXp);

        await store.SetCooldownAsync(guildId, 3600);
        Assert.Null(await store.TryAwardAsync(guildId, 200, 300, [], 25, AwardTime.AddSeconds(3614)));
        var maximum = await store.TryAwardAsync(guildId, 200, 300, [], 25, AwardTime.AddSeconds(3615));
        Assert.NotNull(maximum);
        Assert.Equal(35L, maximum.PreviousXp);
        Assert.Equal(60L, maximum.Member.TotalXp);
        Assert.Equal(3L, maximum.Member.MessageCount);
        var settings = await store.GetSettingsAsync(guildId);
        Assert.True(settings.Enabled);
        Assert.Equal(3600, settings.CooldownSeconds);
        Assert.Equal(new ulong[] { 301 }, settings.ExcludedChannelIds);
        Assert.Equal(new ulong[] { 400 }, settings.ExcludedRoleIds);
        Assert.Equal(new LevelRoleReward(1, 500), Assert.Single(settings.Rewards));
    }

    [Fact]
    public async Task ExactChannelAndAnyMatchingRoleBlockAwardsWithoutChangingProgressAsync()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var store = CreateStore(database);
        const ulong guildId = 100;
        await store.SetEnabledAsync(guildId, true);
        await store.SetChannelExcludedAsync(guildId, 300, true);
        await store.SetRoleExcludedAsync(guildId, 400, true);
        await store.SetRoleExcludedAsync(guildId, 401, true);

        Assert.Null(await store.TryAwardAsync(guildId, 200, 300, [], 25, AwardTime));
        Assert.Null(await store.TryAwardAsync(guildId, 200, 301, [402, 400, 403], 25, AwardTime));
        Assert.Null(await store.TryAwardAsync(guildId, 200, 301, [401], 25, AwardTime));
        Assert.Null(await store.GetRankAsync(guildId, 200));

        var initial = await store.TryAwardAsync(guildId, 200, 301, [402, 403], 15, AwardTime);
        Assert.NotNull(initial);
        var later = AwardTime.AddSeconds(60);
        Assert.Null(await store.TryAwardAsync(guildId, 200, 300, [], 25, later));
        Assert.Null(await store.TryAwardAsync(guildId, 200, 301, [402, 400], 25, later));
        Assert.Equal(initial.Member, (await store.GetRankAsync(guildId, 200))?.Member);

        await store.SetChannelExcludedAsync(guildId, 300, false);
        await store.SetRoleExcludedAsync(guildId, 400, false);
        var included = await store.TryAwardAsync(guildId, 200, 300, [400, 402], 25, later);
        Assert.NotNull(included);
        Assert.Equal(15L, included.PreviousXp);
        Assert.Equal(40L, included.Member.TotalXp);
        Assert.Equal(2L, included.Member.MessageCount);
        Assert.Equal(later.ToUniversalTime(), included.Member.LastAwardedAt);
        var settings = await store.GetSettingsAsync(guildId);
        Assert.Empty(settings.ExcludedChannelIds);
        Assert.Equal(new ulong[] { 401 }, settings.ExcludedRoleIds);
    }

    [Fact]
    public async Task GuildsIsolateSettingsExclusionsRewardsProgressCooldownsAndLeaderboardsAsync()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var store = CreateStore(database);
        const ulong firstGuild = 100;
        const ulong secondGuild = 101;
        await store.SetEnabledAsync(firstGuild, true);
        await store.SetCooldownAsync(firstGuild, 15);
        await store.SetChannelExcludedAsync(firstGuild, 300, true);
        await store.SetRoleExcludedAsync(firstGuild, 400, true);
        await store.SetRewardAsync(firstGuild, 1, 500);
        await store.SetEnabledAsync(secondGuild, true);
        await store.SetRewardAsync(secondGuild, 1, 501);

        Assert.Null(await store.TryAwardAsync(firstGuild, 200, 300, [], 25, AwardTime));
        Assert.Null(await store.TryAwardAsync(firstGuild, 200, 301, [400], 25, AwardTime));
        var first = await store.TryAwardAsync(firstGuild, 200, 301, [], 15, AwardTime);
        var second = await store.TryAwardAsync(secondGuild, 200, 300, [400], 25, AwardTime);
        Assert.NotNull(first);
        Assert.NotNull(second);
        var firstNext = await store.TryAwardAsync(firstGuild, 200, 301, [], 15, AwardTime.AddSeconds(15));
        Assert.NotNull(firstNext);
        Assert.Null(await store.TryAwardAsync(secondGuild, 200, 300, [], 25, AwardTime.AddSeconds(15)));
        Assert.NotNull(await store.TryAwardAsync(firstGuild, 201, 301, [], 25, AwardTime));
        Assert.Null(await store.GetRankAsync(secondGuild, 201));

        await store.SetEnabledAsync(firstGuild, false);
        await store.SetChannelExcludedAsync(firstGuild, 300, false);
        await store.SetRoleExcludedAsync(firstGuild, 400, false);
        await store.RemoveRewardAsync(firstGuild, 1);
        var secondSettings = await store.GetSettingsAsync(secondGuild);
        Assert.True(secondSettings.Enabled);
        Assert.Equal(60, secondSettings.CooldownSeconds);
        Assert.Empty(secondSettings.ExcludedChannelIds);
        Assert.Empty(secondSettings.ExcludedRoleIds);
        Assert.Equal(new LevelRoleReward(1, 501), Assert.Single(secondSettings.Rewards));
        Assert.Equal(second.Member, (await store.GetRankAsync(secondGuild, 200))?.Member);
        Assert.Equal(firstNext.Member, (await store.GetRankAsync(firstGuild, 200))?.Member);
        Assert.Equal(2L, (await store.GetLeaderboardAsync(firstGuild, 1)).TotalMembers);
        Assert.Equal(second.Member, Assert.Single((await store.GetLeaderboardAsync(secondGuild, 1)).Entries).Member);
        Assert.Null(await store.GetRankAsync(102, 200));
        Assert.Equal(0L, (await store.GetLeaderboardAsync(102, 1)).TotalMembers);
        Assert.False((await store.GetSettingsAsync(102)).Enabled);
    }

    [Fact]
    public async Task LeaderboardsUseTenMemberPagesAndDeterministicNumericUserIdTiesMatchingRanksAsync()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var store = CreateStore(database);
        const ulong guildId = 100;
        await store.SetEnabledAsync(guildId, true);
        var userIds = Enumerable.Range(1, 21).Select(index => (ulong)index)
            .Concat([100UL, (ulong)long.MaxValue, (ulong)long.MaxValue + 1, ulong.MaxValue]).ToArray();
        var members = new List<LevelingMember>();
        // Insert in reverse order so neither insertion order nor textual ID ordering can pass.
        for (var index = userIds.Length - 1; index >= 0; index--)
        {
            var award = await store.TryAwardAsync(guildId, userIds[index], 300, [],
                15 + index % 3 * 5, AwardTime);
            Assert.NotNull(award);
            members.Add(award.Member);
        }

        await AssertLeaderboardAndRanksAsync(store, guildId, members);
        Assert.Null(await store.GetRankAsync(guildId, 999));
        Assert.Equal(10, (await store.GetLeaderboardAsync(guildId, 1)).Entries.Count);
        Assert.Equal(10, (await store.GetLeaderboardAsync(guildId, 2)).Entries.Count);
        Assert.Equal(5, (await store.GetLeaderboardAsync(guildId, 3)).Entries.Count);
        foreach (var page in new[] { 4, 1000 })
        {
            var beyondEnd = await store.GetLeaderboardAsync(guildId, page);
            Assert.Empty(beyondEnd.Entries);
            Assert.Equal(25L, beyondEnd.TotalMembers);
        }

        var updated = await store.TryAwardAsync(guildId, ulong.MaxValue, 300, [], 25, AwardTime.AddSeconds(60));
        Assert.NotNull(updated);
        Assert.Equal(15L, updated.PreviousXp);
        members.RemoveAll(member => member.UserId == ulong.MaxValue);
        members.Add(updated.Member);
        await AssertLeaderboardAndRanksAsync(store, guildId, members);
        Assert.Equal(ulong.MaxValue, (await store.GetLeaderboardAsync(guildId, 1)).Entries[0].Member.UserId);
        Assert.Equal(1L, (await store.GetRankAsync(guildId, ulong.MaxValue))?.Position);
    }

    [Fact]
    public async Task RewardUpsertsAndIdempotentRemovalAreScopedByGuildAndLevelAsync()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var store = CreateStore(database);
        const ulong guildId = 100;
        await store.SetCooldownAsync(guildId, 120);
        await store.SetChannelExcludedAsync(guildId, 300, true);
        await store.SetRoleExcludedAsync(guildId, 400, true);
        await store.SetRewardAsync(guildId, 1000, ulong.MaxValue);
        await store.SetRewardAsync(guildId, 1, 500);
        await store.SetRewardAsync(guildId, 5, 501);
        await store.SetRewardAsync(101, 5, 502);
        await store.SetRewardAsync(guildId, 5, 503);
        await store.SetRewardAsync(guildId, 5, 503);

        var settings = await store.GetSettingsAsync(guildId);
        Assert.Equal(new[] { new LevelRoleReward(1, 500), new LevelRoleReward(5, 503),
            new LevelRoleReward(1000, ulong.MaxValue) }, settings.Rewards);
        await store.RemoveRewardAsync(guildId, 5);
        await store.RemoveRewardAsync(guildId, 5);
        await store.RemoveRewardAsync(guildId, 999);
        using var reader = CreateStore(database);
        settings = await reader.GetSettingsAsync(guildId);
        Assert.Equal(new[] { new LevelRoleReward(1, 500), new LevelRoleReward(1000, ulong.MaxValue) },
            settings.Rewards);
        Assert.Equal(new LevelRoleReward(5, 502), Assert.Single((await reader.GetSettingsAsync(101)).Rewards));
        Assert.False(settings.Enabled);
        Assert.Equal(120, settings.CooldownSeconds);
        Assert.Equal(new ulong[] { 300 }, settings.ExcludedChannelIds);
        Assert.Equal(new ulong[] { 400 }, settings.ExcludedRoleIds);
    }

    [Fact]
    public async Task ConcurrentExclusionUpdatesPreserveEveryDistinctIdAndIndependentSettingAsync()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var first = CreateStore(database);
        using var second = CreateStore(database);
        ILevelingStore[] stores = [first, second];
        ulong[] channels = [9, 10, 100, 101, 102, ulong.MaxValue];
        ulong[] roles = [19, 20, 200, 201, 202, ulong.MaxValue - 1];

        for (ulong guildId = 100; guildId < 104; guildId++)
        {
            var additions = channels.Select<ulong, Func<Task>>((id, index) =>
                () => stores[index % 2].SetChannelExcludedAsync(guildId, id, true))
                .Concat(roles.Select<ulong, Func<Task>>((id, index) =>
                    () => stores[index % 2].SetRoleExcludedAsync(guildId, id, true)))
                .Concat([
                    () => first.SetEnabledAsync(guildId, true),
                    () => second.SetCooldownAsync(guildId, 120),
                    () => first.SetRewardAsync(guildId, 5, 500)
                ]);
            await RunConcurrentlyAsync(additions);
            var settings = await second.GetSettingsAsync(guildId);
            Assert.Equal(channels, settings.ExcludedChannelIds);
            Assert.Equal(roles, settings.ExcludedRoleIds);

            // Repeated simultaneous additions must not create duplicate array entries.
            await RunConcurrentlyAsync(Enumerable.Range(0, 12).Select<int, Func<Task>>(index =>
                () => stores[index % 2].SetChannelExcludedAsync(guildId, channels[0], true))
                .Concat(Enumerable.Range(0, 12).Select<int, Func<Task>>(index =>
                    () => stores[index % 2].SetRoleExcludedAsync(guildId, roles[0], true))));
            settings = await first.GetSettingsAsync(guildId);
            Assert.Equal(channels, settings.ExcludedChannelIds);
            Assert.Equal(roles, settings.ExcludedRoleIds);

            await RunConcurrentlyAsync([
                () => first.SetChannelExcludedAsync(guildId, channels[0], false),
                () => second.SetChannelExcludedAsync(guildId, channels[1], false),
                () => first.SetChannelExcludedAsync(guildId, 300, true),
                () => second.SetRoleExcludedAsync(guildId, roles[0], false),
                () => first.SetRoleExcludedAsync(guildId, roles[1], false),
                () => second.SetRoleExcludedAsync(guildId, 400, true)
            ]);
            await first.SetChannelExcludedAsync(guildId, channels[0], false);
            await second.SetRoleExcludedAsync(guildId, roles[0], false);
            settings = await second.GetSettingsAsync(guildId);
            Assert.Equal(channels.Skip(2).Append(300UL).Order(), settings.ExcludedChannelIds);
            Assert.Equal(roles.Skip(2).Append(400UL).Order(), settings.ExcludedRoleIds);
            Assert.True(settings.Enabled);
            Assert.Equal(120, settings.CooldownSeconds);
            Assert.Equal(new LevelRoleReward(5, 500), Assert.Single(settings.Rewards));
        }
    }

    [Theory]
    [InlineData(long.MaxValue, 1L, false)]
    [InlineData(long.MaxValue - 14, 1L, false)]
    [InlineData(long.MaxValue - 15, 1L, true)]
    [InlineData(0L, long.MaxValue, false)]
    [InlineData(0L, long.MaxValue - 1, true)]
    public async Task OverflowGuardsRejectOnlyAwardsThatExceedXpOrMessageCountCapacityAsync(
        long totalXp, long messageCount, bool shouldAward)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var store = CreateStore(database);
        const ulong guildId = 100;
        const ulong userId = 200;
        await store.SetEnabledAsync(guildId, true);
        var initial = new LevelingMember(guildId, userId, totalXp, messageCount, AwardTime.ToUniversalTime());
        // Seed near-capacity counters rather than award an impractical number of messages.
        await using (var command = database.DataSource.CreateCommand("""
            INSERT INTO LevelingMembers (GuildId, UserId, TotalXp, MessageCount, LastAwardedAt)
            VALUES (@guildId, @userId, @totalXp, @messageCount, @timestamp);
            """))
        {
            command.Parameters.AddWithValue("guildId", "100");
            command.Parameters.AddWithValue("userId", "200");
            command.Parameters.AddWithValue("totalXp", totalXp);
            command.Parameters.AddWithValue("messageCount", messageCount);
            command.Parameters.AddWithValue("timestamp", initial.LastAwardedAt);
            await command.ExecuteNonQueryAsync();
        }

        var award = await store.TryAwardAsync(guildId, userId, 300, [], 15, AwardTime.AddSeconds(60));
        var expected = initial;
        if (shouldAward)
        {
            Assert.NotNull(award);
            Assert.Equal(totalXp, award.PreviousXp);
            expected = initial with
            {
                TotalXp = totalXp + 15,
                MessageCount = messageCount + 1,
                LastAwardedAt = AwardTime.AddSeconds(60).ToUniversalTime()
            };
            Assert.Equal(expected, award.Member);
        }
        else
        {
            Assert.Null(award);
        }

        // At least one counter is now at capacity, so another award must not mutate the row.
        Assert.Null(await store.TryAwardAsync(guildId, userId, 300, [], 15, AwardTime.AddSeconds(120)));
        Assert.Equal(expected, (await store.GetRankAsync(guildId, userId))?.Member);
        var leaderboard = await store.GetLeaderboardAsync(guildId, 1);
        Assert.Equal(1L, leaderboard.TotalMembers);
        Assert.Equal(new LevelingRank(expected, 1), Assert.Single(leaderboard.Entries));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AwardsRejectInfinitePostgresTimestampExtremesWithoutOpeningTheDatabaseAsync(bool maximum)
    {
        await using var dataSource = CreateUnusedDataSource();
        using var store = new PostgresLevelingStore(dataSource);
        var timestamp = maximum ? DateTimeOffset.MaxValue : DateTimeOffset.MinValue;

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.TryAwardAsync(100, 200, 300, [], 15, timestamp));
        Assert.Equal("timestamp", exception.ParamName);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(14)]
    [InlineData(3601)]
    [InlineData(int.MaxValue)]
    public async Task CooldownRejectsOutOfRangeValuesWithoutOpeningTheDatabaseAsync(int seconds)
    {
        await using var dataSource = CreateUnusedDataSource();
        using var store = new PostgresLevelingStore(dataSource);

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SetCooldownAsync(100, seconds));
        Assert.Equal("seconds", exception.ParamName);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(14)]
    [InlineData(26)]
    [InlineData(int.MaxValue)]
    public async Task AwardsRejectOutOfRangeXpWithoutOpeningTheDatabaseAsync(int xp)
    {
        await using var dataSource = CreateUnusedDataSource();
        using var store = new PostgresLevelingStore(dataSource);

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.TryAwardAsync(100, 200, 300, [], xp, AwardTime));
        Assert.Equal("xp", exception.ParamName);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1001)]
    [InlineData(int.MaxValue)]
    public async Task RewardOperationsRejectOutOfRangeLevelsWithoutOpeningTheDatabaseAsync(int level)
    {
        await using var dataSource = CreateUnusedDataSource();
        using var store = new PostgresLevelingStore(dataSource);

        var setException = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SetRewardAsync(100, level, 500));
        var removeException = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.RemoveRewardAsync(100, level));
        Assert.Equal("level", setException.ParamName);
        Assert.Equal("level", removeException.ParamName);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1001)]
    [InlineData(int.MaxValue)]
    public async Task LeaderboardRejectsOutOfRangePagesWithoutOpeningTheDatabaseAsync(int page)
    {
        await using var dataSource = CreateUnusedDataSource();
        using var store = new PostgresLevelingStore(dataSource);

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.GetLeaderboardAsync(100, page));
        Assert.Equal("page", exception.ParamName);
    }

    [Fact]
    public async Task AwardsRejectNullRolesWithoutOpeningTheDatabaseAsync()
    {
        await using var dataSource = CreateUnusedDataSource();
        using var store = new PostgresLevelingStore(dataSource);

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            store.TryAwardAsync(100, 200, 300, null!, 15, AwardTime));
        Assert.Equal("roleIds", exception.ParamName);
    }

    private static PostgresLevelingStore CreateStore(PostgresTestDatabase database) => new(database.DataSource);

    private static NpgsqlDataSource CreateUnusedDataSource() =>
        NpgsqlDataSource.Create("Host=localhost;Database=unused;Username=unused;Timeout=1");

    private static async Task AssertLeaderboardAndRanksAsync(
        ILevelingStore store, ulong guildId, IReadOnlyCollection<LevelingMember> members)
    {
        var expected = members.OrderByDescending(member => member.TotalXp).ThenBy(member => member.UserId)
            .Select((member, index) => new LevelingRank(member, index + 1L)).ToArray();
        var actual = new List<LevelingRank>();
        for (var page = 1; page <= (members.Count + 9) / 10; page++)
        {
            var leaderboard = await store.GetLeaderboardAsync(guildId, page);
            Assert.Equal((long)members.Count, leaderboard.TotalMembers);
            Assert.Equal(expected.Skip((page - 1) * 10).Take(10), leaderboard.Entries);
            actual.AddRange(leaderboard.Entries);
        }

        Assert.Equal(expected, actual);
        Assert.Equal(members.Count, actual.Select(rank => rank.Member.UserId).Distinct().Count());
        Assert.All(actual, rank => Assert.Equal(TimeSpan.Zero, rank.Member.LastAwardedAt.Offset));
        foreach (var rank in expected)
        {
            Assert.Equal(rank, await store.GetRankAsync(guildId, rank.Member.UserId));
        }
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
