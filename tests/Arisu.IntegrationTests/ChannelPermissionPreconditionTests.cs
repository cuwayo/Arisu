using System.Reflection;
using Arisu.Modules;
using Discord;
using Discord.Interactions;
using Xunit;

namespace Arisu.IntegrationTests;

public sealed class ChannelPermissionPreconditionTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task PurgePreconditionsUseChannelOverwritesInsteadOfGuildPermissions(bool allowed, bool guildAllowed)
    {
        var channel = Fake<ITextChannel>((method, _) => throw Unexpected(method));
        var member = Fake<IGuildUser>((method, _) => method.Name switch
        {
            "get_GuildPermissions" => new GuildPermissions(guildAllowed ? (ulong)GuildPermission.ManageMessages : 0),
            "GetPermissions" => new ChannelPermissions(allowed ? (ulong)ChannelPermission.ManageMessages : 0),
            _ => throw Unexpected(method)
        });
        var guild = Fake<IGuild>((method, _) => method.Name switch
        {
            "GetCurrentUserAsync" => Task.FromResult(member),
            _ => throw Unexpected(method)
        });
        var context = Fake<IInteractionContext>((method, _) => method.Name switch
        {
            "get_Channel" => channel,
            "get_User" => member,
            "get_Guild" => guild,
            _ => throw Unexpected(method)
        });
        var command = typeof(ChannelModerationModule).GetMethod(nameof(ChannelModerationModule.PurgeAsync))!;
        var userPermission = command.GetCustomAttribute<RequireUserPermissionAttribute>()!;
        var botPermission = command.GetCustomAttribute<RequireBotPermissionAttribute>()!;

        Assert.Null(userPermission.GuildPermission);
        Assert.Null(botPermission.GuildPermission);
        Assert.Equal(ChannelPermission.ManageMessages, userPermission.ChannelPermission);
        Assert.Equal(ChannelPermission.ManageMessages, botPermission.ChannelPermission);
        Assert.Equal(allowed, (await userPermission.CheckRequirementsAsync(context, null!, null!)).IsSuccess);
        Assert.Equal(allowed, (await botPermission.CheckRequirementsAsync(context, null!, null!)).IsSuccess);
    }

    [Theory]
    [InlineData(typeof(ChannelModerationModule), nameof(ChannelModerationModule.SlowmodeAsync))]
    [InlineData(typeof(ChannelModerationModule), nameof(ChannelModerationModule.LockAsync))]
    [InlineData(typeof(ChannelModerationModule), nameof(ChannelModerationModule.UnlockAsync))]
    [InlineData(typeof(VoiceModerationModule), nameof(VoiceModerationModule.MuteAsync))]
    [InlineData(typeof(VoiceModerationModule), nameof(VoiceModerationModule.UnmuteAsync))]
    [InlineData(typeof(VoiceModerationModule), nameof(VoiceModerationModule.DeafenAsync))]
    [InlineData(typeof(VoiceModerationModule), nameof(VoiceModerationModule.UndeafenAsync))]
    [InlineData(typeof(VoiceModerationModule), nameof(VoiceModerationModule.DisconnectAsync))]
    [InlineData(typeof(VoiceModerationModule), nameof(VoiceModerationModule.MoveAsync))]
    public void TargetChannelCommandsDoNotRequirePermissionsInTheGuildOrInvocationChannel(Type module, string methodName)
    {
        // These commands check the affected channel inside their handlers. A
        // precondition would check the guild or the unrelated invocation channel.
        var command = module.GetMethod(methodName)!;
        Assert.Empty(command.GetCustomAttributes<RequireUserPermissionAttribute>());
        Assert.Empty(command.GetCustomAttributes<RequireBotPermissionAttribute>());
    }

    private static T Fake<T>(Func<MethodInfo, object?[], object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, InterfaceProxy>();
        ((InterfaceProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    private static Exception Unexpected(MethodInfo method) =>
        new InvalidOperationException($"Unexpected offline call: {method.Name}");

    public class InterfaceProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Handler { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            Handler(targetMethod!, args ?? []);
    }
}
