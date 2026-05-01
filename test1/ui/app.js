const host = window.chrome && window.chrome.webview ? window.chrome.webview : null;
const state = {
  view: "control", theme: "dark",
  control: {
    connection: { connected: false, station: 0, banner: "PLC disconnected", meta: "MX Component logical station: 0", buttonText: "CONNECT PLC Q" },
    axes: [
      { index: 1, currentPos: "--", currentSpeed: "--", errorCode: "--", warningCode: "--", axisStatus: "--", startNo: "--", errorReset: "--", jogSpeed: "--", newSpeed: "--" },
      { index: 2, currentPos: "--", currentSpeed: "--", errorCode: "--", warningCode: "--", axisStatus: "--", startNo: "--", errorReset: "--", jogSpeed: "--", newSpeed: "--" },
      { index: 3, currentPos: "--", currentSpeed: "--", errorCode: "--", warningCode: "--", axisStatus: "--", startNo: "--", errorReset: "--", jogSpeed: "--", newSpeed: "--" },
      { index: 4, currentPos: "--", currentSpeed: "--", errorCode: "--", warningCode: "--", axisStatus: "--", startNo: "--", errorReset: "--", jogSpeed: "--", newSpeed: "--" }
    ],
    events: []
  },
  dxf: { filePath: "", fileName: "", bounds: { left: 0, top: 0, width: 100, height: 100 }, primitives: [], points: [], selectedPointKey: "", assignedPointKeys: {}, processRows: [] },
  telemetry: {}, logs: []
};

const dom = {};
let modalSubmit = null;
let cadPanX = 0, cadPanY = 0, cadZoom = 1, isCadPanning = false, startCadPanX = 0, startCadPanY = 0;

window.app = { receive(m) { handleHostMessage(m || {}); } };

document.addEventListener("DOMContentLoaded", () => { cacheDom(); bindEvents(); applyTheme(state.theme); applyView(state.view); post("uiReady"); });

function cacheDom() {
  dom.html = document.documentElement;
  dom.topViewButtons = Array.from(document.querySelectorAll(".top-nav [data-view]"));
  dom.sideViewButtons = Array.from(document.querySelectorAll(".side-nav [data-view]"));
  dom.placeholderButtons = Array.from(document.querySelectorAll("[data-placeholder]"));
  dom.themeToggle = document.getElementById("theme-toggle");
  dom.connectButton = document.getElementById("connect-button");
  dom.plcStation = document.getElementById("plc-station");
  dom.plcStatusDot = document.getElementById("plc-status-dot");
  dom.plcStatusText = document.getElementById("plc-status-text");
  dom.connectionMeta = document.getElementById("connection-meta");
  dom.sidebarStatus = document.getElementById("sidebar-status");
  dom.emergencyStop = document.getElementById("emergency-stop");
  dom.eventsList = document.getElementById("events-list");
  dom.eventsEmpty = document.getElementById("events-empty");
  dom.clearEventsButton = document.getElementById("clear-events-button");
  dom.viewControl = document.getElementById("view-control");
  dom.viewLogs = document.getElementById("view-logs");
  dom.viewTelemetry = document.getElementById("view-telemetry");
  dom.viewDxf = document.getElementById("view-dxf");
  dom.openDxf = document.getElementById("open-dxf");
  dom.cadPath = document.getElementById("cad-path");
  dom.cadFile = document.getElementById("cad-file");
  dom.cadPreview = document.getElementById("cad-preview");
  dom.cadPlaceholder = document.getElementById("cad-placeholder");
  dom.pointsBody = document.getElementById("points-table-body");
  dom.processBody = document.getElementById("process-table-body");
  dom.assignButtons = Array.from(document.querySelectorAll("[data-assign-slot]"));
  dom.processButtons = Array.from(document.querySelectorAll("[data-process-key]"));
  dom.runButtons = Array.from(document.querySelectorAll("[data-run-action]"));
  dom.toastContainer = document.getElementById("toast-container");
  dom.telemetryContent = document.getElementById("telemetry-content");
  dom.telemetryWatchInput = document.getElementById("telemetry-watch-input");
  dom.telemetryWatchBtn = document.getElementById("telemetry-watch-btn");
  dom.writeBufferPath = document.getElementById("write-buffer-path");
  dom.writeBufferValue = document.getElementById("write-buffer-value");
  dom.writeBufferButton = document.getElementById("write-buffer-button");
  dom.logsBody = document.getElementById("logs-table-body");
  dom.logsEmpty = document.getElementById("logs-empty");
  dom.clearLogsButton = document.getElementById("clear-logs-button");
  dom.modal = document.getElementById("prompt-modal");
  dom.modalTitle = document.getElementById("modal-title");
  dom.modalLabel = document.getElementById("modal-label");
  dom.modalInput = document.getElementById("modal-input");
  dom.modalConfirm = document.getElementById("modal-confirm");
  dom.modalCancel = document.getElementById("modal-cancel");
}

