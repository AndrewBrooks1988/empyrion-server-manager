"use strict";

// ------------------------------------------------------------------ helpers
const $ = (s, el = document) => el.querySelector(s);
const $$ = (s, el = document) => [...el.querySelectorAll(s)];
const esc = s => String(s ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

async function api(path, body) {
  const opt = body === undefined ? {} : {
    method: "POST",
    headers: { "Content-Type": "application/json", "X-Manager": "1" },
    body: JSON.stringify(body ?? {}),
  };
  let res;
  try { res = await fetch("/api" + path, opt); }
  catch { throw new Error("Can't reach the manager. Is EmpyrionManager.exe still running?"); }
  const data = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(data.error || `Request failed (${res.status})`);
  return data;
}

let toastTimer;
function toast(msg, isErr) {
  const t = $("#toast");
  t.textContent = msg; t.className = "toast" + (isErr ? " err" : ""); t.hidden = false;
  clearTimeout(toastTimer); toastTimer = setTimeout(() => (t.hidden = true), isErr ? 7000 : 3500);
}

const fmtTime = d => new Date(d).toLocaleTimeString([], { hour: "numeric", minute: "2-digit" });
const fmtDateTime = d => new Date(d).toLocaleString([], { weekday: "short", day: "numeric", month: "short", hour: "numeric", minute: "2-digit" });
function fmtDuration(ms) {
  const m = Math.max(0, Math.floor(ms / 60000));
  if (m < 60) return `${m}m`;
  const h = Math.floor(m / 60);
  return h < 24 ? `${h}h ${m % 60}m` : `${Math.floor(h / 24)}d ${h % 24}h`;
}
function fmtAgo(d) {
  const ms = Date.now() - new Date(d);
  if (ms < 60000) return "just now";
  return fmtDuration(ms) + " ago";
}
function uptimeText(u) { const m = /(\d+)h(\d+)m/.exec(u || ""); if (!m) return u || ""; const h = +m[1], mi = +m[2]; return h ? (h >= 24 ? `${Math.floor(h / 24)}d ${h % 24}h` : `${h}h ${mi}m`) : `${mi}m`; }

// ------------------------------------------------------------------ tabs
const tabs = $$('.tabs [role="tab"]');
function showTab(id) {
  tabs.forEach(t => {
    const on = t.id === id;
    t.setAttribute("aria-selected", on);
    $("#" + t.getAttribute("aria-controls")).hidden = !on;
  });
  try { localStorage.setItem("mgr-tab", id); } catch {}
  if (id === "tab-maintenance") loadMaintenance();
  if (id === "tab-backups") loadBackups();
  if (id === "tab-settings") loadSettings();
  if (id === "tab-console") scrollLog(true);
}
tabs.forEach(t => t.addEventListener("click", () => showTab(t.id)));
tabs.forEach((t, i) => t.addEventListener("keydown", e => {
  if (e.key !== "ArrowRight" && e.key !== "ArrowLeft") return;
  const n = tabs[(i + (e.key === "ArrowRight" ? 1 : tabs.length - 1)) % tabs.length];
  n.focus(); showTab(n.id);
}));

// ------------------------------------------------------------------ status
let status = null, tasks = [];
// true when the dashboard is opened from another device (e.g. through Tailscale) rather than on this PC
const isRemote = !["127.0.0.1", "localhost", "[::1]"].includes(location.hostname);

async function refreshStatus() {
  try { status = await api("/status"); }
  catch (e) { setPill("offline", "Manager offline"); return; }
  const s = status, c = s.config;
  const appTitle = s.setup?.title || "Empyrion Server Manager";
  document.title = c.name ? `${c.name} · ${appTitle}` : appTitle;
  $("#brand-title").textContent = appTitle;
  $("#setup-banner").hidden = !!s.setup?.configured;
  $("#srv-name").textContent = c.name || "Empyrion server";
  const busy = s.job && s.job.state === "running";
  setPill(busy ? "busy" : s.state, busy ? s.job.title : { online: "Online", starting: "Starting…", offline: "Offline" }[s.state]);
  $("#meta-uptime").textContent = s.info?.uptime ? "Up " + uptimeText(s.info.uptime) : s.server ? "Up " + fmtDuration(Date.now() - new Date(s.server.started)) : "";
  $("#meta-players").textContent = s.state === "offline" ? "" : `${s.players.length}/${c.maxPlayers} players`;
  $("#meta-scenario").textContent = `${c.scenario || "Default"} · port ${c.port}${c.hasPassword ? " · password" : " · open"}`;

  const running = s.state !== "offline";
  $("#btn-start").disabled = running || busy || !s.setup?.configured;
  $("#btn-restart").disabled = !running || busy;
  $("#btn-stop").disabled = !running || busy;
  $("#btn-play").hidden = isRemote;
  $("#btn-play").disabled = busy || s.gameClientRunning;
  $("#btn-play").title = s.gameClientRunning ? "The game is already running on this PC" : "Restart the server around launching the game on this PC, so Steam lets you play";
  $("#btn-backup").disabled = running || busy;

  // metrics
  const n = s.players.length;
  $("#m-players").textContent = running ? `${n} / ${c.maxPlayers}` : "–";
  $("#m-players-s").textContent = running ? (n ? s.players.map(p => p.name).join(", ") : "Nobody online") : "Server offline";
  const fps = s.info?.fps;
  const fpsEl = $("#m-fps");
  fpsEl.textContent = fps != null ? `${fps.toFixed(0)} fps` : "–";
  fpsEl.className = "v " + (fps == null ? "" : fps >= 35 ? "good" : fps >= 20 ? "meh" : "bad");
  $("#m-fps-s").textContent = s.info?.heapMB ? `Heap ${s.info.heapMB} MB · target 40 fps` : "Reported every minute";
  const pf = /r(\d+)\/i(\d+)\/a(\d+)/.exec(s.info?.playfields || "");
  $("#m-pfs").textContent = pf ? `${pf[3]} active` : running ? "–" : "–";
  $("#m-pfs-s").textContent = pf ? `${pf[2]} idle · ${s.playfieldProcesses.length} process${s.playfieldProcesses.length === 1 ? "" : "es"}` : " ";
  $("#m-load").textContent = running ? `${s.totals.cpuPercent}% CPU` : "–";
  $("#m-load-s").textContent = running ? `${(s.totals.memoryMB / 1024).toFixed(1)} GB RAM across ${1 + s.playfieldProcesses.length} processes` : " ";

  renderPlayers(s);
  renderJobFromStatus(s.job);
  $("#log-file").textContent = s.logFile ? `Reading ${s.logFile}` : "";
}

function setPill(state, text) {
  const p = $("#state-pill");
  p.dataset.state = state; p.textContent = text;
}

function renderPlayers(s) {
  const box = $("#players");
  $("#players-count").textContent = s.state === "offline" ? "" : `${s.players.length} of ${s.config.maxPlayers}`;
  if (s.state === "offline") { box.innerHTML = `<p class="empty">The server is offline.</p>`; return; }
  if (!s.players.length) { box.innerHTML = `<p class="empty">Nobody's online right now.</p>`; return; }
  box.innerHTML = s.players.map(p => `
    <div class="player">
      <div class="who">
        <span class="name">${esc(p.name)}</span>
        <span class="sub">${esc(p.playfield || "Loading in…")} · online ${fmtDuration(Date.now() - new Date(p.since))} · ${esc(p.steamId)}</span>
      </div>
      <div class="acts">
        <button class="btn sm" data-act="message" data-id="${esc(p.steamId)}" data-name="${esc(p.name)}">Message</button>
        <button class="btn sm" data-act="kick" data-id="${esc(p.steamId)}" data-name="${esc(p.name)}">Kick</button>
        <button class="btn sm" data-act="mute" data-id="${esc(p.steamId)}" data-name="${esc(p.name)}">Mute</button>
        <button class="btn sm danger" data-act="ban" data-id="${esc(p.steamId)}" data-name="${esc(p.name)}">Ban</button>
      </div>
    </div>`).join("");
}

async function refreshActivity() {
  try {
    const items = await api("/activity");
    const ol = $("#activity");
    ol.innerHTML = items.length ? items.map(a => `
      <li class="${esc(a.kind)}"><time datetime="${esc(a.time)}">${fmtTime(a.time)}</time><span class="dot"></span><span>${esc(a.text)}</span></li>`).join("")
      : `<li class="muted">Nothing yet. Joins, leaves and restarts appear here.</li>`;
  } catch {}
}

// ------------------------------------------------------------------ jobs
let jobId = null, jobLineCount = 0, jobOpen = false, dismissedJob = null;

function renderJobFromStatus(job) {
  if (!job || job.id === dismissedJob) { $("#jobbar").hidden = true; return; }
  if (job.id !== jobId) { jobId = job.id; jobLineCount = 0; $("#job-output").textContent = ""; }
  const bar = $("#jobbar");
  bar.hidden = false; bar.dataset.state = job.state;
  $("#job-state").textContent = { running: "Working", succeeded: "Done", failed: "Failed" }[job.state];
  $("#job-title").textContent = job.title;
  $("#job-time").textContent = job.ended ? `${fmtTime(job.started)} – ${fmtTime(job.ended)}` : `started ${fmtTime(job.started)}`;
  $("#job-dismiss").hidden = job.state === "running";
}

async function refreshJob() {
  if (!jobId) return;
  try {
    const d = await api(`/job?from=${jobLineCount}`);
    if (!d.job || d.job.id !== jobId) return;
    if (d.lines.length) {
      jobLineCount += d.lines.length;
      const out = $("#job-output");
      out.textContent += d.lines.join("\n") + "\n";
      out.scrollTop = out.scrollHeight;
      $("#job-last").textContent = d.lines[d.lines.length - 1];
    }
    if (d.job.state !== "running" && status?.job?.state === "running") refreshStatus();
  } catch {}
}

$("#job-toggle").addEventListener("click", () => {
  jobOpen = !jobOpen;
  $("#job-output").hidden = !jobOpen;
  $("#job-toggle").textContent = jobOpen ? "Hide output" : "Show output";
  $("#job-toggle").setAttribute("aria-expanded", jobOpen);
});
$("#job-dismiss").addEventListener("click", () => { dismissedJob = jobId; $("#jobbar").hidden = true; });

async function startJob(path, body, label) {
  try {
    const r = await api(path, body);
    dismissedJob = null; jobId = r.id; jobLineCount = 0; $("#job-output").textContent = ""; $("#job-last").textContent = "";
    toast(label);
    await refreshStatus();
  } catch (e) { toast(e.message, true); }
}

// ------------------------------------------------------------------ server controls
const powerText = {
  restart: ["Restart server", "Saves the world, stops the server and starts it again. Players are disconnected for a few minutes.", "Restart"],
  stop: ["Stop server", "Saves the world and shuts the server down. It stays off until you start it again, and the 4am maintenance skips while it's off.", "Stop server"],
  daily: ["Run daily maintenance now", "Restart, back up the world, regenerate ore deposits in the starter systems, and respawn asteroids and POIs in every visited space sector.", "Run now"],
  weekly: ["Run weekly reset now", "Restart, back up the world and reset POIs, ore deposits and terrain on every visited planet. Bases are safe.", "Run now"],
};

function askPower(kind) {
  const [title, desc, ok] = powerText[kind];
  $("#power-title").textContent = title; $("#power-desc").textContent = desc; $("#power-ok").textContent = ok;
  $("#power-ok").className = "btn " + (kind === "stop" ? "danger" : "primary");
  const n = status?.players?.length || 0;
  $$('#dlg-power input[name=warn]').forEach(r => (r.checked = r.value === (n ? "5" : "0")));
  const dlg = $("#dlg-power");
  dlg.returnValue = "";
  dlg.onclose = () => {
    if (dlg.returnValue !== "ok") return;
    const warn = +$('#dlg-power input[name=warn]:checked').value;
    const path = kind === "daily" || kind === "weekly" ? `/maintenance/${kind}` : `/server/${kind}`;
    startJob(path, { warn }, warn ? `Warning players – ${title.toLowerCase()} in ${warn} min` : `${title}…`);
  };
  dlg.showModal();
}

$("#btn-start").addEventListener("click", () => startJob("/server/start", {}, "Starting the server…"));
$("#btn-restart").addEventListener("click", () => askPower("restart"));
$("#btn-stop").addEventListener("click", () => askPower("stop"));
$("#btn-play").addEventListener("click", () => {
  const running = status?.state !== "offline";
  const [t, d] = running
    ? ["Play on this PC", "Warns players (1 minute), stops the server, launches Empyrion through Steam, then starts the server again once the game is open. About 2–3 minutes down."]
    : ["Play on this PC", "The server is off, so this just launches Empyrion through Steam."];
  $("#power-title").textContent = t; $("#power-desc").textContent = d; $("#power-ok").textContent = "Launch game";
  $("#power-ok").className = "btn primary";
  $(".warn-pick").hidden = true;
  const dlg = $("#dlg-power");
  dlg.returnValue = "";
  dlg.onclose = () => { $(".warn-pick").hidden = false; if (dlg.returnValue === "ok") startJob("/server/play", {}, "Launching the game…"); };
  dlg.showModal();
});

// ------------------------------------------------------------------ players
const playerActions = {
  message: { title: n => `Message ${n}`, desc: "Sends a private chat message only they can see.", text: "Message", field: "text", ok: "Send", cls: "primary", done: n => `Message sent to ${n}` },
  kick: { title: n => `Kick ${n}`, desc: "Disconnects them. They can rejoin straight away.", text: "Reason (shown to the player)", field: "reason", ok: "Kick", cls: "danger", done: n => `Kicked ${n}` },
  mute: { title: n => `Mute ${n}`, desc: "Stops them using chat for the chosen time.", duration: true, ok: "Mute", cls: "danger", done: n => `Muted ${n}` },
  ban: { title: n => `Ban ${n}`, desc: "Disconnects them and blocks them from rejoining for the chosen time.", duration: true, ok: "Ban", cls: "danger", done: n => `Banned ${n}` },
};

$("#players").addEventListener("click", e => {
  const b = e.target.closest("button[data-act]");
  if (!b) return;
  const kind = b.dataset.act, a = playerActions[kind], id = b.dataset.id, name = b.dataset.name;
  $("#player-title").textContent = a.title(name);
  $("#player-desc").textContent = a.desc;
  $("#player-reason-wrap").hidden = !!a.duration;
  $("#player-duration-wrap").hidden = !a.duration;
  $("#player-reason-label").textContent = a.text || "";
  $("#player-reason").placeholder = kind === "kick" ? "Kicked by admin" : "";
  $("#player-reason").value = "";
  $("#player-ok").textContent = a.ok; $("#player-ok").className = "btn " + a.cls;
  const dlg = $("#dlg-player");
  dlg.returnValue = "";
  dlg.onclose = async () => {
    if (dlg.returnValue !== "ok") return;
    const body = a.duration ? { duration: $("#player-duration").value } : { [a.field]: $("#player-reason").value };
    try {
      const r = await api(`/players/${id}/${kind}`, body);
      const reply = (r.output || "").split("\n")[0];
      toast(reply ? `${a.done(name)} · server: ${reply}` : a.done(name));
      setTimeout(() => { refreshStatus(); refreshPeople(); }, 1500);
    } catch (err) { toast(err.message, true); }
  };
  dlg.showModal();
  if (!a.duration) $("#player-reason").focus();
});

// ------------------------------------------------------------------ known players + bans
async function refreshPeople() {
  if (!status || status.state !== "online") {
    $("#known").innerHTML = `<p class="empty">Shown while the server is online.</p>`;
    $("#bans").innerHTML = `<p class="empty">Shown while the server is online.</p>`;
    return;
  }
  try {
    const [{ players }, history] = await Promise.all([api("/known-players"), api("/player-history").catch(() => [])]);
    const online = new Set(status.players.map(p => p.name));
    const byName = new Map(history.map(h => [h.name, h]));
    const seen = h => h ? (online.has(h.name) ? "online now" : `last seen ${fmtAgo(h.lastSeen)}`) : "";
    const spawned = new Set(players.map(p => p.name));
    const pending = history.filter(h => h.entityId == null && !spawned.has(h.name));
    $("#known").innerHTML = (players.length ? players.map(p => `
      <div class="kp ${online.has(p.name) ? "on" : ""}"><span class="n">${esc(p.name)}</span><span class="f" title="Faction and faction role">${esc(p.faction)} · ${esc(p.role)}</span>
        <span class="kp-sub">${esc(seen(byName.get(p.name)))}</span>
        ${p.steamId ? `<select class="role-pick" title="Server permission" aria-label="Server role for ${esc(p.name)}" data-sid="${esc(p.steamId)}" data-name="${esc(p.name)}" data-eid="${p.entityId}" data-was="${p.permission}">
          ${[[0, "Player"], [3, "GameMaster"], [6, "Moderator"], [9, "Admin"]].map(([v, n]) => `<option value="${v}" ${v === p.permission ? "selected" : ""}>${n}</option>`).join("")}</select>` : ""}</div>`).join("")
      : `<p class="empty">Nobody has spawned in yet.</p>`)
      + (pending.length ? `
      <div class="kp-head">Joined but never spawned</div>` + pending.map(h => `
      <div class="kp pending ${online.has(h.name) ? "on" : ""}"><span class="n">${esc(h.name)}</span><span class="f">${h.visits} visit${h.visits === 1 ? "" : "s"}</span>
        <span class="kp-sub">${esc(h.lastVisit || "")} · ${esc(seen(h))}</span></div>`).join("") : "");
  } catch {}
  try {
    const { bans } = await api("/bans");
    $("#bans").innerHTML = bans.length ? bans.map(b => `
      <div class="ban"><span><span class="mono">${esc(b.steamId)}</span><br><span class="muted small">until ${esc(b.until)}</span></span>
      <button class="btn sm" data-unban="${esc(b.steamId)}">Unban</button></div>`).join("")
      : `<p class="empty">Nobody is banned.</p>`;
  } catch {}
}
$("#known").addEventListener("change", async e => {
  const sel = e.target.closest("select.role-pick");
  if (!sel) return;
  const names = { 0: "player", 3: "gamemaster", 6: "moderator", 9: "admin" }, labels = { 0: "Player", 3: "GameMaster", 6: "Moderator", 9: "Admin" };
  const perm = +sel.value, name = sel.dataset.name;
  const ok = await confirmBox(`Make ${name} ${labels[perm]}?`,
    perm === 0 ? `${name} loses any special permissions.` : `${name} gets ${labels[perm]} permissions on this server (console commands for that level).`,
    "Change role", perm === 9);
  if (!ok) { sel.value = sel.dataset.was; return; }
  try {
    const r = await api(`/players/${sel.dataset.sid}/role`, { role: names[perm], name, entityId: +sel.dataset.eid });
    toast(r.message + (r.output ? " · server: " + r.output : ""));
    sel.dataset.was = String(perm);
  } catch (err) { toast(err.message, true); sel.value = sel.dataset.was; }
});

async function unban(id) {
  try { await api(`/bans/${id}/unban`, {}); toast(`Unbanned ${id}`); refreshPeople(); }
  catch (e) { toast(e.message, true); }
}
$("#bans").addEventListener("click", e => { const b = e.target.closest("button[data-unban]"); if (b) unban(b.dataset.unban); });
$("#unban-form").addEventListener("submit", e => {
  e.preventDefault();
  const v = $("#unban-id").value.trim();
  if (!/^\d{17}$/.test(v)) { toast("Enter a 17-digit SteamID64.", true); return; }
  unban(v); $("#unban-id").value = "";
});

$("#say-form").addEventListener("submit", async e => {
  e.preventDefault();
  const input = $("#say-text"), st = $("#say-status");
  const text = input.value.trim();
  if (!text) { st.className = "form-status err"; st.textContent = "Type a message first."; return; }
  st.className = "form-status"; st.textContent = "Sending…";
  try {
    await api("/say", { text });
    input.value = "";
    st.textContent = "";
    refreshChat();
  } catch (err) { st.className = "form-status err"; st.textContent = err.message; }
});

// ------------------------------------------------------------------ chat feed
let chatSig = "";
async function refreshChat() {
  let entries;
  try { entries = await api("/chat"); } catch { return; }
  const sig = entries.length + "|" + (entries.at(-1)?.time || "") + (entries.at(-1)?.text || "");
  if (sig === chatSig) return;
  chatSig = sig;
  const box = $("#chat");
  const atBottom = box.scrollHeight - box.scrollTop - box.clientHeight < 30;
  const label = m => ({
    global: "Global", faction: "Faction", alliance: "Alliance", server: "To admins",
    private: `Private → ${m.to}`, dashboard: "To everyone",
  }[m.channel] || m.to);
  box.innerHTML = entries.length ? entries.map(m => `
    <li class="${esc(m.source)} ch-${esc(m.channel)}"><time>${m.time ? fmtTime(m.time) : ""}</time>
      <span><span class="who">${esc(m.from)}</span><span class="badge">${esc(label(m))}</span><br>${esc(m.text)}</span></li>`).join("")
    : `<li class="empty">No messages yet. Messages you send from here appear in this list.</li>`;
  if (atBottom || entries.length) box.scrollTop = box.scrollHeight;
}

// ------------------------------------------------------------------ console
const consoleOut = $("#console-out");
const history = []; let histPos = 0;
async function runCommand(cmd) {
  if (!cmd.trim()) return;
  history.push(cmd); histPos = history.length;
  const block = document.createElement("div");
  block.innerHTML = `<div class="cmd">&gt; ${esc(cmd)}</div><div class="muted">…</div>`;
  consoleOut.appendChild(block); consoleOut.scrollTop = consoleOut.scrollHeight;
  try {
    const r = await api("/console", { text: cmd });
    block.lastElementChild.className = "";
    block.lastElementChild.textContent = r.output || "(no output)";
  } catch (e) {
    block.lastElementChild.className = "err";
    block.lastElementChild.textContent = e.message;
  }
  consoleOut.scrollTop = consoleOut.scrollHeight;
}
$("#console-form").addEventListener("submit", e => {
  e.preventDefault();
  const i = $("#console-cmd"); runCommand(i.value); i.value = "";
});
$("#console-cmd").addEventListener("keydown", e => {
  if (e.key === "ArrowUp" && histPos > 0) { e.target.value = history[--histPos]; e.preventDefault(); }
  if (e.key === "ArrowDown") { histPos = Math.min(history.length, histPos + 1); e.target.value = history[histPos] || ""; e.preventDefault(); }
});
$$(".chip[data-cmd]").forEach(c => c.addEventListener("click", () => runCommand(c.dataset.cmd)));

// ------------------------------------------------------------------ log
const logBox = $("#log");
let logSeq = 0, logLines = [], logFilter = "useful";
try { logFilter = localStorage.getItem("mgr-logfilter") || "useful"; } catch {}

function keepLine(l, q) {
  if (logFilter === "useful" && l.kind === "routine") return false;
  if (logFilter === "useful" && /Thread '|TelnetClient|Spreading:|Raknet|^\S+ \S+ -LOG- \s*$/.test(l.text)) return false;
  if (logFilter === "player" && l.kind !== "player" && l.kind !== "system") return false;
  if (logFilter === "problems" && l.kind !== "error" && l.kind !== "warn" && l.kind !== "system") return false;
  return !q || l.text.toLowerCase().includes(q);
}
function lineHtml(l) {
  const body = l.text.replace(/^\d\d-\d\d:\d\d:\d\d\.\d{3}\s+\S+\s+/, "");
  const t = l.time ? `<span class="t">${new Date(l.time).toLocaleTimeString([], { hour12: false })}</span>` : "";
  return `<div class="ln ${l.kind}">${t}${esc(body)}</div>`;
}
function renderLog(full) {
  const q = $("#log-search").value.trim().toLowerCase();
  if (full) logBox.innerHTML = logLines.filter(l => keepLine(l, q)).slice(-1500).map(lineHtml).join("");
  scrollLog();
}
function scrollLog(force) { if (force || $("#log-follow").checked) logBox.scrollTop = logBox.scrollHeight; }

async function refreshLog() {
  try {
    const lines = await api(`/log?after=${logSeq}&max=${logSeq ? 800 : 2000}`);
    if (!lines.length) return;
    logSeq = lines[lines.length - 1].seq;
    logLines.push(...lines);
    if (logLines.length > 4000) logLines = logLines.slice(-4000);
    const q = $("#log-search").value.trim().toLowerCase();
    logBox.insertAdjacentHTML("beforeend", lines.filter(l => keepLine(l, q)).map(lineHtml).join(""));
    while (logBox.childElementCount > 1500) logBox.firstElementChild.remove();
    scrollLog();
  } catch {}
}
$$(".seg button[data-filter]").forEach(b => {
  b.setAttribute("aria-pressed", b.dataset.filter === logFilter);
  b.addEventListener("click", () => {
    logFilter = b.dataset.filter;
    try { localStorage.setItem("mgr-logfilter", logFilter); } catch {}
    $$(".seg button[data-filter]").forEach(x => x.setAttribute("aria-pressed", x === b));
    renderLog(true);
  });
});
$("#log-search").addEventListener("input", () => renderLog(true));
logBox.addEventListener("scroll", () => {
  const atBottom = logBox.scrollHeight - logBox.scrollTop - logBox.clientHeight < 30;
  $("#log-follow").checked = atBottom;
});

// ------------------------------------------------------------------ maintenance
async function refreshTasks() {
  try { tasks = await api("/tasks"); } catch { return; }
  const upcoming = tasks.filter(t => t.enabled && t.nextRun);
  const next = upcoming.map(t => ({ t, d: parseTaskDate(t.nextRun) })).filter(x => x.d).sort((a, b) => a.d - b.d)[0];
  const restartAt = next ? new Date(next.d.getTime() + (next.t.warningMinutes || 0) * 60000) : null;
  $("#m-next").textContent = restartAt ? fmtTime(restartAt) : tasks.some(t => t.exists) ? "Paused" : "Not set up";
  $("#m-next-s").textContent = next ? `${next.t.key === "weekly" ? "Weekly reset" : "Daily restart"} · ${restartAt.toLocaleDateString([], { weekday: "short", day: "numeric", month: "short" })} · warnings from ${fmtTime(next.d)}` : " ";
}
// task times arrive as ISO 8601 from the manager
function parseTaskDate(s) { if (!s) return null; const d = new Date(s); return isNaN(d) ? null : d; }

const DAYS = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
const WIPES = ["poi", "deposit", "terrain"];
let schedData = null;

async function loadMaintenance() {
  await refreshTasks();
  renderTasks();
  loadSchedule();
  try {
    const lines = await api("/maintenance/log");
    $("#mlog").innerHTML = lines.map(l => `<div class="ln ${/ERROR|FAILED/.test(l) ? "error" : /WARNING/.test(l) ? "warn" : /====/.test(l) ? "system" : ""}">${esc(l)}</div>`).join("")
      || `<div class="muted">No maintenance has run yet.</div>`;
  } catch {}
}

function renderTasks() {
  $("#tasks").innerHTML = tasks.map(t => {
    const title = t.key === "weekly" ? "Weekly reset" : "Daily maintenance";
    const state = !t.exists ? `<span class="tag warn">Not installed</span>`
      : !t.upToDate ? `<span class="tag warn">Needs update</span>`
      : `<label class="switch"><input type="checkbox" data-task="${t.key}" ${t.enabled ? "checked" : ""}> ${t.enabled ? "On" : "Paused"}</label>`;
    return `
    <div class="panel task">
      <div class="panel-head"><h2>${title}</h2>${state}</div>
      <p class="muted small" style="margin:0">${esc(t.description)}</p>
      ${t.problem ? `<p class="form-status err" style="margin:0">${esc(t.problem)}</p>` : ""}
      <dl>
        <dt>Task</dt><dd class="mono small">${esc(t.name)}</dd>
        ${t.exists ? `
        <dt>Schedule</dt><dd>${esc(t.schedule || "–")}</dd>
        <dt>Next restart</dt><dd>${t.enabled ? esc(fmtRestart(t)) : "Paused"}</dd>
        <dt>Last run</dt><dd>${esc(fmtTaskDate(t.lastRun))}${t.lastResult ? ` <span class="muted">· ${esc(t.lastResult)}</span>` : ""}</dd>` : `
        <dt>Status</dt><dd>Not in Task Scheduler yet. Install it to run automatically.</dd>`}
      </dl>
      <div class="row">
        <span class="btn-row">
          <button class="btn sm ${!t.exists || !t.upToDate ? "primary" : ""}" data-tinstall="${t.key}">${t.exists ? (t.upToDate ? "Reinstall" : "Update task") : "Install task"}</button>
          ${t.exists ? `<button class="btn sm danger" data-tremove="${t.key}">Remove</button>` : ""}
        </span>
        <button class="btn sm" data-run="${t.key}">Run now…</button>
      </div>
    </div>`;
  }).join("");
}

function fmtTaskDate(s) { const d = parseTaskDate(s); return d ? fmtDateTime(d) : "Never"; }
function fmtRestart(t) {
  const d = parseTaskDate(t.nextRun);
  if (!d) return "–";
  const at = new Date(d.getTime() + (t.warningMinutes || 0) * 60000);
  return `${fmtDateTime(at)} (warnings from ${fmtTime(d)})`;
}

$("#tasks").addEventListener("change", async e => {
  const cb = e.target.closest("input[data-task]");
  if (!cb) return;
  try {
    await api(`/tasks/${cb.dataset.task}/${cb.checked ? "enable" : "disable"}`, {});
    toast(cb.checked ? "Schedule turned on" : "Schedule paused");
  } catch (err) { toast(err.message, true); cb.checked = !cb.checked; }
  loadMaintenance();
});
$("#tasks").addEventListener("click", async e => {
  const run = e.target.closest("button[data-run]");
  if (run) { askPower(run.dataset.run); return; }
  const inst = e.target.closest("button[data-tinstall]"), rem = e.target.closest("button[data-tremove]");
  if (!inst && !rem) return;
  const key = (inst || rem).dataset.tinstall || (inst || rem).dataset.tremove;
  (inst || rem).disabled = true;
  try {
    await api(`/tasks/${key}/${inst ? "install" : "remove"}`, {});
    toast(inst ? "Task installed in Task Scheduler" : "Task removed");
  } catch (err) { toast(err.message, true); }
  await refreshTasks(); renderTasks();
});

// schedule & reset settings form (stored in manager-settings.json)
async function loadSchedule() {
  try { schedData = await api("/maintenance/settings"); } catch (e) { $("#sched-form").innerHTML = `<p class="form-status err">${esc(e.message)}</p>`; return; }
  const m = schedData.maintenance, tk = schedData.tasks;
  const dayCell = d => {
    const v = m.dailyDays.includes(d) ? "daily" : m.weeklyDays.includes(d) ? "weekly" : "off";
    return `<label class="day"><span>${d}</span><select name="day-${d}" id="day-${d}">
      <option value="daily" ${v === "daily" ? "selected" : ""}>Daily</option>
      <option value="weekly" ${v === "weekly" ? "selected" : ""}>Weekly</option>
      <option value="off" ${v === "off" ? "selected" : ""}>Off</option></select></label>`;
  };
  const wipeBoxes = (name, value) => WIPES.map(w =>
    `<label class="check"><input type="checkbox" name="${name}" value="${w}" ${value.split(/\s+/).includes(w) ? "checked" : ""}> ${w}</label>`).join("");
  const systems = schedData.solarSystems;
  $("#sched-summary").textContent = `Tasks start at ${schedData.taskStartTime}${schedData.taskStartTime2 ? " and " + schedData.taskStartTime2 : ""} so restarts land at ${m.restartTime}${m.twiceDaily ? " and 12 hours later" : ""}.`;
  $("#sched-form").innerHTML = `
    <div class="fgrid">
      <label class="field" for="s-restart">Restart time<input id="s-restart" type="time" value="${esc(m.restartTime)}" required>
        <small>Players are warned before this; the task starts earlier by the longest warning.</small></label>
      <label class="field" for="s-warn">Warnings (minutes before)<input id="s-warn" type="text" value="${esc(m.warnMinutes.join(", "))}">
        <small>Comma-separated, e.g. 15, 10, 5, 1</small></label>
      <label class="field" for="s-backups">Backups to keep<input id="s-backups" type="number" min="1" max="365" value="${m.backupsToKeep}">
        <small>One backup per maintenance run. Manual backups are never pruned.</small></label>
    </div>
    <fieldset class="fset"><legend>Which run on which day</legend><div class="days">${DAYS.map(dayCell).join("")}</div>
      <label class="check"><input type="checkbox" id="s-twice" ${m.twiceDaily ? "checked" : ""}> Twice a day: run the daily maintenance again 12 hours after the restart time, every day (including the weekly day)</label></fieldset>
    <div class="fgrid">
      <fieldset class="fset"><legend>Daily: starter systems</legend><div class="checks">${wipeBoxes("w-starter", m.dailyStarterWipe)}</div>
        <small class="muted">Visited playfields in the starter systems below.</small></fieldset>
      <fieldset class="fset"><legend>Daily: all space sectors</legend><div class="checks">${wipeBoxes("w-space", m.dailySpaceWipe)}</div>
        <small class="muted">Every visited space playfield. <b>poi</b> respawns asteroids in scenarios where they're POIs (e.g. Reforged Eden).</small></fieldset>
      <fieldset class="fset"><legend>Weekly: everywhere visited</legend><div class="checks">${wipeBoxes("w-weekly", m.weeklyWipe)}</div>
        <small class="muted">Player structures are never wiped; terrain is kept around bases.</small></fieldset>
    </div>
    <fieldset class="fset"><legend>Starter systems</legend>
      ${systems.length ? `<div class="systems">${systems.map(s => `<label class="check"><input type="checkbox" name="sys" value="${esc(s.name)}" ${m.starterSystems.includes(s.name) ? "checked" : ""}> ${esc(s.name)}${s.looksLikeStarter ? ' <span class="tag">starter</span>' : ""}</label>`).join("")}</div>
        <small class="muted">From the save's Sectors.yaml. Systems tagged "starter" have a starting-system star class.</small>`
        : `<p class="muted small">No solar systems found yet. Start the server once so the save exists.</p>`}
    </fieldset>
    <details class="fset"><summary>Task Scheduler names</summary>
      <div class="fgrid">
        <label class="field" for="t-folder">Folder<input id="t-folder" type="text" value="${esc(tk.folder)}"></label>
        <label class="field" for="t-daily">Daily task name<input id="t-daily" type="text" value="${esc(tk.dailyName)}"></label>
        <label class="field" for="t-weekly">Weekly task name<input id="t-weekly" type="text" value="${esc(tk.weeklyName)}"></label>
      </div>
      <small class="muted">Changing these leaves the old tasks in place. Remove them first.</small>
    </details>
    <div class="form-actions"><span id="sched-status" class="form-status" role="status"></span>
      <button class="btn primary" type="submit">Save schedule</button></div>`;
}

$("#sched-form").addEventListener("submit", async e => {
  e.preventDefault();
  const f = $("#sched-form"), st = $("#sched-status");
  const checked = n => $$(`input[name="${n}"]:checked`, f).map(x => x.value);
  const days = v => DAYS.filter(d => $(`#day-${d}`).value === v);
  const body = {
    maintenance: {
      ...schedData.maintenance,
      restartTime: $("#s-restart").value,
      warnMinutes: $("#s-warn").value.split(/[\s,]+/).filter(Boolean).map(Number),
      backupsToKeep: +$("#s-backups").value,
      dailyDays: days("daily"), weeklyDays: days("weekly"),
      dailyStarterWipe: checked("w-starter").join(" "),
      dailySpaceWipe: checked("w-space").join(" "),
      weeklyWipe: checked("w-weekly").join(" "),
      starterSystems: checked("sys"),
      twiceDaily: $("#s-twice").checked,
    },
    tasks: { folder: $("#t-folder").value.trim(), dailyName: $("#t-daily").value.trim(), weeklyName: $("#t-weekly").value.trim() },
  };
  st.className = "form-status"; st.textContent = "Saving…";
  try {
    const r = await api("/maintenance/settings", body);
    st.textContent = r.message;
    await refreshTasks(); renderTasks(); loadSchedule();
    toast("Schedule saved");
  } catch (err) { st.className = "form-status err"; st.textContent = err.message; }
});
// ------------------------------------------------------------------ backups
async function loadBackups() {
  let list = [];
  try { list = await api("/backups"); } catch (e) { toast(e.message, true); }
  const running = status && status.state !== "offline";
  $("#backups").innerHTML = list.map(b => `
    <tr>
      <td class="mono">${esc(b.name)}</td>
      <td><span class="tag ${esc(b.kind)}">${esc(b.kind)}</span></td>
      <td>${esc(fmtDateTime(b.created))}</td>
      <td class="n">${b.sizeMB.toLocaleString()} MB</td>
      <td class="n"><button class="btn sm" data-restore="${esc(b.name)}" ${running ? "disabled title='Stop the server first'" : ""}>Restore…</button></td>
    </tr>`).join("") || `<tr><td colspan="5" class="muted">No backups yet.</td></tr>`;
}
$("#btn-backup").addEventListener("click", () => startJob("/backups", {}, "Backing up the world…").then(() => setTimeout(loadBackups, 4000)));
$("#backups").addEventListener("click", e => {
  const b = e.target.closest("button[data-restore]");
  if (!b) return;
  const name = b.dataset.restore;
  $("#restore-name").textContent = name;
  const input = $("#restore-confirm"); input.value = "";
  $("#restore-ok").disabled = true;
  input.oninput = () => ($("#restore-ok").disabled = input.value !== name);
  const dlg = $("#dlg-restore");
  dlg.returnValue = "";
  dlg.onclose = () => { if (dlg.returnValue === "ok") startJob(`/backups/${encodeURIComponent(name)}/restore`, { confirm: name }, "Restoring backup…"); };
  dlg.showModal();
});

