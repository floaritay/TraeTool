namespace TraeTool.Models;

/// <summary>
/// 单账号签到结果，批量执行后汇总展示/记录。
/// </summary>
public sealed record CheckinResult(
    string AccountId,
    string Name,
    bool Ok,
    double Gained,
    string Reason)
{
    /// <summary>Ok=true 且 Gained>0 时的摘要文案。</summary>
    public string Summary => Ok
        ? (Gained > 0 ? $"获得 {Gained:0} 积分" : Reason)
        : $"失败：{Reason}";
}
