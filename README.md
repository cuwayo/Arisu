# Arisu

Arisu is a lightweight Discord moderation bot built with .NET 10, Discord.Net,
and the .NET Generic Host. It provides permission-aware slash commands and a
small, dependency-injection-friendly codebase for extending the bot.

## Features

- Familiar slash commands for member, channel, and voice moderation.
- Join-to-create temporary voice rooms with owner customization and access controls.
- Native Discord AutoMod for spam, mentions, invites, and blocked words.
- Persistent warning management, moderation cases, and in-Discord command help.
- Permission and role-hierarchy checks before moderation actions.
- Ephemeral moderator responses and audit-log reasons.
- Optional direct-message notification for timeouts.
- Per-guild channels for server audits, member logs, and member events.
- PostgreSQL-backed guild settings and persistent warnings.
- Local member-event card archival and custom backgrounds, with optional
  S3-compatible storage.
- AI image generation through the private MOSS-Image ComfyUI instance.
- Configuration through standard .NET configuration providers and environment
  variables.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- A Discord application with a bot user, created in the
  [Discord Developer Portal](https://discord.com/developers/applications)
- A Discord bot token
- PostgreSQL 16 or later, with an existing database and a dedicated bot role

## Get started

1. Invite the bot to a development server with the `bot` and
   `applications.commands` scopes. Grant the bot only the permissions needed
   for the commands that you intend to use.
1. Restore packages and build the project.

    ```bash
    dotnet restore
    dotnet build
    ```

1. Provide the bot token, PostgreSQL connection string, and, for immediate
   command updates during development, a development guild ID.

    ```bash
    export Discord__Token='your-bot-token'
    export Discord__DevelopmentGuildId='your-guild-id'
    export Database__ConnectionString='Host=localhost;Database=arisu;Username=arisu;Password=your-password;SSL Mode=Disable;Maximum Pool Size=10;Timeout=15;Command Timeout=30'
    ```

1. Start the bot.

    ```bash
    dotnet run
    ```

1. Run `/ping` in the development server to confirm that the bot is online.

Do not commit a bot token. Use environment variables, user secrets, or your
deployment platform's secret-management facility.

## Configuration

Arisu binds settings from the `Discord`, `ComfyUi`, `Database`, and
`ObjectStorage` configuration sections. Environment variable names use double
underscores (`__`) to represent nested keys.

| Environment variable | Required | Description |
| --- | --- | --- |
| `Discord__Token` | Yes | Bot token used to authenticate with Discord. |
| `Discord__DevelopmentGuildId` | No | Guild ID for development command registration. |
| `ComfyUi__Enabled` | No | Enables `/imagine`. Defaults to `false`. |
| `ComfyUi__Endpoint` | When enabled | Absolute URL for the Cerebrium `/run` endpoint. |
| `ComfyUi__Token` | When enabled | Cerebrium bearer token for the private image service. |
| `ComfyUi__WorkflowPath` | No | API-format MOSS-Image workflow path. Defaults to the bundled workflow. |
| `ComfyUi__PromptNodeId` | No | Workflow node that receives the image description. Defaults to `18:17`. |
| `ComfyUi__PromptInputName` | No | Prompt-node input that receives the description. Defaults to `value`. |
| `ComfyUi__NegativePromptNodeId` | No | Workflow node that receives the negative description. Defaults to `18:5`. |
| `ComfyUi__NegativePromptInputName` | No | Negative-prompt input that receives the description. Defaults to `text`. |
| `ComfyUi__TimeoutSeconds` | No | Maximum image-generation time, from 1 to 840 seconds. Defaults to `840`. |
| `ComfyUi__MaximumConcurrentRequests` | No | Maximum simultaneous generation requests. Defaults to `1`. |
| `ComfyUi__UserCooldownSeconds` | No | Seconds each user must wait after a generation request completes. Defaults to `60`. |
| `Database__ConnectionString` | Yes | PostgreSQL connection string. Supply through environment variables or deployment secrets. |
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

To enable image generation, configure the private endpoint deployed from the
sibling `comfyui-instance` project and its Cerebrium bearer token. The endpoint
can return the sibling project's direct response or Cerebrium's standard `/run`
response envelope. The bot submits the bundled MOSS-Image API workflow and
replaces its text prompt and, when supplied, its negative prompt for each
`/imagine` request.

## PostgreSQL

`Database__ConnectionString` accepts Npgsql connection strings and
`postgresql://` or `postgres://` URLs, including Neon connection URLs. URL
options include `sslmode`, `channel_binding`, `sslrootcert`, `connect_timeout`,
`command_timeout`, `application_name`, and `options`. Unsupported and duplicate
URL options are rejected. Percent-encode reserved characters in URL credentials.
For example, the following uses placeholder credentials:

```text
postgresql://arisu:your-password@your-endpoint-pooler.aws.neon.tech/neondb?sslmode=verify-full&channel_binding=require
```

Create the database and a dedicated role before starting Arisu. At startup,
Arisu creates its tables and indexes inside a transaction. The role needs
`CONNECT` on the database and `USAGE` and `CREATE` on the selected schema, plus
access to tables and sequences created by that role. Do not use the RDS master
user for the bot. PostgreSQL connections are pooled and database operations
honor cancellation and connection-string timeouts. Do not enable `Log
Parameters` or `Include Error Detail`; startup validation rejects those settings
to keep moderation data out of database error logs.

For AWS, use a private Amazon RDS for PostgreSQL instance. Allow inbound port
5432 on its security group only from the bot's security group. Enable storage
encryption, automated backups, and a retention period that meets your recovery
requirements. Test restoring a backup before relying on it.

Use certificate and hostname verification for RDS connections. Download the
appropriate CA bundle from the
[AWS trust store](https://truststore.pki.rds.amazonaws.com/), install it on the
bot host, and supply a connection string through AWS Secrets Manager or Systems
Manager Parameter Store:

```text
Host=your-database.region.rds.amazonaws.com;Port=5432;Database=arisu;Username=arisu;Password=your-password;SSL Mode=VerifyFull;Root Certificate=/etc/arisu/rds-ca-bundle.pem;Maximum Pool Size=10;Timeout=15;Command Timeout=30
```

`SSL Mode=Disable` in the getting-started example is for local development.
Use `VerifyFull` in AWS. A running bot does not require a local database file.

### Existing SQLite data

This version replaces the SQLite backend. `Database__Path` is no longer used.
Existing SQLite files are left untouched, and their settings and warnings are
not imported automatically. Back up the SQLite database before switching. If
you need its data, transfer channel settings, background object keys, warning
policies, warning history, and escalation markers before starting the bot on
PostgreSQL. Preserve warning IDs and advance the PostgreSQL identity sequence
after an import. Keep the same object-storage backend so imported background
keys still resolve.

## Commands

| Command | Required moderator permission | Description |
| --- | --- | --- |
| `/ping` | None | Confirms that Arisu is online. |
| `/imagine` | None | Opens a form for image descriptions and optional dimensions. |
| `/kick` | Kick Members | Removes a member from the server. |
| `/ban` | Ban Members | Bans a user, including users outside the server; optionally deletes up to 7 days of messages. |
| `/unban` | Ban Members | Removes a ban by user ID. |
| `/timeout` | Moderate Members | Applies a timeout with a duration such as `30s`, `10m`, `2h`, or `7d`, up to 28 days. |
| `/untimeout` | Moderate Members | Removes a member timeout. |
| `/warn` | Moderate Members | Adds a persistent warning to a member. |
| `/warnings` | Moderate Members | Lists a member's warning history. |
| `/delwarn` | Moderate Members | Deletes one warning by its ID. |
| `/clearwarnings` | Moderate Members | Clears a member's warnings with explicit confirmation. |
| `/reason` | Moderate Members | Updates a warning's reason. |
| `/modlogs` | Moderate Members | Shows the latest eight moderation cases recorded by Arisu for a user. |
| `/nick` | Manage Nicknames | Changes a member's nickname, or resets it when omitted. |
| `/purge` | Manage Messages | Deletes up to 100 recent messages, optionally filtered by user. |
| `/slowmode` | Manage Channels | Sets channel slowmode from 0 to 21,600 seconds. |
| `/lock`, `/unlock` | Manage Channels | Saves and denies the everyone role's Send Messages permission, then restores it. |
| `/voice mute`, `/voice unmute` | Mute Members | Sets or removes a server voice mute. |
| `/voice deafen`, `/voice undeafen` | Deafen Members | Sets or removes server voice deafen. |
| `/voice move`, `/voice disconnect` | Move Members | Moves or disconnects a connected member. |
| `/voicemaster setup`, `/voicemaster disable` | Manage Server | Configures or disables the join-to-create lobby. |
| `/voicemaster rename`, `/voicemaster limit` | Room owner | Changes the temporary room's name or member limit. |
| `/voicemaster lock`, `/voicemaster unlock`, `/voicemaster hide`, `/voicemaster show` | Room owner | Controls joining and visibility. |
| `/voicemaster permit`, `/voicemaster reject` | Room owner | Allows or denies a member's access to the room. |
| `/voicemaster transfer`, `/voicemaster claim` | Room owner or eligible occupant | Transfers ownership or claims a room after its owner leaves. |
| `/automod` | Manage Server | Configures native spam, mention, invite, and keyword filters. |
| `/help` | None | Shows commands and setup guidance. |
| `/settings logs set` | Manage Server | Sets the server audit channel. |
| `/settings logs remove` | Manage Server | Removes the server audit channel. |
| `/settings member-logs set` | Manage Server | Sets the member activity log channel. |
| `/settings member-logs remove` | Manage Server | Removes the member log channel. |
| `/settings welcome set` | Manage Server | Sets the welcome, goodbye, and boost channel. |
| `/settings welcome remove` | Manage Server | Removes the member event channel. |
| `/settings welcome background` | Manage Server | Sets the member event card background image. |
| `/settings welcome background-reset` | Manage Server | Restores the default card background image. |
| `/settings warnings set-escalation` | Manage Server | Configures a warning threshold and timeout. |
| `/settings warnings remove-escalation` | Manage Server | Disables automatic warning timeouts. |

Moderation commands also require the bot to have the corresponding permission.
Arisu prevents actions against yourself, the server owner, and members whose
roles are at or above the moderator's or bot's highest role. Moderation
responses are visible only to the moderator. Timeout notifications are sent by
direct message when possible; a timeout still applies if a direct message
cannot be delivered.

Member-action reasons are optional except for warnings. `/timeout` accepts
whole-number durations with `s`, `m`, `h`, or `d`; a number without a suffix
means minutes. `/voice mute` affects voice only. Use `/timeout` to restrict
text and voice communication together.

`/settings logs` replaces `/settings moderation-logs`, and `/settings welcome`
replaces `/settings member-events`. Restart Arisu to register the updated
commands. `/help` lists the current names.

### Channel moderation

`/purge` scans at most 100 recent messages in the current channel and deletes
at most the requested amount. An optional user filter narrows that scan. Older
messages outside Discord's bulk-deletion window are skipped. The response
reports what was deleted. Arisu checks the moderator's and bot's permissions
in the affected channel.

`/lock` saves the everyone role's original Send Messages overwrite in
PostgreSQL before denying it. `/unlock` restores that saved state and preserves
other permission bits. Members with explicit role or member allows, and
administrators, may still send messages. This is a channel messaging lock;
it does not stop voice, threads, or all server activity. Keep PostgreSQL
available so locks remain recoverable across restarts. Both the moderator and
bot need Manage Channels and Manage Permissions in the target channel.
Use a connection pool with at least two available connections for lock
operations; coordination and the durable snapshot use separate connections.
Arisu coordinates local lock requests before opening those connections to avoid
pool exhaustion. If a lock request fails before changing Discord permissions,
retry `/lock`; Arisu reuses the saved original state. Arisu uses
transaction-scoped advisory locks, compatible with Neon pooled connections.

### Temporary voice rooms

Create a regular voice channel for the join-to-create lobby and a category for
new rooms. Grant Arisu View Channel, Connect, Manage Channels, Manage Permissions,
and Move Members in the lobby and destination category. Members need access to
the lobby. A member with Manage Server can then configure one lobby per server:

```text
/voicemaster setup lobby:Join to create category:Voice rooms
```

When a human member joins the lobby, Arisu creates a room that inherits the
category's permission overwrites and moves the member into it. The creator owns
the room and has explicit View Channel and Connect access. Arisu retains explicit
access to manage it. Joining the lobby again returns the owner to an existing
room when one is still occupied. Creation attempts have a 15-second cooldown per
member to reduce repeated channel creation.

While connected to their room, the owner can use:

```text
/voicemaster rename name:Study room
/voicemaster limit limit:5
/voicemaster lock
/voicemaster unlock
/voicemaster hide
/voicemaster show
/voicemaster permit target:@member
/voicemaster reject target:@member
/voicemaster transfer target:@member
```

A limit of `0` means unlimited. Lock and hide deny Connect and View Channel,
respectively, for the everyone role. Unlock and show explicitly allow those
permissions. Administrators and members with explicit role or member allows can
retain access. Permit grants a member View Channel and Connect; reject denies
both and disconnects the member if they are in the room. Server owners,
administrators, and members with Manage Server or Manage Channels cannot be
rejected. Unrelated overwrite permissions remain unchanged.

Ownership transfer requires the new owner to be connected to the room. Run the
commands from a text channel you can still access after hiding the room. If the
owner leaves, another connected member can use `/voicemaster claim`. Previous
owners retain their existing member access overwrite. Owner commands do not
require server-wide moderation permissions, and replies are visible only to the
caller.

Arisu deletes tracked rooms when they become empty. Lobby settings and room
ownership persist in PostgreSQL. On reconnect, and once per minute while online,
Arisu checks for empty or deleted rooms and retries pending lobby creation.
Channels outside its tracked rooms are left alone. Run `/voicemaster disable` to
stop creation; existing rooms remain customizable until they become empty.
Restart Arisu after updating it to register the new commands.

### AutoMod

Both the moderator and Arisu need Manage Server. Configure filters with:

```text
/automod status
/automod spam enable
/automod spam disable
/automod mentions limit:5
/automod invites enable
/automod invites disable
/automod words add keyword:*example*
/automod words remove keyword:*example*
/automod words list
/automod exempt role
/automod exempt channel
```

Spam, mention, invite, and keyword rules block matching messages using
Discord's native AutoMod. Mention rules also enable native mention raid
protection. Detection runs while Arisu is offline. Optional alert channels
send Discord's native alerts; the bot needs access to those channels.
Use Server Settings > AutoMod to inspect rules and remove alert actions.

Arisu manages only rules with its reserved `Arisu:` names. Existing rules
remain in place and count toward Discord's quotas: one spam rule, one mention
rule, and six keyword rules, including disabled rules. Keyword rules allow
1,000 entries, with up to 20 exempt roles and 50 exempt channels per rule.
Configure exemptions separately for each filter after creating its rule.
Removing the last blocked word deletes that rule and its exemptions.

Discord controls spam sensitivity and exempts members with Administrator or
Manage Server. Invite filtering matches ordinary Discord invite links;
obfuscated links can bypass it. This implementation does not add custom
message-rate thresholds, account-age checks, join-raid detection, CAPTCHA,
phishing reputation, scheduled bans, or a web dashboard.

## Warnings

Use `/warn` to create a persistent, per-server warning with a required reason.
Use `/warnings` to see the eight most recent warnings for a member. Warning
history is retained when automatic escalation is disabled. `/delwarn` deletes
a warning by ID, `/reason` edits its reason, and `/clearwarnings` clears a
member's warnings when `confirmed` is `true`. These changes are scoped to the
current server. Warning changes reject self-actions and actions against the
server owner or Arisu. For current members, your highest role must be above the
target's highest role. Removing warnings does not reset escalation markers.

`/modlogs` shows persistent cases for member actions performed through Arisu.
It does not import older Discord audit entries or cases from another bot.
Warning IDs and moderation case IDs are separate; `/reason` takes a warning ID.

To automatically time out members at a warning threshold, use
`/settings warnings set-escalation`. For example, a threshold of `3` with a
timeout of `60` applies a one-hour timeout when a member receives their third
warning. Escalation applies once per member for each configured policy; it
does not time out the member again for later warnings. Updating or re-enabling
the policy lets it apply again after a member's next warning. Use
`/settings warnings remove-escalation` to disable automatic timeouts without
removing warning history. If Discord cannot apply an escalation timeout, the
next warning retries the escalation.

## Server audit channel

Use `/settings logs set` to configure a text channel for each
server, or `/settings logs remove` to stop audit delivery. Arisu
posts every audit event that Discord sends, including moderation actions,
channel and voice channel changes or deletion, role and permission changes,
and other server administration actions. This includes actions made with Arisu
and directly in Discord by admins or moderators. It also records bot channel
configuration activity, including the moderator who changed the setting.

Grant the bot the server-level View Audit Log permission. In the selected
channel, grant View Channel, Send Messages, and Embed Links permissions.

Arisu stores audit-channel mappings in PostgreSQL. The database must remain
available to the bot across deployments.

### Log delivery and database outages

Gateway callbacks enqueue audit and member logs for background delivery. The
queue holds up to 1,024 entries and processes them in order. When it fills,
Arisu drops new entries and logs the dropped count. The queue is in memory;
queued entries do not survive a crash. Shutdown attempts to drain accepted
entries before Discord logs out, within the host's shutdown deadline.

Audit and member log channel settings are cached for five minutes, including
servers without a configured channel. Settings commands update the cache after
a successful database write. During a refresh failure, Arisu keeps the last
known channel and waits 30 seconds before retrying. Without a cached channel,
that log entry cannot be delivered. This reduces database traffic and keeps
connection failures off the gateway callback; it does not repair DNS or
network outages.

If Npgsql reports `System.Net.Dns` errors or connection timeouts, check DNS
resolution and outbound PostgreSQL access from the bot's host. Verify the
endpoint shown in your Neon dashboard and check Neon service status. Preserve
the endpoint hostname for TLS verification; do not replace it with a fixed IP.

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

Use `/settings welcome set` to configure a separate channel for welcome,
goodbye, and boost notices. Notices are image cards that show the event, the
member's avatar, and their username. Use
`/settings welcome background` to upload a PNG, JPEG, or WebP image up
to 8 MB. Arisu verifies the image type, center-crops the image to fit the
card, and stores it in the configured local or S3 storage backend. Use
`/settings welcome background-reset` to restore the default image. Use
`/settings welcome remove` to stop notification delivery. Grant the bot
View Channel, Send Messages, and Attach Files permissions in the selected
channel.

Member events and avatar logs require the privileged Server Members Intent.
Enable it for the bot in the Discord Developer Portal before running Arisu.

Arisu stores channel mappings, warning history, escalation settings, and
custom-background object keys in PostgreSQL. Image data stays in the configured
local folder or S3 bucket.

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

Run database integration tests against a disposable PostgreSQL database. Tests
create and drop isolated schemas and require permission to do so. Supply its
connection string through `ARISU_TEST_DATABASE_CONNECTION_STRING`, then run:

```bash
dotnet test tests/Arisu.IntegrationTests/Arisu.IntegrationTests.csproj
```

The integration tests fail if the connection string is missing. Use a separate
test database and test role. Exercise affected slash commands in a development
guild before submitting a change.

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