// ------------------------------------------------------------------ settings
let setup = null, sub = "setup";
const subs = $$(".subtabs [data-sub]");
function showSub(name) {
  sub = name;
  subs.forEach(b => b.setAttribute("aria-selected", b.dataset.sub === name));
  ["setup", "server", "rules", "admins", "about"].forEach(n => $(`#sub-${n}`).hidden = n !== name);
  try { localStorage.setItem("mgr-sub", name); } catch {}
  ({ setup: loadSetup, server: loadServerConfig, rules: loadRules, admins: loadAdmins, about: loadAbout })[name]();
}
subs.forEach(b => b.addEventListener("click", () => showSub(b.dataset.sub)));
async function loadSettings() {
  let s = "setup"; try { s = localStorage.getItem("mgr-sub") || s; } catch {}
  if (status?.setup && !status.setup.configured) s = "setup";
  showSub(s);
}
$("#setup-go").addEventListener("click", () => { showTab("tab-settings"); showSub("setup"); });

// ---- generic field rendering for the config forms
function fieldHtml(f, scenarios) {
  const id = `f-${f.key}`, v = f.value ?? "";
  const def = `<option value="" ${v === "" ? "selected" : ""}>${f.required ? "(choose)" : "Default (not set)"}</option>`;
  let input;
  switch (f.type) {
    case "bool":
      input = `<select id="${id}" data-key="${f.key}">${def}${["true", "false"].map(o => `<option value="${o}" ${v.toLowerCase() === o ? "selected" : ""}>${o === "true" ? "Yes" : "No"}</option>`).join("")}</select>`; break;
    case "select":
      input = `<select id="${id}" data-key="${f.key}">${def}${f.options.map(o => `<option value="${esc(o)}" ${v.toLowerCase() === o.toLowerCase() ? "selected" : ""}>${esc(o)}</option>`).join("")}</select>`; break;
    case "scenario":
      input = `<select id="${id}" data-key="${f.key}">${def}${(scenarios || []).map(o => `<option value="${esc(o)}" ${v === o ? "selected" : ""}>${esc(o)}</option>`).join("")}</select>`; break;
    case "password":
      input = `<div class="pw-row"><input id="${id}" data-key="${f.key}" type="password" autocomplete="new-password" placeholder="${f.isSet ? "•••••• (set, leave blank to keep)" : "Not set"}">
        ${f.key === "Tel_Pwd" ? `<button class="btn sm" type="button" data-gen="${id}">Generate</button>` : ""}
        ${f.key === "Srv_Password" && f.isSet ? `<label class="check"><input type="checkbox" data-clear="${f.key}"> Remove</label>` : ""}</div>`; break;
    case "number":
      input = `<input id="${id}" data-key="${f.key}" type="number" value="${esc(v)}">`; break;
    default:
      input = `<input id="${id}" data-key="${f.key}" type="text" value="${esc(v)}" ${f.key === "Srv_Description" ? 'maxlength="127"' : ""}>`;
  }
  return `<label class="field" for="${id}"><span>${esc(f.label)}${f.required ? ' <span class="req">*</span>' : ""}</span>${input}${f.help ? `<small>${esc(f.help)}</small>` : ""}</label>`;
}
function groupedForm(fields, scenarios) {
  const groups = [...new Set(fields.map(f => f.group || "General"))];
  return groups.map(g => `<fieldset class="fset"><legend>${esc(g)}</legend><div class="fgrid">${
    fields.filter(f => (f.group || "General") === g).map(f => fieldHtml(f, scenarios)).join("")}</div></fieldset>`).join("");
}
function collectChanges(root, fields) {
  const values = {};
  for (const f of fields) {
    const el = $(`[data-key="${f.key}"]`, root);
    if (!el) continue;
    if (f.type === "password") {
      if ($(`[data-clear="${f.key}"]`, root)?.checked) values[f.key] = "";
      else if (el.value) values[f.key] = el.value;
      continue;
    }
    const now = el.value ?? "", was = f.value ?? "";
    const same = (f.type === "bool" || f.type === "select") ? now.toLowerCase() === was.toLowerCase() : now === was;
    if (!same) values[f.key] = now;
  }
  return values;
}
function saveBar(id, running) {
  return `<div class="form-actions sticky"><span id="${id}-status" class="form-status" role="status"></span>
    ${running ? `<button class="btn" type="button" data-save-restart="${id}">Save &amp; restart server…</button>` : ""}
    <button class="btn primary" type="submit">Save changes</button></div>`;
}
document.addEventListener("click", e => {
  const g = e.target.closest("button[data-gen]");
  if (!g) return;
  const chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";
  const a = new Uint32Array(20); crypto.getRandomValues(a);
  const el = $("#" + g.dataset.gen); el.type = "text"; el.value = [...a].map(n => chars[n % chars.length]).join("");
});

