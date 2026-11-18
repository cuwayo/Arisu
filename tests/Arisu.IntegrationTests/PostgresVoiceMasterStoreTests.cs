using Arisu.Storage;
using Xunit;

namespace Arisu.IntegrationTests;

public sealed class PostgresVoiceMasterStoreTests
{
    [Fact]
    public async Task InitializationIsIdempotentAndConcurrentAndPersistsConfigurationAndRoomsAsync()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var first = CreateStore(database);
        using var second = CreateStore(database);

        await Task.WhenAll(first.InitializeAsync(), second.InitializeAsync(), first.InitializeAsync());

        const ulong guildId = ulong.MaxValue;
        var configuration = new VoiceMasterConfiguration(guildId, ulong.MaxValue, ulong.MaxValue);
        var room = new VoiceMasterRoom(guildId, ulong.MaxValue, ulong.MaxValue);
        await first.SetConfigurationAsync(configuration);
        await first.SaveRoomAsync(room);

        using var reader = CreateStore(database);
        await Task.WhenAll(first.InitializeAsync(), second.InitializeAsync(), reader.InitializeAsync());
        Assert.Equal(configuration, await reader.GetConfigurationAsync(guildId));
        Assert.Equal(room, await reader.GetRoomAsync(guildId, room.ChannelId));
        Assert.Equal(room, Assert.Single(await reader.GetRoomsAsync(guildId)));
    }

    [Fact]
    public async Task ConfigurationsAndRoomsAreIsolatedByGuildAsync()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var store = CreateStore(database);
        const ulong guildId = 100;
        const ulong otherGuildId = 101;
        var configuration = new VoiceMasterConfiguration(guildId, 200, 300);
        var otherConfiguration = new VoiceMasterConfiguration(otherGuildId, 201, 301);
        var room = new VoiceMasterRoom(guildId, 400, 500);
        var otherRoom = new VoiceMasterRoom(otherGuildId, 401, 501);

        await store.SetConfigurationAsync(configuration);
        await store.SetConfigurationAsync(otherConfiguration);
        await store.SaveRoomAsync(room);
        await store.SaveRoomAsync(otherRoom);

        Assert.Equal(configuration, await store.GetConfigurationAsync(guildId));
        Assert.Equal(otherConfiguration, await store.GetConfigurationAsync(otherGuildId));
        Assert.Equal(room, Assert.Single(await store.GetRoomsAsync(guildId)));
        Assert.Equal(otherRoom, Assert.Single(await store.GetRoomsAsync(otherGuildId)));
        Assert.Null(await store.GetConfigurationAsync(102));
        Assert.Empty(await store.GetRoomsAsync(102));
    }

    [Fact]
    public async Task SettingConfigurationAndSavingRoomUpdateExistingValuesAsync()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var store = CreateStore(database);
        const ulong guildId = 100;
        const ulong channelId = 200;

        await store.SetConfigurationAsync(new VoiceMasterConfiguration(guildId, 300, 400));
        await store.SetConfigurationAsync(new VoiceMasterConfiguration(guildId, 301, 401));
        await store.SaveRoomAsync(new VoiceMasterRoom(guildId, channelId, 500));
        await store.SaveRoomAsync(new VoiceMasterRoom(guildId, channelId, 501));

        Assert.Equal(new VoiceMasterConfiguration(guildId, 301, 401),
            await store.GetConfigurationAsync(guildId));
        Assert.Equal(new VoiceMasterRoom(guildId, channelId, 501),
            await store.GetRoomAsync(guildId, channelId));
        Assert.Single(await store.GetRoomsAsync(guildId));
    }

    [Fact]
    public async Task RemovingConfigurationPreservesRoomsAndRoomRemovalIsScopedAsync()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var store = CreateStore(database);
        const ulong guildId = 100;
        const ulong otherGuildId = 101;
        var firstRoom = new VoiceMasterRoom(guildId, 200, 300);
        var secondRoom = new VoiceMasterRoom(guildId, 201, 301);
        var otherRoom = new VoiceMasterRoom(otherGuildId, 200, 302);

        await store.SetConfigurationAsync(new VoiceMasterConfiguration(guildId, 400, 500));
        await store.SaveRoomAsync(firstRoom);
        await store.SaveRoomAsync(secondRoom);
        await store.SaveRoomAsync(otherRoom);

        await store.RemoveConfigurationAsync(guildId);
        Assert.Null(await store.GetConfigurationAsync(guildId));
        Assert.Equal([firstRoom, secondRoom], await store.GetRoomsAsync(guildId));

        Assert.Null(await store.GetRoomAsync(otherGuildId, secondRoom.ChannelId));
        await store.RemoveRoomAsync(otherGuildId, secondRoom.ChannelId);
        Assert.Equal(otherRoom, Assert.Single(await store.GetRoomsAsync(otherGuildId)));
        Assert.Equal([firstRoom, secondRoom], await store.GetRoomsAsync(guildId));
        await store.RemoveRoomAsync(guildId, firstRoom.ChannelId);
        Assert.Null(await store.GetRoomAsync(guildId, firstRoom.ChannelId));
        Assert.Equal(secondRoom, Assert.Single(await store.GetRoomsAsync(guildId)));
        Assert.Equal(otherRoom, await store.GetRoomAsync(otherGuildId, firstRoom.ChannelId));
        await store.RemoveRoomAsync(guildId, firstRoom.ChannelId);
    }

    private static PostgresVoiceMasterStore CreateStore(PostgresTestDatabase database) =>
        new(database.DataSource);
}
