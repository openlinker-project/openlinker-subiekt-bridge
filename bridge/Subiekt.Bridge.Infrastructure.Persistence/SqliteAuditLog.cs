using Microsoft.Data.Sqlite;
using Subiekt.Bridge.Application.Ports;
using Subiekt.Bridge.Domain.Common;

namespace Subiekt.Bridge.Infrastructure.Persistence;

/// <summary>
/// Durable <see cref="IAuditLog"/> backed by SQLite, replacing the legacy
/// in-memory list. Each call opens and closes its own connection (SQLite is a
/// local file; connection pooling makes this cheap) so there is no shared
/// mutable connection to guard. The schema is created on first construction.
/// </summary>
public sealed class SqliteAuditLog : IAuditLog
{
    private readonly string _connectionString;

    public SqliteAuditLog(SqliteAuditLogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var path = string.IsNullOrWhiteSpace(options.DatabasePath) ? "audit-log.db" : options.DatabasePath;

        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        EnsureSchema();
    }

    public async Task<Result> LogAsync(
        string operationType,
        string? inputJson,
        string? outputJson,
        int statusCode,
        string? errorMessage = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                @"INSERT INTO AuditLog (TimestampUtc, OperationType, InputJson, OutputJson, StatusCode, ErrorMessage)
                  VALUES ($ts, $op, $in, $out, $status, $err);";
            cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$op", operationType ?? string.Empty);
            cmd.Parameters.AddWithValue("$in", (object?)inputJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$out", (object?)outputJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$status", statusCode);
            cmd.Parameters.AddWithValue("$err", (object?)errorMessage ?? DBNull.Value);

            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure(new Error("audit.log_failed", $"Failed to write audit entry: {ex.Message}"));
        }
    }

    public async Task<Result<IReadOnlyList<AuditEntry>>> GetLastAsync(
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
            limit = 10;

        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                @"SELECT Id, TimestampUtc, OperationType, InputJson, OutputJson, StatusCode, ErrorMessage
                  FROM AuditLog
                  ORDER BY Id DESC
                  LIMIT $limit;";
            cmd.Parameters.AddWithValue("$limit", limit);

            var list = new List<AuditEntry>(limit);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var id = reader.GetInt64(0);
                var ts = DateTime.Parse(
                    reader.GetString(1),
                    null,
                    System.Globalization.DateTimeStyles.RoundtripKind);
                var op = reader.GetString(2);
                var inJson = reader.IsDBNull(3) ? null : reader.GetString(3);
                var outJson = reader.IsDBNull(4) ? null : reader.GetString(4);
                var status = reader.GetInt32(5);
                var err = reader.IsDBNull(6) ? null : reader.GetString(6);

                list.Add(new AuditEntry(id, ts, op, inJson, outJson, status, err));
            }

            return Result.Success<IReadOnlyList<AuditEntry>>(list);
        }
        catch (Exception ex)
        {
            return Result.Failure<IReadOnlyList<AuditEntry>>(
                new Error("audit.read_failed", $"Failed to read audit entries: {ex.Message}"));
        }
    }

    private void EnsureSchema()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            @"CREATE TABLE IF NOT EXISTS AuditLog (
                  Id            INTEGER PRIMARY KEY AUTOINCREMENT,
                  TimestampUtc  TEXT    NOT NULL,
                  OperationType TEXT    NOT NULL,
                  InputJson     TEXT    NULL,
                  OutputJson    TEXT    NULL,
                  StatusCode    INTEGER NOT NULL,
                  ErrorMessage  TEXT    NULL
              );";
        cmd.ExecuteNonQuery();
    }
}
