# Repository Guidelines

Arisu is a .NET 10 Discord bot built with Discord.Net and the .NET Generic
Host. Keep changes small, asynchronous, and compatible with dependency
injection.

## Project Structure and Module Organization

- `Program.cs` configures the host, logging, options, and service registrations.
- `Configuration/` contains strongly typed options bound from configuration.
- `Services/` owns Discord gateway lifecycle and interaction dispatch.
- `Modules/` contains Discord.Net interaction modules; add slash commands here.
- `Storage/` contains PostgreSQL persistence and object-storage adapters.
- `tests/Arisu.IntegrationTests/` tests persistence against real PostgreSQL.
- `appsettings.json` holds non-secret defaults. Build output is generated in
  `bin/` and `obj/`; do not edit or commit generated files.

Use the `Arisu.<Area>` namespace that corresponds to the folder. For example,
place a new command module at `Modules/StatusModule.cs` in `Arisu.Modules`.

## Build, Run, and Formatting Commands

Run these commands from the repository root:

```bash
dotnet restore
dotnet build
dotnet format --verify-no-changes
Discord__Token='your-bot-token' \
  Discord__DevelopmentGuildId='your-guild-id' \
  Database__ConnectionString='your-postgresql-connection-string' dotnet run
```

`restore` fetches packages, `build` compiles the bot, and the format check
detects style drift. Use a development guild when running locally so slash
command registration is immediate; omit the guild ID only when intentionally
registering commands globally.

## Coding Style and Naming

Use four-space indentation, file-scoped namespaces, nullable reference types,
and `async` APIs for Discord and host operations. Follow existing C# naming:
PascalCase for public types and members, camelCase for parameters and locals,
and `_camelCase` for private fields. Name command methods with an `Async`
suffix, such as `StatusAsync`. Prefer constructor injection over service
location, and log failures with structured message templates.

## Documentation Style

Always follow the [Google Developer Documentation Style Guide](https://google.github.io/styleguide/docguide/style.html)
when creating or editing repository documentation. Use clear ATX headings,
concise active prose, standard Markdown, and wrapped text without trailing
whitespace.

## Testing and Manual Verification

Before submitting a change, run `dotnet build`, the format check, and affected
integration tests. Set `ARISU_TEST_DATABASE_CONNECTION_STRING` to a disposable
PostgreSQL database, then run
`dotnet test tests/Arisu.IntegrationTests/Arisu.IntegrationTests.csproj`. Tests
create and drop isolated schemas; use a separate test database and role. Name
test methods after the behavior being verified. Exercise affected commands in
a development guild.

## Configuration, Commits, and Pull Requests

Never place a Discord token in `appsettings.json` or source control. Supply it
through `Discord__Token`, user secrets, or deployment configuration.

This checkout contains no Git history, so no established commit convention can
be verified. Use concise imperative subjects (for example, `Add status
command`). Pull requests should describe behavior changes, list validation
commands, link relevant issues, and include Discord screenshots or command
output when user-visible behavior changes.
