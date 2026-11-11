using Microsoft.Data.Sqlite;
using TraeTool.Models;

namespace TraeTool.Services;

/// <summary>一条签到记录（读侧展示模型）。记录随账号走，删账号不删历史。</summary>
public sealed record CheckinRecordRow(
    long Id,
    string AccountId,
    string AccountName,
    string CheckedAt,
    string CheckedDate,
    double Credits,
    bool IsMember,
    int RawCode)
{
    /// <summary>说明列（记录表第 4 列）：区分正常签到 / 服务器已签补记 / 会员加成。</summary>
    public string Note =>
        Credits > 0 ? (IsMember ? "签到 · 会员加成" : "签到")
        : RawCode == 0 ? "服务器已签 · 补记"
        : $"code={RawCode}";
}

/// <summary>checkin_record / credits_snapshot 读写。</summary>
public static class CheckinRecordRepo
{
    public static void Insert(Account account, double gained, int rawCode)
    {
        var now = DateTime.Now;
        using var conn = Database.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO checkin_record
                (account_id, account_name, checked_at, checked_date, credits, is_member, raw_code)
            VALUES
                ($id, $name, $at, $date, $credits, $member, $code);
            """;
        cmd.Parameters.AddWithValue("$id", account.Id);
        cmd.Parameters.AddWithValue("$name", account.DisplayName(0));
        cmd.Parameters.AddWithValue("$at", now.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.Parameters.AddWithValue("$date", now.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("$credits", gained);
        cmd.Parameters.AddWithValue("$member", account.IsMember ? 1 : 0);
        cmd.Parameters.AddWithValue("$code", rawCode);
        cmd.ExecuteNonQuery();
    }

    /// <summary>最近 N 条；accountId 为空查全部。</summary>
    public static List<CheckinRecordRow> Recent(int limit = 100, string? accountId = null)
    {
        var result = new List<CheckinRecordRow>();
        using var conn = Database.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, account_id, account_name, checked_at, checked_date, credits, is_member, raw_code
            FROM checkin_record
            WHERE ($acc = '' OR account_id = $acc)
            ORDER BY id DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$acc", accountId ?? "");
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new CheckinRecordRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetDouble(5),
                reader.GetInt64(6) != 0,
                reader.GetInt32(7)));
        }
        return result;
    }

    /// <summary>所有签到过的日期集合（日历打点 + 连签计算）。元素为 yyyy-MM-dd 字符串。</summary>
    public static HashSet<string> SignedDates()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        using var conn = Database.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT checked_date FROM checkin_record;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) set.Add(reader.GetString(0));
        return set;
    }

    /// <summary>某账号的已签日期集合（该账号连签天数用）。</summary>
    public static HashSet<string> SignedDates(string accountId)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        using var conn = Database.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT checked_date FROM checkin_record WHERE account_id = $id;";
        cmd.Parameters.AddWithValue("$id", accountId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) set.Add(reader.GetString(0));
        return set;
    }

    /// <summary>某账号今日实际获得的积分。</summary>
    public static double GetTodayCreditsFor(string accountId)
    {
        using var conn = Database.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(credits),0) FROM checkin_record WHERE account_id = $id AND checked_date = $d;";
        cmd.Parameters.AddWithValue("$id", accountId);
        cmd.Parameters.AddWithValue("$d", DateTime.Now.ToString("yyyy-MM-dd"));
        var v = cmd.ExecuteScalar();
        return v == null || v == DBNull.Value ? 0 : Convert.ToDouble(v);
    }

    /// <summary>今日所有账号实际获得的总积分。</summary>
    public static double GetTodayTotalCredits()
    {
        using var conn = Database.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(credits),0) FROM checkin_record WHERE checked_date = $d;";
        cmd.Parameters.AddWithValue("$d", DateTime.Now.ToString("yyyy-MM-dd"));
        var v = cmd.ExecuteScalar();
        return v == null || v == DBNull.Value ? 0 : Convert.ToDouble(v);
    }

    /// <summary>某账号剩余额积分趋势（近 N 条，时间正序）——仪表盘折线图数据源。</summary>
    public static List<(DateTime Date, double Remaining)> GetSnapshotTrend(string accountId, int take = 14)
    {
        var rows = new List<(DateTime, double)>();
        using var conn = Database.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT date, credits FROM credits_snapshot
            WHERE account_id = $id ORDER BY date DESC LIMIT $n;
            """;
        cmd.Parameters.AddWithValue("$id", accountId);
        cmd.Parameters.AddWithValue("$n", take);
        using var reader = cmd.ExecuteReader();
        while (reader.Read() && DateTime.TryParse(reader.GetString(0), out var d))
            rows.Add((d, reader.GetDouble(1)));
        rows.Reverse();
        return rows;
    }

    /// <summary>写入/更新某账号某日的剩余额快照。</summary>
    public static void UpsertSnapshot(string accountId, double remaining)
    {
        try
        {
            using var conn = Database.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO credits_snapshot (date, account_id, credits)
                VALUES ($d, $id, $c)
                ON CONFLICT(date, account_id) DO UPDATE SET credits = $c;
                """;
            cmd.Parameters.AddWithValue("$d", DateTime.Now.ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("$id", accountId);
            cmd.Parameters.AddWithValue("$c", remaining);
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            AppLog.Error("db", "UpsertSnapshot 失败: " + ex.Message);
        }
    }
}
