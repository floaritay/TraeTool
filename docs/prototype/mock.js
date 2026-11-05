/* mock.js — 单一数据源（原型演示数据）
   所有页面均从此处读取；与真实 ViewModel 属性一一对应。
   TODO: 接入后端后由 /api/accounts、/api/records、/api/usage 返回同构数据。 */
window.DB = {
  version: "1.4.2",
  today: "2026-11-04",
  usedBytes: "1.2 MB",

  /* ── 账号 ─────────────────────────────────────────
     status: checked | unchecked | cooldown | expired | failed
     archive.state: ok | none | warn
     clientCurrent: 桌面客户端登录态所处账号（绿色「客户端当前」徽标） */
  accounts: [
    {
      id: "a1", name: "陈临", handle: "chen.lin@trae.ai", initial: "陈", hue: 222,
      enabled: true, loggedIn: true, status: "checked",
      credits: 2847.5, todayReward: 33.5, streak: 12,
      member: true, memberNote: "Trae Pro · 2026-12-31 到期",
      clientCurrent: true, lastCheckin: "08:31",
      deviceId: "TR7F-2C91-88A4-3D10",
      archive: { state: "ok", at: "2026-11-03 22:14", files: 38, size: "1.2 MB", similarity: 0.97, note: "档案与客户端登录态一致" }
    },
    {
      id: "a2", name: "周米卡", handle: "mika.zhou@trae.ai", initial: "周", hue: 168,
      enabled: true, loggedIn: true, status: "checked",
      credits: 1203.0, todayReward: 26.5, streak: 5,
      member: false, memberNote: "非会员",
      clientCurrent: false, lastCheckin: "08:31",
      deviceId: "TR9C-71B4-5E02-9A86",
      archive: { state: "ok", at: "2026-11-02 19:40", files: 37, size: "1.1 MB", similarity: 0.94, note: "档案快照可用" }
    },
    {
      id: "a3", name: "王砚", handle: "yan.wang@trae.ai", initial: "王", hue: 32,
      enabled: true, loggedIn: true, status: "cooldown",
      credits: 968.2, todayReward: 0, streak: 3,
      member: false, memberNote: "非会员",
      clientCurrent: false, lastCheckin: "11-03 08:34",
      deviceId: "TR5D-90F2-47C1-6B33",
      archive: { state: "none", at: "", files: 0, size: "", similarity: 0, note: "尚未建立客户端档案" }
    },
    {
      id: "a4", name: "曲小满", handle: "xiaoman.qu@trae.ai", initial: "曲", hue: 282,
      enabled: false, loggedIn: false, status: "unchecked",
      credits: 312.4, todayReward: 0, streak: 0,
      member: false, memberNote: "非会员",
      clientCurrent: false, lastCheckin: "—",
      deviceId: "TR2A-66E7-1B90-8F42",
      archive: { state: "none", at: "", files: 0, size: "", similarity: 0, note: "未登录，无法建档" }
    },
    {
      id: "a5", name: "团队号", handle: "team.trae@trae.ai", initial: "团", hue: 4,
      enabled: true, loggedIn: true, status: "expired",
      credits: 5420.0, todayReward: 0, streak: 21,
      member: true, memberNote: "Trae Pro · 2026-11-30 到期",
      clientCurrent: false, lastCheckin: "11-03 08:33",
      deviceId: "TR8E-3A55-0D27-C176",
      archive: { state: "warn", at: "2026-10-28 17:05", files: 35, size: "1.0 MB", similarity: 0.61, note: "档案与当前客户端登录账号不一致" }
    }
  ],

  /* ── 签到记录（最多 100 条，失败/跳过也入库） ── */
  records: [
    { at: "11-04 08:33", date: "2026-11-04", accountId: "a4", name: "曲小满", credits: 0, kind: "skip", reason: "未登录，已跳过" },
    { at: "11-04 08:32", date: "2026-11-04", accountId: "a5", name: "团队号", credits: 0, kind: "fail", reason: "会话失效，请重新登录" },
    { at: "11-04 08:32", date: "2026-11-04", accountId: "a3", name: "王砚", credits: 0, kind: "skip", reason: "9074 限流冷却中，等待补跑" },
    { at: "11-04 08:31", date: "2026-11-04", accountId: "a2", name: "周米卡", credits: 26.5, kind: "success", reason: "签到成功" },
    { at: "11-04 08:31", date: "2026-11-04", accountId: "a1", name: "陈临", credits: 33.5, kind: "success", reason: "签到成功" },
    { at: "11-03 08:34", date: "2026-11-03", accountId: "a3", name: "王砚", credits: 29.0, kind: "success", reason: "签到成功" },
    { at: "11-03 08:33", date: "2026-11-03", accountId: "a5", name: "团队号", credits: 0, kind: "fail", reason: "签到失败（code=500）" },
    { at: "11-03 08:32", date: "2026-11-03", accountId: "a2", name: "周米卡", credits: 26.5, kind: "success", reason: "签到成功" },
    { at: "11-03 08:31", date: "2026-11-03", accountId: "a1", name: "陈临", credits: 31.0, kind: "success", reason: "签到成功" },
    { at: "11-02 08:35", date: "2026-11-02", accountId: "a5", name: "团队号", credits: 32.0, kind: "success", reason: "签到成功" },
    { at: "11-02 08:34", date: "2026-11-02", accountId: "a4", name: "曲小满", credits: 0, kind: "skip", reason: "今日已签（服务器判定），跳过" },
    { at: "11-02 08:33", date: "2026-11-02", accountId: "a3", name: "王砚", credits: 0, kind: "skip", reason: "9074 限流，等待后未补跑" },
    { at: "11-02 08:32", date: "2026-11-02", accountId: "a2", name: "周米卡", credits: 28.0, kind: "success", reason: "签到成功" },
    { at: "11-02 08:31", date: "2026-11-02", accountId: "a1", name: "陈临", credits: 28.5, kind: "success", reason: "签到成功" },
    { at: "11-01 09:12", date: "2026-11-01", accountId: "a5", name: "团队号", credits: 0, kind: "fail", reason: "签到失败（code=429）" },
    { at: "11-01 09:11", date: "2026-11-01", accountId: "a2", name: "周米卡", credits: 26.5, kind: "success", reason: "签到成功" }
  ],

  /* ── 用量统计（模型消耗明细） ── */
  usage: [
    { date: "2026-11-04", at: "14:22", accountId: "a1", model: "Claude 3.7 Sonnet", mode: "思考", credits: 3.20, tokens: 12480, preview: "帮我重构 useAccountStore 的登录态回写逻辑…" },
    { date: "2026-11-04", at: "13:05", accountId: "a2", model: "Trae-Coder", mode: "智能体", credits: 8.40, tokens: 28740, preview: "把这段 XAML 资源字典提取成可复用的 Style…" },
    { date: "2026-11-04", at: "11:47", accountId: "a1", model: "DeepSeek-V4", mode: "快速", credits: 0.80, tokens: 3120, preview: "解释一下这段 DPAPI 加密保存的调用顺序" },
    { date: "2026-11-04", at: "10:19", accountId: "a5", model: "GPT-4.1", mode: "思考", credits: 5.60, tokens: 19620, preview: "为签到失败场景补一个回滚快照的流程图…" },
    { date: "2026-11-04", at: "09:38", accountId: "a1", model: "Trae-Coder", mode: "智能体", credits: 12.30, tokens: 41260, preview: "实现托盘气泡的 Win32 通知区域封装，注意…" },
    { date: "2026-11-03", at: "22:04", accountId: "a3", model: "Claude 3.7 Sonnet", mode: "思考", credits: 3.80, tokens: 14180, preview: "审阅一下这段并发签到队列是否存在竞态…" },
    { date: "2026-11-03", at: "18:42", accountId: "a2", model: "DeepSeek-V4", mode: "快速", credits: 1.10, tokens: 4380, preview: "把这个表格的空态文案改得更克制一些" },
    { date: "2026-11-03", at: "16:15", accountId: "a1", model: "Trae-Coder", mode: "智能体", credits: 9.70, tokens: 33450, preview: "给 WebView2 登录窗加一个轮询超时的兜底…" },
    { date: "2026-11-03", at: "11:26", accountId: "a5", model: "GPT-4.1", mode: "思考", credits: 5.10, tokens: 17360, preview: "对比两种 150% DPI 下的布局方案，给出建议" },
    { date: "2026-11-02", at: "20:58", accountId: "a1", model: "Claude 3.7 Sonnet", mode: "思考", credits: 4.60, tokens: 16040, preview: "把签到日历的月切换做成可测试的纯函数…" },
    { date: "2026-11-02", at: "17:33", accountId: "a2", model: "Trae-Coder", mode: "智能体", credits: 7.20, tokens: 24980, preview: "补全这个 MVVM 的属性变更通知链" },
    { date: "2026-11-02", at: "10:07", accountId: "a3", model: "DeepSeek-V4", mode: "快速", credits: 0.90, tokens: 3620, preview: "解释 9074 限流码的重试退避策略" },
    { date: "2026-11-01", at: "21:19", accountId: "a1", model: "Trae-Coder", mode: "智能体", credits: 11.40, tokens: 38600, preview: "整理客户端切换的步骤日志，注意失败回滚…" },
    { date: "2026-11-01", at: "15:44", accountId: "a2", model: "GPT-4.1", mode: "思考", credits: 4.20, tokens: 14800, preview: "给会员状态条写一句更自然的中文说明" },
    { date: "2026-10-31", at: "19:02", accountId: "a1", model: "Claude 3.7 Sonnet", mode: "思考", credits: 3.10, tokens: 11240, preview: "评审这个账号卡片的信息层级是否过密" },
    { date: "2026-10-30", at: "14:26", accountId: "a5", model: "Trae-Coder", mode: "智能体", credits: 6.80, tokens: 23320, preview: "把签到记录的失败态做成统一的语义色…" },
    { date: "2026-10-29", at: "09:51", accountId: "a2", model: "DeepSeek-V4", mode: "快速", credits: 1.40, tokens: 5210, preview: "这个正则能不能同时匹配手机号和验证码…" },
    { date: "2026-10-28", at: "22:37", accountId: "a1", model: "GPT-4.1", mode: "思考", credits: 6.30, tokens: 21060, preview: "梳理建档向导五步里每步的失败出口" }
  ],

  /* ── 14 天积分趋势 ── */
  trend: {
    labels: ["10-22", "10-23", "10-24", "10-25", "10-26", "10-27", "10-28", "10-29", "10-30", "10-31", "11-01", "11-02", "11-03", "11-04"],
    values: [2210, 2180, 2260, 2310, 2295, 2380, 2410, 2365, 2470, 2520, 2610, 2665, 2750, 2847.5]
  },

  /* ── 签到日历：月份键 → 已签日期；dayRewards 用于格子提示 ── */
  checkedDays: {
    "2026-11": [1, 2, 3, 4],
    "2026-10": [3, 4, 5, 9, 10, 11, 12, 16, 17, 18, 19, 23, 24, 25, 26, 30, 31]
  },
  dayRewards: { 1: 28.5, 2: 31.0, 3: 29.0, 4: 33.5 },

  clientExePath: "C:\\Users\\lin\\AppData\\Local\\Programs\\Trae\\Trae.exe"
};
