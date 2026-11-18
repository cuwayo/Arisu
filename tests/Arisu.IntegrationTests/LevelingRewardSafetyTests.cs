using Arisu.Utilities;
using Discord;
using Xunit;

namespace Arisu.IntegrationTests;

public sealed class LevelingRewardSafetyTests
{
    private const GuildPermission SafePermissions =
        GuildPermission.ViewChannel |
        GuildPermission.SendMessages |
        GuildPermission.EmbedLinks |
        GuildPermission.AttachFiles |
        GuildPermission.ReadMessageHistory |
        GuildPermission.AddReactions |
        GuildPermission.Connect |
        GuildPermission.Speak |
        GuildPermission.Stream |
        GuildPermission.UseVAD |
        GuildPermission.ChangeNickname;

    [Theory]
    [InlineData(GuildPermission.Administrator)]
    [InlineData(GuildPermission.ManageGuild)]
    [InlineData(GuildPermission.ManageRoles)]
    [InlineData(GuildPermission.ManageChannels)]
    [InlineData(GuildPermission.KickMembers)]
    [InlineData(GuildPermission.BanMembers)]
    [InlineData(GuildPermission.ModerateMembers)]
    [InlineData(GuildPermission.ManageMessages)]
    [InlineData(GuildPermission.ManageWebhooks)]
    [InlineData(GuildPermission.ManageThreads)]
    [InlineData(GuildPermission.ManageNicknames)]
    [InlineData(GuildPermission.ManageEvents)]
    [InlineData(GuildPermission.ManageEmojisAndStickers)]
    [InlineData(GuildPermission.ViewAuditLog)]
    [InlineData(GuildPermission.MentionEveryone)]
    [InlineData(GuildPermission.PinMessages)]
    [InlineData(GuildPermission.BypassSlowmode)]
    [InlineData(GuildPermission.MuteMembers)]
    [InlineData(GuildPermission.DeafenMembers)]
    [InlineData(GuildPermission.MoveMembers)]
    public void PrivilegedPermissionsAreUnsafeAloneOrCombinedWithOrdinaryPermissions(GuildPermission permission)
    {
        Assert.False(LevelingRewardSafety.IsSafe(new GuildPermissions((ulong)permission)));
        Assert.False(LevelingRewardSafety.IsSafe(new GuildPermissions((ulong)(permission | SafePermissions))));
    }

    [Theory]
    [InlineData(GuildPermission.ViewChannel)]
    [InlineData(GuildPermission.SendMessages)]
    [InlineData(GuildPermission.EmbedLinks)]
    [InlineData(GuildPermission.AttachFiles)]
    [InlineData(GuildPermission.ReadMessageHistory)]
    [InlineData(GuildPermission.AddReactions)]
    [InlineData(GuildPermission.Connect)]
    [InlineData(GuildPermission.Speak)]
    [InlineData(GuildPermission.Stream)]
    [InlineData(GuildPermission.UseVAD)]
    [InlineData(GuildPermission.ChangeNickname)]
    public void OrdinaryPermissionsAreSafe(GuildPermission permission) =>
        Assert.True(LevelingRewardSafety.IsSafe(new GuildPermissions((ulong)permission)));

    [Fact]
    public void NoPermissionsAndCombinedOrdinaryPermissionsAreSafe()
    {
        Assert.True(LevelingRewardSafety.IsSafe(new GuildPermissions(0)));
        Assert.True(LevelingRewardSafety.IsSafe(new GuildPermissions((ulong)SafePermissions)));
    }

    [Fact]
    public void AllPermissionBitsAreUnsafe() =>
        Assert.False(LevelingRewardSafety.IsSafe(new GuildPermissions(ulong.MaxValue)));
}