// ---- Setup
async function loadSetup() {
  const box = $("#sub-setup");
  try { setup = await api("/setup"); } catch (e) { box.innerHTML = `<div class="panel"><p class="form-status err">${esc(e.message)}</p></div>`; return; }
  const s = setup;
  const tel = s.telnet;
  const steps = [
    ["Server folder", s.serverDirValid],
    ["Config file", s.configValid],
    ["Telnet console on", !!(tel && tel.enabled && tel.hasPassword)],
    ["Settings saved", s.settingsFileExists],
  ];
  box.innerHTML = `
  <div class="panel">
    <div class="panel-head"><h2>Setup</h2><span class="muted small">Saved to <span class="mono">${esc(s.settingsPath)}</span></span></div>
    <ol class="checklist">${steps.map(([n, ok]) => `<li class="${ok ? "ok" : "todo"}">${ok ? "✓" : "•"} ${n}</li>`).join("")}</ol>
    <form id="setup-form" class="form" autocomplete="off">
      <fieldset class="fset"><legend>Server</legend><div class="fgrid">
        <label class="field wide" for="su-dir"><span>Dedicated server folder <span class="req">*</span></span>
          <div class="pw-row"><input id="su-dir" type="text" class="mono" value="${esc(s.serverDir)}" placeholder="C:\\...\\Empyrion - Dedicated Server" required>
          <button class="btn sm" type="button" id="su-detect">Find</button></div>
          <small id="su-dir-hint">${s.serverDirValid ? "✓ Empyrion dedicated server found." : "The folder that contains EmpyrionLauncher.exe."}</small>
          <div id="su-found"></div></label>
        <label class="field" for="su-config"><span>Server config file <span class="req">*</span></span>
          <select id="su-config">${s.configFiles.length ? s.configFiles.map(f => `<option ${f === s.configFile ? "selected" : ""}>${esc(f)}</option>`).join("") : `<option value="">(no .yaml configs found)</option>`}</select>
          <small>The <span class="mono">.yaml</span> passed to the server with <span class="mono">-dedicated</span>. Edit it under <b>Server config</b>.</small></label>
        <div class="field"><span>New config file</span>
          <div class="pw-row"><input id="su-newname" type="text" placeholder="my-server.yaml" aria-label="New config file name">
          <select id="su-copy" aria-label="Start from"><option value="">from template</option>${s.configFiles.map(f => `<option value="${esc(f)}">copy of ${esc(f)}</option>`).join("")}</select>
          <button class="btn sm" type="button" id="su-create">Create</button></div>
          <small>Creates the file in the server folder (with Telnet already on), then selects it.</small></div>
        <label class="field" for="su-launch">Start the server
          <select id="su-launch"><option value="-startDedi" ${s.launchMode === "-startDedi" ? "selected" : ""}>Headless (no window)</option>
          <option value="-startDediWithGfx" ${s.launchMode === "-startDediWithGfx" ? "selected" : ""}>With the server's console window</option></select></label>
      </div></fieldset>
      <fieldset class="fset"><legend>Telnet console</legend>
        ${!tel ? `<p class="muted small">Pick a config file first.</p>` : tel.enabled && tel.hasPassword
          ? `<p class="small">✓ Telnet is on (port ${esc(tel.port || "30004")}, password set). The manager uses it for chat, players and restarts. <b>Never port-forward this port.</b></p>`
          : `<p class="small">The manager needs the server's Telnet console. <button class="btn sm primary" type="button" id="su-telnet">Turn on Telnet</button></p>`}
      </fieldset>
      <fieldset class="fset"><legend>Dashboard</legend><div class="fgrid">
        <label class="field" for="su-title">Dashboard title<input id="su-title" type="text" value="${esc(s.title)}"></label>
        <label class="field" for="su-url">Dashboard address<input id="su-url" type="text" class="mono" value="${esc(s.url)}">
          <small>Keep 127.0.0.1 (this PC only) and use Tailscale for remote access. Changing it needs a manager restart.</small></label>
        <label class="field" for="su-steamcmd">SteamCMD (for server updates)<input id="su-steamcmd" type="text" class="mono" value="${esc(s.steamCmdPath)}" placeholder="${esc(s.steamCmdDetected || "C:\\steamcmd\\steamcmd.exe")}">
          <small>${s.steamCmdDetected ? `Found: <span class="mono">${esc(s.steamCmdDetected)}</span>` : "Not found. Install SteamCMD to update the server from here."}</small></label>
      </div></fieldset>
      <div class="form-actions"><span id="setup-status" class="form-status" role="status"></span><button class="btn primary" type="submit">Save setup</button></div>
    </form>
  </div>
  <div class="cols">
    <div class="panel">
      <div class="panel-head"><h2>Dashboard password</h2><span class="tag">${s.hasPassword ? "On" : "Off"}</span></div>
      <p class="muted small" style="margin:0">Optional. When set, any device that isn't this PC (other computers, phones over Tailscale) must sign in. This PC never asks.</p>
      <form id="pw-form" class="inline-form" autocomplete="off">
        <label for="pw-new" class="sr-only">New password</label>
        <input id="pw-new" type="password" autocomplete="new-password" placeholder="${s.hasPassword ? "New password (blank = remove)" : "At least 8 characters"}">
        <button class="btn" type="submit">${s.hasPassword ? "Change" : "Set password"}</button>
      </form>
    </div>
    <div class="panel">
      <div class="panel-head"><h2>Update server</h2></div>
      <p class="muted small" style="margin:0">Runs SteamCMD <span class="mono">app_update 530870 validate</span>. Stop the server first. Saves and custom config files are kept, but stock files such as <span class="mono">dedicated.yaml</span> are restored.</p>
      <div><button class="btn" type="button" id="su-update" ${s.running ? "disabled title='Stop the server first'" : ""}>Update server now</button></div>
    </div>
  </div>`;
}

$("#sub-setup").addEventListener("click", async e => {
  const t = e.target;
  if (t.id === "su-detect") {
    t.disabled = true; $("#su-found").innerHTML = `<small>Searching…</small>`;
    try {
      const { dirs } = await api("/setup/detect");
      $("#su-found").innerHTML = dirs.length ? `<div class="found">${dirs.map(d => `<button type="button" class="chip" data-dir="${esc(d)}">${esc(d)}</button>`).join("")}</div>` : `<small>No server found automatically. Paste the folder path.</small>`;
    } catch (err) { $("#su-found").innerHTML = `<small class="err">${esc(err.message)}</small>`; }
    t.disabled = false;
  }
  if (t.dataset.dir) { $("#su-dir").value = t.dataset.dir; $("#su-found").innerHTML = ""; }
  if (t.id === "su-create") {
    try {
      const r = await api("/setup/config-file", { name: $("#su-newname").value, copyFrom: $("#su-copy").value });
      await api("/setup", { serverDir: $("#su-dir").value, configFile: r.file });
      toast(`Created ${r.file}`); loadSetup(); refreshStatus();
    } catch (err) { toast(err.message, true); }
  }
  if (t.id === "su-telnet") {
    try { const r = await api("/setup/telnet", {}); toast(r.message); loadSetup(); } catch (err) { toast(err.message, true); }
  }
  if (t.id === "su-update") startJob("/server/update", {}, "Updating the server with SteamCMD…");
});
$("#sub-setup").addEventListener("submit", async e => {
  e.preventDefault();
  if (e.target.id === "pw-form") {
    try { const r = await api("/setup/password", { password: $("#pw-new").value }); toast(r.message); loadSetup(); }
    catch (err) { toast(err.message, true); }
    return;
  }
  const st = $("#setup-status");
  st.className = "form-status"; st.textContent = "Saving…";
  try {
    const r = await api("/setup", {
      serverDir: $("#su-dir").value, configFile: $("#su-config").value, launchMode: $("#su-launch").value,
      title: $("#su-title").value, url: $("#su-url").value, steamCmdPath: $("#su-steamcmd").value,
    });
    toast(r.message); await refreshStatus(); loadSetup();
  } catch (err) { st.className = "form-status err"; st.textContent = err.message; }
});

// ---- Server config (.yaml)
let serverCfg = null;
async function loadServerConfig() {
  const box = $("#sub-server");
  try { serverCfg = await api("/server-config"); } catch (e) { box.innerHTML = `<div class="panel"><p class="form-status err">${esc(e.message)}</p></div>`; return; }
  box.innerHTML = `<form id="srv-form" class="panel form" autocomplete="off">
    <div class="panel-head"><h2>Server config</h2><span class="muted small mono">${esc(serverCfg.file)}</span></div>
    <p class="muted small" style="margin:0">Edits only the lines you change. Comments and layout are kept, and a <span class="mono">.bak</span> copy is saved first.
      ${serverCfg.running ? "The server is running, so changes take effect on the next restart." : "Changes take effect when the server starts."}</p>
    ${groupedForm(serverCfg.fields, serverCfg.scenarios)}
    ${saveBar("srv", serverCfg.running)}
  </form>`;
}
async function saveForm(kind, restartAfter) {
  const [path, data, sel] = kind === "srv" ? ["/server-config", serverCfg, "#srv-form"] : ["/game-options", rulesCfg, "#rules-form"];
  const st = $(`#${kind}-status`);
  const values = collectChanges($(sel), data.fields);
  if (!Object.keys(values).length && !restartAfter) { st.className = "form-status"; st.textContent = "No changes."; return; }
  st.className = "form-status"; st.textContent = "Saving…";
  try {
    if (Object.keys(values).length) { const r = await api(path, { values }); toast(r.message); }
    if (restartAfter) askPower("restart");
    kind === "srv" ? loadServerConfig() : loadRules();
    refreshStatus();
  } catch (err) { st.className = "form-status err"; st.textContent = err.message; }
}
$("#sub-server").addEventListener("submit", e => { e.preventDefault(); saveForm("srv", false); });
$("#sub-server").addEventListener("click", e => { if (e.target.dataset.saveRestart === "srv") saveForm("srv", true); });