function bindEvents() {
  dom.topViewButtons.forEach(b => b.addEventListener("click", () => { state.view = b.dataset.view; applyView(state.view); post("switchView", { view: state.view }); }));
  dom.sideViewButtons.forEach(b => b.addEventListener("click", () => { state.view = b.dataset.view; applyView(state.view); post("switchView", { view: state.view }); }));
  dom.placeholderButtons.forEach(b => b.addEventListener("click", () => showToast("info", b.dataset.placeholder, "Mục này đang để placeholder.")));
  dom.themeToggle.addEventListener("click", () => { state.theme = state.theme === "dark" ? "light" : "dark"; applyTheme(state.theme); post("setTheme", { theme: state.theme }); });
  dom.connectButton.addEventListener("click", () => { post("connectToggle", { station: parseInt(dom.plcStation.value, 10) || 0 }); });

  const goHomeBtn = document.getElementById("go-home-btn");
  if (goHomeBtn) {
    const stopHome = () => post("goHomeStop");
    goHomeBtn.addEventListener("pointerdown", e => { if (e.button !== 0) return; post("goHomeStart"); });
    goHomeBtn.addEventListener("pointerup", stopHome);
    goHomeBtn.addEventListener("pointerleave", stopHome);
    goHomeBtn.addEventListener("pointercancel", stopHome);
  }

  const resetErrorBtn = document.getElementById("reset-error-btn");
  if (resetErrorBtn) {
    const stopReset = () => post("resetErrorStop");
    resetErrorBtn.addEventListener("pointerdown", e => { if (e.button !== 0) return; post("resetErrorStart"); });
    resetErrorBtn.addEventListener("pointerup", stopReset);
    resetErrorBtn.addEventListener("pointerleave", stopReset);
    resetErrorBtn.addEventListener("pointercancel", stopReset);
  }

  const startBtn = document.getElementById("start-btn");
  if (startBtn) {
    const stopStart = () => post("startActionStop");
    startBtn.addEventListener("pointerdown", e => { if (e.button !== 0) return; post("startActionStart"); });
    startBtn.addEventListener("pointerup", stopStart);
    startBtn.addEventListener("pointerleave", stopStart);
    startBtn.addEventListener("pointercancel", stopStart);
  }
  dom.emergencyStop.addEventListener("click", () => post("emergencyStop"));
  if (dom.clearEventsButton) dom.clearEventsButton.addEventListener("click", () => { state.control.events = []; renderEvents(); post("clearLogs"); });

  const setJogSpeedBtn = document.getElementById("set-jog-speed-btn");
  const jogSpeedInput = document.getElementById("jog-speed-input");
  if (setJogSpeedBtn && jogSpeedInput) {
    setJogSpeedBtn.addEventListener("click", () => {
      post("setJogSpeed", { value: parseFloat(jogSpeedInput.value) || 0 });
    });
  }

  // Jog buttons (sidebar)
  document.querySelectorAll("[data-jog-offset]").forEach(btn => {
    const offset = parseInt(btn.dataset.jogOffset, 10);
    const stop = () => post("jogStop", { offset });
    btn.addEventListener("pointerdown", e => { if (e.button !== 0) return; post("jogStart", { offset }); });
    btn.addEventListener("pointerup", stop);
    btn.addEventListener("pointerleave", stop);
    btn.addEventListener("pointercancel", stop);
  });

  dom.openDxf.addEventListener("click", () => post("openDxf"));
  dom.assignButtons.forEach(b => b.addEventListener("click", () => {
    if (!state.dxf.selectedPointKey) { showToast("info", "DXF", "Please select a point before assigning."); return; }
    post("assignPoint", { slot: b.dataset.assignSlot, key: state.dxf.selectedPointKey });
  }));
  dom.processButtons.forEach(b => b.addEventListener("click", () => {
    const key = b.dataset.processKey;
    const row = state.dxf.processRows.find(i => i.key === key);
    const cv = key === "speed" ? (row ? row.speed : "") : (row ? row.mCodeValue : "");
    const tm = { zDown: "Z down height", zSafe: "Z safe height", speed: "Speed" };
    openPrompt(tm[key] || "Input", "Enter value:", cv || "", v => post("setProcessValue", { key, value: v }));
  }));
  dom.runButtons.forEach(b => b.addEventListener("click", () => post("runAction", { command: b.dataset.runAction })));
  dom.pointsBody.addEventListener("click", e => {
    const row = e.target.closest("[data-point-key]"); if (!row) return;
    state.dxf.selectedPointKey = row.dataset.pointKey; renderPointsTable(); renderCadPreview();
    post("selectCadPoint", { key: state.dxf.selectedPointKey });
  });
  dom.processBody.addEventListener("change", e => {
    const input = e.target; if (input.tagName === "INPUT" && input.dataset.processIndex !== undefined)
      post("setProcessRowValue", { index: parseInt(input.dataset.processIndex, 10), field: input.dataset.processField, value: input.value.trim() });
  });

  const addTelemetryBtn = document.getElementById("telemetry-add-btn");
  if (addTelemetryBtn) {
    addTelemetryBtn.addEventListener("click", () => {
      const path = document.getElementById("telemetry-add-path").value.trim();
      if (!path) return;
      const len = parseInt(document.getElementById("telemetry-add-len").value, 10) || 1;
      if (path.toUpperCase().includes("U") && path.toUpperCase().includes("\\G")) {
        post("addTelemetryBuffer", { path, length: len });
      } else {
        post("addTelemetryRegister", { register: path });
      }
      document.getElementById("telemetry-add-path").value = "";
    });
  }

  window.removeReg = (reg) => { post("removeTelemetryRegister", { register: reg }); };
  window.removeBuf = (path) => { post("removeTelemetryBuffer", { path: path }); };

  // CAD pan/zoom
  dom.cadPreview.addEventListener("click", e => {
    const t = e.target.closest("[data-point-key]"); if (!t) return;
    state.dxf.selectedPointKey = t.dataset.pointKey; renderPointsTable(); renderCadPreview();
    post("selectCadPoint", { key: state.dxf.selectedPointKey });
  });
  dom.cadPreview.addEventListener("wheel", e => {
    e.preventDefault(); const d = e.deltaY > 0 ? -0.1 : 0.1;
    const rect = dom.cadPreview.getBoundingClientRect();
    const mx = e.clientX - rect.left, my = e.clientY - rect.top;
    const old = cadZoom; cadZoom = Math.max(0.1, Math.min(10, cadZoom + d));
    const sc = cadZoom / old; cadPanX = mx - (mx - cadPanX) * sc; cadPanY = my - (my - cadPanY) * sc;
    updateCadTransform();
  });
  dom.cadPreview.addEventListener("mousedown", e => {
    if (e.button === 1 || (e.button === 0 && !e.target.closest("[data-point-key]"))) {
      e.preventDefault(); isCadPanning = true; startCadPanX = e.clientX - cadPanX; startCadPanY = e.clientY - cadPanY;
      dom.cadPreview.style.cursor = "grabbing";
    }
  });
  dom.cadPreview.addEventListener("mousemove", e => { if (!isCadPanning) return; cadPanX = e.clientX - startCadPanX; cadPanY = e.clientY - startCadPanY; updateCadTransform(); });
  dom.cadPreview.addEventListener("mouseup", () => { isCadPanning = false; dom.cadPreview.style.cursor = "grab"; });
  dom.cadPreview.addEventListener("mouseleave", () => { isCadPanning = false; dom.cadPreview.style.cursor = "grab"; });

  function updateCadTransform() {
    const g = document.getElementById("cad-transform-group");
    if (g) g.setAttribute("transform", `translate(${cadPanX},${cadPanY}) scale(${cadZoom})`);
  }

  if (dom.writeBufferButton) dom.writeBufferButton.addEventListener("click", () => {
    if (!state.control || !state.control.connection || !state.control.connection.connected) { showToast("error", "Telemetry", "Chưa kết nối PLC."); return; }
    post("writeBufferRequest", { path: dom.writeBufferPath.value.trim(), value: parseInt(dom.writeBufferValue.value, 10) || 0 });
  });
  const importBtn = document.getElementById("import-cad-to-process-button");
  if (importBtn) importBtn.addEventListener("click", () => post("importCadToProcess"));
  const sendBtn = document.getElementById("send-cad-x-button");
  if (sendBtn) sendBtn.addEventListener("click", () => {
    if (!state.control || !state.control.connection || !state.control.connection.connected) { showToast("error", "Telemetry", "Chưa kết nối PLC."); return; }
    post("sendCadX");
  });
  if (dom.clearLogsButton) dom.clearLogsButton.addEventListener("click", () => post("clearLogs"));
  if (dom.telemetryWatchBtn) {
    dom.telemetryWatchBtn.addEventListener("click", () => {
      const val = dom.telemetryWatchInput.value || "";
      const regs = val.split(",").map(s => s.trim()).filter(s => s.length > 0);
      post("setTelemetryWatchList", { registers: regs });
    });
  }
  dom.modalCancel.addEventListener("click", closePrompt);
  dom.modalConfirm.addEventListener("click", submitPrompt);
  dom.modal.addEventListener("click", e => { if (e.target === dom.modal) closePrompt(); });
  dom.modalInput.addEventListener("keydown", e => { if (e.key === "Enter") submitPrompt(); if (e.key === "Escape") closePrompt(); });
}

