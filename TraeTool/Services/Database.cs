using System.IO;
using Microsoft.Data.Sqlite;

namespace TraeTool.Services;

/// <summary>
/// SQLite 数据库（%APPDATA%\TraeTool\data\checkin.db）。
/// 启动时 Open 一次建齐 schema；连接串共享（WAL 模式允许并发读）。
/// </summary>
public static class Database
{
    private static string? _connectionString;

    public static void Open()
    {
        if (_connectionString != null) return;
        Directory.CreateDirectory(Paths.DataDir);
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = Paths.DatabaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
        using var conn = new SqliteConnection(cs);
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                PRAGMA journal_mode=WAL;

                CREATE TABLE IF NOT EXISTS checkin_record (
                    id           INTEGER PRIMARY KEY AUTOINCREMENT,
                    account_id   TEXT    NOT NULL,
                    account_name TEXT    NOT NULL DEFAULT '',
                    checked_at   TEXT    NOT NULL,
                    checked_date TEXT    NOT NULL,
                    credits      REAL    NOT NULL DEFAULT 0,
                    is_member    INTEGER NOT NULL DEFAULT 0,
                    raw_code     INTEGER NOT NULL DEFAULT 0
                );
                CREATE INDEX IF NOT EXISTS idx_record_date_account
                    ON checkin_record(checked_date, account_id);

                CREATE TABLE IF NOT EXISTS credits_snapshot (
                    date       TEXT NOT NULL,
                    account_id TEXT NOT NULL,
                    credits    REAL NOT NULL,
                    PRIMARY KEY (date, account_id)
                );
                """;
            cmd.ExecuteNonQuery();
        }
        _connectionString = cs;
        AppLog.Info("db", "数据库已打开: " + Paths.DatabaseFile);
    }

    internal static SqliteConnection CreateConnection()
    {
        if (_connectionString == null)
            throw new InvalidOperationException("Database.Open() 未调用");
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }
}
