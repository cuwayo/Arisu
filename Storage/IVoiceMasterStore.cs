namespace Arisu.Storage;

public sealed record VoiceMasterConfiguration(ulong GuildId, ulong LobbyChannelId, ulong CategoryId);

public sealed record VoiceMasterRoom(ulong GuildId, ulong ChannelId, ulong OwnerId);

public interface IVoiceMasterStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<VoiceMasterConfiguration?> GetConfigurationAsync(
        ulong guildId,
        CancellationToken cancellationToken = default);

    Task SetConfigurationAsync(
        VoiceMasterConfiguration configuration,
        CancellationToken cancellationToken = default);

    Task RemoveConfigurationAsync(ulong guildId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VoiceMasterRoom>> GetRoomsAsync(
        ulong guildId,
        CancellationToken cancellationToken = default);

    Task<VoiceMasterRoom?> GetRoomAsync(
        ulong guildId,
        ulong channelId,
        CancellationToken cancellationToken = default);

    Task SaveRoomAsync(VoiceMasterRoom room, CancellationToken cancellationToken = default);

    Task RemoveRoomAsync(
        ulong guildId,
        ulong channelId,
        CancellationToken cancellationToken = default);
}
