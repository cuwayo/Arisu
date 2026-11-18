using Microsoft.Extensions.Hosting;

namespace Arisu.Storage;

public sealed class DatabaseInitializationService : IHostedService
{
    private readonly PostgresGuildChannelConfigurationStore _store;

    private readonly IModerationHistoryStore _historyStore;
    private readonly IVoiceMasterStore _voiceMasterStore;
    private readonly ILevelingStore _levelingStore;

    public DatabaseInitializationService(
        PostgresGuildChannelConfigurationStore store,
        IModerationHistoryStore historyStore,
        IVoiceMasterStore voiceMasterStore,
        ILevelingStore levelingStore)
    {
        _store = store;
        _historyStore = historyStore;
        _voiceMasterStore = voiceMasterStore;
        _levelingStore = levelingStore;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _store.InitializeAsync(cancellationToken);
        await _historyStore.InitializeAsync(cancellationToken);
        await _voiceMasterStore.InitializeAsync(cancellationToken);
        await _levelingStore.InitializeAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