function handleHostMessage(msg) {
  if (!msg || !msg.type) return;
  switch (msg.type) {
    case "controlState":
      state.control = msg.payload || state.control;
      state.view = state.control.view || state.view;
      state.theme = state.control.theme || state.theme;
      applyTheme(state.theme); applyView(state.view); renderControl(); break;
    case "dxfState":
      state.dxf = msg.payload || state.dxf;
      state.view = state.dxf.view || state.view;
      state.theme = state.dxf.theme || state.theme;
      applyTheme(state.theme); applyView(state.view); renderDxf(); break;
    case "telemetry":
      state.telemetry = msg.payload || {}; renderTelemetry(); break;
    case "logsState":
      state.logs = (msg.payload && msg.payload.logs) || []; renderLogs(); break;
    case "eventsState":
      state.control.events = (msg.payload && msg.payload.events) || []; renderEvents(); break;
    case "notify":
      showToast(msg.payload.kind, msg.payload.title, msg.payload.message);
      addLocalEvent(msg.payload.kind, msg.payload.title, msg.payload.message);
      break;
  }
}

function renderControl() {
  const conn = state.control.connection || {};
  syncInputValue(dom.plcStation, conn.station != null ? String(conn.station) : "0");
  dom.connectButton.textContent = conn.buttonText || "CONNECT PLC Q";
  dom.plcStatusDot.classList.toggle("connected", !!conn.connected);
  dom.plcStatusDot.classList.toggle("disconnected", !conn.connected);
  dom.plcStatusText.textContent = conn.connected ? "OK" : "DC";
  dom.connectionMeta.textContent = conn.meta || "";
  dom.sidebarStatus.textContent = conn.connected ? "Mitsu: OK" : "Mitsu: DC";
  
  const jogSpeedD406 = state.control.jogSpeedD406;
  if (jogSpeedD406 != null) {
    const jogInput = document.getElementById("jog-speed-input");
    if (jogInput) syncInputValue(jogInput, String(jogSpeedD406));
  }



  // Render 4 axes dynamically
  const axes = state.control.axes || [];
  const accents = ['accent-axis-1', 'accent-axis-2', 'accent-axis-3', 'accent-axis-4'];
  const fields = [
    { key: 'currentPos', label: 'CURRENT POSITION (mm)', addrKey: 'currentPosAddr', big: true },
    { key: 'currentSpeed', label: 'CURRENT SPEED (mm/min)', addrKey: 'currentSpeedAddr', big: true },
    { key: 'errorCode', label: 'ERROR CODE', addrKey: 'errorCodeAddr' },
    { key: 'warningCode', label: 'WARNING CODE', addrKey: 'warningCodeAddr' },
    { key: 'axisStatus', label: 'AXIS STATUS', addrKey: 'axisStatusAddr' },
    { key: 'startNo', label: 'START NO.', addrKey: 'startNoAddr' },

  ];
  const grid = document.getElementById('axis-grid');
  if (grid) {
    grid.innerHTML = axes.map((a, i) => {
      const n = a.index || (i + 1);
      const rows = fields.map(f => {
        const val = a[f.key] || '--';
        const addr = a[f.addrKey] || '';
        const cls = f.big ? 'axis-field-value' : 'axis-field-value sm';
        return `<div class="axis-field"><div class="axis-field-label">${esc(f.label)} <span class="axis-addr">${esc(addr)}</span></div><div class="${cls}">${esc(val)}</div></div>`;
      }).join('');
      return `<div class="axis-card"><div class="axis-header ${accents[i] || ''}">AXIS ${n}</div><div class="axis-body">${rows}</div></div>`;
    }).join('');
  }
  renderEvents();
  updateNavState();
}

