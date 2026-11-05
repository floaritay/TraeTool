/* app.js — 状态 / 渲染 / 交互（纯静态原型，无真实后端） */
(() => {
  "use strict";

  const $ = (sel, root = document) => root.querySelector(sel);
  const $$ = (sel, root = document) => Array.from(root.querySelectorAll(sel));
  const clone = (v) => JSON.parse(JSON.stringify(v));
  const esc = (s) => String(s).replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

  const TODAY = new Date(2026, 10, 4);
  const WEEK = ["日", "一", "二", "三", "四", "五", "六"];

  const state = {
    page: "dashboard",
    accounts: [],
    records: [],
    usage: [],
    trend: null,
    checkedDays: {},
    dayRewards: {},
    currentId: "a1",
    clientCurrentId: "a1",
    filterId: "all",
    usageAccount: "all",
    rangeDays: 7,
    month: new Date(2026, 10, 1),
    messages: {},
    busy: { batch: false, switch: false },
    progress: { lines: [], now: "", countdown: null },
    logs: [],
    usageLoading: false,
    demo: "normal",
    annotate: false,
    scroll: {},
    auto: { enabled: true, time: "08:30", interval: 12 },
    settings: { autostart: false, tray: true, device: false },
    client: { detected: true, running: true, path: window.DB.clientExePath },
    login: { open: false, mode: "add", accountId: null, phase: "init" },
    wizard: { open: false, step: 1, accountId: null, phase: "idle", similarity: 0, confirmed: false, progress: 0 },
    dialog: null,
    runToken: 0,
    tempid: 0
  };

  /* ───────── 基础工具 ───────── */
  const pad = (n) => String(n).padStart(2, "0");
  const nowTime = () => {
    const d = new Date();
    return `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;
  };
  const nowClock = () => {
    const d = new Date();
    return `${pad(d.getHours())}:${pad(d.getMinutes())}`;
  };
  const fmtCredits = (n) => {
    if (n == null) return "—";
    return Number(n).toLocaleString("zh-CN", { minimumFractionDigits: 1, maximumFractionDigits: 1 });
  };
  const fmtInt = (n) => Number(n).toLocaleString("zh-CN");
  const fmtTokens = (n) => (n >= 10000 ? `${(n / 10000).toFixed(2)}M` : fmtInt(n));
  const dateKey = (d) => `${d.getFullYear()}-${pad(d.getMonth() + 1)}`;

  const getAccount = (id) => state.accounts.find((a) => a.id === id) || null;
  const currentAccount = () => getAccount(state.currentId) || state.accounts[0] || null;
  const enabledAccounts = () => state.accounts.filter((a) => a.enabled);
  const clientAccount = () => getAccount(state.clientCurrentId) || null;
  const monthKey = () => dateKey(state.month);

  function statusOf(a) {
    if (!a) return { label: "—", cls: "s-mute", text: "暂无账号" };
    if (!a.loggedIn) return { label: "未登录", cls: "s-warn", text: "凭据缺失，请到账号管理重新登录" };
    if (a.status === "checked") return { label: "今日已签", cls: "s-ok", text: `今日已签 · 今日 +${fmtCredits(a.todayReward)}` };
    if (a.status === "cooldown") return { label: "9074 冷却中", cls: "s-info", text: "限流冷却中，随后补跑（不是失败）" };
    if (a.status === "expired") return { label: "会话失效，请重新登录", cls: "s-warn", text: "登录态已过期，需重新登录" };
    if (a.status === "failed") return { label: "签到失败（code=500）", cls: "s-danger", text: "签到失败，记录已入库" };
    return { label: "今日未签", cls: "s-mute", text: "等待批量签到" };
  }

  function todayStatusShort(a) {
    if (!a) return "—";
    if (!a.loggedIn) return "未登录";
    if (a.status === "checked") return "已签";
    if (a.status === "cooldown") return "冷却中";
    if (a.status === "expired") return "会话失效";
    if (a.status === "failed") return "签到失败";
    return "未签";
  }

  function initState() {
    state.accounts = clone(window.DB.accounts);
    state.records = clone(window.DB.records);
    state.usage = clone(window.DB.usage);
    state.trend = clone(window.DB.trend);
    state.checkedDays = clone(window.DB.checkedDays);
    state.dayRewards = clone(window.DB.dayRewards);
    state.currentId = "a1";
    state.clientCurrentId = "a1";
  }

  /* ───────── Toast ───────── */
  function toast(kind, title, text) {
    const wrap = $("#toastWrap");
    const el = document.createElement("div");
    const icons = { ok: "ic-check", warn: "ic-alert", err: "ic-alert", info: "ic-info" };
    el.className = `toast ${kind || "info"}`;
    el.innerHTML = `
      <svg class="ico"><use href="#${icons[kind] || "ic-info"}"/></svg>
      <div><div class="toast-title">${esc(title)}</div>${text ? `<div class="toast-text">${esc(text)}</div>` : ""}</div>`;
    wrap.appendChild(el);
    setTimeout(() => {
      el.classList.add("hide");
      setTimeout(() => el.remove(), 260);
    }, 3600);
    while (wrap.children.length > 3) wrap.firstElementChild.remove();
  }

  /* ───────── 横幅 ───────── */
  function renderBanner(el, msg) {
    if (!el) return;
    if (!msg) { el.hidden = true; el.innerHTML = ""; return; }
    el.hidden = false;
    el.className = `banner banner-${msg.type || "danger"}`;
    const icon = msg.type === "ok" ? "ic-check" : msg.type === "info" ? "ic-info" : "ic-alert";
    el.innerHTML = `
      <svg class="ico"><use href="#${icon}"/></svg>
      <p>${esc(msg.text)}</p>
      ${msg.action ? `<button type="button" class="banner-action" data-act="${esc(msg.act)}">${esc(msg.action)}</button>` : ""}`;
  }

  /* ───────── 导航 / 忙碌指示 ───────── */
  function navSubtitle() {
    const cur = currentAccount();
    const enabled = enabledAccounts().length;
    switch (state.page) {
      case "dashboard": return cur ? `${cur.name} · ${todayStatusShort(cur)}` : "暂无账号，先去添加";
      case "checkin": return state.busy.batch ? "批量签到执行中…" : `${enabled} 个账号参与签到`;
      case "accounts": return `${state.accounts.length} 个账号 · ${enabled} 个参与签到`;
      case "switch": return clientAccount() ? `客户端当前 · ${clientAccount().name}` : "尚未建立客户端档案";
      case "usage": return `近 ${state.rangeDays} 天 · ${state.usage.length} 次会话`;
      case "settings": return state.settings.autostart ? "开机自启已开启" : "手动启动";
      default: return "";
    }
  }

  function renderNav() {
    document.body.dataset.page = state.page;
    $$(".nav-item").forEach((el) => el.classList.toggle("active", el.dataset.nav === state.page));
    $("#navSubtitle").textContent = navSubtitle();

    const anyBusy = state.busy.batch || state.busy.switch;
    $("#navRun").hidden = !anyBusy;
    $("#tbBusy").hidden = !anyBusy;
    if (anyBusy) {
      $("#tbBusyText").textContent = state.busy.batch ? "批量签到执行中" : "客户端切换执行中";
    }
    $$(".nav-item").forEach((el) => {
      const dot = el.querySelector(".nav-run-dot");
      if (!dot) return;
      dot.hidden = !(state.busy.batch && el.dataset.nav === "checkin") && !(state.busy.switch && el.dataset.nav === "switch");
    });
  }

  /* ───────── 数值 / 表单 ───────── */
  function setVal(sel, value) {
    const el = $(sel);
    if (!el) return;
    if (document.activeElement === el) return;
    el.value = value;
  }

  /* ═════════ P1 仪表盘 ═════════ */
  function renderDashboard() {
    const cur = currentAccount();
    const enabled = enabledAccounts();

    $("#dashDate").textContent = `${TODAY.getFullYear()} 年 ${TODAY.getMonth() + 1} 月 ${TODAY.getDate()} 日 星期${WEEK[TODAY.getDay()]}`;
    $("#dashMeta").textContent = enabled.length ? `今日 ${enabled.length} 个账号参与签到` : "尚未添加账号";

    $("#statCredits").textContent = cur ? fmtCredits(cur.credits) : "—";
    $("#statAccount").textContent = cur ? `所属账号 · ${cur.name}` : "暂无账号";
    $("#statToday").textContent = todayStatusShort(cur);
    $("#statTodaySub").textContent = cur ? statusOf(cur).text : "先去账号管理添加账号";
    const todayTotal = state.accounts.reduce((sum, a) => sum + (a.todayReward || 0), 0);
    $("#statReward").textContent = state.accounts.length ? `+${fmtCredits(todayTotal)}` : "—";
    $("#statRewardSub").textContent = `${enabled.length} 个启用账号合计`;
    $("#statStreak").textContent = cur ? `${cur.streak} 天` : "—";
    $("#statStreakSub").textContent = cur ? `当前账号 · ${cur.name}` : "—";

    const memberRow = $("#memberRow");
    if (cur) {
      memberRow.hidden = false;
      memberRow.querySelector(".status-dot").className = `status-dot ${cur.member ? "s-ok" : "s-mute"}`;
      $("#memberText").textContent = cur.member ? (cur.memberNote || "会员") : "非会员（仅信息展示，无操作）";
    } else {
      memberRow.hidden = true;
    }

    const btn = $("#btnQuickCheckin");
    const locked = state.busy.switch;
    btn.disabled = state.busy.batch || locked || !state.accounts.length;
    btn.title = state.busy.batch ? "批量签到执行中，请等待完成" : locked ? "客户端切换执行中，签到暂时禁用" : !state.accounts.length ? "请先添加账号" : "为全部启用账号签到";
    btn.querySelector(".btn-stack b").textContent = state.busy.batch ? "签到执行中…" : "立即签到";

    const refBtn = $("#btnDashRefresh");
    refBtn.disabled = state.busy.batch || locked;
    refBtn.title = state.busy.batch ? "批量签到执行中，稍后再刷新" : locked ? "客户端切换执行中，稍后再刷新" : "同步积分、签到状态与会员信息";

    renderBanner($("#dashBanner"), state.messages.dashboard);
    renderTrend();
    renderCalendar();
    renderQuickAccounts();
  }

  function renderTrend() {
    const svg = $("#trendSvg");
    const empty = $("#trendEmpty");
    const data = state.trend;
    const has = state.accounts.length > 0 && data && data.values && data.values.length > 1;
    $("#chartMeta").textContent = has ? `今日 ${fmtCredits(data.values[data.values.length - 1])} · 近 14 天` : "暂无记录";
    empty.hidden = has;
    svg.style.display = has ? "block" : "none";
    if (!has) { svg.innerHTML = ""; return; }

    const W = 600, H = 120, padL = 36, padR = 12, padT = 10, padB = 20;
    const values = data.values;
    const labels = data.labels;
    const max = Math.max(...values);
    const min = Math.min(...values);
    const mid = (max + min) / 2;
    const n = values.length;
    const xAt = (i) => padL + (i * (W - padL - padR)) / (n - 1);
    const yAt = (v) => padT + ((max - v) / (max - min || 1)) * (H - padT - padB);

    let grid = "";
    [max, mid, min].forEach((v, i) => {
      const y = yAt(v);
      grid += `<line x1="${padL}" x2="${W - padR}" y1="${y}" y2="${y}" stroke="#EDEFEA" stroke-width="1" ${i === 2 ? "" : 'stroke-dasharray="4 5"'} />`;
      grid += `<text x="${padL - 6}" y="${y + 3}" text-anchor="end" font-size="9" fill="#9AA4B2" font-family="Bahnschrift, sans-serif">${Math.round(v)}</text>`;
    });

    const points = values.map((v, i) => `${xAt(i)},${yAt(v)}`).join(" ");
    const area = `M ${padL},${yAt(values[0])} ` + values.map((v, i) => `L ${xAt(i)},${yAt(v)}`).join(" ") + ` L ${xAt(n - 1)},${H - padB} L ${padL},${H - padB} Z`;

    const idx = [0, 4, 9, n - 1];
    let xLabels = "";
    idx.forEach((i) => {
      const anchor = i === 0 ? "start" : i === n - 1 ? "end" : "middle";
      xLabels += `<text x="${xAt(i)}" y="${H - 5}" text-anchor="${anchor}" font-size="9" fill="#A5AEBA" font-family="Bahnschrift, sans-serif">${labels[i]}</text>`;
    });

    const tx = xAt(n - 1), ty = yAt(values[n - 1]);
    svg.innerHTML = `
      <defs>
        <linearGradient id="trendFill" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" stop-color="#2C5BF6" stop-opacity="0.18"/>
          <stop offset="100%" stop-color="#2C5BF6" stop-opacity="0"/>
        </linearGradient>
      </defs>
      ${grid}
      <path d="${area}" fill="url(#trendFill)" />
      <polyline points="${points}" fill="none" stroke="#2C5BF6" stroke-width="1.8" stroke-linejoin="round" stroke-linecap="round" />
      <circle cx="${tx}" cy="${ty}" r="7" fill="#2C5BF6" opacity="0.14" />
      <circle cx="${tx}" cy="${ty}" r="3.5" fill="#2C5BF6" stroke="#fff" stroke-width="1.2" />
      <text x="${tx - 8}" y="${ty - 10}" text-anchor="end" font-size="9.5" fill="#2C5BF6" font-family="Bahnschrift, sans-serif">${fmtCredits(values[n - 1])}</text>
      ${xLabels}`;
  }

  function renderCalendar() {
    const y = state.month.getFullYear();
    const m = state.month.getMonth();
    $("#calMonth").textContent = `${y} 年 ${m + 1} 月`;
    $("#calWeek").innerHTML = WEEK.map((w) => `<span>${w}</span>`).join("");

    const first = new Date(y, m, 1).getDay();
    const days = new Date(y, m + 1, 0).getDate();
    const key = monthKey();
    const checked = new Set(state.checkedDays[key] || []);
    const rewards = state.dayRewards || {};
    let cells = "";
    for (let i = 0; i < first; i += 1) cells += `<div class="cal-cell blank"></div>`;
    let checkedCount = 0;
    for (let d = 1; d <= days; d += 1) {
      const isChecked = checked.has(d);
      const isToday = y === TODAY.getFullYear() && m === TODAY.getMonth() && d === TODAY.getDate();
      if (isChecked) checkedCount += 1;
      const tip = isChecked ? `${m + 1} 月 ${d} 日 · 已签${rewards[d] ? ` · +${fmtCredits(rewards[d])} 积分` : ""}` : `${m + 1} 月 ${d} 日 · ${isToday ? "今天" : "未签到"}`;
      cells += `<div class="cal-cell${isChecked ? " checked" : ""}${isToday ? " today" : ""}" title="${esc(tip)}">${d}</div>`;
    }
    $("#calGrid").innerHTML = cells;
    $("#calHint").textContent = state.accounts.length ? `本月已签 ${checkedCount} 天` : "";
  }

  function renderQuickAccounts() {
    const list = $("#qaList");
    if (!state.accounts.length) {
      list.innerHTML = `<div class="qa-empty">暂无账号<br>添加后可在任意页面快速切换查看</div>`;
      return;
    }
    list.innerHTML = state.accounts.map((a) => {
      const st = statusOf(a);
      return `
      <div class="qa-row" data-act="qa-open" data-id="${a.id}" role="button" tabindex="0" title="跳转签到页并切换当前查看账号">
        <span class="avatar" style="background:hsl(${a.hue} 46% 44%)">${esc(a.initial)}</span>
        <span class="qa-info">
          <span class="qa-name"><b>${esc(a.name)}</b>${state.currentId === a.id ? '<span class="badge badge-current">当前</span>' : ""}</span>
          <span class="qa-sub">${st.label} · 剩 ${fmtCredits(a.credits)}${a.enabled ? "" : " · 未参与"}</span>
        </span>
        <svg class="ico qa-chev"><use href="#ic-arrow-right"/></svg>
      </div>`;
    }).join("");
  }

  function setMessage(page, msg) {
    if (msg) state.messages[page] = msg;
    else delete state.messages[page];
  }

  /* ═════════ P2 每日签到 ═════════ */
  function renderCheckin() {
    const cur = currentAccount();
    const enabled = enabledAccounts();
    const sel = $("#acctSelect");
    sel.innerHTML = state.accounts.length
      ? state.accounts.map((a) => `<option value="${a.id}">${esc(a.name)}${a.enabled ? "" : "（未参与）"}</option>`).join("")
      : `<option value="">暂无账号</option>`;
    sel.disabled = !state.accounts.length;
    if (cur) sel.value = cur.id;

    $("#checkinCount").textContent = state.accounts.length
      ? `${enabled.length} 个账号参与 · 间隔 ${state.auto.interval}s`
      : "暂无账号";

    $("#activeName").textContent = cur ? cur.name : "—";
    $("#activeCredits").textContent = cur ? fmtCredits(cur.credits) : "—";
    $("#activeReward").textContent = cur ? fmtCredits(cur.todayReward) : "0";
    $("#activeStreak").textContent = cur ? cur.streak : "0";
    if (cur) {
      const st = statusOf(cur);
      $("#activeStatus").textContent = st.label;
      $("#activeDot").className = `status-dot ${st.cls}`;
    } else {
      $("#activeStatus").textContent = "暂无账号";
      $("#activeDot").className = "status-dot s-mute";
    }

    const btn = $("#btnCheckin");
    btn.disabled = state.busy.batch || state.busy.switch || !enabled.length;
    btn.title = state.busy.batch ? "批量签到执行中，请等待完成" : state.busy.switch ? "客户端切换执行中，签到暂时禁用" : !enabled.length ? "请先勾选参与签到的账号" : "开始批量签到";
    btn.querySelector(".btn-stack b").textContent = state.busy.batch ? "签到执行中…" : "一键签到";

    const chip = $("#runChip");
    const last = state.lastRun;
    if (state.busy.batch) {
      chip.textContent = "执行中";
      chip.className = "chip is-running";
    } else if (last) {
      chip.textContent = last.failed > 0 ? "部分失败" : "已完成";
      chip.className = `chip ${last.failed > 0 ? "is-fail" : "is-done"}`;
    } else {
      chip.textContent = "待命";
      chip.className = "chip";
    }

    const runNow = $("#runNow");
    if (state.busy.batch) {
      runNow.innerHTML = `<span class="spinner"></span><span>${esc(state.progress.now || "准备中…")}</span>`;
    } else if (last) {
      runNow.innerHTML = `<span class="status-dot s-ok"></span><span>完成 · +${fmtCredits(last.credits)} 积分 · 跳过 ${last.skipped} · 失败 ${last.failed}</span>`;
    } else {
      runNow.innerHTML = `<span class="run-idle">尚未执行。点击「一键签到」开始，或等待每日自动签到。</span>`;
    }

    renderCheckinLog();
    renderCheckinHint();
    renderRecords();
    renderBanner($("#checkinBanner"), state.messages.checkin);

    setVal("#autoTime", state.auto.time);
    setVal("#autoInterval", state.auto.interval);
    const autoBox = $("#autoEnabled");
    autoBox.checked = state.auto.enabled;
    autoBox.disabled = state.busy.batch;
    $("#autoTime").disabled = state.busy.batch;
    $("#autoInterval").disabled = state.busy.batch;
  }

  function renderCheckinLog() {
    const box = $("#checkinLog");
    const lines = state.progress.lines;
    if (!lines.length) {
      box.innerHTML = `<div class="log-empty">执行明细会逐行追加在这里</div>`;
      return;
    }
    box.innerHTML = lines.map((l) => `
      <div class="log-line ${l.kind}">
        <span class="lt">${l.time}</span>
        <span class="lm">${esc(l.text)}</span>
      </div>`).join("");
    box.scrollTop = box.scrollHeight;
  }

  function renderCheckinHint() {
    const hint = $("#runHint");
    if (state.progress.countdown != null) {
      hint.className = "run-hint is-warn";
      hint.innerHTML = `<svg class="ico"><use href="#ic-clock"/></svg><span>9074 冷却补跑中 · 剩余 ${state.progress.countdown}s（原型演示已加速），请勿重复点击。</span>`;
    } else if (state.busy.batch) {
      hint.className = "run-hint";
      hint.innerHTML = `<svg class="ico"><use href="#ic-info"/></svg><span>批量签到执行中；跳过 / 已签不算失败，请勿重复点击。</span>`;
    } else {
      hint.className = "run-hint";
      hint.innerHTML = `<svg class="ico"><use href="#ic-info"/></svg><span>跳过 / 已签不算失败；9074 冷却期间请勿重复点击。</span>`;
    }
  }

  function renderRecords() {
    const filter = $("#recordFilter");
    const curFilter = state.filterId || "all";
    filter.innerHTML = `<option value="all">全部账号</option>` + state.accounts.map((a) => `<option value="${a.id}">${esc(a.name)}</option>`).join("");
    if (filter.value !== curFilter) filter.value = curFilter;
    filter.disabled = !state.accounts.length;

    const rows = state.records.filter((r) => {
      if (curFilter !== "all" && r.accountId !== curFilter) return false;
      if (curFilter === "all" && state.accounts.length === 0) return false;
      return true;
    });
    $("#recordCount").textContent = `${rows.length} 条 · 最多保留 100 条`;
    const body = $("#recordBody");
    const empty = $("#recordEmpty");
    empty.hidden = rows.length > 0;
    empty.textContent = state.accounts.length ? "当前筛选下暂无记录" : "暂无账号，添加账号后开始签到";
    body.innerHTML = rows.map((r) => {
      const dot = r.kind === "success" ? "s-ok" : r.kind === "fail" ? "s-danger" : r.kind === "warn" ? "s-warn" : "s-mute";
      const cls = r.kind === "fail" ? "rec-fail" : r.kind === "warn" ? "rec-warn" : "";
      const credit = r.credits > 0
        ? `<span class="credit-pos">+${fmtCredits(r.credits)}</span>`
        : `<span class="credit-zero">0</span>`;
      return `<tr class="${cls}">
        <td class="mono">${esc(r.at)}</td>
        <td><span class="rec-account">${esc(r.name)}</span></td>
        <td class="ta-r">${credit}</td>
        <td><span class="rec-reason"><i class="status-dot ${dot}"></i>${esc(r.reason)}</span></td>
      </tr>`;
    }).join("");
  }

  function pushLog(kind, text) {
    state.progress.lines.push({ time: nowTime(), kind, text });
    if (state.progress.lines.length > 120) state.progress.lines.shift();
  }

  function addRecord(accountId, name, credits, kind, reason) {
    state.records.unshift({
      at: `${pad(TODAY.getMonth() + 1)}-${pad(TODAY.getDate())} ${nowClock()}`,
      date: "2026-11-04",
      accountId, name, credits, kind, reason
    });
    if (state.records.length > 100) state.records.length = 100;
  }

  async function startBatch() {
    if (state.busy.batch || state.busy.switch) return;
    if (!state.accounts.length) {
      toast("warn", "暂无账号", "请先在账号管理中添加账号。");
      return;
    }
    if (!enabledAccounts().length) {
      toast("warn", "没有参与签到的账号", "请在账号管理中勾选至少一个「参与签到」。");
      return;
    }

    const token = ++state.runToken;
    state.busy.batch = true;
    state.lastRun = null;
    state.progress = { lines: [], now: "准备执行批量签到…", countdown: null };
    setMessage("checkin", { type: "info", text: `批量签到执行中（${enabledAccounts().length} 个账号），完成后会逐条写入记录。` });
    renderAll();

    const snapshot = clone(state.accounts);
    let results = [];
    let done = false;

    const onEvent = (ev) => {
      if (token !== state.runToken) return;
      if (ev.type === "start") {
        state.progress.now = `正在签到 ${ev.name}（${ev.index}/${ev.total}）…`;
        pushLog("info", `开始签到 ${ev.name}（${ev.index}/${ev.total}）`);
      } else if (ev.type === "cooldown") {
        state.progress.now = `9074 冷却补跑 · ${ev.name} · 剩余 ${ev.seconds}s`;
        state.progress.countdown = ev.seconds;
        if (ev.seconds === 8) pushLog("warn", `${ev.name} 触发 9074 限流，进入冷却补跑`);
      } else if (ev.type === "skip") {
        state.progress.countdown = null;
        pushLog("skip", `${ev.name}：${ev.text}`);
        addRecord(ev.accountId, ev.name, 0, "skip", ev.text);
        results.push({ ...ev });
      } else if (ev.type === "success") {
        state.progress.countdown = null;
        const acc = getAccount(ev.accountId);
        if (acc) {
          acc.status = "checked";
          acc.credits += ev.gain;
          acc.todayReward += ev.gain;
          acc.streak += 1;
          acc.lastCheckin = nowClock();
        }
        pushLog("ok", `${ev.name}：${ev.text}`);
        addRecord(ev.accountId, ev.name, ev.gain, "success", "签到成功");
        results.push({ ...ev });
      } else if (ev.type === "fail") {
        state.progress.countdown = null;
        pushLog("fail", `${ev.name}：${ev.text}`);
        addRecord(ev.accountId, ev.name, 0, "fail", ev.text);
        results.push({ ...ev });
      } else if (ev.type === "done") {
        done = true;
        state.lastRun = { credits: ev.credits, skipped: ev.skipped, failed: ev.failed };
        state.progress.now = "全部处理完成";
        pushLog("ok", `批次完成：+${fmtCredits(ev.credits)} 积分，跳过 ${ev.skipped}，失败 ${ev.failed}`);
        if (ev.credits > 0) {
          const key = dateKey(TODAY);
          const list = state.checkedDays[key] || [];
          if (!list.includes(TODAY.getDate())) list.push(TODAY.getDate());
          state.checkedDays[key] = list;
        }
      }
      renderNav();
      renderDashboard();
      renderCheckin();
    };

    try {
      const res = await window.Api.checkinBatch(snapshot, onEvent);
      if (token !== state.runToken) return;
      state.busy.batch = false;
      state.progress.countdown = null;
      if (!done) {
        state.lastRun = { credits: res.credits, skipped: res.skipped, failed: res.failed };
      }
      if (res.credits > 0) {
        setMessage("checkin", { type: "ok", text: `签到完成：+${fmtCredits(res.credits)} 积分（跳过 ${res.skipped}，失败 ${res.failed}）。` });
        toast("ok", `签到成功 +${fmtCredits(res.credits)} 积分`, `已记录 ${results.length} 条执行结果，失败 ${res.failed} 个。`);
      } else {
        setMessage("checkin", { type: "warn", text: `本次没有新增积分（均已签到或跳过：${res.skipped} 个，失败 ${res.failed} 个），不弹出成功提示。` });
      }
    } catch (err) {
      if (token !== state.runToken) return;
      state.busy.batch = false;
      setMessage("checkin", { type: "danger", text: "批量签到中断：网络请求失败，已进行中的账号结果已保留。" });
      toast("err", "签到中断", "请检查网络后重试。");
    }
    renderAll();
  }

  /* ═════════ P3 账号管理 ═════════ */
  function renderAccounts() {
    const en = enabledAccounts().length;
    $("#acctMeta").textContent = `${state.accounts.length} 个账号 · ${en} 个参与签到`;
    $("#accountsHint").textContent = "勾选「参与签到」决定批量签到集合";
    $("#btnAddAccount").disabled = state.busy.batch || state.busy.switch;
    $("#btnAddAccount").title = state.busy.batch ? "批量签到执行中，暂不能添加账号" : state.busy.switch ? "客户端切换执行中，暂不能添加账号" : "打开浏览器登录新账号";
    $("#btnAcctRefresh").disabled = state.busy.batch || state.busy.switch;

    const list = $("#accountList");
    const empty = $("#accountsEmpty");
    empty.hidden = state.accounts.length > 0;
    list.hidden = state.accounts.length === 0;
    renderBanner($("#accountsBanner"), state.messages.accounts);
    if (!state.accounts.length) { list.innerHTML = ""; return; }

    const colorMap = { "s-ok": "var(--c-success)", "s-warn": "var(--c-warn)", "s-danger": "var(--c-danger)", "s-info": "var(--c-primary)", "s-mute": "var(--c-sub)" };
    list.innerHTML = state.accounts.map((a) => {
      const st = statusOf(a);
      const busy = state.busy.batch || state.busy.switch;
      return `
      <div class="acct-row">
        <div class="acct-avatar"><span class="avatar" style="background:hsl(${a.hue} 46% 44%)">${esc(a.initial)}</span></div>
        <div class="acct-id">
          <div class="acct-title">
            <b>${esc(a.name)}</b>
            ${state.currentId === a.id ? '<span class="badge badge-current">当前</span>' : ""}
            ${!a.loggedIn ? '<span class="badge badge-warn">未登录</span>' : ""}
          </div>
          <div class="acct-handle">${esc(a.handle)} · 设备 ${esc(a.deviceId)}</div>
          <div class="acct-status">
            <i class="status-dot ${st.cls}"></i>
            <span style="color:${colorMap[st.cls]}">${esc(st.label)}</span>
            <span class="acct-detail">剩余 ${fmtCredits(a.credits)} · 今日 +${fmtCredits(a.todayReward)} · 连签 ${a.streak} 天</span>
          </div>
        </div>
        <div class="acct-actions">
          <label class="check-inline" title="${busy ? "任务执行中，暂不能修改" : "是否进入批量签到集合"}">
            <input type="checkbox" data-act="acct-toggle" data-id="${a.id}" ${a.enabled ? "checked" : ""} ${busy ? "disabled" : ""}>
            <span class="check-box"><svg class="ico"><use href="#ic-check"/></svg></span>
            参与签到
          </label>
          <button type="button" class="btn btn-ghost btn-sm" data-act="acct-login" data-id="${a.id}" ${busy ? "disabled" : ""} title="打开浏览器重新登录此账号">
            <svg class="ico"><use href="#ic-login"/></svg>${a.loggedIn ? "重新登录" : "登录"}
          </button>
          <button type="button" class="btn btn-ghost btn-sm btn-danger" data-act="acct-del" data-id="${a.id}" ${busy ? "disabled" : ""} title="删除账号及其本机凭据">
            <svg class="ico"><use href="#ic-trash"/></svg>删除
          </button>
        </div>
      </div>`;
    }).join("");
  }

  /* ═════════ P4 客户端切换 ═════════ */
  function archiveState(a) {
    if (!a) return { label: "—", cls: "s-mute" };
    if (a.archive.state === "ok") return { label: "已建档", cls: "s-ok" };
    if (a.archive.state === "warn") return { label: "待确认", cls: "s-warn" };
    return { label: "未建档", cls: "s-mute" };
  }

  function renderSwitch() {
    setVal("#clientPath", state.client.path === window.DB.clientExePath ? "" : state.client.path);
    const busyAll = state.busy.batch || state.busy.switch;
    $("#clientPath").disabled = busyAll;
    const detected = state.client.detected && state.client.running;
    $("#clientDot").className = `status-dot ${detected ? "s-ok" : "s-warn"}`;
    $("#clientStatusText").textContent = detected ? "已检测 · Trae.exe 运行中" : "未检测到运行中的 Trae.exe";
    $("#clientPathText").textContent = state.client.detected ? state.client.path : "路径未探测到，可手动填写后重试";
    $("#btnDetect").disabled = state.busy.batch || state.busy.switch;

    const archived = state.accounts.filter((a) => a.archive.state !== "none").length;
    $("#archiveMeta").textContent = `${state.accounts.length} 个账号 · ${archived} 个已建档`;
    renderBanner($("#switchBanner"), state.messages.switch);

    const list = $("#archiveList");
    const empty = $("#archiveEmpty");
    empty.hidden = state.accounts.length > 0;
    list.hidden = state.accounts.length === 0;

    if (state.accounts.length) {
      const busy = state.busy.batch || state.busy.switch;
      list.innerHTML = state.accounts.map((a) => {
        const ar = archiveState(a);
        const isClient = a.clientCurrent;
        const noArchive = a.archive.state === "none";
        const switchTitle = busy ? "任务执行中，暂不能切换" : isClient ? "客户端当前已是该账号" : !detected ? "未检测到 Trae.exe：请先启动客户端，或用上方路径重新检测" : noArchive ? "尚未建档，将先进入建档向导" : `切换并重启客户端至 ${a.name}`;
        return `
        <div class="archive-row ${isClient ? "is-client" : ""}">
          <span class="avatar" style="background:hsl(${a.hue} 46% 44%)">${esc(a.initial)}</span>
          <div class="arc-main">
            <div class="arc-head">
              <span class="arc-name">${esc(a.name)}</span>
              ${isClient ? '<span class="badge badge-client">客户端当前</span>' : ""}
              <span class="arc-state" style="color:${ar.cls === "s-ok" ? "var(--c-success)" : ar.cls === "s-warn" ? "var(--c-warn)" : "var(--c-mute)"}">
                <i class="status-dot ${ar.cls}"></i>${ar.label}
              </span>
            </div>
            <div class="arc-detail">${noArchive ? "尚未建立档案" : `${esc(a.archive.at)} · ${a.archive.files} 个文件 · ${esc(a.archive.size)}`}</div>
            <div class="arc-note ${a.archive.state === "warn" ? "is-warn" : ""}">
              <svg class="ico"><use href="#${a.archive.state === "warn" ? "ic-alert" : "ic-info"}"/></svg>
              <span>${esc(a.archive.note)}</span>
            </div>
          </div>
          <div class="arc-actions">
            <button type="button" class="btn btn-ghost btn-sm" data-act="arc-build" data-id="${a.id}" ${busy || !a.loggedIn ? "disabled" : ""} title="${!a.loggedIn ? "请先登录该账号" : "建立 / 重新建立客户端档案"}">
              <svg class="ico"><use href="#ic-shield"/></svg>建档
            </button>
            <button type="button" class="btn ${isClient ? "btn-ghost" : "btn-accent"} btn-sm" data-act="arc-switch" data-id="${a.id}" ${busy || isClient || !detected ? "disabled" : ""} title="${esc(switchTitle)}">
              <svg class="ico"><use href="#ic-switch"/></svg>${isClient ? "已是当前" : "切换至此"}
            </button>
            <button type="button" class="btn btn-ghost btn-sm" data-act="arc-verify" data-id="${a.id}" ${busy || noArchive ? "disabled" : ""} title="${noArchive ? "建档后才能排障" : "校验档案完整性与登录态"}">
              <svg class="ico"><use href="#ic-wrench"/></svg>排障
            </button>
            <button type="button" class="btn btn-ghost btn-sm btn-danger" data-act="arc-del" data-id="${a.id}" ${busy || noArchive ? "disabled" : ""} title="删除该账号的客户端档案">
              <svg class="ico"><use href="#ic-trash"/></svg>删除
            </button>
          </div>
        </div>`;
      }).join("");
    } else {
      list.innerHTML = "";
    }

    const running = state.busy.switch;
    $("#logState").className = `log-state ${running ? "is-running" : state.logs.length ? "is-done" : ""}`;
    $("#logState").textContent = running ? "执行中" : state.logs.length ? "已完成" : "空闲";
    $("#switchProgress").hidden = !running;
    $("#btnDetect").title = state.busy.batch ? "批量签到执行中，暂不能检测" : state.busy.switch ? "客户端切换执行中，暂不能检测" : "填写路径可覆盖自动探测结果";
    $("#btnClearLog").disabled = running;
    renderSwitchLog();
  }

  function renderSwitchLog() {
    const term = $("#switchLog");
    const logs = state.logs;
    if (!logs.length) {
      term.innerHTML = `<div class="term-empty">日志将按步骤追加：杀进程 → 快照 → 恢复 → 拉起 → 守护 → 回写</div>`;
      return;
    }
    const tags = { info: "INFO", ok: " OK ", run: " RUN", warn: "WARN" };
    term.innerHTML = logs.map((l) => `
      <div class="term-line ${l.kind}">
        <span class="term-time">${esc(l.time)}</span>
        <span class="term-tag">[${tags[l.kind] || "INFO"}]</span>
        <span class="term-text">${esc(l.text)}</span>
      </div>`).join("");
    term.scrollTop = term.scrollHeight;
  }

  function pushSwitchLog(kind, text) {
    state.logs.push({ time: nowTime(), kind, text });
    if (state.logs.length > 200) state.logs.shift();
    renderSwitchLog();
  }

  async function startSwitch(id) {
    const acc = getAccount(id);
    if (!acc || state.busy.batch || state.busy.switch) return;
    if (acc.archive.state === "none") {
      openWizard(id);
      return;
    }
    const token = ++state.runToken;
    state.busy.switch = true;
    state.logs = [];
    setMessage("switch", { type: "info", text: `正在切换至「${acc.name}」，请勿关闭程序；失败会自动回滚。` });
    renderAll();
    const content = $("#appContent");
    if (content) content.scrollTop = content.scrollHeight;

    try {
      await window.Api.switchClient(acc, (step) => {
        if (token !== state.runToken) return;
        pushSwitchLog(step.kind, step.text);
      });
      if (token !== state.runToken) return;
      state.accounts.forEach((a) => { a.clientCurrent = a.id === id; });
      state.clientCurrentId = id;
      acc.archive = {
        state: "ok",
        at: `${TODAY.getFullYear()}-${pad(TODAY.getMonth() + 1)}-${pad(TODAY.getDate())} ${nowClock()}`,
        files: 38,
        size: "1.2 MB",
        similarity: 0.98,
        note: "档案与客户端登录态一致（切换后校验通过）"
      };
      state.busy.switch = false;
      setMessage("switch", { type: "ok", text: `已切换至「${acc.name}」，客户端已重启并校验通过。` });
      toast("ok", `已切换至 ${acc.name}`, "客户端已重启，登录态校验通过；原状态快照保留用于回滚。");
    } catch (err) {
      if (token !== state.runToken) return;
      state.busy.switch = false;
      setMessage("switch", { type: "danger", text: "切换失败，已自动回滚到原登录态。请查看日志了解失败步骤。" });
      toast("err", "切换失败，已回滚", "快照已还原，客户端可继续正常使用。");
    }
    renderAll();
  }

  /* ═════════ P5 用量统计 ═════════ */
  function usageRows() {
    const today = new Date(2026, 10, 4);
    const from = new Date(today);
    from.setDate(from.getDate() - state.rangeDays);
    return state.usage.filter((u) => {
      if (state.usageAccount !== "all" && u.accountId !== state.usageAccount) return false;
      const d = new Date(`${u.date}T00:00:00`);
      return d >= from && d <= today;
    });
  }

  function renderUsage() {
    const sel = $("#usageAccount");
    const prevValue = state.usageAccount;
    sel.innerHTML = `<option value="all">全部账号</option>` + state.accounts.map((a) => `<option value="${a.id}">${esc(a.name)}</option>`).join("");
    if (prevValue !== "all" && !getAccount(prevValue)) state.usageAccount = "all";
    sel.value = state.usageAccount;
    sel.disabled = !state.accounts.length || state.usageLoading;
    $("#usageRange").value = String(state.rangeDays);

    const rows = usageRows();
    const credits = rows.reduce((s, r) => s + r.credits, 0);
    const tokens = rows.reduce((s, r) => s + r.tokens, 0);
    $("#sumSessions").textContent = state.accounts.length ? fmtInt(rows.length) : "—";
    $("#sumCredits").textContent = state.accounts.length ? fmtCredits(credits) : "—";
    $("#sumTokens").textContent = state.accounts.length ? fmtTokens(tokens) : "—";
    $("#sumCost").textContent = state.accounts.length ? `¥${(credits * 0.1).toFixed(2)}` : "—";
    $("#sumScope").textContent = `近 ${state.rangeDays} 天 · ${state.usageAccount === "all" ? "全部账号" : getAccount(state.usageAccount)?.name || "—"}`;

    $("#expireBanner").hidden = !state.accounts.length;
    $("#btnPullUsage").disabled = state.usageLoading || !state.accounts.length;
    $("#btnPullUsage").title = !state.accounts.length ? "请先在账号管理中添加并登录账号" : "拉取当前账号与时间范围的最新用量";
    $("#usageRange").disabled = state.usageLoading;
    renderBanner($("#usageBanner"), state.messages.usage);

    const body = $("#usageBody");
    const empty = $("#usageEmpty");
    empty.hidden = !(rows.length === 0 && !state.usageLoading);
    $("#usageEmptyDays").textContent = state.rangeDays;
    if (state.usageLoading) {
      body.innerHTML = Array.from({ length: 6 }).map(() => `
        <tr class="skel-row">
          <td><span class="skel" style="width:58px"></span></td>
          <td><span class="skel" style="width:88px"></span></td>
          <td><span class="skel" style="width:30px"></span></td>
          <td class="ta-r"><span class="skel" style="width:36px"></span></td>
          <td class="ta-r"><span class="skel" style="width:42px"></span></td>
          <td><span class="skel" style="width:88%"></span></td>
        </tr>`).join("");
      return;
    }
    body.innerHTML = rows.map((u) => `
      <tr>
        <td class="mono">${esc(u.date.slice(5))} ${esc(u.at)}</td>
        <td><span class="usage-model">${esc(u.model)}</span></td>
        <td>${esc(u.mode)}</td>
        <td class="ta-r"><span class="credit-pos">${u.credits.toFixed(1)}</span></td>
        <td class="ta-r"><span class="usage-tokens">${fmtInt(u.tokens)}</span></td>
        <td class="usage-preview" title="${esc(u.preview)}">${esc(u.preview)}</td>
      </tr>`).join("");
  }

  /* ═════════ P6 设置 ═════════ */
  function renderSettings() {
    const boxes = {
      setAutostart: state.settings.autostart,
      setTray: state.settings.tray,
      setDevice: state.settings.device
    };
    Object.entries(boxes).forEach(([id, val]) => {
      const el = document.getElementById(id);
      if (el) el.checked = val;
    });
  }

  function renderAll() {
    renderNav();
    renderDashboard();
    renderCheckin();
    renderAccounts();
    renderSwitch();
    renderUsage();
    renderSettings();
  }

  /* ═════════ 页面切换（六页常驻，只改可见性） ═════════ */
  const PAGES = ["dashboard", "checkin", "accounts", "switch", "usage", "settings"];

  function go(page) {
    if (!PAGES.includes(page)) return;
    const content = $("#appContent");
    if (state.page !== page) state.scroll[state.page] = content.scrollTop;
    state.page = page;
    $$(".page").forEach((p) => p.classList.toggle("active", p.dataset.page === page));
    content.scrollTop = state.scroll[page] || 0;
    if (window.history && window.history.replaceState) {
      try { window.history.replaceState(null, "", `#${page}`); } catch (err) { /* file:// 下忽略 */ }
    }
    renderNav();
  }

  /* ═════════ G5 通用对话框 ═════════ */
  function openDialog(opts) {
    state.dialog = opts;
    const overlay = $("#dialogOverlay");
    const dialog = $("#dialog");
    const type = opts.type || "info";
    const icon = type === "danger" ? "ic-alert" : type === "warn" ? "ic-alert" : "ic-info";
    dialog.innerHTML = `
      <div class="dlg-head">
        <span class="dlg-icon ${type}"><svg class="ico"><use href="#${icon}"/></svg></span>
        <div>
          <div class="dlg-title">${esc(opts.title)}</div>
          ${opts.sub ? `<div class="dlg-sub">${esc(opts.sub)}</div>` : ""}
        </div>
      </div>
      ${opts.message ? `<p class="dlg-msg">${esc(opts.message)}</p>` : ""}
      ${opts.detail ? `<div class="dlg-detail">${opts.detail}</div>` : ""}
      <div class="dlg-actions">
        <button type="button" class="btn btn-ghost" data-dlg="cancel">${esc(opts.cancel || "取消")}</button>
        <button type="button" class="btn ${type === "danger" ? "btn-danger-solid" : "btn-accent"}" data-dlg="ok">${esc(opts.confirm || "确定")}</button>
      </div>`;
    overlay.hidden = false;
    dialog.querySelector('[data-dlg="cancel"]').addEventListener("click", closeDialog);
    dialog.querySelector('[data-dlg="ok"]').addEventListener("click", () => {
      const fn = opts.onConfirm;
      closeDialog();
      if (fn) fn();
    });
  }

  function closeDialog() {
    $("#dialogOverlay").hidden = true;
    $("#dialog").innerHTML = "";
    state.dialog = null;
  }

  /* ═════════ G4 建档向导 ═════════ */
  const WIZARD_STEPS = ["确认", "打开客户端", "关闭客户端", "交叉比对", "备份建档"];
  let wizardTimer = null;

  function wizardLater(ms, fn) {
    clearTimeout(wizardTimer);
    wizardTimer = setTimeout(fn, ms);
  }

  function openWizard(id) {
    const acc = getAccount(id);
    if (!acc) return;
    if (!acc.loggedIn) {
      toast("warn", "账号未登录", "请先登录该账号，再建立客户端档案。");
      return;
    }
    clearTimeout(wizardTimer);
    state.wizard = { open: true, step: 1, accountId: id, phase: "idle", similarity: 0, confirmed: false, progress: 0 };
    $("#wizardOverlay").hidden = false;
    renderWizard();
  }

  function closeWizard() {
    clearTimeout(wizardTimer);
    state.wizard.open = false;
    $("#wizardOverlay").hidden = true;
  }

  function wizardGo(step) {
    const w = state.wizard;
    w.step = step;
    w.confirmed = false;
    if (step === 2) {
      w.phase = "opening";
      wizardLater(1500, () => { w.phase = "waiting"; renderWizard(); });
    } else if (step === 3) {
      w.phase = "closing";
      renderWizard();
      wizardLater(1500, () => {
        w.phase = "closed";
        renderWizard();
        wizardLater(700, () => wizardGo(4));
      });
    } else if (step === 4) {
      w.phase = "scanning";
      const acc = getAccount(w.accountId);
      w.similarity = acc && acc.id.startsWith("na") ? 0.87 : 0.54;
      renderWizard();
      wizardLater(1700, () => { w.phase = "result"; w.confirmed = w.similarity < 0.85; renderWizard(); });
    } else if (step === 5) {
      w.phase = "building";
      w.progress = 0;
      renderWizard();
      const tick = () => {
        w.progress = Math.min(100, w.progress + 23);
        if (w.progress >= 100) {
          w.phase = "done";
          const acc = getAccount(w.accountId);
          if (acc) {
            acc.archive = {
              state: "ok",
              at: `${TODAY.getFullYear()}-${pad(TODAY.getMonth() + 1)}-${pad(TODAY.getDate())} ${nowClock()}`,
              files: 38,
              size: "1.2 MB",
              similarity: w.similarity,
              note: "档案已建立并校验通过"
            };
          }
          setMessage("switch", { type: "ok", text: `「${acc ? acc.name : ""}」的客户端档案已建立，现在可以切换。` });
          toast("ok", "档案建立完成", "已备份 38 个文件（1.2 MB），可随时用于客户端切换。");
          renderAll();
          renderWizard();
        } else {
          renderWizard();
          wizardLater(420, tick);
        }
      };
      wizardLater(420, tick);
      return;
    }
    renderWizard();
  }

  function renderWizard() {
    const w = state.wizard;
    const acc = getAccount(w.accountId);
    if (!w.open) return;
    $("#wizardSub").textContent = acc ? `账号：${acc.name} · ${acc.handle}` : "为账号备份一份客户端登录态";

    $("#wizardSteps").innerHTML = WIZARD_STEPS.map((label, i) => {
      const n = i + 1;
      const done = n < w.step || (w.step === 5 && w.phase === "done");
      const current = n === w.step && !done;
      return `<div class="w-step ${done ? "done" : ""} ${current ? "current" : ""}">
        <span class="ws-num">${done ? '<svg class="ico"><use href="#ic-check"/></svg>' : n}</span>
        <span class="ws-label">${label}</span>
      </div>`;
    }).join("");

    $("#wizardBody").innerHTML = wizardBody(acc);
    $("#wizardFooter").innerHTML = wizardFooter();

    $("#wizardSteps").className = `wizard-steps ${w.step === 5 && w.phase === "done" ? "all-done" : ""}`;
  }

  function wizardBody(acc) {
    const w = state.wizard;
    if (w.step === 1) {
      return `
        <h4 class="wz-title">确认前置条件</h4>
        <p class="wz-desc">建档会把 Trae 客户端当前的登录态完整备份成一份档案，用于之后在不同账号之间切换。开始前请确认：</p>
        <ul class="wz-list">
          <li><svg class="ico"><use href="#ic-check"/></svg><span>已在 Trae 客户端手动登录「${esc(acc.name)}」</span></li>
          <li><svg class="ico"><use href="#ic-check"/></svg><span>客户端未进行重要对话，或已保存当前会话</span></li>
          <li><svg class="ico"><use href="#ic-check"/></svg><span>了解建档期间会自动关闭并重新打开客户端</span></li>
        </ul>
        <div class="wz-callout ok"><svg class="ico"><use href="#ic-shield"/></svg><span>档案只保存在本机 <b>%LOCALAPPDATA%\\TraeTool\\archives</b>，不上传云端。</span></div>`;
    }
    if (w.step === 2) {
      return `
        <h4 class="wz-title">在客户端完成登录</h4>
        <p class="wz-desc">正在为你启动 Trae 客户端。请确认客户端内显示的是「${esc(acc.name)}」，再继续下一步。</p>
        <div class="wz-visual">
          <div class="wz-node"><span class="n-icon"><svg class="ico"><use href="#ic-database"/></svg></span><span>TraeTool 档案</span></div>
          <div class="wz-link"><i class="live"></i><i class="live"></i><i></i></div>
          <div class="wz-node active"><span class="n-icon"><svg class="ico"><use href="#ic-monitor"/></svg></span><span>Trae 客户端</span></div>
        </div>
        ${w.phase === "opening"
          ? `<div class="wz-wait"><span class="spinner"></span><span>正在打开 Trae 客户端…（若未安装，将回退到手动启动）</span></div>`
          : `<div class="wz-callout ok"><svg class="ico"><use href="#ic-check"/></svg><span><b>客户端已打开。</b> 请在该窗口完成登录，确认账号无误后点击右下角「我已完成登录」。</span></div>`}`;
    }
    if (w.step === 3) {
      return `
        <h4 class="wz-title">安全关闭客户端</h4>
        <p class="wz-desc">为了复制完整的登录态文件，需要先正常关闭 Trae 客户端；未保存的对话会丢失。</p>
        <div class="wz-visual">
          <div class="wz-node"><span class="n-icon"><svg class="ico"><use href="#ic-folder"/></svg></span><span>登录态目录</span></div>
          <div class="wz-link"><i></i><i></i><i></i></div>
          <div class="wz-node active"><span class="n-icon"><svg class="ico"><use href="#ic-power"/></svg></span><span>Trae 客户端</span></div>
        </div>
        <div class="wz-wait"><span class="spinner"></span><span>${w.phase === "closing" ? "正在等待客户端正常退出…" : "客户端已关闭，正在校验进程状态…"}</span></div>`;
    }
    if (w.step === 4) {
      if (w.phase === "scanning") {
        return `
          <h4 class="wz-title">与已有档案交叉比对</h4>
          <p class="wz-desc">比对登录态文件的指纹与已有档案，避免重复建档或串号。</p>
          <div class="wz-sim">
            <div class="wz-sim-row"><span>扫描登录态目录</span><span class="wz-sim-val">…</span></div>
            <div class="wz-sim-bar"><i style="width:62%"></i></div>
          </div>
          <div class="wz-wait"><span class="spinner"></span><span>正在计算相似度（约 1.7 秒）…</span></div>`;
      }
      const pct = Math.round(w.similarity * 100);
      const warn = w.similarity >= 0.85;
      return `
        <h4 class="wz-title">比对结果</h4>
        <p class="wz-desc">已与 ${state.accounts.length} 个账号档案比对完成。</p>
        <div class="wz-sim">
          <div class="wz-sim-row"><span>与「${esc(acc.name)}」已有档案的相似度</span><span class="wz-sim-val">${pct}%</span></div>
          <div class="wz-sim-bar"><i style="width:${pct}%;background:${warn ? "var(--c-warn)" : "var(--c-primary)"}"></i></div>
        </div>
        ${warn
          ? `<div class="wz-callout warn"><svg class="ico"><use href="#ic-alert"/></svg><span><b>相似度超过 0.85 阈值。</b> 该档案可能与其他账号重复，继续建档会覆盖旧档案。请确认客户端内登录的确实是「${esc(acc.name)}」。</span></div>
             <label class="wz-confirm"><input type="checkbox" data-wsim="1" ${w.confirmed ? "checked" : ""}><span class="check-box"><svg class="ico"><use href="#ic-check"/></svg></span>我确认客户端当前登录的是「${esc(acc.name)}」，并同意覆盖旧档案</label>`
          : `<div class="wz-callout ok"><svg class="ico"><use href="#ic-check"/></svg><span>未发现重复档案，可以继续建立新档案。</span></div>`}`;
    }
    if (w.step === 5) {
      if (w.phase === "done") {
        return `
          <div class="wz-succ">
            <span class="wz-succ-icon"><svg class="ico"><use href="#ic-check"/></svg></span>
            <b>档案建立完成</b>
            <p>已备份 38 个文件 · 1.2 MB · 相似度 ${Math.round(w.similarity * 100)}%<br>现在可以在「客户端切换」中切换至「${esc(acc.name)}」。</p>
          </div>`;
      }
      return `
        <h4 class="wz-title">正在备份登录态…</h4>
        <p class="wz-desc">请勿关闭程序。备份完成后会自动校验文件完整性。</p>
        <div class="wz-progress"><i style="width:${w.progress}%"></i></div>
        <div class="wz-sim-row"><span>写入档案文件</span><span class="wz-sim-val">${w.progress}%</span></div>
        <div class="wz-callout ok"><svg class="ico"><use href="#ic-shield"/></svg><span>目标：%LOCALAPPDATA%\\TraeTool\\archives\\${esc(acc.name)}_20261104</span></div>`;
    }
    return "";
  }

  function wizardFooter() {
    const w = state.wizard;
    const btn = (act, text, cls, disabled) => `<button type="button" class="btn ${cls}" data-wact="${act}" ${disabled ? "disabled" : ""}>${text}</button>`;
    if (w.step === 5) {
      if (w.phase === "done") {
        return `<span class="wz-ft-left">档案已校验通过</span>${btn("done", "完成", "btn-accent")}`;
      }
      return `<span class="wz-ft-left">备份中，请稍候…</span><button type="button" class="btn btn-ghost" disabled>取消</button>`;
    }
    if (w.step === 2) {
      return `<span class="wz-ft-left">等待客户端登录完成</span>
        ${btn("prev", "上一步", "btn-ghost", w.phase === "opening")}
        ${btn("cancel", "取消", "btn-ghost")}
        <button type="button" class="btn btn-accent" data-wact="next" ${w.phase === "waiting" ? "" : "disabled"}>我已完成登录</button>`;
    }
    if (w.step === 3) {
      return `<span class="wz-ft-left">正在关闭客户端，请勿操作</span><button type="button" class="btn btn-ghost" disabled>取消</button>`;
    }
    if (w.step === 4) {
      if (w.phase === "scanning") {
        return `<span class="wz-ft-left">比对中…</span><button type="button" class="btn btn-ghost" disabled>取消</button>`;
      }
      return `<span class="wz-ft-left">${w.similarity >= 0.85 ? "需确认后才能建档" : "比对通过"}</span>
        ${btn("cancel", "取消", "btn-ghost")}
        <button type="button" class="btn btn-accent" data-wact="confirm" ${w.confirmed ? "" : "disabled"}>确认建档</button>`;
    }
    return `<span class="wz-ft-left">第 1 / 5 步</span>${btn("cancel", "取消", "btn-ghost")}${btn("next", "下一步", "btn-accent")}`;
  }

  /* ═════════ G3 登录窗口 ═════════ */
  const wait = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
  let loginTimer = null;
  let loginAuto = null;
  let codeTimer = null;

  function setLoginPhase(phase) {
    state.login.phase = phase;
    const labels = {
      init: "正在初始化浏览器…",
      waiting: "等待登录 · 请在浏览器内完成手机号登录",
      saving: "已识别到登录成功，正在加密保存凭据…",
      ok: "登录成功，正在关闭窗口…"
    };
    $("#loginStatus").textContent = labels[phase] || labels.init;
    const ft = $("#loginOverlay .lf-left");
    ft.classList.toggle("is-ok", phase === "ok");
    $("#loginSpinner").style.display = "";
    const lockedPhase = phase === "saving" || phase === "ok";
    $("#btnLoginSubmit").disabled = lockedPhase;
    $("#btnLoginDemo").disabled = lockedPhase;
  }

  function openLogin(mode, accountId) {
    if (state.busy.batch || state.busy.switch) {
      toast("warn", "任务执行中", "请等待批量签到或客户端切换完成后再登录账号。");
      return;
    }
    clearTimeout(loginTimer);
    clearTimeout(loginAuto);
    clearInterval(codeTimer);
    state.login = { open: true, mode: mode || "add", accountId: accountId || null, phase: "init" };

    const acc = accountId ? getAccount(accountId) : null;
    const title = $("#loginOverlay .lt-left span:last-child");
    if (title) title.textContent = mode === "login" && acc ? `重新登录 · ${acc.name}` : "登录 Trae 账号";
    const phone = $("#loginPhone");
    const code = $("#loginCode");
    if (phone) phone.value = "138 0013 8000";
    if (code) code.value = "8842";
    const sendBtn = $("#btnSendCode");
    sendBtn.disabled = false;
    sendBtn.textContent = "获取验证码";
    $$(".lc-tab", $("#loginOverlay")).forEach((t, i) => t.classList.toggle("active", i === 0));

    $("#loginOverlay").hidden = false;
    setLoginPhase("init");
    loginTimer = setTimeout(() => {
      if (state.login.open && state.login.phase === "init") setLoginPhase("waiting");
    }, 900);
    loginAuto = setTimeout(() => {
      if (state.login.open && state.login.phase === "waiting") loginSuccess();
    }, 6200);
  }

  function loginSuccess() {
    if (!state.login.open) return;
    if (state.login.phase === "saving" || state.login.phase === "ok") return;
    clearTimeout(loginTimer);
    clearTimeout(loginAuto);
    setLoginPhase("saving");
    loginTimer = setTimeout(() => {
      if (state.login.open) applyLogin();
    }, 850);
  }

  function createAccount() {
    state.tempid += 1;
    const n = state.tempid;
    const base = ["许知遥", "林可", "路远", "沈青禾"][(n - 1) % 4];
    const name = n > 4 ? `${base} ${n}` : base;
    const account = {
      id: `na${n}`, name, handle: `new.account${n}@trae.ai`, initial: name.slice(0, 1),
      hue: 198 + (n % 4) * 18, enabled: true, loggedIn: true, status: "unchecked",
      credits: 0, todayReward: 0, streak: 0, member: false, memberNote: "非会员",
      clientCurrent: false, lastCheckin: "—",
      deviceId: `TR${String(n).padStart(2, "0")}-6B2D-91E4-C0A7`,
      archive: { state: "none", at: "", files: 0, size: "", similarity: 0, note: "尚未建立客户端档案" }
    };
    state.accounts.push(account);
    state.currentId = account.id;
    return account.id;
  }

  function applyLogin() {
    setLoginPhase("ok");
    const mode = state.login.mode;
    let newId = null;

    if (mode === "login") {
      const acc = getAccount(state.login.accountId);
      if (acc) {
        const needReset = !acc.loggedIn || acc.status === "expired";
        acc.loggedIn = true;
        if (needReset) acc.status = "unchecked";
        if (acc.archive.state === "none") acc.archive.note = "尚未建立客户端档案";
        setMessage("accounts", { type: "ok", text: `「${acc.name}」已重新登录，凭据已用 DPAPI 加密保存。` });
      }
    } else {
      newId = createAccount();
      setMessage("accounts", { type: "ok", text: "新账号已添加。建议接着建立客户端档案，之后即可一键切换客户端登录态。" });
    }

    renderAll();

    loginTimer = setTimeout(() => {
      closeLogin();
      renderAll();
      if (newId) {
        toast("ok", "账号已添加", "登录凭据已加密保存；接下来为它建立客户端档案（可选）。");
        setTimeout(() => {
          if (getAccount(newId) && !document.body.classList.contains("trayed")) openWizard(newId);
        }, 720);
      } else {
        toast("ok", "已重新登录", "登录态已更新，可继续参与每日签到。");
      }
    }, 460);
  }

  function closeLogin() {
    clearTimeout(loginTimer);
    clearTimeout(loginAuto);
    clearInterval(codeTimer);
    state.login.open = false;
    $("#loginOverlay").hidden = true;
  }

  function sendCode() {
    const btn = $("#btnSendCode");
    if (btn.disabled) return;
    const code = $("#loginCode");
    if (code) code.value = "8842";
    let left = 20;
    btn.disabled = true;
    btn.textContent = `${left}s 后重发`;
    toast("info", "验证码已发送", "原型演示：验证码已自动填入 8842，也可直接点「模拟登录成功」。");
    clearInterval(codeTimer);
    codeTimer = setInterval(() => {
      left -= 1;
      if (left <= 0) {
        clearInterval(codeTimer);
        btn.disabled = false;
        btn.textContent = "获取验证码";
      } else {
        btn.textContent = `${left}s 后重发`;
      }
    }, 1000);
  }

  /* ═════════ 演示状态 / 托盘（G2） ═════════ */
  function syncDemoButtons() {
    $$("#stateSeg .seg-btn").forEach((b) => b.classList.toggle("active", b.dataset.state === state.demo));
    const compact = document.body.classList.contains("size-compact");
    $$("#sizeSeg .seg-btn").forEach((b) => b.classList.toggle("active", b.dataset.size === (compact ? "compact" : "default")));
    $("#btnMods").classList.toggle("active", document.body.classList.contains("show-mods"));
  }

  function clearTimers() {
    clearTimeout(wizardTimer);
    clearTimeout(loginTimer);
    clearTimeout(loginAuto);
    clearInterval(codeTimer);
    state.runToken += 1;
    state.busy.batch = false;
    state.busy.switch = false;
    state.progress = { lines: [], now: "", countdown: null };
    state.logs = [];
    state.usageLoading = false;
    state.lastRun = null;
    closeWizard();
    closeLogin();
    closeDialog();
  }

  function resetCommon() {
    initState();
    state.messages = {};
    state.filterId = "all";
    state.usageAccount = "all";
    state.rangeDays = 7;
    state.month = new Date(2026, 10, 1);
    state.auto = { enabled: true, time: "08:30", interval: 12 };
    state.settings = { autostart: false, tray: true, device: false };
    state.client = { detected: true, running: true, path: window.DB.clientExePath };
    state.tempid = 0;
  }

  function applyDemo(kind) {
    clearTimers();
    resetCommon();
    state.demo = kind;

    if (kind === "empty") {
      state.accounts = [];
      state.records = [];
      state.usage = [];
      state.trend = null;
      state.checkedDays = {};
      state.dayRewards = {};
      state.currentId = "";
      state.clientCurrentId = "";
      state.messages.dashboard = { type: "info", text: "还没有账号。添加账号后即可开始每日签到、查看积分趋势与切换客户端。", action: "添加第一个账号", act: "add-account" };
      state.messages.accounts = { type: "info", text: "账号列表为空：添加账号会弹出浏览器完成登录，凭据以 DPAPI 加密保存在本机。" };
    } else if (kind === "error") {
      state.client = { detected: false, running: false, path: window.DB.clientExePath };
      state.messages.dashboard = { type: "danger", text: "无法连接 Trae 服务（网络超时 12s），积分与签到状态可能不是最新。", action: "重试", act: "retry-all" };
      state.messages.checkin = { type: "danger", text: "批量签到中断：9074 冷却后网络超时，已完成账号的结果已保留。", action: "刷新状态", act: "refresh-accounts" };
      state.messages.accounts = { type: "warn", text: "2 个账号需要处理：1 个会话失效、1 个未登录。", action: "刷新状态", act: "refresh-accounts" };
      state.messages.switch = { type: "warn", text: "未检测到运行中的 Trae.exe，切换前请先启动客户端。", action: "检测客户端", act: "detect-client" };
      state.messages.usage = { type: "danger", text: "用量拉取失败（401 未登录）：请先到账号管理登录后再拉取。", action: "去账号管理", act: "goto-accounts" };
    } else if (kind === "busy") {
      state.messages.checkin = { type: "info", text: "批量签到执行中，完成后会逐条写入记录；跳过 / 已签不算失败。" };
    }

    renderAll();
    syncDemoButtons();
    if (kind === "busy") {
      setTimeout(() => {
        if (state.demo === "busy" && !state.busy.batch) startBatch();
      }, 90);
    }
  }

  function resetDemo() {
    clearTimers();
    resetCommon();
    state.demo = "normal";
    document.body.classList.remove("size-compact", "show-mods", "trayed", "exited");
    $("#trayDock").hidden = true;
    $("#specPanel").hidden = true;
    renderAll();
    syncDemoButtons();
    go("dashboard");
    toast("info", "已重置演示数据", "回到 5 个账号的常态数据，所有模拟任务已停止。");
  }

  function sendToTray(reason) {
    document.body.classList.add("trayed");
    document.body.classList.remove("exited");
    $("#trayDock").hidden = false;
    $("#trayTitle").textContent = reason === "min" ? "TraeTool 已最小化到托盘" : "TraeTool 已收至托盘";
    $("#trayText").textContent = state.busy.batch || state.busy.switch
      ? "后台任务仍在执行，完成前请勿退出程序；点击图标可恢复窗口。"
      : "后台继续自动签到，点击图标可恢复窗口。";
  }

  function exitApp() {
    clearTimers();
    document.body.classList.add("exited");
    document.body.classList.remove("trayed");
    $("#trayDock").hidden = false;
    $("#trayTitle").textContent = "TraeTool 已退出";
    $("#trayText").textContent = "自动签到已停止。点击图标可重新打开原型窗口（演示）。";
  }

  function restoreTray() {
    document.body.classList.remove("trayed", "exited");
    $("#trayDock").hidden = true;
    toast("info", "窗口已恢复", "后台任务状态已同步；自动签到按设置继续执行。");
  }

  function requestClose() {
    if (!state.settings.tray) {
      openDialog({
        type: "warn",
        title: "关闭窗口将退出程序",
        message: "「关闭窗口时收到系统托盘」已关闭，此时关闭会退出 TraeTool，后台自动签到也会停止。",
        detail: "<b>提示</b>：可在「设置 → 行为设置」重新开启托盘，让程序在后台继续签到。",
        confirm: "退出程序",
        onConfirm: exitApp
      });
      return;
    }
    sendToTray("close");
  }

  /* ═════════ 异步动作 ═════════ */
  function shiftMonth(delta) {
    state.month = new Date(state.month.getFullYear(), state.month.getMonth() + delta, 1);
    renderCalendar();
  }

  async function refreshDashboard() {
    const btn = $("#btnDashRefresh");
    if (btn.disabled) return;
    const old = btn.innerHTML;
    btn.disabled = true;
    btn.innerHTML = `<span class="spinner"></span>刷新中`;
    setMessage("dashboard", null);
    await wait(620);
    btn.innerHTML = old;
    renderDashboard();
    toast("ok", "已刷新", "积分、签到状态与会员信息已同步（原型模拟）。");
  }

  async function refreshAccounts() {
    const btn = $("#btnAcctRefresh");
    if (btn.disabled) return;
    const old = btn.innerHTML;
    btn.disabled = true;
    btn.innerHTML = `<span class="spinner"></span>刷新中`;
    setMessage("accounts", null);
    await wait(560);
    btn.innerHTML = old;
    renderAccounts();
    toast("ok", "账号状态已刷新", "已同步登录态、今日签到状态与设备号。");
  }

  async function detectClient() {
    const btn = $("#btnDetect");
    if (btn.disabled) return;
    const old = btn.innerHTML;
    btn.disabled = true;
    btn.innerHTML = `<span class="spinner"></span>检测中`;
    const raw = $("#clientPath").value.trim();
    const res = await window.Api.detectClient(raw || state.client.path);
    state.client = { detected: res.data.detected, running: res.data.running, path: res.data.path };
    btn.innerHTML = old;
    setMessage("switch", null);
    renderSwitch();
    toast("ok", "客户端检测完成", "已找到 Trae.exe 且进程运行中，可以执行切换。");
  }

  async function pullUsage() {
    if (state.usageLoading) return;
    state.usageLoading = true;
    setMessage("usage", { type: "info", text: "正在拉取最新用量…（最多保留 200 行）" });
    renderUsage();
    let count = 0;
    try {
      const res = await window.Api.pullUsage({ accountId: "all", rangeDays: 30 });
      state.usage = res.data;
      count = usageRows().length;
      setMessage("usage", null);
    } catch (err) {
      state.usageLoading = false;
      state.messages.usage = { type: "danger", text: "拉取失败：网络错误，请稍后重试。", action: "重新拉取", act: "usage-pull" };
      renderAll();
      toast("err", "用量拉取失败", "网络不可用，已保留上次结果。");
      return;
    }
    state.usageLoading = false;
    renderAll();
    toast("ok", "用量已更新", `当前范围共 ${count} 次会话。`);
  }

  /* ═════════ 账号 / 档案操作 ═════════ */
  function toggleAccount(id, checked) {
    const acc = getAccount(id);
    if (!acc) return;
    acc.enabled = checked;
    renderAll();
    toast("info", checked ? `「${acc.name}」已加入签到集合` : `「${acc.name}」已移出签到集合`, checked ? "下次一键签到会包含该账号。" : "一键签到将跳过该账号。");
  }

  function confirmDeleteAccount(id) {
    const acc = getAccount(id);
    if (!acc) return;
    openDialog({
      type: "danger",
      title: `删除账号「${acc.name}」？`,
      sub: acc.handle,
      message: "将删除本机保存的登录凭据与设备号，不影响服务端账号与已获得的积分。",
      detail: "<b>同时移除</b><ul><li>今日及历史签到记录（本机）</li><li>档案引用（档案文件仍保留在磁盘）</li></ul>",
      confirm: "删除账号",
      onConfirm: () => {
        const idx = state.accounts.findIndex((a) => a.id === id);
        if (idx < 0) return;
        state.accounts.splice(idx, 1);
        state.records = state.records.filter((r) => r.accountId !== id);
        if (state.currentId === id) state.currentId = state.accounts.length ? state.accounts[0].id : "";
        if (state.clientCurrentId === id) state.clientCurrentId = "";
        if (state.filterId === id) state.filterId = "all";
        if (state.usageAccount === id) state.usageAccount = "all";
        setMessage("accounts", { type: "ok", text: `已删除账号「${acc.name}」及其本机凭据。` });
        renderAll();
        toast("ok", "账号已删除", `「${acc.name}」的登录凭据与设备号已清除。`);
      }
    });
  }

  function confirmDeleteArchive(id) {
    const acc = getAccount(id);
    if (!acc) return;
    openDialog({
      type: "danger",
      title: `删除「${acc.name}」的客户端档案？`,
      sub: `${acc.archive.files} 个文件 · ${acc.archive.size}`,
      message: "档案文件将从本机磁盘删除；不会影响客户端当前的登录态。",
      detail: "删除后可重新通过「建档」向导生成新档案；已切换过的客户端不会自动回退。",
      confirm: "删除档案",
      onConfirm: () => {
        acc.archive = { state: "none", at: "", files: 0, size: "", similarity: 0, note: "尚未建立客户端档案" };
        setMessage("switch", { type: "ok", text: `已删除「${acc.name}」的客户端档案，如需切换请先重新建档。` });
        renderAll();
        toast("ok", "档案已删除", "客户端当前登录态未受影响。");
      }
    });
  }

  function confirmSwitch(id) {
    const acc = getAccount(id);
    if (!acc) return;
    if (state.busy.batch || state.busy.switch) {
      toast("warn", "任务执行中", "请等待当前任务完成后再切换客户端。");
      return;
    }
    if (!acc.loggedIn) {
      toast("warn", "账号未登录", "请先登录该账号，再建立档案或切换。");
      return;
    }
    if (acc.archive.state === "none") {
      openWizard(id);
      return;
    }
    openDialog({
      type: "warn",
      title: `把客户端切换到「${acc.name}」？`,
      sub: `${acc.handle} · 档案 ${acc.archive.files} 个文件`,
      message: "切换会关闭并重启 Trae 客户端，正在编辑的对话请先保存。",
      detail: "<b>执行顺序</b><ul><li>安全关闭 Trae.exe → 快照当前登录态</li><li>恢复目标档案 → 重新拉起客户端</li><li>守护校验登录态 → 回写档案时间</li></ul><b>失败回滚</b>：快照位于 <code>%TEMP%\\traeswitch_rollback_*</code>，失败会自动还原。",
      confirm: "开始切换",
      onConfirm: () => startSwitch(id)
    });
  }

  function verifyArchive(id) {
    const acc = getAccount(id);
    if (!acc) return;
    openDialog({
      type: "info",
      title: "档案排障校验",
      sub: `${acc.name} · ${acc.archive.at}`,
      detail: `<b>将检查</b><ul><li>档案文件完整性（${acc.archive.files} / ${acc.archive.files}）</li><li>客户端登录态与档案是否同一账号</li><li>快照可回滚性</li></ul><b>上次比对相似度</b>：${(acc.archive.similarity || 0).toFixed(2)}`,
      confirm: "运行校验",
      onConfirm: () => {
        setTimeout(() => {
          if (acc.archive.state === "warn") toast("warn", "校验发现不一致", "档案与客户端当前登录账号不一致，建议重新建档。");
          else toast("ok", "校验通过", "档案完整，且与客户端当前登录态一致。");
        }, 900);
      }
    });
  }

  /* ═════════ 动作委托 ═════════ */
  function onAction(e) {
    const el = e.target.closest("[data-act]");
    if (!el) return;
    const act = el.dataset.act;
    const id = el.dataset.id;
    if (act === "acct-toggle") return;

    switch (act) {
      case "qa-open":
        if (!id) return;
        state.currentId = id;
        go("checkin");
        renderAll();
        break;
      case "add-account": openLogin("add"); break;
      case "goto-accounts": go("accounts"); break;
      case "acct-login": openLogin("login", id); break;
      case "acct-del": confirmDeleteAccount(id); break;
      case "arc-build": openWizard(id); break;
      case "arc-switch": confirmSwitch(id); break;
      case "arc-verify": verifyArchive(id); break;
      case "arc-del": confirmDeleteArchive(id); break;
      case "retry-all": refreshDashboard(); break;
      case "refresh-accounts":
        go("accounts");
        refreshAccounts();
        break;
      case "detect-client": detectClient(); break;
      case "usage-pull": pullUsage(); break;
      default: break;
    }
  }

  function cancelWizard() {
    if (state.wizard.step === 5 && state.wizard.phase !== "done") {
      toast("warn", "正在备份", "备份完成后才能关闭向导，请稍候。");
      return;
    }
    closeWizard();
  }

  /* ═════════ 自定义下拉选项面板 ═════════
     原生 <option> 弹层由系统渲染，无法改圆角；
     这里保留原生 select 作为数据源，鼠标点击时改为渲染自绘面板。 */
  let activeSelect = null;

  function ensureSelectPop() {
    let pop = document.getElementById("selectPop");
    if (pop) return pop;
    pop = document.createElement("div");
    pop.id = "selectPop";
    pop.className = "select-pop";
    pop.hidden = true;
    pop.innerHTML = `<div class="select-pop-list" role="listbox"></div>`;
    document.body.appendChild(pop);
    return pop;
  }

  function openSelectPop(sel) {
    if (!sel || sel.disabled) return;
    if (activeSelect) activeSelect.classList.remove("is-pop-open");
    sel.focus({ preventScroll: true });
    const pop = ensureSelectPop();
    const list = pop.querySelector(".select-pop-list");
    list.innerHTML = "";
    Array.from(sel.options).forEach((opt) => {
      const btn = document.createElement("button");
      btn.type = "button";
      btn.className = `select-opt${opt.value === sel.value ? " is-selected" : ""}`;
      btn.setAttribute("role", "option");
      btn.setAttribute("aria-selected", opt.value === sel.value ? "true" : "false");
      btn.title = opt.text;
      btn.innerHTML = `<span class="select-opt-txt"></span><svg class="ico select-opt-check"><use href="#ic-check"/></svg>`;
      btn.querySelector(".select-opt-txt").textContent = opt.text;
      btn.addEventListener("click", () => chooseSelectOption(sel, opt.value));
      list.appendChild(btn);
    });

    activeSelect = sel;
    sel.classList.add("is-pop-open");
    pop.hidden = false;
    pop.style.visibility = "hidden";

    const r = sel.getBoundingClientRect();
    const width = Math.min(Math.max(r.width, 144), 260, window.innerWidth - 16);
    pop.style.width = width + "px";
    const height = pop.offsetHeight;
    let left = r.left;
    if (left + width > window.innerWidth - 8) left = window.innerWidth - width - 8;
    let top = r.bottom + 6;
    if (top + height > window.innerHeight - 8) top = r.top - height - 6;
    pop.style.left = Math.max(8, left) + "px";
    pop.style.top = Math.max(8, top) + "px";
    pop.style.visibility = "";

    const cur = list.querySelector(".is-selected");
    if (cur) list.scrollTop = Math.max(0, cur.offsetTop - list.clientHeight / 2 + cur.offsetHeight / 2);
  }

  function chooseSelectOption(sel, value) {
    closeSelectPop();
    if (sel.value !== value) {
      sel.value = value;
      sel.dispatchEvent(new Event("change", { bubbles: true }));
    }
    sel.focus();
  }

  function closeSelectPop() {
    const pop = document.getElementById("selectPop");
    if (pop) pop.hidden = true;
    if (activeSelect) activeSelect.classList.remove("is-pop-open");
    activeSelect = null;
  }

  function initSelects() {
    const pop = ensureSelectPop();
    document.addEventListener("pointerdown", (e) => {
      const sel = e.target.closest ? e.target.closest("select.select") : null;
      if (sel) {
        if (sel.disabled) return;
        e.preventDefault();
        if (!pop.hidden && activeSelect === sel) { closeSelectPop(); return; }
        openSelectPop(sel);
        return;
      }
      if (!pop.hidden && !(e.target.closest && e.target.closest("#selectPop"))) closeSelectPop();
    }, true);
    /* 同时拦掉系统的 mousedown 默认行为，避免原生弹层抢在前面打开 */
    document.addEventListener("mousedown", (e) => {
      if (e.target.closest && e.target.closest("select.select")) e.preventDefault();
    }, true);
    window.addEventListener("resize", closeSelectPop);
    document.addEventListener("scroll", (e) => {
      if (pop.hidden) return;
      if (e.target.closest && e.target.closest("#selectPop")) return;
      closeSelectPop();
    }, true);
  }

  /* ═════════ 事件绑定 ═════════ */
  function bindEvents() {
    $$(".nav-item").forEach((a) => {
      a.addEventListener("click", (e) => {
        e.preventDefault();
        go(a.dataset.nav);
      });
    });
    window.addEventListener("hashchange", () => {
      const p = window.location.hash.replace("#", "");
      if (PAGES.includes(p)) go(p);
    });

    $$("[data-win]").forEach((btn) => {
      btn.addEventListener("click", () => {
        const kind = btn.dataset.win;
        if (kind === "min") sendToTray("min");
        else if (kind === "max") toast("info", "原型固定窗口尺寸", "WPF 主窗口默认 980×680、最小 860×560，可在系统设置中调整。");
        else requestClose();
      });
    });

    $$("#sizeSeg .seg-btn").forEach((btn) => {
      btn.addEventListener("click", () => {
        const compact = btn.dataset.size === "compact";
        document.body.classList.toggle("size-compact", compact);
        syncDemoButtons();
        toast("info", compact ? "已切换到最小尺寸 860×560" : "已切换到默认尺寸 980×680", "内容超出时在内容区纵向滚动，不破版。");
      });
    });
    $$("#stateSeg .seg-btn").forEach((btn) => {
      btn.addEventListener("click", () => applyDemo(btn.dataset.state));
    });
    $("#btnMods").addEventListener("click", () => {
      document.body.classList.toggle("show-mods");
      syncDemoButtons();
    });
    $("#btnSpec").addEventListener("click", () => { $("#specPanel").hidden = false; });
    $("#specClose").addEventListener("click", () => { $("#specPanel").hidden = true; });
    $("#specPanel").addEventListener("click", (e) => {
      if (e.target === $("#specPanel")) $("#specPanel").hidden = true;
    });
    $("#btnReset").addEventListener("click", resetDemo);

    /* P1 */
    $("#btnDashRefresh").addEventListener("click", refreshDashboard);
    $("#btnQuickCheckin").addEventListener("click", startBatch);
    $("#calPrev").addEventListener("click", () => shiftMonth(-1));
    $("#calNext").addEventListener("click", () => shiftMonth(1));
    $("#qaList").addEventListener("keydown", (e) => {
      if (e.key === "Enter" && e.target.closest("[data-act]")) onAction(e);
    });

    /* P2 */
    $("#acctSelect").addEventListener("change", (e) => {
      state.currentId = e.target.value;
      renderAll();
    });
    $("#recordFilter").addEventListener("change", (e) => {
      state.filterId = e.target.value;
      renderRecords();
    });
    $("#autoEnabled").addEventListener("change", (e) => {
      state.auto.enabled = e.target.checked;
      renderNav();
      toast("info", e.target.checked ? "已开启每日自动签到" : "已关闭每日自动签到",
        e.target.checked ? `每天 ${state.auto.time} 自动执行（程序需在运行中）。` : "仍可手动点击「一键签到」。");
    });
    $("#autoTime").addEventListener("change", (e) => {
      state.auto.time = e.target.value || "08:30";
      toast("info", "自动签到时间已更新", `每天 ${state.auto.time} 执行；执行时附加 ±2 秒随机抖动。`);
    });
    $("#autoInterval").addEventListener("change", (e) => {
      const v = Math.min(120, Math.max(5, Number(e.target.value) || 12));
      state.auto.interval = v;
      e.target.value = v;
      renderCheckin();
      toast("info", "账号间隔已更新", `每个账号之间等待 ${v} 秒（±2 秒抖动）。`);
    });

    /* P3 */
    $("#btnAddAccount").addEventListener("click", () => openLogin("add"));
    $("#btnAcctRefresh").addEventListener("click", refreshAccounts);

    /* P4 */
    $("#btnDetect").addEventListener("click", detectClient);
    $("#clientPath").addEventListener("change", (e) => {
      state.client.path = e.target.value.trim() || window.DB.clientExePath;
      toast("info", "客户端路径已更新", "留空时会自动探测 Trae.exe 的默认安装位置。");
    });
    $("#btnClearLog").addEventListener("click", () => {
      state.logs = [];
      renderSwitch();
    });

    /* P5 */
    $("#usageAccount").addEventListener("change", (e) => {
      state.usageAccount = e.target.value;
      renderUsage();
      renderNav();
    });
    $("#usageRange").addEventListener("change", (e) => {
      state.rangeDays = Number(e.target.value);
      renderUsage();
      renderNav();
    });
    $("#btnPullUsage").addEventListener("click", pullUsage);

    /* P6 */
    $("#setAutostart").addEventListener("change", (e) => {
      state.settings.autostart = e.target.checked;
      renderNav();
      toast("info", e.target.checked ? "开机自启已开启" : "开机自启已关闭", "原型演示，不写入注册表。");
    });
    $("#setTray").addEventListener("change", (e) => {
      state.settings.tray = e.target.checked;
      toast("info", e.target.checked ? "关闭窗口将收至托盘" : "关闭窗口将直接退出程序",
        e.target.checked ? "后台继续自动签到。" : "后台自动签到会随程序退出而停止。");
    });
    $("#setDevice").addEventListener("change", (e) => {
      state.settings.device = e.target.checked;
      toast("info", e.target.checked ? "新账号优先使用真实设备号" : "新账号使用独立生成的设备号", "每个账号的设备号均独立保存，共用会被服务端每日去重。");
    });

    /* G3 登录窗 */
    $("#btnLoginDemo").addEventListener("click", loginSuccess);
    $("#btnLoginSubmit").addEventListener("click", loginSuccess);
    $("#btnLoginClose").addEventListener("click", closeLogin);
    $("#btnSendCode").addEventListener("click", sendCode);
    $("#loginOverlay").addEventListener("click", (e) => {
      if (e.target === $("#loginOverlay")) { closeLogin(); return; }
      const tab = e.target.closest(".lc-tab");
      if (!tab) return;
      $$(".lc-tab", $("#loginOverlay")).forEach((t) => t.classList.toggle("active", t === tab));
      if (state.login.phase === "waiting" || state.login.phase === "init") {
        $("#loginStatus").textContent = tab.textContent.includes("扫码")
          ? "等待扫码 · 请使用 Trae App 扫描二维码"
          : "等待登录 · 请在浏览器内完成手机号登录";
      }
    });

    /* G4 向导 */
    $("#wizardClose").addEventListener("click", cancelWizard);
    $("#wizardOverlay").addEventListener("click", (e) => {
      if (e.target === $("#wizardOverlay")) cancelWizard();
    });
    $("#wizardFooter").addEventListener("click", (e) => {
      const btn = e.target.closest("[data-wact]");
      if (!btn || btn.disabled) return;
      const act = btn.dataset.wact;
      if (act === "cancel") cancelWizard();
      else if (act === "prev") wizardGo(Math.max(1, state.wizard.step - 1));
      else if (act === "next") wizardGo(state.wizard.step + 1);
      else if (act === "confirm") wizardGo(5);
      else if (act === "done") closeWizard();
    });
    $("#wizardBody").addEventListener("change", (e) => {
      if (e.target.matches("[data-wsim]")) {
        state.wizard.confirmed = e.target.checked;
        renderWizard();
      }
    });

    /* G5 对话框 / G2 托盘 */
    $("#dialogOverlay").addEventListener("click", (e) => {
      if (e.target === $("#dialogOverlay")) closeDialog();
    });
    $("#btnTrayRestore").addEventListener("click", restoreTray);

    /* 全局委托 */
    document.addEventListener("click", onAction);
    document.addEventListener("change", (e) => {
      const box = e.target.closest('[data-act="acct-toggle"]');
      if (!box) return;
      toggleAccount(box.dataset.id, box.checked);
    });
    document.addEventListener("keydown", (e) => {
      if (e.key !== "Escape") return;
      const pop = document.getElementById("selectPop");
      if (pop && !pop.hidden) { closeSelectPop(); return; }
      if (!$("#dialogOverlay").hidden) { closeDialog(); return; }
      if (!$("#wizardOverlay").hidden) { cancelWizard(); return; }
      if (!$("#loginOverlay").hidden) { closeLogin(); return; }
      if (!$("#specPanel").hidden) $("#specPanel").hidden = true;
    });

    initSelects();
  }

  /* ═════════ 启动 ═════════ */
  function init() {
    initState();
    bindEvents();
    syncDemoButtons();
    renderAll();
    const start = window.location.hash.replace("#", "");
    go(PAGES.includes(start) ? start : "dashboard");
  }

  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", init);
  else init();
})();
