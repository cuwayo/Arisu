using System.Net;
using Arisu.Services;
using Discord;
using Discord.Interactions;
using Discord.Net;
using Microsoft.Extensions.Logging;

namespace Arisu.Modules;

[Group("voicemaster", "Configure and manage temporary voice rooms.")]
[RequireContext(ContextType.Guild)]
public sealed class VoiceMasterModule : DeferredInteractionModuleBase
{
    private readonly IVoiceMasterService _voiceMaster;
    private readonly ILogger<VoiceMasterModule> _logger;

    public VoiceMasterModule(IVoiceMasterService voiceMaster, ILogger<VoiceMasterModule> logger)
    {
        _voiceMaster = voiceMaster;
        _logger = logger;
    }

    [SlashCommand("setup", "Set the lobby and category used to create temporary voice rooms.")]
    [RequireUserPermission(GuildPermission.ManageGuild)]
    public Task SetupAsync(
        [Summary("lobby", "Join this voice channel to create a temporary room.")]
        [ChannelTypes(ChannelType.Voice)] IVoiceChannel lobby,
        [Summary("category", "Create temporary voice rooms in this category.")]
        [ChannelTypes(ChannelType.Category)] ICategoryChannel category) =>
        ExecuteAsync("setup", () => _voiceMaster.ConfigureAsync(Context.Guild, lobby, category));

    [SlashCommand("disable", "Stop creating temporary voice rooms in this server.")]
    [RequireUserPermission(GuildPermission.ManageGuild)]
    public Task DisableAsync() =>
        ExecuteAsync("disable", () => _voiceMaster.DisableAsync(Context.Guild));

    [SlashCommand("rename", "Rename your temporary voice room.")]
    public Task RenameAsync(
        [Summary("name", "The new room name, from 1 to 100 characters.")]
        [MinLength(1), MaxLength(100)] string name) =>
        UpdateRoomAsync(VoiceMasterAction.Rename, name: name);

    [SlashCommand("limit", "Set your temporary voice room's member limit.")]
    public Task LimitAsync(
        [Summary("limit", "Maximum members, from 0 to 99. Use 0 for no limit.")]
        [MinValue(0), MaxValue(99)] int limit) =>
        UpdateRoomAsync(VoiceMasterAction.Limit, limit: limit);

    [SlashCommand("lock", "Prevent other members from joining your temporary voice room.")]
    public Task LockAsync() => UpdateRoomAsync(VoiceMasterAction.Lock);

    [SlashCommand("unlock", "Allow other members to join your temporary voice room.")]
    public Task UnlockAsync() => UpdateRoomAsync(VoiceMasterAction.Unlock);

    [SlashCommand("hide", "Hide your temporary voice room from other members.")]
    public Task HideAsync() => UpdateRoomAsync(VoiceMasterAction.Hide);

    [SlashCommand("show", "Make your temporary voice room visible to other members.")]
    public Task ShowAsync() => UpdateRoomAsync(VoiceMasterAction.Show);

    [SlashCommand("permit", "Allow a member to view and join your temporary voice room.")]
    public Task PermitAsync(
        [Summary("target", "The member to allow into your room.")] IGuildUser target) =>
        UpdateRoomAsync(VoiceMasterAction.Permit, target: target);

    [SlashCommand("reject", "Remove a member's access to your temporary voice room.")]
    public Task RejectAsync(
        [Summary("target", "The member to deny access to your room.")] IGuildUser target) =>
        UpdateRoomAsync(VoiceMasterAction.Reject, target: target);

    [SlashCommand("transfer", "Transfer ownership of your temporary voice room to another member.")]
    public Task TransferAsync(
        [Summary("target", "The member who should own your room.")] IGuildUser target) =>
        UpdateRoomAsync(VoiceMasterAction.Transfer, target: target);

    [SlashCommand("claim", "Claim a temporary voice room after its owner leaves.")]
    public Task ClaimAsync() => UpdateRoomAsync(VoiceMasterAction.Claim);

    private Task UpdateRoomAsync(
        VoiceMasterAction action, string? name = null, int limit = 0, IGuildUser? target = null) =>
        ExecuteAsync(action.ToString(), () =>
            _voiceMaster.UpdateRoomAsync((IGuildUser)Context.User, action, name, limit, target));

    private async Task ExecuteAsync(string operation, Func<Task<string>> action)
    {
        await DeferAsync(ephemeral: true);
        try
        {
            await ReplyAsync(await action());
        }
        catch (HttpException exception)
        {
            _logger.LogWarning(exception,
                "Discord rejected VoiceMaster operation {Operation} by {ActorId} in guild {GuildId}; HTTP {StatusCode}, code {DiscordCode}.",
                operation, Context.User.Id, Context.Guild.Id, exception.HttpCode, exception.DiscordCode);
            var message = exception.HttpCode switch
            {
                HttpStatusCode.Forbidden =>
                    "Discord denied this change. Ask a server administrator to check my View Channel, " +
                    "Manage Channels, Move Members, and permission-management access in the lobby, category, and room.",
                HttpStatusCode.NotFound =>
                    "The room, member, lobby, or category no longer exists. Check your voice channel " +
                    "and ask a server administrator to review /voicemaster setup.",
                HttpStatusCode.BadRequest =>
                    "Discord rejected this change. Check the room name, member limit, and selected channels or member.",
                HttpStatusCode.TooManyRequests =>
                    "Discord is rate limiting voice room changes. Wait briefly and check your room before retrying.",
                _ => "Discord could not complete this change. Check your voice room before retrying."
            };
            await ReplyAsync(message);
        }
        catch (InvalidOperationException exception)
        {
            _logger.LogWarning(exception,
                "VoiceMaster operation {Operation} could not complete for {ActorId} in guild {GuildId}.",
                operation, Context.User.Id, Context.Guild.Id);
            await ReplyAsync("The voice room or its configuration is no longer available for this change. " +
                "Check that you are in the correct room, then retry. A server administrator can review /voicemaster setup.");
        }
        catch (Exception exception)
        {
            _logger.LogError(exception,
                "Unexpected failure during VoiceMaster operation {Operation} by {ActorId} in guild {GuildId}.",
                operation, Context.User.Id, Context.Guild.Id);
            await ReplyAsync("I could not complete this voice room change. Check your room before retrying. " +
                "If the problem continues, ask a server administrator to check the bot logs.");
        }
    }
}