function renderEvents() {
  const events = state.control.events || [];
  if (!dom.eventsList) return;
  if (events.length === 0) {
    dom.eventsList.innerHTML = '<div class="events-empty">No events yet.</div>';
    return;
  }
  dom.eventsList.innerHTML = events.slice(0, 50).map(ev =>
    `<div class="event-row"><span class="event-time">${esc(ev.time || "")}</span><span class="event-tag ${esc(ev.kind || "info")}">${esc(ev.tag || ev.kind || "Info")}</span><span class="event-msg">${esc(ev.message || "")}</span></div>`
  ).join("");
}

function addLocalEvent(kind, title, message) {
  if (!state.control.events) state.control.events = [];
  const now = new Date();
  const time = now.toTimeString().substring(0, 5);
  state.control.events.unshift({ time, kind: kind || "info", tag: title || "Info", message: message || "" });
  if (state.control.events.length > 100) state.control.events.length = 100;
  renderEvents();
}

function renderDxf() {
  syncInputValue(dom.cadPath, state.dxf.filePath || "");
  syncInputValue(dom.cadFile, state.dxf.fileName || "");
  const sendBtn = document.getElementById("send-cad-x-button");
  if (sendBtn) sendBtn.disabled = !(state.control && state.control.connection && state.control.connection.connected);
  renderPointsTable(); renderProcessTable(); renderCadPreview(); updateNavState();
}

