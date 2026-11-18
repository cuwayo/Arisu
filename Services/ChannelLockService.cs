using System.Globalization;
using System.Runtime.CompilerServices;
using Discord;
using Discord.WebSocket;
using Npgsql;

namespace Arisu.Services;

public interface IChannelLockService
{
    Task<ChannelLockResult> LockAsync(IGuildChannel channel, string auditReason);

    Task<ChannelLockResult> UnlockAsync(IGuildChannel channel, string auditReason);
}

public enum ChannelLockResult
{
    Locked,
    Unlocked,
    AlreadyLocked,
    NotLocked,
    PermissionsChanged
}

public sealed class ChannelLockService : IChannelLockService
{
    // Coordinate separate service instances sharing a pool without retaining disposed data sources.
    private static readonly ConditionalWeakTable<NpgsqlDataSource, SemaphoreSlim> AdmissionGates = new();
    private readonly NpgsqlDataSource _dataSource;
    private readonly DiscordSocketClient _client;
    private readonly SemaphoreSlim _admissionGate;

    public ChannelLockService(NpgsqlDataSource dataSource, DiscordSocketClient client)
    {
        _dataSource = dataSource;
        _client = client;
        _admissionGate = AdmissionGates.GetValue(dataSource, static _ => new SemaphoreSlim(1, 1));
    }

    public Task<ChannelLockResult> LockAsync(IGuildChannel channel, string auditReason) =>
        ChangeAsync(channel, auditReason, unlock: false);

    public Task<ChannelLockResult> UnlockAsync(IGuildChannel channel, string auditReason) =>
        ChangeAsync(channel, auditReason, unlock: true);

    private async Task<ChannelLockResult> ChangeAsync(
        IGuildChannel channel,
        string auditReason,
        bool unlock)
    {
        if (channel is not ITextChannel || channel is IThreadChannel)
        {
            throw new ArgumentException("Select a text channel, not a thread.", nameof(channel));
        }

        // Admit before acquiring either connection: concurrent lock holders or
        // advisory-lock waiters must not exhaust the pool needed to save snapshots.
        await _admissionGate.WaitAsync();
        try
        {
            return await ChangeAdmittedAsync(channel, auditReason, unlock);
        }
        finally
        {
            _admissionGate.Release();
        }
    }

