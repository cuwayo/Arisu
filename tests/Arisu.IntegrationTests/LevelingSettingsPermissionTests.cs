using System.Reflection;
using Arisu.Modules;
using Discord;
using Discord.Interactions;
using Xunit;

namespace Arisu.IntegrationTests;

public sealed class LevelingSettingsPermissionTests
{
    [Fact]
    public void LevelingConfigurationRequiresManageGuild()
    {
        var requirement = typeof(SettingsModule.LevelingSettingsModule)
            .GetCustomAttribute<RequireUserPermissionAttribute>();

        Assert.NotNull(requirement);
        Assert.Equal(GuildPermission.ManageGuild, requirement.GuildPermission);
    }

    [Fact]
    public void MemberLevelingCommandsDoNotRequireModeratorPermissions()
    {
        Assert.Empty(typeof(LevelingModule).GetCustomAttributes<RequireUserPermissionAttribute>());
        foreach (var method in typeof(LevelingModule).GetMethods()
                     .Where(method => method.GetCustomAttribute<SlashCommandAttribute>() is not null))
        {
            Assert.Empty(method.GetCustomAttributes<RequireUserPermissionAttribute>());
        }
    }
}