function renderPointsTable() {
  const points = state.dxf.points || [], primitives = state.dxf.primitives || [], rows = [];
  function findPK(x, y) { const p = points.find(p => Math.abs((p.x || 0) - x) < 1e-3 && Math.abs((p.y || 0) - y) < 1e-3); return p ? p.key : ""; }
  function findPI(x, y) { const p = points.find(p => Math.abs((p.x || 0) - x) < 1e-3 && Math.abs((p.y || 0) - y) < 1e-3); return p && p.index != null ? p.index : ""; }
  let ai = 1;
  for (const prim of primitives) {
    if (!prim.points || !prim.points.length) continue;
    let dt = "Line";
    if ((prim.sourceType || "").toLowerCase().includes("arc")) dt = "Arc";
    if ((prim.sourceType || "").toLowerCase().includes("circle")) dt = "Circle";
    let cx = "", cy = "";
    if (prim.center) { cx = Number(prim.center.x).toFixed(3); cy = Number(prim.center.y).toFixed(3); }
    if (dt === "Line") {
      for (let j = 0; j < prim.points.length - 1; j++) {
        const s = prim.points[j], e = prim.points[j + 1], key = findPK(s.x, s.y), stt = findPI(s.x, s.y) || ai++;
        rows.push(`<tr data-point-key="${esc(key)}"><td>${esc(stt)}</td><td>${esc(dt)}</td><td>${Number(s.x).toFixed(3)}</td><td>${Number(s.y).toFixed(3)}</td><td>${Number(e.x).toFixed(3)}</td><td>${Number(e.y).toFixed(3)}</td><td></td><td></td></tr>`);
      }
    } else {
      const s = prim.points[0], e = prim.points[prim.points.length - 1], key = findPK(s.x, s.y), stt = findPI(s.x, s.y) || ai++;
      rows.push(`<tr data-point-key="${esc(key)}"><td>${esc(stt)}</td><td>${esc(dt)}</td><td>${Number(s.x).toFixed(3)}</td><td>${Number(s.y).toFixed(3)}</td><td>${Number(e.x).toFixed(3)}</td><td>${Number(e.y).toFixed(3)}</td><td>${esc(cx)}</td><td>${esc(cy)}</td></tr>`);
    }
  }
  dom.pointsBody.innerHTML = rows.join("");
}

