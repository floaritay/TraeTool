/* api.js — 异步服务 stub 层
   真实实现的形状即未来 API 的形状；此处全部返回 mock，并保留 loading/进度/失败语义。
   TODO: 接入后端时逐个替换 fetch(...)，保持参数与返回值不变。 */
window.Api = (() => {
  const delay = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

  /* GET /api/accounts → { code, data: Account[] } */
  async function fetchAccounts() {
    await delay(280);
    return { code: 0, data: JSON.parse(JSON.stringify(window.DB.accounts)) };
  }

  /* POST /api/accounts/refresh → { code, data: Account[] } */
  async function refreshStatus() {
    await delay(520);
    return { code: 0, data: JSON.parse(JSON.stringify(window.DB.accounts)) };
  }

  /* POST /api/checkin/batch  body:{ accountIds, intervalSeconds, jitter:true }
     ← 服务端逐账号回报进度；9074 冷却会阻塞批次 60~75 秒（原型演示加速） */
  async function checkinBatch(accounts, onEvent) {
    const list = accounts.filter((a) => a.enabled);
    let credits = 0;
    let skipped = 0;
    let failed = 0;

    for (let i = 0; i < list.length; i += 1) {
      const a = list[i];
      onEvent({ type: "start", accountId: a.id, name: a.name, index: i + 1, total: list.length });
      await delay(450);

      if (!a.loggedIn) {
        await delay(380);
        skipped += 1;
        onEvent({ type: "skip", accountId: a.id, name: a.name, text: "未登录，已跳过" });
        continue;
      }
      if (a.status === "checked") {
        await delay(520);
        skipped += 1;
        onEvent({ type: "skip", accountId: a.id, name: a.name, text: "今日已签（服务器判定），跳过" });
        continue;
      }
      if (a.status === "expired") {
        await delay(720);
        failed += 1;
        onEvent({ type: "fail", accountId: a.id, name: a.name, text: "会话失效，签到失败（code=401）" });
        continue;
      }
      if (a.status === "cooldown") {
        /* 9074 限流：真实环境冷却 60~75s；原型压缩为 8s 便于演示 */
        for (let s = 8; s >= 0; s -= 1) {
          onEvent({ type: "cooldown", accountId: a.id, name: a.name, seconds: s });
          await delay(620);
        }
        credits += 31.5;
        onEvent({
          type: "success", accountId: a.id, name: a.name, gain: 31.5,
          text: "9074 冷却结束，补跑成功 +31.5"
        });
        continue;
      }

      const gain = 28.0;
      await delay(760);
      credits += gain;
      onEvent({ type: "success", accountId: a.id, name: a.name, gain, text: `签到成功 +${gain.toFixed(1)}` });
    }

    onEvent({ type: "done", credits, skipped, failed });
    return { code: 0, credits, skipped, failed };
  }

  /* POST /api/client/detect body:{ path } → { code, data:{ detected, running, path } } */
  async function detectClient(path) {
    await delay(620);
    return {
      code: 0,
      data: { detected: true, running: true, path: path || window.DB.clientExePath }
    };
  }

  /* POST /api/client/switch body:{ accountId }
     推进步骤：杀进程 → 快照 → 恢复 → 拉起 → 守护 → 回写；失败自动回滚。 */
  async function switchClient(account, onStep) {
    const steps = [
      { kind: "info", text: `开始切换至「${account.name}」…` },
      { kind: "ok", text: "已安全关闭 Trae.exe 进程（2 个句柄）" },
      { kind: "run", text: "快照当前登录态 → %TEMP%\\traeswitch_rollback_20261104_143012" },
      { kind: "ok", text: "快照完成（34 个文件 · 1.1 MB）" },
      { kind: "run", text: `恢复目标档案：${account.name}（${account.archive.files || 37} 个文件）` },
      { kind: "ok", text: "档案恢复完成，校验通过" },
      { kind: "run", text: "重新拉起 Trae.exe…" },
      { kind: "ok", text: "客户端已启动（PID 18472）" },
      { kind: "run", text: "守护校验客户端登录态…" },
      { kind: "ok", text: "登录态校验通过：客户端当前账号 = " + account.name },
      { kind: "ok", text: "已回写档案时间戳，切换流程结束" }
    ];
    for (const step of steps) {
      await delay(step.kind === "ok" ? 560 : 720);
      onStep(step);
    }
    return { code: 0, data: { success: true } };
  }

  /* POST /api/usage/pull body:{ accountId, rangeDays } → { code, data: UsageRow[] } */
  async function pullUsage({ accountId = "all", rangeDays = 7 } = {}) {
    await delay(900);
    const today = new Date(window.DB.today + "T00:00:00");
    const from = new Date(today);
    from.setDate(from.getDate() - rangeDays);
    const rows = window.DB.usage
      .filter((u) => {
        const d = new Date(u.date + "T00:00:00");
        return d >= from && (accountId === "all" || u.accountId === accountId);
      })
      .map((u) => ({ ...u }));
    return { code: 0, data: rows };
  }

  return { fetchAccounts, refreshStatus, checkinBatch, detectClient, switchClient, pullUsage };
})();
