# Arisu

Arisu is a lightweight Discord moderation bot built with .NET 10, Discord.Net,
and the .NET Generic Host. It provides permission-aware slash commands and a
small, dependency-injection-friendly codebase for extending the bot.

## Features

- Slash commands for health checks and member moderation.
- Permission and role-hierarchy checks before moderation actions.
- Ephemeral moderator responses and audit-log reasons.
- Optional direct-message notification for timeouts.
- Per-guild channels for server audits, member logs, and member events.
- SQLite-backed guild settings.
- Local member-event card archival and custom backgrounds, with optional
  S3-compatible storage.
- Configuration through standard .NET configuration providers and environment
  variables.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- A Discord application with a bot user, created in the
  [Discord Developer Portal](https://discord.com/developers/applications)
- A Discord bot token

## Get started

1. Invite the bot to a development server with the `bot` and
   `applications.commands` scopes. Grant the bot only the permissions needed
   for the commands that you intend to use.
1. Restore packages and build the project.

    ```bash
    dotnet restore
    dotnet build
    ```

1. Provide the bot token and, for immediate command updates during development,
   a development guild ID.

    ```bash
    export Discord__Token='your-bot-token'
    export Discord__DevelopmentGuildId='your-guild-id'
    ```

1. Start the bot.

    ```bash
    dotnet run
    ```

1. Run `/ping` in the development server to confirm that the bot is online.

Do not commit a bot token. Use environment variables, user secrets, or your
deployment platform's secret-management facility.

## Configuration

Arisu binds settings from the `Discord`, `Database`, and `ObjectStorage`
configuration sections. Environment variable names use double underscores
(`__`) to represent nested keys.

| Environment variable | Required | Description |
| --- | --- | --- |
| `Discord__Token` | Yes | Bot token used to authenticate with Discord. |
| `Discord__DevelopmentGuildId` | No | Guild ID for development command registration. |
| `Database__Path` | No | SQLite database path. Defaults to `data/arisu.db`. |
| `ObjectStorage__Enabled` | No | Uses S3 for card archival and custom backgrounds. Defaults to `false`. |
| `ObjectStorage__FolderPath` | No | Local storage folder when S3 is disabled. Defaults to `data/storage`. |
| `ObjectStorage__LocalEventCardArchiveRetentionDays` | No | Days to retain local event-card archives. Set to `0` to disable local card archival. Defaults to `30`. |
| `ObjectStorage__BucketName` | When enabled | S3 bucket for event-card archives and custom backgrounds. |
| `ObjectStorage__Region` | When enabled | AWS region used to sign S3 requests. Defaults to `us-east-1`. |
| `ObjectStorage__ServiceUrl` | No | Absolute S3-compatible endpoint, such as MinIO or Cloudflare R2. |
| `ObjectStorage__ForcePathStyle` | No | Enables path-style bucket URLs for compatible S3 providers. |
| `ObjectStorage__AccessKey` | No | S3 access key. Set with `ObjectStorage__SecretKey`. |
| `ObjectStorage__SecretKey` | No | S3 secret key. Set with `ObjectStorage__AccessKey`. |

When `Discord__DevelopmentGuildId` is set, Arisu registers slash commands in
that guild. When it is not set, Arisu registers commands globally; global
commands can take time to become available.

When S3 storage is disabled, Arisu stores event-card archives and custom
backgrounds in `ObjectStorage__FolderPath`. Local card archives are retained
for 30 days by default and cleaned in the background. Set
`ObjectStorage__LocalEventCardArchiveRetentionDays` to `0` to disable local
card archival. When S3 storage is enabled without `ObjectStorage__AccessKey`
and `ObjectStorage__SecretKey`, the AWS SDK uses its default credential
provider chain. Do not commit S3 credentials; use environment variables, user
secrets, or your deployment platform's secret-management facility.

## Commands

| Command | Required moderator permission | Description |
| --- | --- | --- |
| `/ping` | None | Confirms that Arisu is online. |
| `/kick` | Kick Members | Removes a member from the server. |
| `/ban` | Ban Members | Bans a member from the server. |
| `/timeout` | Moderate Members | Prevents a member from communicating for 1 minute to 28 days. |
| `/untimeout` | Moderate Members | Removes a member timeout. |
| `/settings moderation-logs set` | Manage Server | Sets the server audit channel. |
| `/settings moderation-logs remove` | Manage Server | Removes the server audit channel. |
| `/settings member-logs set` | Manage Server | Sets the member activity log channel. |
| `/settings member-logs remove` | Manage Server | Removes the member log channel. |
| `/settings member-events set` | Manage Server | Sets the welcome, goodbye, and boost channel. |
| `/settings member-events remove` | Manage Server | Removes the member event channel. |
| `/settings member-events background` | Manage Server | Sets the member event card background image. |
| `/settings member-events background-reset` | Manage Server | Restores the default card background image. |

Moderation commands also require the bot to have the corresponding permission.
Arisu prevents actions against yourself, the server owner, and members whose
roles are at or above the moderator's or bot's highest role. Moderation
responses are visible only to the moderator. Timeout notifications are sent by
direct message when possible; a timeout still applies if a direct message
cannot be delivered.

## Server audit channel

Use `/settings moderation-logs set` to configure a text channel for each
server, or `/settings moderation-logs remove` to stop audit delivery. Arisu
posts every audit event that Discord sends, including moderation actions,
channel and voice channel changes or deletion, role and permission changes,
and other server administration actions. This includes actions made with Arisu
and directly in Discord by admins or moderators. It also records bot channel
configuration activity, including the moderator who changed the setting.

Grant the bot the server-level View Audit Log permission. In the selected
channel, grant View Channel, Send Messages, and Embed Links permissions.

Arisu stores audit-channel mappings in SQLite. By default, the database is
`data/arisu.db` under the content root. Keep the database directory writable
and persistent across deployments.

## Member log channel

Use `/settings member-logs set` to configure a channel for member activity
logs, or `/settings member-logs remove` to stop delivery. Arisu
logs member joins and leaves, voice channel joins, leaves, moves, and voice
state changes. It also logs message edits, individual and bulk message
deletions, global avatar changes, and server-specific avatar changes. It keeps
the last 100 messages per channel in memory while running so an edit or
individual deletion can include the member and message content. Edits include
the previous and new content when the original message is cached. Uncached
deletions include only the message ID and channel.

Grant the bot View Channel, Send Messages, and Embed Links permissions in the
selected channel. Enable the Message Content Intent in the Discord Developer
Portal before starting the bot so deleted-message logs can include content.

## Member event channel

Use `/settings member-events set` to configure a separate channel for welcome,
goodbye, and boost notices. Notices are image cards that show the event, the
member's avatar, and their username. Use
`/settings member-events background` to upload a PNG, JPEG, or WebP image up
to 8 MB. Arisu verifies the image type, center-crops the image to fit the
card, and stores it in the configured local or S3 storage backend. Use
`/settings member-events background-reset` to restore the default image. Use
`/settings member-events remove` to stop notification delivery. Grant the bot
View Channel, Send Messages, and Attach Files permissions in the selected
channel.

Member events and avatar logs require the privileged Server Members Intent.
Enable it for the bot in the Discord Developer Portal before running Arisu.

Arisu stores all channel mappings and custom-background object keys in the same
SQLite database. It does not store image data in SQLite.

When `ObjectStorage__Enabled` is `true`, Arisu copies generated event-card PNG
files to the configured S3 bucket before it sends them to Discord and stores
custom event-card backgrounds there. Upload failures for generated cards are
logged and do not stop Discord delivery. Configure a lifecycle rule in the
bucket to retain archives for the appropriate period.

## Development

Run the following checks from the repository root before submitting a change:

```bash
dotnet restore
dotnet build
dotnet format --verify-no-changes
```

There is no automated test project yet. Exercise affected slash commands in a
development guild before submitting a change.

## Project structure

```text
Configuration/  Strongly typed configuration options
Modules/        Discord interaction modules and slash commands
Services/       Gateway lifecycle and interaction dispatch
Program.cs      Host, logging, configuration, and service registration
```

## Contributing

Keep changes focused, asynchronous, and compatible with dependency injection.
Follow the existing C# style and include the validation you performed in your
pull request.

## License

Arisu is licensed under the GNU Affero General Public License, version 3 only.
See [LICENSE](LICENSE) for the full license text. Operators who provide
modified versions for use over a network must offer the corresponding source
code to their users.
