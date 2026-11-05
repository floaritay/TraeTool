namespace TraeTool.Models;

/// <summary>一条按会话聚合的用量记录（query_user_usage_group_by_session）。</summary>
public sealed class UsageSessionRecord
{
    public string SessionId { get; set; } = "";
    /// <summary>用量时间（Unix 秒）。</summary>
    public long UsageTime { get; set; }
    public string ModelName { get; set; } = "";
    public string Mode { get; set; } = "";
    public bool UseMaxMode { get; set; }
    /// <summary>本次会话消耗积分（credits_float，兼容 amount_float）。</summary>
    public double CreditsFloat { get; set; }
    /// <summary>折合金额（元）。</summary>
    public double CostMoneyFloat { get; set; }
    public string UserInputPreview { get; set; } = "";
    public long InputToken { get; set; }
    public long OutputToken { get; set; }
    public long CacheReadToken { get; set; }
    public long CacheWriteToken { get; set; }
    public DateTime FetchedAt { get; set; }
}

/// <summary>一页用量查询结果。</summary>
public sealed class UsagePageResult
{
    public int Total { get; set; }
    public List<UsageSessionRecord> Items { get; } = new();
}

/// <summary>资格包过期记录（积分快过期提醒）。</summary>
public sealed class ExpiredEnt
{
    public string Name { get; set; } = "";
    public double CreditsLimit { get; set; }
    public long ExpireTimeMs { get; set; }
}