// ---- Game rules (gameoptions.yaml)
let rulesCfg = null;
async function loadRules() {
  const box = $("#sub-rules");
  try { rulesCfg = await api("/game-options"); } catch (e) { box.innerHTML = `<div class="panel"><p class="form-status err">${esc(e.message)}</p></div>`; return; }
  if (rulesCfg.problem) { box.innerHTML = `<div class="panel"><h2>Game rules</h2><p class="muted">${esc(rulesCfg.problem)}</p></div>`; return; }
  box.innerHTML = `<form id="rules-form" class="panel form" autocomplete="off">
    <div class="panel-head"><h2>Game rules</h2><span class="muted small mono">${esc(rulesCfg.file)}</span></div>
    <p class="muted small" style="margin:0">The multiplayer ${esc(rulesCfg.mode)} block of ${rulesCfg.isSave ? "the save's own" : "the scenario's"} <span class="mono">gameoptions.yaml</span>.
      ${rulesCfg.isSave ? "" : "The save doesn't exist yet, so these become its starting rules. "}Unset options use the game's defaults.
      Some difficulty options only apply to a new save.</p>
    ${groupedForm(rulesCfg.fields, [])}
    ${saveBar("rules", rulesCfg.running)}
  </form>`;
}
$("#sub-rules").addEventListener("submit", e => { e.preventDefault(); saveForm("rules", false); });
$("#sub-rules").addEventListener("click", e => { if (e.target.dataset.saveRestart === "rules") saveForm("rules", true); });