function renderProcessTable() {
  const rows = state.dxf.processRows || [];
  dom.processBody.innerHTML = rows.map((r, i) => `<tr><td>${esc(r.motionType || "")}</td><td><input type="text" class="text-input compact" style="margin:0;width:100%;min-width:80px" data-process-index="${i}" data-process-field="mcode" value="${esc(r.mCodeValue || "")}"></td><td><input type="text" class="text-input compact" style="margin:0;width:100%;min-width:60px" data-process-index="${i}" data-process-field="dwell" value="${esc(r.dwell || "")}"></td><td><input type="text" class="text-input compact" style="margin:0;width:100%;min-width:60px" data-process-index="${i}" data-process-field="speed" value="${esc(r.speed || "")}"></td><td>${esc(r.endCoordinate || "")}</td><td>${esc(r.centerCoordinate || "")}</td></tr>`).join("");
}

function renderCadPreview() {
  const primitives = state.dxf.primitives || [], points = state.dxf.points || [], bounds = state.dxf.bounds || { left: 0, top: 0, width: 100, height: 100 };
  if (!primitives.length) { dom.cadPreview.innerHTML = ""; dom.cadPlaceholder.classList.remove("hidden"); return; }
  dom.cadPlaceholder.classList.add("hidden");
  const W = 1000, H = 560, pad = 28, ww = Math.max(bounds.width || 0, 1), wh = Math.max(bounds.height || 0, 1);
  const sc = Math.min((W - pad * 2) / ww, (H - pad * 2) / wh), ox = (W - ww * sc) / 2, oy = (H - wh * sc) / 2;
  const proj = p => ({ x: ox + (p.x - bounds.left) * sc, y: H - oy - (p.y - bounds.top) * sc });
  const polyM = primitives.map(pr => { const pa = (pr.points || []).map(p => { const pp = proj(p); return `${pp.x.toFixed(2)},${pp.y.toFixed(2)}`; }).join(" "); return `<polyline class="cad-line" points="${pa}"></polyline>`; }).join("");
  const ptM = points.map(p => { const pp = proj(p); const sel = p.key === state.dxf.selectedPointKey ? "is-selected" : ""; return `<circle class="cad-point ${sel}" cx="${pp.x.toFixed(2)}" cy="${pp.y.toFixed(2)}" r="4.8" data-point-key="${esc(p.key || "")}"></circle>`; }).join("");
  const aM = Object.entries(state.dxf.assignedPointKeys || {}).map(([slot, key]) => { const p = points.find(i => i.key === key); if (!p) return ""; const pp = proj(p); const t = getAssignmentTone(slot); return `<circle cx="${pp.x.toFixed(2)}" cy="${pp.y.toFixed(2)}" r="10.5" fill="${t.fill}" stroke="white" stroke-width="1.8"></circle><text class="cad-assignment-text" x="${pp.x.toFixed(2)}" y="${pp.y.toFixed(2)}">${t.label}</text>`; }).join("");
  dom.cadPreview.innerHTML = `<g id="cad-transform-group" transform="translate(${cadPanX},${cadPanY}) scale(${cadZoom})"><g>${polyM}</g><g>${ptM}</g><g>${aM}</g></g>`;
}

