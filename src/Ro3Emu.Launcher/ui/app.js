// RO3Emu Launcher page. The host (MainWindow + LauncherController) posts state/progress/server/settings/news messages and
// receives {cmd} messages. Opened in a plain browser the page runs against a mock host: index.html?mock=install|update|
// ready|downloading|repair|offline (&tab=news|settings).
(function () {
  "use strict";
  var $ = function (id) { return document.getElementById(id); };
  var host;
  var state = { phase: "checking" };
  var busy = false;
  var stage = null;

  var DEFAULT_NEWS = [
    { tag: "歡迎", color: "blue", title: "歡迎來到 Ragnarok 3", text: "這是我們自己的 Ragnarok Online 3 伺服器。目前仍在持續開發中，若遇到問題請協助回報。" },
    { tag: "帳號", color: "green", title: "不需事先註冊", text: "在遊戲中輸入任意帳號與密碼，第一次登入時會自動建立帳號。" },
    { tag: "更新", color: "orange", title: "自動更新遊戲", text: "新版本會由此自動下載；如果下載中斷，下次可以從中斷處繼續。" },
  ];
  var COLORS = { blue: "var(--blue)", green: "var(--green)", orange: "var(--orange)", red: "var(--red)" };

  function send(o) { host.post(o); }
  function size(b) { return b >= 1073741824 ? (b / 1073741824).toFixed(2) + " GB" : Math.round(b / 1048576) + " MB"; }
  function eta(s) {
    if (!(s > 0) || !isFinite(s)) return "";
    s = Math.round(s);
    if (s >= 3600) return Math.floor(s / 3600) + " 小時 " + Math.floor(s % 3600 / 60) + " 分";
    if (s >= 60) return Math.floor(s / 60) + " 分 " + (s % 60) + " 秒";
    return s + " 秒";
  }

  // phase -> active step (1 install, 2 verify, 3 ready), button label/icon, default message
  var PHASES = {
    checking: { step: 0, button: "檢查中…", icon: "download", wait: true, text: "正在檢查更新…" },
    install: { step: 1, button: "安裝遊戲", icon: "download", text: "尚未安裝遊戲，需要下載完整遊戲檔案。" },
    update: { step: 1, button: "立即更新", icon: "download", text: "有新的遊戲版本可以更新。" },
    updating: { step: 1, button: "暫停下載", icon: null, wait: true, text: "正在更新遊戲檔案…" },
    repair: { step: 2, button: "修復遊戲", icon: "repair", text: "部分遊戲檔案與目前版本不一致。", warn: true },
    ready: { step: 3, button: "開始遊戲", icon: null, text: "已更新至最新版本，祝您遊戲愉快！" },
    offline: { step: 0, button: "重新連線", icon: "repair", text: "無法連線至更新伺服器。", warn: true },
    error: { step: 0, button: "再試一次", icon: "repair", text: "發生未預期的錯誤。", warn: true },
    launching: { step: 3, button: "啟動中…", icon: null, wait: true, text: "正在啟動遊戲…" },
    running: { step: 3, button: "開始遊戲", icon: null, text: "遊戲目前正在執行。" },
  };
  var STAGES = { scanning: "檢查檔案", downloading: "下載中", assembling: "組裝檔案", verifying: "驗證中", committing: "儲存中" };

  function setSteps(active) {
    for (var i = 1; i <= 3; i++) {
      $("step-" + i).className = "step" + (i < active ? " done" : i === active ? " active" : "");
    }
  }

  function render() {
    var p = PHASES[state.phase] || PHASES.checking;
    $("progress").hidden = !busy;
    $("message").hidden = busy;
    if (busy) setSteps(stage === "verifying" || stage === "committing" ? 2 : 1);
    else setSteps(p.step);
    var msg = $("message");
    msg.className = "message" + (p.warn ? " warn" : "");
    $("message-text").textContent = state.message || p.text;

    var btn = $("primary"), ico = $("primary-icon");
    if (busy) {
      btn.disabled = false;
      $("primary-label").textContent = "暫停下載";
      ico.hidden = true;
    } else {
      btn.disabled = !!p.wait;
      $("primary-label").textContent = p.button;
      ico.hidden = !p.icon;
      // Button icons: drop PNGs into ui/assets/ui/<name>.png and set ico.src here.
    }
    $("repair").disabled = busy || !state.installed || !!p.wait;
    $("set-dir-change").disabled = busy;

    var v = "啟動器 " + (state.launcher || "") ;
    if (state.installed && state.remote && state.installed !== state.remote) v += " · 遊戲版本 " + state.installed + " → " + state.remote;
    else if (state.installed || state.remote) v += " · 遊戲版本 " + (state.installed || state.remote);
    $("ver-line").textContent = v;
  }

  function renderProgress(m) {
    stage = m.stage;
    $("p-stage").textContent = STAGES[m.stage] || "處理中";
    var f = m.total > 0 ? Math.min(1, m.done / m.total) : 0;
    $("p-pct").textContent = Math.floor(f * 100) + "%";
    $("p-fill").style.width = (f * 100).toFixed(2) + "%";
    $("p-bytes").textContent = m.total > 0 ? size(m.done) + " / " + size(m.total) : "";
    $("p-rate").textContent = m.stage === "downloading" && m.rate > 0 ? (m.rate / 1048576).toFixed(1) + " MB/s · 剩餘 " + eta(m.eta) : "";
  }

  function renderNews(items) {
    var list = $("news-list");
    list.innerHTML = "";
    (items && items.length ? items : DEFAULT_NEWS).forEach(function (n) {
      var li = document.createElement("li");
      li.style.setProperty("--c", COLORS[n.color] || COLORS.blue);
      var tag = document.createElement("span"); tag.className = "tag"; tag.textContent = n.tag || "公告";
      var b = document.createElement("b"); b.textContent = n.title;
      var p = document.createElement("p"); p.textContent = n.text || "";
      li.appendChild(tag); li.appendChild(b); li.appendChild(p);
      list.appendChild(li);
    });
  }

  function setBrand(name) {
    if (typeof name !== "string" || !name.trim()) return;
    name = name.trim();
    $("brand").textContent = name;
    $("realm-name").textContent = name + " · 私人測試伺服器";
    document.title = name;
  }

  function showTab(name) {
    document.querySelectorAll(".tab").forEach(function (t) { t.classList.toggle("active", t.dataset.tab === name); });
    $("settings").hidden = name !== "settings";
    $("notices").hidden = name === "settings";
    $("notices").classList.toggle("wide", name === "news");
  }

  function handle(m) {
    switch (m.type) {
      case "state":
        busy = m.phase === "updating";
        var launcher = state.launcher;
        if (!busy) { state = m; state.launcher = launcher; stage = null; }
        else {
          state.phase = m.phase;
          state.message = m.message;
          state.installed = m.installed || state.installed;
          state.remote = m.remote || state.remote;
        }
        render();
        break;
      case "progress":
        if (!busy) busy = true;
        renderProgress(m);
        render();
        break;
      case "server":
        $("server").className = "server " + (m.online ? "on" : "off");
        $("server-text").innerHTML = m.online ? "<b>伺服器上線</b>" + (m.players != null ? " · " + m.players + " 位玩家" : "") : "<b>伺服器離線</b>";
        break;
      case "settings":
        $("set-dir").textContent = m.dir;
        state.launcher = m.launcher;
        $("set-version").textContent = "啟動器版本 " + m.launcher;
        setBrand(m.serverName);
        render();
        break;
      case "news":
        renderNews(m.items);
        break;
    }
  }

  function bind() {
    $("drag").addEventListener("mousedown", function (e) {
      if (e.button === 0 && !e.target.closest("button")) send({ cmd: "window", action: "drag" });
    });
    $("btn-min").onclick = function () { send({ cmd: "window", action: "minimize" }); };
    $("btn-close").onclick = function () { send({ cmd: "window", action: "close" }); };
    document.querySelectorAll(".tab").forEach(function (t) { t.onclick = function () { showTab(t.dataset.tab); }; });
    $("primary").onclick = function () { send({ cmd: busy ? "pause" : "primary" }); };
    $("repair").onclick = function () { send({ cmd: "repair" }); };
    $("set-dir-change").onclick = function () { send({ cmd: "changeDir" }); };
    $("set-open").onclick = function () { send({ cmd: "openDir" }); };
  }

  // ---------- mock host for a plain browser ----------
  function createMockHost(scenario) {
    function emit(o) { setTimeout(function () { handle(o); }, 0); }
    var remote = "2026.10.09.1";
    return {
      post: function (c) {
        if (c.cmd !== "ready") return;
        emit({ type: "settings", dir: "C:\\Games\\Ragnarok3\\Client", launcher: "1.0.0", serverName: "RO3 私人伺服器" });
        emit({ type: "server", online: scenario !== "offline", players: 7 });
        if (scenario === "offline") return emit({ type: "state", phase: "offline" });
        if (scenario === "downloading") {
          emit({ type: "state", phase: "install", remote: remote });
          emit({ type: "progress", stage: "downloading", done: 6.3e9, total: 14.1e9, rate: 31.4 * 1048576, eta: 254 });
          return;
        }
        var phase = { install: "install", ready: "ready", repair: "repair" }[scenario] || "update";
        emit({ type: "state", phase: phase, installed: phase === "install" ? null : phase === "update" ? "2026.10.08.2" : remote, remote: remote });
      },
    };
  }

  function start() {
    var params = new URLSearchParams(location.search);
    var wv = window.chrome && window.chrome.webview;
    if (wv) {
      host = { post: function (o) { wv.postMessage(o); } };
      wv.addEventListener("message", function (e) { handle(e.data); });
      document.addEventListener("contextmenu", function (e) { e.preventDefault(); });
    } else {
      host = createMockHost(params.get("mock") || "update");
    }
    bind();
    renderNews(null);
    render();
    if (params.get("tab")) showTab(params.get("tab"));
    send({ cmd: "ready" });
  }

  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", start);
  else start();
})();
