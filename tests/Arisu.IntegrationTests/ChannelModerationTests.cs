using Arisu.Modules;
using Arisu.Services;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Arisu.IntegrationTests;

public sealed class ChannelModerationTests
{
    [Fact]
    public async Task CommandsRegisterWithDiscordInteractionService()
    {
        using var client = new DiscordSocketClient();
        using var interactions = new InteractionService(client);
        await using var dataSource = NpgsqlDataSource.Create("Host=localhost;Database=unused;Username=unused");
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(client)
            .AddSingleton(dataSource)
            .AddSingleton<IChannelLockService, ChannelLockService>()
            .AddSingleton<IAuditChannelService, TestAuditChannelService>()
            .BuildServiceProvider();

        var module = await interactions.AddModuleAsync<ChannelModerationModule>(services);

        Assert.Equal(4, module.SlashCommands.Count);
        Assert.Equal(new[] { "lock", "purge", "slowmode", "unlock" },
            module.SlashCommands.Select(command => command.Name).Order());
    }

    [Theory]
    [InlineData(PermValue.Allow)]
    [InlineData(PermValue.Deny)]
    [InlineData(PermValue.Inherit)]
    public void UnlockRestoresOriginalSendMessagesAndPreservesOtherPermissionChanges(PermValue original)
    {
        var current = new OverwritePermissions(
            sendMessages: PermValue.Deny,
            attachFiles: PermValue.Allow,
            embedLinks: PermValue.Deny);
        var snapshot = new ChannelLockSnapshot(original, HadOverwrite: true);

        Assert.True(ChannelLockPermissions.CanRestore(current, snapshot));
        var restored = ChannelLockPermissions.Restore(current, snapshot);

        Assert.NotNull(restored);
        Assert.Equal(original, restored.Value.SendMessages);
        Assert.Equal(PermValue.Allow, restored.Value.AttachFiles);
        Assert.Equal(PermValue.Deny, restored.Value.EmbedLinks);
    }

    [Fact]
    public void UnlockRemovesOtherwiseEmptyOverwriteCreatedByLock()
    {
        var current = new OverwritePermissions(sendMessages: PermValue.Deny);
        var snapshot = new ChannelLockSnapshot(PermValue.Inherit, HadOverwrite: false);

        Assert.Null(ChannelLockPermissions.Restore(current, snapshot));
    }

    [Fact]
    public void UnlockPreservesExistingEmptyOverwrite()
    {
        var current = new OverwritePermissions(sendMessages: PermValue.Deny);
        var snapshot = new ChannelLockSnapshot(PermValue.Inherit, HadOverwrite: true);

        var restored = ChannelLockPermissions.Restore(current, snapshot);

        Assert.NotNull(restored);
        Assert.Equal(PermValue.Inherit, restored.Value.SendMessages);
    }

    [Fact]
    public void UnlockKeepsUnrelatedPermissionsAddedToOverwriteCreatedByLock()
    {
        var current = new OverwritePermissions(
            sendMessages: PermValue.Deny, attachFiles: PermValue.Deny);
        var snapshot = new ChannelLockSnapshot(PermValue.Inherit, HadOverwrite: false);

        var restored = ChannelLockPermissions.Restore(current, snapshot);

        Assert.NotNull(restored);
        Assert.Equal(PermValue.Inherit, restored.Value.SendMessages);
        Assert.Equal(PermValue.Deny, restored.Value.AttachFiles);
    }

    [Theory]
    [InlineData(PermValue.Allow, PermValue.Inherit)]
    [InlineData(PermValue.Inherit, PermValue.Allow)]
    [InlineData(PermValue.Deny, PermValue.Allow)]
    public void UnlockRefusesConflictingExternalSendPermissionChange(PermValue original, PermValue changed)
    {
        var current = new OverwritePermissions(sendMessages: changed);
        var snapshot = new ChannelLockSnapshot(original, HadOverwrite: true);

        Assert.False(ChannelLockPermissions.CanRestore(current, snapshot));
    }

    [Fact]
    public void UnlockAcceptsAlreadyRestoredPermissionAfterDatabaseCleanupFailure()
    {
        var current = new OverwritePermissions(sendMessages: PermValue.Allow);
        var snapshot = new ChannelLockSnapshot(PermValue.Allow, HadOverwrite: true);

        Assert.True(ChannelLockPermissions.CanRestore(current, snapshot));
    }

    [Fact]
    public void UnlockRefusesDeletedOverwriteWhenOriginalPermissionWasAllow()
    {
        var snapshot = new ChannelLockSnapshot(PermValue.Allow, HadOverwrite: true);

        Assert.False(ChannelLockPermissions.CanRestore(null, snapshot));
    }

    private sealed class TestAuditChannelService : IAuditChannelService
    {
        public Task<bool> SetAuditChannelAsync(ulong guildId, ulong channelId) => Task.FromResult(true);

        public Task<bool> RemoveAuditChannelAsync(ulong guildId) => Task.FromResult(true);

        public Task WriteAsync(ModerationAuditEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact]
    public void PurgeSkipsAncientMessagesAndBoundaryMessages()
    {
        var now = DateTimeOffset.Parse("2026-11-17T12:00:00Z");

        Assert.False(ChannelPurgePolicy.CanDelete(now.AddDays(-15), now));
        Assert.False(ChannelPurgePolicy.CanDelete(now.AddDays(-14), now));
        Assert.False(ChannelPurgePolicy.CanDelete(now.AddDays(-14).AddSeconds(59), now));
        Assert.True(ChannelPurgePolicy.CanDelete(now.AddDays(-14).AddMinutes(2), now));
        Assert.True(ChannelPurgePolicy.CanDelete(now.AddMinutes(-1), now));
    }
}