function renderTelemetry() {
  const t = state.telemetry || {}, connected = t.connected, dValues = t.dValues || [], buffers = t.buffers || [];
  if (dom.writeBufferButton) dom.writeBufferButton.disabled = !connected;
  const sendBtn = document.getElementById("send-cad-x-button"); if (sendBtn) sendBtn.disabled = !connected;
  if (!dom.telemetryContent) return;

  const rows = [`<div class="telemetry-header">Telemetry (${connected ? "connected" : "disconnected"})</div>`];

  if (dValues.length) {
    rows.push('<div class="telemetry-section"><div class="telemetry-title">D registers</div><table class="telemetry-table data-table compact"><thead><tr><th>Register</th><th>Value</th><th>Status</th><th style="width: 40px"></th></tr></thead><tbody>');
    dValues.forEach(i => {
      const reg = esc(i.register || "");
      const val = esc(i.value != null ? String(i.value) : "");
      const stat = esc(i.ok ? "OK" : (i.error || "ERR"));
      const oc = `onclick="document.getElementById('write-buffer-path').value='${reg}'; document.getElementById('write-buffer-value').value='${val}'; document.getElementById('write-buffer-value').focus(); document.getElementById('write-buffer-value').select();"`;
      const delBtn = `<button class="secondary-button compact" style="padding: 2px 6px" onclick="event.stopPropagation(); window.removeReg('${reg}')">X</button>`;
      rows.push(`<tr ${oc} style="cursor:pointer" title="Click to overwrite"><td>${reg}</td><td>${val}</td><td>${stat}</td><td>${delBtn}</td></tr>`);
    });
    rows.push("</tbody></table></div>");
  }

  if (buffers.length) {
    rows.push('<div class="telemetry-section"><div class="telemetry-title">Buffers (Un\\Gx)</div><table class="telemetry-table data-table compact"><thead><tr><th>Buffer Path</th><th>Values</th><th>Status</th><th style="width: 40px"></th></tr></thead><tbody>');
    buffers.forEach(b => {
      const path = esc(b.path || "");
      const vArr = b.values || [];
      const v = vArr.map(val => esc(String(val))).join(", ");
      const stat = esc(b.ok ? "OK" : (b.error || "ERR"));
      const firstVal = vArr.length > 0 ? esc(String(vArr[0])) : "";
      const oc = `onclick="document.getElementById('write-buffer-path').value='${path}'; document.getElementById('write-buffer-value').value='${firstVal}'; document.getElementById('write-buffer-value').focus(); document.getElementById('write-buffer-value').select();"`;
      const delBtn = `<button class="secondary-button compact" style="padding: 2px 6px" onclick="event.stopPropagation(); window.removeBuf('${path}')">X</button>`;
      rows.push(`<tr ${oc} style="cursor:pointer" title="Click to overwrite (first value)"><td>${path}</td><td style="max-width:300px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">[${v}]</td><td>${stat}</td><td>${delBtn}</td></tr>`);
    });
    rows.push("</tbody></table></div>");
  }

  dom.telemetryContent.innerHTML = rows.join("");
}

function renderLogs() {
  const rows = state.logs || []; if (!dom.logsBody) return;
  dom.logsBody.innerHTML = rows.map(r => `<tr><td>${esc(r.timestamp || "")}</td><td>${esc(r.direction || "")}</td><td>${esc(r.address || "")}</td><td>${esc(r.value != null ? String(r.value) : "")}</td><td>${esc(r.status || "")}</td><td>${esc(r.message || "")}</td></tr>`).join("");
  dom.logsEmpty.classList.toggle("hidden", rows.length > 0);
}

function applyTheme(t) { dom.html.classList.toggle("theme-dark", t === "dark"); dom.html.classList.toggle("theme-light", t !== "dark"); dom.themeToggle.textContent = t === "dark" ? "◐" : "◑"; }
function applyView(v) {
  state.view = v;
  dom.viewControl && dom.viewControl.classList.toggle("is-active", v === "control");
  dom.viewLogs && dom.viewLogs.classList.toggle("is-active", v === "logs");
  dom.viewTelemetry && dom.viewTelemetry.classList.toggle("is-active", v === "telemetry");
  dom.viewDxf && dom.viewDxf.classList.toggle("is-active", v === "dxf");
  updateNavState();
}
function updateNavState() { const s = b => { b.classList.toggle("is-active", b.dataset.view === state.view); }; dom.topViewButtons.forEach(s); dom.sideViewButtons.forEach(s); }