// ---- Admins
let adminsCfg = null;
const ROLES = { 9: "Admin", 6: "Moderator", 3: "GameMaster" };
function adminRow(a = { steamId: "", permission: 9, note: "" }) {
  return `
    <div class="admin-row">
      <span class="aid"><input type="text" class="mono" inputmode="numeric" maxlength="17" placeholder="SteamID64" value="${esc(a.steamId)}" aria-label="SteamID64" data-a="id">
        <span class="aname" data-a="name">${adminName(a.steamId)}</span></span>
      <select data-a="perm" aria-label="Role">${Object.entries(ROLES).map(([p, n]) => `<option value="${p}" ${+p === a.permission ? "selected" : ""}>${n}</option>`).join("")}</select>
      <input type="text" placeholder="Note (e.g. who this is)" value="${esc(a.note || "")}" aria-label="Note" data-a="note">
      <label class="check"><input type="checkbox" data-a="prio" ${adminsCfg.priority.includes(a.steamId) ? "checked" : ""}> Login priority</label>
      <button class="btn sm danger" type="button" data-a="drop" aria-label="Take off the list">✕</button>
    </div>`;
}
function adminName(id) {
  const n = adminsCfg?.names?.[id];
  if (!id) return "";
  if (!n || (!n.inGame && !n.steam)) return `<span class="muted">Unknown player</span>`;
  if (n.inGame && n.steam && n.inGame !== n.steam) return `${esc(n.inGame)} <span class="muted">· Steam: ${esc(n.steam)}</span>`;
  return esc(n.inGame || n.steam);
}
async function loadAdmins() {
  const box = $("#sub-admins");
  try { adminsCfg = await api("/admins"); } catch (e) { box.innerHTML = `<div class="panel"><p class="form-status err">${esc(e.message)}</p></div>`; return; }
  const extraPrio = adminsCfg.priority.filter(p => !adminsCfg.admins.some(a => a.steamId === p));
  box.innerHTML = `<form id="admins-form" class="panel form" autocomplete="off">
    <div class="panel-head"><h2>Admins</h2><span class="muted small mono">${esc(adminsCfg.file)}</span></div>
    <p class="muted small" style="margin:0">Players with special permissions. Find a SteamID64 at steamid.io.
      ${adminsCfg.running ? "Role changes apply to the running server straight away (setrole) and are saved for future restarts." : "Changes take effect when the server starts."}</p>
    <div id="admin-rows" class="admin-rows">${adminsCfg.admins.map(adminRow).join("")}</div>
    <div><button class="btn sm" type="button" id="admin-add">+ Add admin</button></div>
    <label class="field" for="prio-extra">Other players with login priority (SteamID64s, comma-separated)
      <input id="prio-extra" type="text" class="mono" value="${esc(extraPrio.join(", "))}">
      <small>Login priority puts these players ahead in the queue when the server is full.</small></label>
    <div class="form-actions"><span id="admins-status" class="form-status" role="status"></span><button class="btn primary" type="submit">Save admins</button></div>
  </form>`;
}
$("#sub-admins").addEventListener("change", async e => {
  if (e.target.dataset.a !== "id") return;
  const id = e.target.value.trim(), span = e.target.parentElement.querySelector("[data-a=name]");
  if (!/^\d{17}$/.test(id)) { span.innerHTML = id ? `<span class="err">Not a 17-digit SteamID64</span>` : ""; return; }
  span.innerHTML = `<span class="muted">Looking up…</span>`;
  try { const n = await api(`/steam-name/${id}`); adminsCfg.names = { ...(adminsCfg.names || {}), [id]: n }; span.innerHTML = adminName(id); }
  catch { span.innerHTML = `<span class="muted">Couldn't look up</span>`; }
});
$("#sub-admins").addEventListener("click", e => {
  if (e.target.id === "admin-add") $("#admin-rows").insertAdjacentHTML("beforeend", adminRow());
  if (e.target.dataset.a === "drop") e.target.closest(".admin-row").outerHTML = "";
});
$("#sub-admins").addEventListener("submit", async e => {
  e.preventDefault();
  const st = $("#admins-status");
  const admins = [], priority = [];
  for (const r of $$(".admin-row", e.target)) {
    const id = $('[data-a="id"]', r).value.trim();
    if (!id) continue;
    admins.push({ steamId: id, permission: +$('[data-a="perm"]', r).value, note: $('[data-a="note"]', r).value.trim() || null });
    if ($('[data-a="prio"]', r).checked) priority.push(id);
  }
  priority.push(...$("#prio-extra").value.split(/[\s,]+/).filter(Boolean));
  st.className = "form-status"; st.textContent = "Saving…";
  try {
    const r = await api("/admins", { admins, priority });
    toast(r.message);
    await loadAdmins();
    if (r.applied?.length) $("#admins-status").textContent = r.applied.join(" · ");
  } catch (err) { st.className = "form-status err"; st.textContent = err.message; }
});

