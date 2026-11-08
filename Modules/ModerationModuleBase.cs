using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace Arisu.Modules;

public abstract class ModerationModuleBase : InteractionModuleBase<SocketInteractionContext>
{
    protected async Task<bool> CanModerateMemberAsync(
        SocketGuildUser member,
        bool requireBotHierarchy)
    {
        if (member.Id == Context.User.Id)
        {
            await RespondAsync("You cannot moderate yourself.", ephemeral: true);
            return false;
        }

        if (member.Id == Context.Guild.OwnerId)
        {
            await RespondAsync("You cannot moderate the server owner.", ephemeral: true);
            return false;
        }

        if (Context.User.Id != Context.Guild.OwnerId)
        {
            IGuildUser? moderator = Context.Guild.GetUser(Context.User.Id);
            moderator ??= await ((IGuild)Context.Guild).GetUserAsync(
                Context.User.Id,
                CacheMode.AllowDownload);
            if (moderator is null)
            {
                await RespondAsync(
                    "I could not verify your server role. Please try again.",
                    ephemeral: true);
                return false;
            }

            if (moderator.Hierarchy <= member.Hierarchy)
            {
                await RespondAsync(
                    "You can only moderate members below your highest role.",
                    ephemeral: true);
                return false;
            }
        }

        if (requireBotHierarchy &&
            Context.Guild.CurrentUser.Id != Context.Guild.OwnerId &&
            Context.Guild.CurrentUser.Hierarchy <= member.Hierarchy)
        {
            await RespondAsync(
                "My highest role must be above the member's highest role.",
                ephemeral: true);
            return false;
        }

        return true;
    }

    protected async Task<string?> GetReasonAsync(string reason)
    {
        var normalizedReason = reason.Trim();
        if (normalizedReason.Length == 0)
        {
            await RespondAsync("A moderation reason is required.", ephemeral: true);
            return null;
        }

        if (normalizedReason.Length > 400)
        {
            await RespondAsync(
                "A moderation reason can contain at most 400 characters.",
                ephemeral: true);
            return null;
        }

        return normalizedReason;
    }

    protected string BuildAuditReason(string reason) =>
        $"Moderator {Context.User.Id}: {reason}";
}