function openPrompt(title, label, val, onSubmit) { modalSubmit = onSubmit; dom.modalTitle.textContent = title; dom.modalLabel.textContent = label; dom.modalInput.value = val || ""; dom.modal.classList.remove("hidden"); dom.modalInput.focus(); dom.modalInput.select(); }
function closePrompt() { modalSubmit = null; dom.modal.classList.add("hidden"); }
function submitPrompt() { if (typeof modalSubmit === "function") modalSubmit(dom.modalInput.value.trim()); closePrompt(); }

function showToast(kind, title, message) {
  const t = document.createElement("div"); t.className = `toast ${kind || "info"}`;
  t.innerHTML = `<div class="toast-title">${esc(title || "Message")}</div><div class="toast-message">${esc(message || "")}</div>`;
  dom.toastContainer.appendChild(t); setTimeout(() => t.remove(), 4200);
}

function post(action, payload = {}) { if (host) host.postMessage({ action, payload }); }
function syncInputValue(input, value) { if (!input || document.activeElement === input) return; input.value = value; }
function setText(id, value) { const el = document.getElementById(id); if (el) el.textContent = value; }
function getAssignmentTone(slot) { switch (slot) { case "start": return { fill: "#22c55e", label: "S" }; case "glueStart": return { fill: "#f59e0b", label: "B" }; case "glueEnd": return { fill: "#ef4444", label: "E" }; default: return { fill: "#94a3b8", label: "?" }; } }
function esc(v) { return String(v).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;").replace(/'/g, "&#39;"); }

// Dev mode mock data
if (!host) {
  setTimeout(() => {
    handleHostMessage({
      type: "controlState", payload: {
        view: "control", theme: "dark",
        connection: { connected: false, station: 0, banner: "PLC disconnected", meta: "MX Component logical station: 0", buttonText: "CONNECT PLC Q" },
        axes: [
          {
            index: 1, currentPos: "--", currentSpeed: "--", errorCode: "--", warningCode: "--", axisStatus: "--", startNo: "--", errorReset: "--", jogSpeed: "--", newSpeed: "--",
            currentPosAddr: "D0", currentSpeedAddr: "D4", errorCodeAddr: "U0\\G806", warningCodeAddr: "U0\\G807", axisStatusAddr: "U0\\G814",
            startNoAddr: "U0\\G1500", errorResetAddr: "U0\\G1502", jogSpeedAddr: "D406", newSpeedAddr: "U0\\G1518"
          },
          {
            index: 2, currentPos: "--", currentSpeed: "--", errorCode: "--", warningCode: "--", axisStatus: "--", startNo: "--", errorReset: "--", jogSpeed: "--", newSpeed: "--",
            currentPosAddr: "D10", currentSpeedAddr: "D14", errorCodeAddr: "U0\\G906", warningCodeAddr: "U0\\G907", axisStatusAddr: "U0\\G914",
            startNoAddr: "U0\\G1600", errorResetAddr: "U0\\G1602", jogSpeedAddr: "D406", newSpeedAddr: "U0\\G1618"
          },
          {
            index: 3, currentPos: "--", currentSpeed: "--", errorCode: "--", warningCode: "--", axisStatus: "--", startNo: "--", errorReset: "--", jogSpeed: "--", newSpeed: "--",
            currentPosAddr: "D20", currentSpeedAddr: "D24", errorCodeAddr: "U0\\G1006", warningCodeAddr: "U0\\G1007", axisStatusAddr: "U0\\G1014",
            startNoAddr: "U0\\G1700", errorResetAddr: "U0\\G1702", jogSpeedAddr: "D406", newSpeedAddr: "U0\\G1718"
          },
          {
            index: 4, currentPos: "--", currentSpeed: "--", errorCode: "--", warningCode: "--", axisStatus: "--", startNo: "--", errorReset: "--", jogSpeed: "--", newSpeed: "--",
            currentPosAddr: "D30", currentSpeedAddr: "D34", errorCodeAddr: "U0\\G1106", warningCodeAddr: "U0\\G1107", axisStatusAddr: "U0\\G1114",
            startNoAddr: "U0\\G1800", errorResetAddr: "U0\\G1802", jogSpeedAddr: "D406", newSpeedAddr: "U0\\G1818"
          }
        ],
        events: [
          { time: "08:51", kind: "security", tag: "Security", message: "Administrator logged in successfully." }
        ]
      }
    });
  }, 300);
}
