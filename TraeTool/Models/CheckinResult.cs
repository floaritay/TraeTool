namespace TraeTool.Models;

/// <summary>
/// 单账号签到结果，批量执行后汇总展示/记录。
/// </summary>
public sealed record CheckinResult(
    string AccountId,
    string Name,
    bool Ok,
    double Gained,
    string Reason);