// ------------------------------------------------------------------ remote access (Tailscale Serve)
let remote = null, remoteBusy = false;

async function refreshRemote() {
  if (remoteBusy) return;
  try { remote = await api("/remote"); } catch { return; }
  renderRemote();
}

function renderRemote(note) {
  const r = remote, pill = $("#remote-pill"), body = $("#remote-body"), chip = $("#meta-remote");
  if (!r) return;
  let state, label;
  if (!r.installed) { state = "offline"; label = "Not installed"; }
  else if (!r.running) { state = "busy"; label = "Tailscale off"; }
  else if (r.served) { state = "online"; label = "Shared"; }
  else { state = "idle"; label = "Not shared"; }
  pill.dataset.state = state; pill.textContent = remoteBusy ? "Working…" : label;

  chip.hidden = !r.installed;
  chip.dataset.on = String(!!r.served);
  chip.textContent = r.served && r.url ? r.url.replace(/^https?:\/\//, "") : "Tailnet: off";
  chip.title = r.served ? "Dashboard is shared on your tailnet" : "Dashboard is only available on this PC";

  if (!r.installed || !r.running) {
    body.innerHTML = `<p class="muted small">${esc(r.detail || "Tailscale isn't available.")}</p>`;
    return;
  }
  body.innerHTML = `
    <div class="addr ${r.served ? "" : "off"}">
      <a href="${esc(r.url)}" target="_blank" rel="noopener">${esc(r.url.replace(/^https?:\/\//, ""))}</a>
      <button class="btn sm" id="remote-copy" type="button">Copy</button>
    </div>
    <p class="muted small" style="margin:0">${r.served
      ? `Open this on your phone or tablet while it's connected to Tailscale. Only devices on your tailnet can reach it.`
      : `Share the dashboard with your other Tailscale devices. It stays off the open internet and your home network.`}
      ${note ? `<br>${esc(note)}` : ""}</p>
    <div class="row">
      <span class="muted small mono">${esc(r.dnsName || "")}</span>
      ${isRemote ? "" : `<button class="btn sm ${r.served ? "danger" : "primary"}" id="remote-toggle" type="button" ${remoteBusy ? "disabled" : ""}>${r.served ? "Stop sharing" : "Share on tailnet"}</button>`}
    </div>`;
}

$("#remote-body").addEventListener("click", async e => {
  if (e.target.id === "remote-copy") {
    const url = remote?.url || "";
    try { await navigator.clipboard.writeText(url); toast("Address copied"); }
    catch { toast(url); }
  }
  if (e.target.id === "remote-toggle") {
    const on = !remote.served;
    remoteBusy = true; renderRemote();
    try {
      const r = await api(`/remote/${on ? "on" : "off"}`, {});
      remote = r.state;
      toast(on ? `Shared at ${remote.url.replace(/^https?:\/\//, "")}` : "Stopped sharing on the tailnet");
    } catch (err) { toast(err.message, true); }
    remoteBusy = false; renderRemote();
  }
});

