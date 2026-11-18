using Discord;

namespace Arisu.Utilities;

public static class LevelingRewardSafety
{
    private const GuildPermission UnsafePermissions =
        GuildPermission.Administrator |
        GuildPermission.ManageGuild |
        GuildPermission.ManageRoles |
        GuildPermission.ManageChannels |
        GuildPermission.KickMembers |
        GuildPermission.BanMembers |
        GuildPermission.ModerateMembers |
        GuildPermission.ManageMessages |
        GuildPermission.ManageWebhooks |
        GuildPermission.ManageThreads |
        GuildPermission.ManageNicknames |
        GuildPermission.ManageEvents |
        GuildPermission.ManageEmojisAndStickers |
        GuildPermission.CreateGuildExpressions |
        GuildPermission.CreateEvents |
        GuildPermission.ViewAuditLog |
        GuildPermission.MentionEveryone |
        GuildPermission.PinMessages |
        GuildPermission.BypassSlowmode |
        GuildPermission.MuteMembers |
        GuildPermission.DeafenMembers |
        GuildPermission.MoveMembers;

    public static bool IsSafe(GuildPermissions permissions)
    {
        return (permissions.RawValue & (ulong)UnsafePermissions) == 0;
    }

    public static bool IsSafe(OverwritePermissions permissions)
    {
        return (permissions.AllowValue & (ulong)UnsafePermissions) == 0;
    }
}
