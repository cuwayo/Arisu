# Integration and regression tests

Persistence tests use the real PostgreSQL stores and fail with a setup message
if `ARISU_TEST_DATABASE_CONNECTION_STRING` is missing or blank. Other tests run
offline with fake Discord transports or stores to verify command registration,
authorization, permission updates, and service coordination without contacting
Discord.

## Prepare a disposable database

Use a **fresh, isolated, disposable database only**. Never point these tests at
production, staging, or any database with data you need to keep.

Each test creates a uniquely named `arisu_test_<guid>` schema. Its Npgsql data
source sets `SearchPath` to that schema without a `public` fallback. Cleanup
closes the data source and executes `DROP SCHEMA <test_schema> CASCADE`. Tests do
not reset or drop `public`. The database role must be able to connect, create
schemas, and create and drop objects in its own schemas.

For example, start a disposable PostgreSQL 18 instance with Docker:

```bash
docker run --detach --rm --name arisu-integration-postgres \
  --publish 127.0.0.1:55432:5432 \
  --env POSTGRES_USER=arisu_test \
  --env POSTGRES_PASSWORD=local-disposable-only \
  --env POSTGRES_DB=arisu_integration \
  postgres:18
```

Wait for PostgreSQL to accept connections:

```bash
docker exec arisu-integration-postgres \
  pg_isready --username=arisu_test --dbname=arisu_integration
```

## Run the tests

From the repository root, run:

```bash
export ARISU_TEST_DATABASE_CONNECTION_STRING='Host=127.0.0.1;Port=55432;Database=arisu_integration;Username=arisu_test;Password=local-disposable-only'
dotnet test tests/Arisu.IntegrationTests/Arisu.IntegrationTests.csproj
```

Supply credentials through your shell or CI secret configuration. Do not commit
connection strings for shared services.

Persistence tests cover channel and background settings, empty-row cleanup,
bounded warning history, unsigned Discord snowflakes, timestamps, concurrent
warning counts and escalation, policy resets, application retries, guild
isolation, moderation history, and VoiceMaster room ownership. Concurrency tests
use multiple store instances sharing one database schema.

Offline regression tests cover command registration, warning mutation
authorization, channel-specific permission preconditions, VoiceMaster permission
updates, channel-lock recovery, log-channel cache publication, and moderation
history-save notices. Separate theory cases validate database connection strings,
including required fields, finite timeouts, safe logging options, local
connections, and certificate-verified RDS connections.

Stop and remove the disposable database after the run:

```bash
docker stop arisu-integration-postgres
```

Normal completion drops each test schema, including when an assertion fails.
If the test process is terminated or database cleanup fails, schemas can remain.
Discard the disposable database or container after use.