// ------------------------------------------------------------------ updates + about
let update = null, updateDismissed = null;
try { updateDismissed = localStorage.getItem("mgr-update-later"); } catch {}

async function refreshUpdate(force) {
  try {
    const r = force ? await api("/update/check", {}) : await api("/update");
    update = { ...(update || {}), ...r, state: r.state };
  } catch { return; }
  const s = update.state, banner = $("#update-banner");
  banner.hidden = !(s.available && s.latest !== updateDismissed && !isRemote);
  if (!banner.hidden) {
    $("#update-text").innerHTML = `<b>Version ${esc(s.latest)} is available.</b> You have ${esc(s.current)}.`;
    $("#update-notes").href = s.releaseUrl || "#";
    $("#update-install").hidden = !s.canInstall;
    if (!s.canInstall) $("#update-text").innerHTML += " This copy wasn't installed with the setup program, so download the installer from the release page.";
  }
  if (sub === "about" && !$("#sub-about").hidden) renderAbout();
}
async function installUpdate() {
  try {
    const r = await api("/update/install", {});
    toast(r.message);
    $("#update-banner").hidden = true;
    // the manager restarts; reload once it's back
    setTimeout(function waitBack() { fetch("/api/status").then(x => x.ok ? location.reload() : setTimeout(waitBack, 2000)).catch(() => setTimeout(waitBack, 2000)); }, 6000);
  } catch (e) { toast(e.message, true); }
}
$("#update-install").addEventListener("click", installUpdate);
$("#update-later").addEventListener("click", () => {
  updateDismissed = update?.state?.latest || null;
  try { localStorage.setItem("mgr-update-later", updateDismissed || ""); } catch {}
  $("#update-banner").hidden = true;
});

