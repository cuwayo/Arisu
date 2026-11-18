using Discord;

namespace Arisu.Modules;

public abstract class ModerationModuleBase : DeferredInteractionModuleBase
{
    protected async Task<bool> CanModerateMemberAsync(
        IGuildUser member,
        bool requireBotHierarchy)
    {
        if (!await CanModerateTargetAsync(member.Id))
        {
            return false;
        }

        if (member.GuildId != Context.Guild.Id)
        {
            await ReplyAsync("Select a member from this server.");
            return false;
        }

        if (Context.User.Id != Context.Guild.OwnerId)
        {
            IGuildUser? moderator;
            try
            {
                moderator = await ((IGuild)Context.Guild).GetUserAsync(
                    Context.User.Id, CacheMode.AllowDownload);
            }
            catch (Exception)
            {
                await ReplyAsync("I could not verify your server role. Please try again.");
                return false;
            }

            if (moderator is null || moderator.Id != Context.User.Id || moderator.GuildId != Context.Guild.Id)
            {
                await ReplyAsync("I could not verify your server role. Please try again.");
                return false;
            }

            if (moderator.Hierarchy <= member.Hierarchy)
            {
                await ReplyAsync(
                    "You can only moderate members below your highest role.");
                return false;
            }
        }

        if (requireBotHierarchy)
        {
            var bot = Context.Guild.CurrentUser;
            if (bot is null)
            {
                await ReplyAsync("I could not verify my server role. Please try again.");
                return false;
            }

            if (bot.Id != Context.Guild.OwnerId && bot.Hierarchy <= member.Hierarchy)
            {
                await ReplyAsync(
                    "My highest role must be above the member's highest role.");
                return false;
            }
        }

        return true;
    }

    protected async Task<bool> CanModerateUserAsync(ulong userId, bool requireBotHierarchy)
    {
        if (!await CanModerateTargetAsync(userId))
        {
            return false;
        }

        IGuildUser? member;
        try
        {
            // A cache miss is not proof that the user has left. Download membership
            // before allowing history changes or actions against an absent user.
            member = await ((IGuild)Context.Guild).GetUserAsync(userId, CacheMode.AllowDownload);
        }
        catch (Exception)
        {
            await ReplyAsync("I could not verify that user's server membership. Please try again.");
            return false;
        }

        if (member is null)
        {
            return true;
        }

        if (member.Id != userId)
        {
            await ReplyAsync("I could not verify that user's server membership. Please try again.");
            return false;
        }

        return await CanModerateMemberAsync(member, requireBotHierarchy);
    }

    private async Task<bool> CanModerateTargetAsync(ulong userId)
    {
        if (userId == Context.User.Id)
        {
            await ReplyAsync("You cannot moderate yourself.");
            return false;
        }

        if (userId == Context.Client.CurrentUser.Id)
        {
            await ReplyAsync("I cannot moderate myself.");
            return false;
        }

        if (userId == Context.Guild.OwnerId)
        {
            await ReplyAsync("You cannot moderate the server owner.");
            return false;
        }

        return true;
    }

    protected async Task<string?> GetReasonAsync(string reason)
    {
        var normalizedReason = reason.Trim();
        if (normalizedReason.Length == 0)
        {
            await ReplyAsync("A moderation reason is required.");
            return null;
        }

        if (normalizedReason.Length > 400)
        {
            await ReplyAsync(
                "A moderation reason can contain at most 400 characters.");
            return null;
        }

        return normalizedReason;
    }

    protected string BuildAuditReason(string reason) =>
        $"Moderator {Context.User.Id}: {reason}";
}
