using Microsoft.Data.Sqlite;

namespace Orbis.Stream.Core.Data;

/// <summary>
/// Opens SQLite connections against the application database. The pool settings replace
/// the HikariCP configuration of the Java version (pool of 10, 30s connection timeout):
/// Microsoft.Data.Sqlite pools connections internally, so the pragmas below are what
/// actually matters (write concurrency and referential integrity).
/// </summary>
public sealed class SqliteConnectionFactory
{
    private const int BusyTimeoutMilliseconds = 30_000;

    private readonly string _connectionString;

    public SqliteConnectionFactory(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        DatabasePath = Path.GetFullPath(databasePath);

        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = BusyTimeoutMilliseconds / 1000
        }.ToString();
    }

    public string DatabasePath { get; }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        ApplyPragmas(connection);
        return connection;
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        ApplyPragmas(connection);
        return connection;
    }

    private static void ApplyPragmas(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 30000; PRAGMA journal_mode = WAL;";
        command.ExecuteNonQuery();
    }
}