const CREDITS = [
  ["Empyrion – Galactic Survival", "Eleon Game Studios", "https://empyriongame.com", "The game and its dedicated server. This project is an unofficial fan-made tool and isn't affiliated with or endorsed by Eleon."],
  ["Reforged Eden", "Vermillion and contributors", "https://steamcommunity.com/sharedfiles/filedetails/?id=3143225812", "Scenario this manager was first built and tested with (asteroid/POI behaviour, starter systems)."],
  [".NET / ASP.NET Core", "Microsoft and the .NET Foundation", "https://dotnet.microsoft.com", "Runtime and web server (MIT licence)."],
  ["Microsoft.Data.Sqlite + SQLite", "Microsoft / SQLite authors", "https://www.sqlite.org", "Reads in-game chat from a copy of the save database (MIT / public domain)."],
  ["Chakra Petch, IBM Plex Sans & Mono", "Cadson Demak / IBM, via Google Fonts", "https://fonts.google.com", "Dashboard typefaces (SIL Open Font Licence)."],
  ["Inno Setup", "Jordan Russell and Martijn Laan", "https://jrsoftware.org/isinfo.php", "Builds the Windows installer."],
  ["SteamCMD", "Valve", "https://developer.valvesoftware.com/wiki/SteamCMD", "Installs and updates the dedicated server (optional)."],
  ["Tailscale", "Tailscale Inc.", "https://tailscale.com", "Optional private remote access via Tailscale Serve."],
  ["Steam Community profiles", "Valve", "https://steamcommunity.com", "Public profile names shown next to admin SteamIDs."],
  ["Empyrion community knowledge", "Empyrion Wiki, Empyrion forums and Steam discussions", "https://empyrion.fandom.com", "Console commands, dedicated-server settings and POI/asteroid regeneration behaviour."],
  ["Claude Code", "Anthropic", "https://claude.com/claude-code", "AI pair-programmer used to design and build this project."],
];

async function loadAbout() {
  if (!update) await refreshUpdate(false);
  renderAbout();
}
function renderAbout() {
  const s = update?.state || {};
  const repo = s.repo || "";
  $("#sub-about").innerHTML = `
  <div class="cols">
    <div class="panel col-main">
      <div class="panel-head"><h2>Empyrion Server Manager</h2><span class="tag">v${esc(s.current || "?")}</span></div>
      <p style="margin:0">A web dashboard for running an Empyrion – Galactic Survival dedicated server: restarts with in-game warnings, players, chat,
        console, scheduled maintenance and resets, backups and settings.</p>
      <dl class="paths about-dl">
        <div><dt>Source code &amp; releases</dt><dd><a href="https://github.com/${esc(repo)}" target="_blank" rel="noopener">github.com/${esc(repo)}</a></dd></div>
        <div><dt>Licence</dt><dd>MIT. Free to use, change and share. See <a href="https://github.com/${esc(repo)}/blob/main/LICENSE" target="_blank" rel="noopener">LICENSE</a>.</dd></div>
        <div><dt>Disclaimer</dt><dd>Unofficial fan-made tool, not affiliated with Eleon Game Studios. Provided as is, without warranty.</dd></div>
      </dl>
    </div>
    <div class="panel col-side">
      <div class="panel-head"><h2>Updates</h2>${s.available ? `<span class="tag warn">v${esc(s.latest)} available</span>` : s.latest ? `<span class="tag">Up to date</span>` : ""}</div>
      <p class="muted small" style="margin:0">${s.error ? esc(s.error) : s.latest ? `Latest release: v${esc(s.latest)}${s.publishedAt ? " · " + esc(fmtDateTime(s.publishedAt)) : ""}` : "Not checked yet."}
        ${s.checkedAt ? `<br>Checked ${esc(fmtAgo(s.checkedAt))}.` : ""}</p>
      <p class="muted small" style="margin:0">Updates are downloaded from GitHub Releases and only installed if their signature matches this project's update key.
        ${s.installed ? "" : "<br><b>This copy wasn't installed with the setup program</b>, so updates must be downloaded manually."}</p>
      <div class="btn-row">
        <button class="btn sm" id="ab-check" type="button">Check now</button>
        ${s.canInstall ? `<button class="btn sm primary" id="ab-install" type="button">Install v${esc(s.latest)}</button>` : ""}
        ${s.releaseUrl ? `<a class="btn sm ghost" href="${esc(s.releaseUrl)}" target="_blank" rel="noopener">Release page</a>` : ""}
      </div>
      <label class="check"><input type="checkbox" id="ab-auto" ${update?.checkForUpdates ? "checked" : ""}> Check for updates automatically</label>
      <label class="check"><input type="checkbox" id="ab-browser" ${update?.openBrowserOnStart ? "checked" : ""}> Open the dashboard when the manager starts</label>
    </div>
  </div>
  <div class="panel">
    <div class="panel-head"><h2>Credits &amp; sources</h2></div>
    <div class="table-wrap"><table><tbody>${CREDITS.map(([n, by, url, what]) => `
      <tr><td><a href="${url}" target="_blank" rel="noopener"><b>${esc(n)}</b></a><br><span class="muted small">${esc(by)}</span></td><td>${esc(what)}</td></tr>`).join("")}
    </tbody></table></div>
  </div>`;
}
$("#sub-about").addEventListener("click", async e => {
  if (e.target.id === "ab-check") { e.target.disabled = true; await refreshUpdate(true); toast(update.state.available ? `v${update.state.latest} is available` : (update.state.error || "You're up to date")); }
  if (e.target.id === "ab-install") installUpdate();
});
$("#sub-about").addEventListener("change", async e => {
  if (e.target.id !== "ab-auto" && e.target.id !== "ab-browser") return;
  try {
    await api("/update/prefs", e.target.id === "ab-auto" ? { checkForUpdates: e.target.checked } : { openBrowserOnStart: e.target.checked });
    update[e.target.id === "ab-auto" ? "checkForUpdates" : "openBrowserOnStart"] = e.target.checked;
    toast("Saved");
  } catch (err) { toast(err.message, true); e.target.checked = !e.target.checked; }
});

// ------------------------------------------------------------------ confirm dialog (generic)
function confirmBox(title, text, okLabel, danger) {
  return new Promise(resolve => {
    $("#confirm-title").textContent = title; $("#confirm-text").textContent = text;
    const ok = $("#confirm-ok"); ok.textContent = okLabel; ok.className = "btn " + (danger ? "danger" : "primary");
    const dlg = $("#dlg-confirm");
    dlg.returnValue = "";
    dlg.onclose = () => resolve(dlg.returnValue === "ok");
    dlg.showModal();
  });
}

// ------------------------------------------------------------------ boot
let start = "tab-overview";
try { start = localStorage.getItem("mgr-tab") || start; } catch {}
if (!$("#" + start)) start = "tab-overview";
showTab(start);

refreshStatus().then(refreshPeople); refreshActivity(); refreshLog(); refreshTasks();
setInterval(() => { if (!$("#view-overview").hidden && !document.hidden) refreshPeople(); }, 60000);
refreshChat();
setInterval(() => { if (!document.hidden) refreshChat(); }, 5000);
refreshUpdate(false);
setInterval(() => refreshUpdate(false), 30 * 60000);
refreshRemote();
setInterval(refreshRemote, 20000);
setInterval(refreshStatus, 3000);
setInterval(refreshActivity, 5000);
setInterval(refreshLog, 2000);
setInterval(refreshJob, 1000);
setInterval(refreshTasks, 60000);