    private async Task<ChannelLockResult> ChangeAdmittedAsync(
        IGuildChannel channel,
        string auditReason,
        bool unlock)
    {
        // Keep the lock transaction open while a second connection commits the
        // recovery snapshot and performs the Discord operation. Transaction locks
        // remain safe behind PgBouncer transaction pooling (including Neon).
        // This operation requires a pool with at least two connections.
        await using var lockConnection = await _dataSource.OpenConnectionAsync();
        await using var lockTransaction = await lockConnection.BeginTransactionAsync();
        await using var advisoryLock = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(@channelId);", lockConnection, lockTransaction);
        advisoryLock.Parameters.AddWithValue("channelId", unchecked((long)channel.Id));
        await advisoryLock.ExecuteNonQueryAsync();
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using (var schema = new NpgsqlCommand("""
                CREATE TABLE IF NOT EXISTS ChannelModerationLocks (
                    GuildId TEXT NOT NULL,
                    ChannelId TEXT NOT NULL,
                    OriginalSendMessages INTEGER NOT NULL,
                    HadOverwrite BOOLEAN NOT NULL,
                    PRIMARY KEY (GuildId, ChannelId)
                );
                """, connection))
        {
            // Serialize concurrent first use without touching the shared schema version.
            await using var schemaTransaction = await connection.BeginTransactionAsync();
            schema.Transaction = schemaTransaction;
            await using var schemaLock = new NpgsqlCommand(
                "SELECT pg_advisory_xact_lock(4715977861328022636);", connection, schemaTransaction);
            await schemaLock.ExecuteNonQueryAsync();
            await schema.ExecuteNonQueryAsync();
            await schemaTransaction.CommitAsync();
        }

        var current = await GetFreshChannelAsync(channel);
        var everyone = channel.Guild.EveryoneRole;
        var overwrite = current.GetPermissionOverwrite(everyone);
        ChannelLockSnapshot? snapshot = null;
        await using (var read = CreateCommand(connection, channel, """
                SELECT OriginalSendMessages, HadOverwrite FROM ChannelModerationLocks
                WHERE GuildId = @guildId AND ChannelId = @channelId;
                """))
        await using (var reader = await read.ExecuteReaderAsync())
        {
            if (await reader.ReadAsync())
            {
                var original = (PermValue)reader.GetInt32(0);
                if (!Enum.IsDefined(original))
                {
                    throw new InvalidOperationException("The saved channel lock permission is invalid.");
                }

                snapshot = new ChannelLockSnapshot(original, reader.GetBoolean(1));
            }
        }

        if (!unlock)
        {
            if (snapshot is not null)
            {
                if ((overwrite ?? default).SendMessages == PermValue.Deny)
                {
                    return ChannelLockResult.AlreadyLocked;
                }

                // A failed Discord deny leaves the original permission and snapshot
                // intact. Retry without replacing the saved recovery information.
                if ((overwrite ?? default).SendMessages != snapshot.OriginalSendMessages)
                {
                    return ChannelLockResult.PermissionsChanged;
                }
            }
            else
            {
                snapshot = new ChannelLockSnapshot(
                    (overwrite ?? default).SendMessages, overwrite.HasValue);
                // Commit the recovery information BEFORE touching Discord. If the
                // request fails or its result is unknown, retain this snapshot.
                await using var save = CreateCommand(connection, channel, """
                    INSERT INTO ChannelModerationLocks
                        (GuildId, ChannelId, OriginalSendMessages, HadOverwrite)
                    VALUES (@guildId, @channelId, @original, @hadOverwrite);
                    """);
                save.Parameters.AddWithValue("original", (int)snapshot.OriginalSendMessages);
                save.Parameters.AddWithValue("hadOverwrite", snapshot.HadOverwrite);
                await save.ExecuteNonQueryAsync();
            }

            // Fetch again after persistence so unrelated overwrites are as fresh
            // as possible. Discord does not offer conditional overwrite updates.
            current = await GetFreshChannelAsync(channel);
            overwrite = current.GetPermissionOverwrite(everyone);
            if ((overwrite ?? default).SendMessages != snapshot.OriginalSendMessages)
            {
                return ChannelLockResult.PermissionsChanged;
            }

            await current.AddPermissionOverwriteAsync(
                everyone,
                (overwrite ?? default).Modify(sendMessages: PermValue.Deny),
                new RequestOptions { AuditLogReason = auditReason });
            return ChannelLockResult.Locked;
        }

        if (snapshot is null)
        {
            return ChannelLockResult.NotLocked;
        }

        if (!ChannelLockPermissions.CanRestore(overwrite, snapshot))
        {
            return ChannelLockResult.PermissionsChanged;
        }

        // If a previous restore succeeded but database cleanup failed, this is
        // already the original value. Never issue another permission update.
        if ((overwrite ?? default).SendMessages != snapshot.OriginalSendMessages)
        {
            var restored = ChannelLockPermissions.Restore(overwrite, snapshot);
            var options = new RequestOptions { AuditLogReason = auditReason };
            if (restored is { } permissions)
            {
                await current.AddPermissionOverwriteAsync(everyone, permissions, options);
            }
            else
            {
                await current.RemovePermissionOverwriteAsync(everyone, options);
            }
        }

        // Delete recovery information only after a successful Discord restore.
        await using var delete = CreateCommand(connection, channel, """
                DELETE FROM ChannelModerationLocks
                WHERE GuildId = @guildId AND ChannelId = @channelId;
                """);
        await delete.ExecuteNonQueryAsync();
        return ChannelLockResult.Unlocked;
    }

    private async Task<IGuildChannel> GetFreshChannelAsync(IGuildChannel channel)
    {
        // The socket cache can lag behind this process's or another moderator's writes.
        var fresh = await _client.Rest.GetChannelAsync(channel.Id) as IGuildChannel;
        if (fresh is null || fresh.GuildId != channel.GuildId)
        {
            throw new InvalidOperationException("The channel is no longer available in this server.");
        }

        return fresh;
    }

    private static NpgsqlCommand CreateCommand(
        NpgsqlConnection connection,
        IGuildChannel channel,
        string sql)
    {
        var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("guildId", channel.GuildId.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("channelId", channel.Id.ToString(CultureInfo.InvariantCulture));
        return command;
    }
}

public sealed record ChannelLockSnapshot(PermValue OriginalSendMessages, bool HadOverwrite);

public static class ChannelLockPermissions
{
    public static bool CanRestore(OverwritePermissions? current, ChannelLockSnapshot snapshot)
    {
        var sendMessages = (current ?? default).SendMessages;
        return sendMessages == PermValue.Deny || sendMessages == snapshot.OriginalSendMessages;
    }

    public static OverwritePermissions? Restore(
        OverwritePermissions? current,
        ChannelLockSnapshot snapshot)
    {
        var restored = (current ?? default).Modify(sendMessages: snapshot.OriginalSendMessages);
        // Remove only an overwrite created by the lock that is otherwise empty.
        return !snapshot.HadOverwrite && restored.AllowValue == 0 && restored.DenyValue == 0
            ? null
            : restored;
    }
}
