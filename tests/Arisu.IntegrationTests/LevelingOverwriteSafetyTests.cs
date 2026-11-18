using Arisu.Utilities;
using Discord;
using Xunit;

namespace Arisu.IntegrationTests;

public sealed class LevelingOverwriteSafetyTests
{
    [Theory]
    [InlineData(ChannelPermission.ManageMessages)]
    [InlineData(ChannelPermission.ManageRoles)]
    [InlineData(ChannelPermission.ManageChannels)]
    [InlineData(ChannelPermission.ManageWebhooks)]
    [InlineData(ChannelPermission.ManageThreads)]
    [InlineData(ChannelPermission.MuteMembers)]
    [InlineData(ChannelPermission.MoveMembers)]
    [InlineData(ChannelPermission.MentionEveryone)]
    [InlineData(ChannelPermission.CreateEvents)]
    [InlineData(ChannelPermission.PinMessages)]
    [InlineData(ChannelPermission.BypassSlowmode)]
    public void ModerationChannelAllowsAreUnsafe(ChannelPermission permission)
    {
        Assert.False(LevelingRewardSafety.IsSafe(new OverwritePermissions((ulong)permission, 0)));
    }

    [Fact]
    public void CosmeticChannelAllowsAndModerationDeniesAreSafe()
    {
        Assert.True(LevelingRewardSafety.IsSafe(new OverwritePermissions(
            (ulong)(ChannelPermission.ViewChannel | ChannelPermission.SendMessages),
            (ulong)ChannelPermission.ManageMessages)));
    }
}
