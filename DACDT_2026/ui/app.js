const host = window.chrome && window.chrome.webview ? window.chrome.webview : null;
const state = {
  view: "control", theme: "dark",
  control: {
    connection: { connected: false, station: 0, banner: "PLC disconnected", meta: "MX Component logical station: 0", buttonText: "CONNECT PLC Q" },
    axes: [
      { index: 1, currentPos: "--", currentSpeed: "--", errorCode: "--", warningCode: "--", axisStatus: "--", currentDataNo: "--", lastDataNo: "--", errorReset: "--", jogSpeed: "--", newSpeed: "--" },
      { index: 2, currentPos: "--", currentSpeed: "--", errorCode: "--", warningCode: "--", axisStatus: "--", currentDataNo: "--", lastDataNo: "--", errorReset: "--", jogSpeed: "--", newSpeed: "--" },
      { index: 3, currentPos: "--", currentSpeed: "--", errorCode: "--", warningCode: "--", axisStatus: "--", currentDataNo: "--", lastDataNo: "--", errorReset: "--", jogSpeed: "--", newSpeed: "--" },
      { index: 4, currentPos: "--", currentSpeed: "--", errorCode: "--", warningCode: "--", axisStatus: "--", currentDataNo: "--", lastDataNo: "--", errorReset: "--", jogSpeed: "--", newSpeed: "--" }
    ],
    events: []
  },
  dxf: { fileKind: "DXF", filePath: "", fileName: "", bounds: { left: 0, top: 0, width: 100, height: 100 }, primitives: [], points: [], selectedPointKey: "", assignedPointKeys: {}, processRows: [] },
  telemetry: {}, logs: []
};

const dom = {};
let modalSubmit = null;
let cadPanX = 0, cadPanY = 0, cadZoom = 1, isCadPanning = false, startCadPanX = 0, startCadPanY = 0;

// Global function to update G-code line numbers
function updateGcodeLineNumbers() {
  const gcodeTa = document.getElementById("gcode-textarea");
  const gcodeLineNumbers = document.getElementById("gcode-line-numbers");
  if (!gcodeTa || !gcodeLineNumbers) return;
  const lines = gcodeTa.value.split('\n');
  const lineCount = lines.length;
  let html = '';
  for (let i = 1; i <= lineCount; i++) {
    html += i + '\n';
  }
  gcodeLineNumbers.textContent = html;
}

// Global function to highlight active G-code line
let lastHighlightedLine = -1;
function highlightGcodeLine(lineNumber) {
  const gcodeTa = document.getElementById("gcode-textarea");
  const gcodeLineNumbers = document.getElementById("gcode-line-numbers");
  
  if (!gcodeTa || !gcodeLineNumbers) return;
  if (lineNumber === lastHighlightedLine) return; // No change
  
  lastHighlightedLine = lineNumber;
  
  // Remove previous highlight
  gcodeTa.classList.remove('gcode-line-active');
  gcodeLineNumbers.classList.remove('gcode-line-number-active');
  
  if (lineNumber <= 0) return; // No active line
  
  const lines = gcodeTa.value.split('\n');
  if (lineNumber > lines.length) return; // Invalid line number
  
  // Scroll to line (line numbers are 1-indexed)
  const lineHeight = 18; // 12px font-size * 1.5 line-height
  const scrollTop = (lineNumber - 1) * lineHeight;
  const viewportHeight = gcodeTa.clientHeight;
  const centerOffset = viewportHeight / 2 - lineHeight;
  
  gcodeTa.scrollTop = Math.max(0, scrollTop - centerOffset);
  gcodeLineNumbers.scrollTop = gcodeTa.scrollTop;
  
  // Highlight line number (visual feedback only - can't highlight specific line in textarea)
  // We'll use a different approach: wrap line numbers in spans
  updateGcodeLineNumbersWithHighlight(lineNumber);
}

// Update line numbers with highlight support
function updateGcodeLineNumbersWithHighlight(activeLine) {
  const gcodeTa = document.getElementById("gcode-textarea");
  const gcodeLineNumbers = document.getElementById("gcode-line-numbers");
  if (!gcodeTa || !gcodeLineNumbers) return;
  
  const lines = gcodeTa.value.split('\n');
  const lineCount = lines.length;
  let html = '';
  
  for (let i = 1; i <= lineCount; i++) {
    if (i === activeLine) {
      html += `<span class="gcode-line-number-active">${i}</span>\n`;
    } else {
      html += i + '\n';
    }
  }
  
  gcodeLineNumbers.innerHTML = html;
}

window.app = { receive(m) { handleHostMessage(m || {}); } };
if (host) {
  host.addEventListener('message', e => {
    handleHostMessage(e.data || {});
  });
}
// Expose state globally for cad3d.js access
window.cadState = state;

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
  dom.offsetXInput = document.getElementById("offset-x-input");
  dom.offsetYInput = document.getElementById("offset-y-input");
  dom.applyOffsetBtn = document.getElementById("apply-offset-btn");
  dom.sendRowCount   = document.getElementById("send-row-count");
  dom.viewSettings   = document.getElementById("view-settings");
  dom.viewHelp       = document.getElementById("view-help");
}

function bindEvents() {
  dom.topViewButtons.forEach(b => b.addEventListener("click", () => {
    state.view = b.dataset.view; applyView(state.view); post("switchView", { view: state.view });
  }));
  dom.sideViewButtons.forEach(b => b.addEventListener("click", () => {
    state.view = b.dataset.view; applyView(state.view); post("switchView", { view: state.view });
  }));
  dom.placeholderButtons.forEach(b => b.addEventListener("click", () => showToast("info", b.dataset.placeholder, "This feature is a placeholder.")));
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

  // RUN button (hold to run, same as START ACTION)
  const runActionBtn = document.getElementById("run-action-btn");
  if (runActionBtn) {
    const stopRun = () => post("startActionStop");
    runActionBtn.addEventListener("pointerdown", e => { if (e.button !== 0) return; post("startActionStart"); });
    runActionBtn.addEventListener("pointerup", stopRun);
    runActionBtn.addEventListener("pointerleave", stopRun);
    runActionBtn.addEventListener("pointercancel", stopRun);
  }

  // HOME button (hold, M503)
  const dxfHomeBtn = document.getElementById("dxf-home-btn");
  if (dxfHomeBtn) {
    const stopHome = () => post("goHomeStop");
    dxfHomeBtn.addEventListener("pointerdown", e => { if (e.button !== 0) return; post("goHomeStart"); });
    dxfHomeBtn.addEventListener("pointerup", stopHome);
    dxfHomeBtn.addEventListener("pointerleave", stopHome);
    dxfHomeBtn.addEventListener("pointercancel", stopHome);
  }

  // RESET button (hold, M300)
  const dxfResetBtn = document.getElementById("dxf-reset-btn");
  if (dxfResetBtn) {
    const stopReset = () => post("resetErrorStop");
    dxfResetBtn.addEventListener("pointerdown", e => { if (e.button !== 0) return; post("resetErrorStart"); });
    dxfResetBtn.addEventListener("pointerup", stopReset);
    dxfResetBtn.addEventListener("pointerleave", stopReset);
    dxfResetBtn.addEventListener("pointercancel", stopReset);
  }

  const saveGcodeBtn = document.getElementById("save-gcode-btn");
  const gcodeTa = document.getElementById("gcode-textarea");
  const gcodeLineNumbers = document.getElementById("gcode-line-numbers");
  
  // Sync scroll between line numbers and textarea
  if (gcodeTa && gcodeLineNumbers) {
    gcodeTa.addEventListener('scroll', () => {
      gcodeLineNumbers.scrollTop = gcodeTa.scrollTop;
    });
  }
  
  let gcodeTimeout;
  let isSavingGcode = false; // Block preview khi đang save dialog
  if (saveGcodeBtn && gcodeTa) {
    saveGcodeBtn.addEventListener("click", () => {
      isSavingGcode = true;
      clearTimeout(gcodeTimeout);
      post("saveGcode", { text: gcodeTa.value.toUpperCase() });
      // Release sau 5s (save nhanh, không cần dialog)
      setTimeout(() => { isSavingGcode = false; }, 5000);
    });
    gcodeTa.addEventListener("input", function() {
      const start = this.selectionStart;
      const end = this.selectionEnd;
      const upper = this.value.toUpperCase();
      if (this.value !== upper) {
        this.value = upper;
        this.setSelectionRange(start, end);
      }
      updateGcodeLineNumbers();
      clearTimeout(gcodeTimeout);
      if (isSavingGcode) return; // Bỏ qua preview khi đang save
      gcodeTimeout = setTimeout(() => {
        post("previewGcode", { text: this.value });
      }, 400);
    });
  }
  const newGcodeBtn = document.getElementById("new-gcode-btn");
  if (newGcodeBtn) {
    newGcodeBtn.addEventListener("click", () => {
      post("newGcode");
    });
  }
  dom.assignButtons.forEach(b => b.addEventListener("click", () => {
    if (!state.dxf.selectedPointKey) { showToast("info", "DXF", "Please select a point before assigning."); return; }
    post("assignPoint", { slot: b.dataset.assignSlot, key: state.dxf.selectedPointKey });
  }));
  dom.pointsBody.addEventListener("click", e => {
    const row = e.target.closest("[data-point-key]"); if (!row) return;
    state.dxf.selectedPointKey = row.dataset.pointKey; renderPointsTable(); renderCadPreview();
    post("selectCadPoint", { key: state.dxf.selectedPointKey });
    // Cập nhật bảng process khi tương tác với bảng trên
    renderProcessTable();
  });
  // Click vào panel points hoặc gcode editor → cập nhật bảng process
  const dxfPointsContainer = document.getElementById("dxf-points-container");
  if (dxfPointsContainer) dxfPointsContainer.addEventListener("click", () => renderProcessTable());
  const gcodeEditorContainer = document.getElementById("gcode-editor-container");
  if (gcodeEditorContainer) gcodeEditorContainer.addEventListener("click", () => renderProcessTable());

  dom.processBody.addEventListener("change", e => {
    const input = e.target; if (input.tagName === "INPUT" && input.dataset.processIndex !== undefined)
      post("setProcessRowValue", { index: parseInt(input.dataset.processIndex, 10), field: input.dataset.processField, value: input.value.trim() });
  });
  dom.processBody.addEventListener("click", e => {
    const tr = e.target.closest("tr");
    if (!tr) return;
    Array.from(dom.processBody.querySelectorAll("tr")).forEach(r => r.classList.remove("is-selected"));
    tr.classList.add("is-selected");
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
    if (!state.control || !state.control.connection || !state.control.connection.connected) { showToast("error", "Telemetry", "PLC not connected."); return; }
    post("writeBufferRequest", { path: dom.writeBufferPath.value.trim(), value: parseInt(dom.writeBufferValue.value, 10) || 0 });
  });
  const importBtn = document.getElementById("import-cad-to-process-button");
  if (importBtn) importBtn.addEventListener("click", () => post("importCadToProcess"));
  
  const sendBtn = document.getElementById("send-cad-x-button");
  if (sendBtn) sendBtn.addEventListener("click", () => {
    if (!state.control || !state.control.connection || !state.control.connection.connected) { showToast("error", "Telemetry", "PLC not connected."); return; }
    post("sendCadX");
  });
  
  const clearBufferBtn = document.getElementById("clear-buffer-button");
  if (clearBufferBtn) clearBufferBtn.addEventListener("click", () => {
    if (!state.control || !state.control.connection || !state.control.connection.connected) { 
      showToast("error", "Clear Buffer", "PLC not connected."); 
      return; 
    }
    if (confirm("Clear all PLC buffer (G2000+, G8000+, G14000+)?\n\nThis will erase all data sent to PLC.")) {
      post("clearBuffer");
    }
  });
  
  if (dom.applyOffsetBtn) {
    dom.applyOffsetBtn.addEventListener("click", () => {
      const x = parseFloat(dom.offsetXInput ? dom.offsetXInput.value : 0) || 0;
      const y = parseFloat(dom.offsetYInput ? dom.offsetYInput.value : 0) || 0;
      post("setOffset", { x, y });
      const spdInput = document.getElementById("global-speed-input");
      if (spdInput) {
        post("setProcessValue", { key: "speed", value: spdInput.value });
      }
      const dwellM3 = document.getElementById("dwell-m3-input");
      if (dwellM3) {
        post("setProcessValue", { key: "dwellM3", value: dwellM3.value });
      }
      const dwellM4 = document.getElementById("dwell-m4-input");
      if (dwellM4) {
        post("setProcessValue", { key: "dwellM4", value: dwellM4.value });
      }
      const st = document.getElementById("offset-status");
      if (st) { st.textContent = "✓ Applied"; setTimeout(() => { st.textContent = ""; }, 3000); }
    });
  }
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

  // ── Settings buttons ─────────────────────────────────────────────────────
  const g0SpeedBtn = document.getElementById("set-g0-speed-btn");
  if (g0SpeedBtn) {
    g0SpeedBtn.addEventListener("click", () => {
      const val = parseInt((document.getElementById("g0-speed-input") || {}).value, 10);
      if (!val || val < 1) return;
      post("setG0Speed", { value: val });
      const st = document.getElementById("g0-speed-status");
      if (st) { st.textContent = "✓ " + val + " mm/min"; setTimeout(() => { st.textContent = ""; }, 3000); }
    });
  }

  const workspaceBtn = document.getElementById("set-workspace-btn");
  if (workspaceBtn) {
    workspaceBtn.addEventListener("click", () => {
      const w = parseFloat((document.getElementById("workspace-width-input") || {}).value) || 170;
      const h = parseFloat((document.getElementById("workspace-height-input") || {}).value) || 170;
      post("setWorkspace", { width: w, height: h });
      const st = document.getElementById("workspace-status");
      if (st) { st.textContent = "✓ " + w + " × " + h + " mm"; setTimeout(() => { st.textContent = ""; }, 3000); }
    });
  }

  const wcsBtn = document.getElementById("set-wcs-btn");
  const wcsSelect = document.getElementById("wcs-select");
  const wcsOxInput = document.getElementById("wcs-offset-x");
  const wcsOyInput = document.getElementById("wcs-offset-y");

  // Khi đổi dropdown WCS → load giá trị offset đã lưu cho WCS đó
  if (wcsSelect) {
    wcsSelect.addEventListener("change", () => {
      const wcs = wcsSelect.value || "G54";
      const wcsData = state.dxf && state.dxf.wcsOffsets ? state.dxf.wcsOffsets : {};
      const ox = (wcsData[wcs] && wcsData[wcs].x) || 0;
      const oy = (wcsData[wcs] && wcsData[wcs].y) || 0;
      if (wcsOxInput) wcsOxInput.value = ox;
      if (wcsOyInput) wcsOyInput.value = oy;
    });
  }

  if (wcsBtn) {
    wcsBtn.addEventListener("click", () => {
      const wcs = (wcsSelect || {}).value || "G54";
      const ox = parseFloat((wcsOxInput || {}).value) || 0;
      const oy = parseFloat((wcsOyInput || {}).value) || 0;
      post("setWcsOffset", { wcs, x: ox, y: oy });
      const st = document.getElementById("wcs-status");
      if (st) { st.textContent = "✓ " + wcs + " X=" + ox + " Y=" + oy; setTimeout(() => { st.textContent = ""; }, 3000); }
    });
  }
}

// ── App Controls ─────────────────────────────────────────────────────────
function initLogin() {
  const exitBtn = document.getElementById("exit-app-btn");
  if (exitBtn) {
    exitBtn.addEventListener("click", () => {
      if (confirm("Exit application?")) {
        post("exitApp");
      }
    });
  }
}

function applyPermissions() {}

document.addEventListener("DOMContentLoaded", () => { initLogin(); });

function handleHostMessage(msg) {
  if (!msg || !msg.type) return;
  switch (msg.type) {
    case "controlState":
      const prevEvents = (state.control && state.control.events) ? state.control.events : [];
      state.control = msg.payload || state.control;
      // Giữ lại events cũ — không để server ghi đè (server luôn gửi events=[])
      if (!state.control.events || state.control.events.length === 0)
        state.control.events = prevEvents;
      else
        state.control.events = state.control.events.concat(prevEvents).slice(0, 500);
      state.view = state.control.view || state.view;
      state.theme = state.control.theme || state.theme;
      applyTheme(state.theme); applyView(state.view); renderControl(); break;
    case "dxfState":
      state.dxf = msg.payload || state.dxf;
      state.view = state.dxf.view || state.view;
      state.theme = state.dxf.theme || state.theme;
      applyTheme(state.theme); applyView(state.view); renderDxf();
      break;
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
    case "log":
      addLocalEvent("info", msg.payload.title, msg.payload.message);
      break;
    case "progress":
      if (msg.payload) {
        const container = document.getElementById("dxf-progress-container");
        const fill = document.getElementById("dxf-progress-fill");
        const text = document.getElementById("dxf-progress-text");
        const btn = document.getElementById("send-cad-x-button");
        if (container && fill && text) {
          if (msg.payload.visible) {
            container.style.display = "flex";
            if (btn) btn.style.display = "none";
            const p = Math.max(0, Math.min(100, msg.payload.percent || 0));
            fill.style.width = p + "%";
            text.textContent = Math.round(p) + "%";
          } else {
            container.style.display = "none";
            if (btn) btn.style.display = "block";
          }
        }
      }
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



  // Render 3 axes + Program Monitor panel
  const axes = state.control.axes || [];
  const accents = ['accent-axis-1', 'accent-axis-2', 'accent-axis-3'];
  const fields = [
    { key: 'currentPos', label: 'CURRENT POSITION (mm)', addrKey: 'currentPosAddr', big: true },
    { key: 'currentSpeed', label: 'CURRENT SPEED (mm/min)', addrKey: 'currentSpeedAddr', big: true },
    { key: 'mCode', label: 'CURRENT M CODE', addrKey: 'mCodeAddr' },
    { key: 'errorCode', label: 'ERROR CODE', addrKey: 'errorCodeAddr' },
    { key: 'warningCode', label: 'WARNING CODE', addrKey: 'warningCodeAddr' },
    { key: 'axisStatus', label: 'AXIS STATUS', addrKey: 'axisStatusAddr' },
    { key: 'currentDataNo', label: 'MD.44 CURR DATA NO.', addrKey: 'currentDataNoAddr' },
    { key: 'lastDataNo', label: 'MD.46 LAST DATA NO.', addrKey: 'lastDataNoAddr' },
  ];
  const grid = document.getElementById('axis-grid');
  if (grid) {
    // Render 3 axis cards
    let html = axes.slice(0, 3).map((a, i) => {
      const n = a.index || (i + 1);
      const rows = fields.map(f => {
        const val = a[f.key] || '--';
        const addr = a[f.addrKey] || '';
        const cls = f.big ? 'axis-field-value' : 'axis-field-value sm';
        return `<div class="axis-field"><div class="axis-field-label">${esc(f.label)} <span class="axis-addr">${esc(addr)}</span></div><div class="${cls}">${esc(val)}</div></div>`;
      }).join('');
      return `<div class="axis-card"><div class="axis-header ${accents[i] || ''}">AXIS ${n}</div><div class="axis-body">${rows}</div></div>`;
    }).join('');

    // Program Monitor panel (thay Axis 4)
    const processRows = state.dxf && state.dxf.processRows ? state.dxf.processRows : [];
    const currentLine = (axes[0] && axes[0].currentDataNo && axes[0].currentDataNo !== "--") ? parseInt(axes[0].currentDataNo, 10) : 0;

    // Chỉ rebuild Program Monitor khi data thay đổi (tránh reset scroll mỗi 50ms)
    const progKey = processRows.length + '_' + currentLine;
    const existingMonitor = document.getElementById('program-monitor-body');
    if (!existingMonitor || existingMonitor.dataset.progKey !== progKey) {
      let progHtml = '<div class="axis-card" style="display:flex;flex-direction:column;overflow:visible;"><div class="axis-header accent-axis-4">PROGRAM MONITOR</div><div style="padding:0;overflow-y:auto;max-height:400px;" id="program-monitor-body" data-prog-key="' + progKey + '">';
      progHtml += '<table class="data-table compact" style="font-size:11px;margin:0;"><thead style="position:sticky;top:0;background:var(--panel-2);z-index:1;"><tr><th style="width:30px">#</th><th>Motion</th><th>End (X;Y)</th><th>M</th></tr></thead><tbody>';
      for (let i = 0; i < processRows.length; i++) {
        const r = processRows[i];
        const lineNo = i + 1;
        const isActive = lineNo === currentLine;
        const cls = isActive ? 'style="background:rgba(34,197,94,0.25);color:#22c55e;font-weight:bold;"' : '';
        const marker = isActive ? '▶ ' : '';
        const endCoord = r.endCoordinateDisplay || r.endCoordinate || "";
        progHtml += `<tr ${cls} id="prog-line-${lineNo}"><td>${marker}${lineNo}</td><td>${esc(r.motionType || "").split("(")[0].trim()}</td><td>${esc(endCoord)}</td><td>${esc(r.mCodeValue || "")}</td></tr>`;
      }
      if (processRows.length === 0) {
        progHtml += '<tr><td colspan="4" style="text-align:center;color:var(--muted);padding:12px;">No program loaded</td></tr>';
      }
      progHtml += '</tbody></table></div></div>';
      html += progHtml;
      grid.innerHTML = html;

      // Auto-scroll chỉ khi đang chạy (currentLine > 0)
      if (currentLine > 0) {
        const activeRow = document.getElementById('prog-line-' + currentLine);
        if (activeRow) activeRow.scrollIntoView({ block: 'center', behavior: 'auto' });
      }
    } else {
      // Chỉ update 3 axis cards, giữ nguyên Program Monitor (không reset scroll)
      const axisCards = grid.querySelectorAll('.axis-card');
      if (axisCards.length >= 3) {
        for (let i = 0; i < 3 && i < axisCards.length; i++) {
          const a = axes[i] || {};
          const n = a.index || (i + 1);
          const rowsHtml = fields.map(f => {
            const val = a[f.key] || '--';
            const addr = a[f.addrKey] || '';
            const cls = f.big ? 'axis-field-value' : 'axis-field-value sm';
            return `<div class="axis-field"><div class="axis-field-label">${esc(f.label)} <span class="axis-addr">${esc(addr)}</span></div><div class="${cls}">${esc(val)}</div></div>`;
          }).join('');
          const body = axisCards[i].querySelector('.axis-body');
          if (body) body.innerHTML = rowsHtml;
        }
      }
    }
  }
  renderEvents();
  updateNavState();
  
  // Highlight active G-code line (from Axis 1 Current Data No.)
  if (state.dxf && state.dxf.fileKind === "GCODE") {
    const axis1 = axes[0]; // Axis 1 (master axis)
    if (axis1 && axis1.currentDataNo && axis1.currentDataNo !== "--") {
      const lineNumber = parseInt(axis1.currentDataNo, 10);
      if (!isNaN(lineNumber) && lineNumber > 0) {
        highlightGcodeLine(lineNumber);
      }
    }
  }

  // Update position marker on 2D CAD view
  updatePositionMarker();

  // Update highlight completed paths based on marker position
  const posXStr2 = axes[0] && axes[0].currentPos ? axes[0].currentPos : null;
  const posYStr2 = axes[1] && axes[1].currentPos ? axes[1].currentPos : null;
  if (posXStr2 && posYStr2 && posXStr2 !== "--" && posYStr2 !== "--") {
    const mx = parseFloat(posXStr2);
    const my = parseFloat(posYStr2);
    if (!isNaN(mx) && !isNaN(my)) {
      const prims = state.dxf && state.dxf.primitives ? state.dxf.primitives : [];
      const allLines = document.querySelectorAll("#cad-transform-group .cad-line");
      allLines.forEach((line, idx) => {
        if (line.classList.contains("cad-line-done")) return; // Already done
        const prim = prims[idx];
        if (!prim || !prim.points || prim.points.length < 2) return;
        const endPt = prim.points[prim.points.length - 1];
        // Marker đã đi qua nếu khoảng cách đến end point < 1mm
        const dx = mx - (endPt.x || 0);
        const dy = my - (endPt.y || 0);
        const dist = Math.sqrt(dx * dx + dy * dy);
        if (dist < 1.0) {
          line.classList.add("cad-line-done");
        }
      });
    }
  }
}

function updatePositionMarker() {
  const svgGroup = document.getElementById("cad-transform-group");
  if (!svgGroup) return;
  // Remove old marker
  const old = svgGroup.querySelector(".cad-pos-marker");
  if (old) old.remove();

  const axes = state.control && state.control.axes ? state.control.axes : [];
  if (axes.length < 2) return;
  const posXStr = axes[0] && axes[0].currentPos ? axes[0].currentPos : null;
  const posYStr = axes[1] && axes[1].currentPos ? axes[1].currentPos : null;
  if (!posXStr || !posYStr || posXStr === "--" || posYStr === "--") return;
  const posX = parseFloat(posXStr);
  const posY = parseFloat(posYStr);
  if (isNaN(posX) || isNaN(posY)) return;

  const bounds = state.dxf && state.dxf.bounds ? state.dxf.bounds : { left: 0, top: 0, width: 100, height: 100 };
  const isGcodeView = (state.dxf && state.dxf.fileKind || "").toUpperCase() === "GCODE";
  let vox = 0, voy = 0;
  if (isGcodeView) {
    const wcsData = state.dxf.wcsOffsets || {};
    const aw = state.dxf.activeWcs || "G54";
    const wa = wcsData[aw] || {};
    vox = wa.x || 0; voy = wa.y || 0;
  } else {
    vox = state.dxf.offsetX || 0; voy = state.dxf.offsetY || 0;
  }
  const W = 1000, H = 560, pad = 28;
  const drawLeft = bounds.left + vox;
  const drawTop = bounds.top + voy;
  const drawRight = drawLeft + (bounds.width || 0);
  const drawBottom = drawTop + (bounds.height || 0);
  const effLeft = Math.min(0, drawLeft);
  const effTop = Math.min(0, drawTop);
  const effRight = Math.max(drawRight, state.dxf.workspaceWidth || 170);
  const effBottom = Math.max(drawBottom, state.dxf.workspaceHeight || 170);
  const ww = Math.max(effRight - effLeft, 1), wh = Math.max(effBottom - effTop, 1);
  const sc = Math.min((W - pad * 2) / ww, (H - pad * 2) / wh);
  const oxv = (W - ww * sc) / 2, oyv = (H - wh * sc) / 2;
  // Marker dùng tọa độ thực từ PLC (tuyệt đối)
  const px = oxv + (posX - effLeft) * sc;
  const py = H - oyv - (posY - effTop) * sc;

  const marker = document.createElementNS("http://www.w3.org/2000/svg", "g");
  marker.setAttribute("class", "cad-pos-marker");
  marker.innerHTML = `<circle cx="${px.toFixed(2)}" cy="${py.toFixed(2)}" r="3" fill="rgba(239,68,68,0.9)" stroke="white" stroke-width="1"/><circle cx="${px.toFixed(2)}" cy="${py.toFixed(2)}" r="6" fill="none" stroke="rgba(239,68,68,0.5)" stroke-width="0.8" stroke-dasharray="2,2"/>`;
  svgGroup.appendChild(marker);
}

function renderEvents() {
  const events = state.control.events || [];
  if (!dom.eventsList) return;
  if (events.length === 0) {
    dom.eventsList.innerHTML = '<div class="events-empty">No events yet.</div>';
    return;
  }
  dom.eventsList.innerHTML = events.slice(0, 500).map(ev =>
    `<div class="event-row"><span class="event-time">${esc(ev.time || "")}</span><span class="event-tag ${esc(ev.kind || "info")}">${esc(ev.tag || ev.kind || "Info")}</span><span class="event-msg">${esc(ev.message || "")}</span></div>`
  ).join("");
}

function addLocalEvent(kind, title, message) {
  if (!state.control.events) state.control.events = [];
  const now = new Date();
  const time = now.toTimeString().substring(0, 8); // HH:MM:SS
  state.control.events.unshift({ time, kind: kind || "info", tag: title || "Info", message: message || "" });
  if (state.control.events.length > 500) state.control.events.length = 500;
  renderEvents();
}

function renderDxf() {
  syncInputValue(dom.cadPath, state.dxf.filePath || "");
  syncInputValue(dom.cadFile, state.dxf.fileName || "");
  const speedInput = document.getElementById("global-speed-input");
  if (speedInput && state.dxf.globalSpeed) syncInputValue(speedInput, state.dxf.globalSpeed);

  // Sync all settings inputs from backend state
  const g0Input = document.getElementById("g0-speed-input");
  if (g0Input && state.dxf.rapidSpeed) syncInputValue(g0Input, state.dxf.rapidSpeed);
  const wsWInput = document.getElementById("workspace-width-input");
  if (wsWInput && state.dxf.workspaceWidth) syncInputValue(wsWInput, String(state.dxf.workspaceWidth));
  const wsHInput = document.getElementById("workspace-height-input");
  if (wsHInput && state.dxf.workspaceHeight) syncInputValue(wsHInput, String(state.dxf.workspaceHeight));
  const oxInput = document.getElementById("offset-x-input");
  if (oxInput && state.dxf.offsetX != null) syncInputValue(oxInput, String(state.dxf.offsetX));
  const oyInput = document.getElementById("offset-y-input");
  if (oyInput && state.dxf.offsetY != null) syncInputValue(oyInput, String(state.dxf.offsetY));
  const dwM3Input = document.getElementById("dwell-m3-input");
  if (dwM3Input && state.dxf.globalDwellM3) syncInputValue(dwM3Input, state.dxf.globalDwellM3);
  const dwM4Input = document.getElementById("dwell-m4-input");
  if (dwM4Input && state.dxf.globalDwellM4) syncInputValue(dwM4Input, state.dxf.globalDwellM4);

  // Sync WCS settings
  const wcsSelectEl = document.getElementById("wcs-select");
  if (wcsSelectEl && state.dxf.activeWcs) {
    syncInputValue(wcsSelectEl, state.dxf.activeWcs);
    const wcsData = state.dxf.wcsOffsets || {};
    const activeData = wcsData[state.dxf.activeWcs] || {};
    const wcsOx = document.getElementById("wcs-offset-x");
    const wcsOy = document.getElementById("wcs-offset-y");
    if (wcsOx) syncInputValue(wcsOx, String(activeData.x || 0));
    if (wcsOy) syncInputValue(wcsOy, String(activeData.y || 0));
  }

  const importBtn = document.getElementById("import-cad-to-process-button");
  if (importBtn) {
    const isGcode = (state.dxf.fileKind || "").toUpperCase() === "GCODE";
    importBtn.disabled = isGcode;
    importBtn.textContent = isGcode ? "G-code imported" : "Import CAD -> Process";
  }
  const sendBtn = document.getElementById("send-cad-x-button");
  if (sendBtn) {
    sendBtn.disabled = !(state.control && state.control.connection && state.control.connection.connected);
    sendBtn.onclick = () => post("sendCadX");
  }
  if (dom.sendRowCount) {
    const count = (state.dxf.processRows || []).length;
    if (count > 0) {
      dom.sendRowCount.textContent = count + " rows";
      dom.sendRowCount.style.display = "inline";
    } else {
      dom.sendRowCount.style.display = "none";
    }
  }
  
  const isGcode = (state.dxf.fileKind || "").toUpperCase() === "GCODE";
  const dxfContainer = document.getElementById("dxf-points-container");
  const gcodeContainer = document.getElementById("gcode-editor-container");
  const gcodeTextarea = document.getElementById("gcode-textarea");

  if (isGcode) {
    if (dxfContainer) dxfContainer.style.display = "none";
    if (gcodeContainer) gcodeContainer.style.display = "flex";
    if (gcodeTextarea && gcodeTextarea._lastRaw !== state.dxf.rawText) {
      if (document.activeElement !== gcodeTextarea) {
        gcodeTextarea.value = state.dxf.rawText || "";
        // Update line numbers when G-code is loaded
        updateGcodeLineNumbers();
      }
      gcodeTextarea._lastRaw = state.dxf.rawText;
    }
  } else {
    if (dxfContainer) dxfContainer.style.display = "block";
    if (gcodeContainer) gcodeContainer.style.display = "none";
  }

  renderPointsTable(); renderProcessTable(); renderCadPreview(); updateNavState();
}

function renderPointsTable() {
  const points = state.dxf.points || [], primitives = state.dxf.primitives || [], rows = [];
  const MAX_VISIBLE_PRIMS = 500; // Giới hạn số primitive hiển thị để tránh lag UI
  function pz(p) { return p && p.z != null ? Number(p.z) : 0; }

  // Build lookup map một lần O(n) thay vì find() O(n) mỗi điểm
  const pointMap = new Map();
  for (const pt of points) {
    const k = `${Math.round((pt.x||0)*1000)},${Math.round((pt.y||0)*1000)},${Math.round(pz(pt)*1000)}`;
    if (!pointMap.has(k)) pointMap.set(k, pt);
  }
  function findPt(x, y, z) {
    const k = `${Math.round(x*1000)},${Math.round(y*1000)},${Math.round(z*1000)}`;
    return pointMap.get(k);
  }

  let ai = 1;
  const primsToRender = primitives.length > MAX_VISIBLE_PRIMS
    ? primitives.slice(0, MAX_VISIBLE_PRIMS)
    : primitives;
  for (const prim of primsToRender) {
    if (!prim.points || !prim.points.length) continue;
    let dt = "Line";
    const st = (prim.sourceType || "").toLowerCase();
    if (st.includes("arc")) dt = "Arc";
    else if (st.includes("circle")) dt = "Circle";
    else if (st.includes("g0") || st.includes("rapid")) dt = "Rapid (G0)";
    let cx = "", cy = "", cz = "";
    if (prim.center) {
      cx = Number(prim.center.x).toFixed(3);
      cy = Number(prim.center.y).toFixed(3);
      cz = prim.center.z != null ? Number(prim.center.z).toFixed(3) : "";
    }
    if (dt === "Line" || dt === "Rapid (G0)") {
      for (let j = 0; j < prim.points.length - 1; j++) {
        const s = prim.points[j], e = prim.points[j + 1];
        const found = findPt(s.x, s.y, pz(s));
        const key = found ? found.key : "";
        const stt = (found && found.index != null) ? found.index : ai++;
        rows.push(`<tr data-point-key="${esc(key)}"><td>${esc(stt)}</td><td>${esc(dt)}</td><td>${Number(s.x).toFixed(3)}</td><td>${Number(s.y).toFixed(3)}</td><td>${pz(s).toFixed(3)}</td><td>${Number(e.x).toFixed(3)}</td><td>${Number(e.y).toFixed(3)}</td><td>${pz(e).toFixed(3)}</td><td></td><td></td><td></td></tr>`);
      }
    } else {
      const s = prim.points[0], e = prim.points[prim.points.length - 1];
      const found = findPt(s.x, s.y, pz(s));
      const key = found ? found.key : "";
      const stt = (found && found.index != null) ? found.index : ai++;
      rows.push(`<tr data-point-key="${esc(key)}"><td>${esc(stt)}</td><td>${esc(dt)}</td><td>${Number(s.x).toFixed(3)}</td><td>${Number(s.y).toFixed(3)}</td><td>${pz(s).toFixed(3)}</td><td>${Number(e.x).toFixed(3)}</td><td>${Number(e.y).toFixed(3)}</td><td>${pz(e).toFixed(3)}</td><td>${esc(cx)}</td><td>${esc(cy)}</td><td>${esc(cz)}</td></tr>`);
    }
  }
  if (primitives.length > MAX_VISIBLE_PRIMS) {
    rows.push(`<tr><td colspan="11" style="text-align:center;color:var(--muted);font-style:italic;">... ${primitives.length - MAX_VISIBLE_PRIMS} primitive khác bị ẩn để tăng tốc UI (tổng ${primitives.length})</td></tr>`);
  }
  dom.pointsBody.innerHTML = rows.join("");
}

function renderProcessTable() {
  const rows = state.dxf.processRows || [];
  const MAX_VISIBLE = 300;
  const visible = rows.slice(0, MAX_VISIBLE);
  const overflow = rows.length - visible.length;
  dom.processBody.innerHTML = visible.map((r, i) => {
    const endDisp   = r.endCoordinateDisplay   || r.endCoordinate   || "";
    const centDisp  = r.centerCoordinateDisplay || r.centerCoordinate || "";
    return `<tr data-process-index="${i}"><td>${esc(r.motionType || "")}</td><td><input type="text" class="text-input compact" style="margin:0;width:100%;min-width:80px" data-process-index="${i}" data-process-field="mcode" value="${esc(r.mCodeValue || "")}"></td><td><input type="text" class="text-input compact" style="margin:0;width:100%;min-width:60px" data-process-index="${i}" data-process-field="dwell" value="${esc(r.dwell || "")}"></td><td><input type="text" class="text-input compact" style="margin:0;width:100%;min-width:60px" data-process-index="${i}" data-process-field="speed" value="${esc(r.speed || "")}"></td><td>${esc(endDisp)}</td><td>${esc(centDisp)}</td></tr>`;
  }).join("") + (overflow > 0 ? `<tr><td colspan="6" style="text-align:center;color:var(--muted);font-size:11px;padding:6px;">... ${overflow} more rows (total ${rows.length})</td></tr>` : "");
}

function renderCadPreview() {
  const primitives = state.dxf.primitives || [], points = state.dxf.points || [], bounds = state.dxf.bounds || { left: 0, top: 0, width: 100, height: 100 };
  const isGcode = (state.dxf.fileKind || "").toUpperCase() === "GCODE";
  if (!primitives.length) { dom.cadPreview.innerHTML = ""; dom.cadPlaceholder.classList.remove("hidden"); return; }
  dom.cadPlaceholder.classList.add("hidden");

  // Xác định offset hiển thị (giống thực tế gửi PLC)
  let viewOffsetX = 0, viewOffsetY = 0;
  if (isGcode) {
    const wcsData = state.dxf.wcsOffsets || {};
    const activeWcs = state.dxf.activeWcs || "G54";
    const wcsActive = wcsData[activeWcs] || {};
    viewOffsetX = wcsActive.x || 0;
    viewOffsetY = wcsActive.y || 0;
  } else {
    viewOffsetX = state.dxf.offsetX || 0;
    viewOffsetY = state.dxf.offsetY || 0;
  }

  const W = 1000, H = 560;
  const zPanelH = 0;
  const xyH = H - zPanelH;
  const pad = 28;
  // Bounds mở rộng để chứa cả bản vẽ (có offset) và gốc tọa độ (0,0)
  const drawLeft = bounds.left + viewOffsetX;
  const drawTop = bounds.top + viewOffsetY;
  const drawRight = drawLeft + (bounds.width || 0);
  const drawBottom = drawTop + (bounds.height || 0);
  const effLeft = Math.min(0, drawLeft);
  const effTop = Math.min(0, drawTop);
  const effRight = Math.max(drawRight, state.dxf.workspaceWidth || 170);
  const effBottom = Math.max(drawBottom, state.dxf.workspaceHeight || 170);
  const ww = Math.max(effRight - effLeft, 1), wh = Math.max(effBottom - effTop, 1);
  const sc = Math.min((W - pad * 2) / ww, (xyH - pad * 2) / wh), ox = (W - ww * sc) / 2, oy = (xyH - wh * sc) / 2;
  // projAbs: project tọa độ tuyệt đối (cho marker, workspace, trục)
  const projAbs = p => ({ x: ox + (p.x - effLeft) * sc, y: xyH - oy - (p.y - effTop) * sc });
  // proj: project tọa độ file + offset (cho bản vẽ CAD)
  const proj = p => projAbs({ x: p.x + viewOffsetX, y: p.y + viewOffsetY });

  function primIsRapid(pr) {
    const st = (pr.sourceType || "").toLowerCase();
    return st.includes("g0") || st.includes("rapid");
  }

  // Xác định dòng đang chạy để highlight biên dạng đã hoàn thành
  const axes2 = state.control && state.control.axes ? state.control.axes : [];
  const currentDataNo = (axes2[0] && axes2[0].currentDataNo && axes2[0].currentDataNo !== "--") ? parseInt(axes2[0].currentDataNo, 10) : 0;

  const polyM = primitives.map((pr, idx) => {
    const pts = pr.points || [];
    if (pts.length < 2) return "";
    const pa = pts.map(p => { const pp = proj(p); return `${pp.x.toFixed(2)},${pp.y.toFixed(2)}`; }).join(" ");
    const rapid = isGcode && primIsRapid(pr);
    const completed = currentDataNo > 0 && idx < currentDataNo;
    let cls;
    if (completed) {
      cls = "cad-line cad-line-done";
    } else if (rapid) {
      cls = "cad-line cad-line-rapid";
    } else {
      cls = "cad-line";
    }
    return `<polyline class="${cls}" points="${pa}"></polyline>`;
  }).join("");

  const ptM = "";
  const aM = Object.entries(state.dxf.assignedPointKeys || {}).map(([slot, key]) => { const p = points.find(i => i.key === key); if (!p) return ""; const pp = proj(p); const t = getAssignmentTone(slot); return `<circle cx="${pp.x.toFixed(2)}" cy="${pp.y.toFixed(2)}" r="10.5" fill="${t.fill}" stroke="white" stroke-width="1.8"></circle><text class="cad-assignment-text" x="${pp.x.toFixed(2)}" y="${pp.y.toFixed(2)}">${t.label}</text>`; }).join("");

  let zProfileSvg = "";

  const wsW = state.dxf.workspaceWidth || 170;
  const wsH = state.dxf.workspaceHeight || 170;
  const p0 = projAbs({ x: 0, y: 0 });
  const px = projAbs({ x: wsW, y: 0 });
  const py = projAbs({ x: 0, y: wsH });
  const pxy = projAbs({ x: wsW, y: wsH });
  const pxArrow = projAbs({ x: 20, y: 0 });
  const pyArrow = projAbs({ x: 0, y: 20 });

  const originMarker = `<g class="cad-workspace-limit">
    <polygon points="${p0.x.toFixed(2)},${p0.y.toFixed(2)} ${px.x.toFixed(2)},${px.y.toFixed(2)} ${pxy.x.toFixed(2)},${pxy.y.toFixed(2)} ${py.x.toFixed(2)},${py.y.toFixed(2)}" fill="none" stroke="rgba(50, 200, 255, 0.3)" stroke-width="1.8" stroke-dasharray="4,3" />
    <line x1="${p0.x.toFixed(2)}" y1="${p0.y.toFixed(2)}" x2="${pxArrow.x.toFixed(2)}" y2="${pxArrow.y.toFixed(2)}" stroke="rgba(255, 50, 50, 0.7)" stroke-width="1.8" />
    <polygon points="${pxArrow.x.toFixed(2)},${pxArrow.y.toFixed(2)} ${(pxArrow.x - 6).toFixed(2)},${(pxArrow.y - 3).toFixed(2)} ${(pxArrow.x - 6).toFixed(2)},${(pxArrow.y + 3).toFixed(2)}" fill="rgba(255, 50, 50, 0.7)" />
    <text x="${(pxArrow.x + 4).toFixed(2)}" y="${(pxArrow.y + 3).toFixed(2)}" fill="rgba(255, 50, 50, 0.8)" font-size="10" font-weight="bold" font-family="monospace">X</text>
    <line x1="${p0.x.toFixed(2)}" y1="${p0.y.toFixed(2)}" x2="${pyArrow.x.toFixed(2)}" y2="${pyArrow.y.toFixed(2)}" stroke="rgba(50, 255, 50, 0.7)" stroke-width="1.8" />
    <polygon points="${pyArrow.x.toFixed(2)},${pyArrow.y.toFixed(2)} ${(pyArrow.x - 3).toFixed(2)},${(pyArrow.y + 6).toFixed(2)} ${(pyArrow.x + 3).toFixed(2)},${(pyArrow.y + 6).toFixed(2)}" fill="rgba(50, 255, 50, 0.7)" />
    <text x="${(pyArrow.x + 5).toFixed(2)}" y="${(pyArrow.y + 3).toFixed(2)}" fill="rgba(50, 255, 50, 0.8)" font-size="10" font-weight="bold" font-family="monospace">Y</text>
    <circle cx="${p0.x.toFixed(2)}" cy="${p0.y.toFixed(2)}" r="3" fill="none" stroke="yellow" stroke-width="1.8"/>
  </g>`;

  // Current position marker (from axis monitor)
  let posMarker = "";
  const axes = state.control && state.control.axes ? state.control.axes : [];
  if (axes.length >= 2) {
    const posXStr = axes[0] && axes[0].currentPos ? axes[0].currentPos : null;
    const posYStr = axes[1] && axes[1].currentPos ? axes[1].currentPos : null;
    if (posXStr && posYStr && posXStr !== "--" && posYStr !== "--") {
      const posX = parseFloat(posXStr);
      const posY = parseFloat(posYStr);
      if (!isNaN(posX) && !isNaN(posY)) {
        const pp = proj({ x: posX, y: posY });
        posMarker = `<g class="cad-pos-marker">
          <circle cx="${pp.x.toFixed(2)}" cy="${pp.y.toFixed(2)}" r="6" fill="rgba(239,68,68,0.8)" stroke="white" stroke-width="2"/>
          <circle cx="${pp.x.toFixed(2)}" cy="${pp.y.toFixed(2)}" r="12" fill="none" stroke="rgba(239,68,68,0.5)" stroke-width="1.5" stroke-dasharray="3,3"/>
        </g>`;
      }
    }
  }

  dom.cadPreview.innerHTML = `<g id="cad-transform-group" transform="translate(${cadPanX},${cadPanY}) scale(${cadZoom})">${originMarker}<g>${polyM}</g><g>${ptM}</g><g>${aM}</g>${posMarker}</g>${zProfileSvg}`;
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
  dom.viewControl  && dom.viewControl.classList.toggle("is-active",  v === "control");
  dom.viewLogs     && dom.viewLogs.classList.toggle("is-active",     v === "logs");
  dom.viewTelemetry && dom.viewTelemetry.classList.toggle("is-active", v === "telemetry");
  dom.viewDxf      && dom.viewDxf.classList.toggle("is-active",      v === "dxf");
  dom.viewSettings && dom.viewSettings.classList.toggle("is-active", v === "settings");
  dom.viewHelp && dom.viewHelp.classList.toggle("is-active", v === "help");
  updateNavState();
}
function updateNavState() { const s = b => { b.classList.toggle("is-active", b.dataset.view === state.view); }; dom.topViewButtons.forEach(s); dom.sideViewButtons.forEach(s); }

function openPrompt(title, label, val, onSubmit) { modalSubmit = onSubmit; dom.modalTitle.textContent = title; dom.modalLabel.textContent = label; dom.modalInput.value = val || ""; dom.modal.classList.remove("hidden"); dom.modalInput.focus(); dom.modalInput.select(); }
function closePrompt() { modalSubmit = null; dom.modal.classList.add("hidden"); }
function submitPrompt() { if (typeof modalSubmit === "function") modalSubmit(dom.modalInput.value.trim()); closePrompt(); }

function showToast(kind, title, message) {
  // User requested to completely remove popups, everything is logged to System Events instead.
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
            index: 1, currentPos: "--", currentSpeed: "--", errorCode: "--", warningCode: "--", axisStatus: "--", currentDataNo: "--", lastDataNo: "--", errorReset: "--", jogSpeed: "--", newSpeed: "--",
            currentPosAddr: "D0", currentSpeedAddr: "D4", errorCodeAddr: "U0\\G806", warningCodeAddr: "U0\\G807", axisStatusAddr: "U0\\G814",
            currentDataNoAddr: "U0\\G835", lastDataNoAddr: "U0\\G837", errorResetAddr: "U0\\G1502", jogSpeedAddr: "D406", newSpeedAddr: "U0\\G1518"
          },
          {
            index: 2, currentPos: "--", currentSpeed: "--", errorCode: "--", warningCode: "--", axisStatus: "--", currentDataNo: "--", lastDataNo: "--", errorReset: "--", jogSpeed: "--", newSpeed: "--",
            currentPosAddr: "D10", currentSpeedAddr: "D14", errorCodeAddr: "U0\\G906", warningCodeAddr: "U0\\G907", axisStatusAddr: "U0\\G914",
            currentDataNoAddr: "U0\\G935", lastDataNoAddr: "U0\\G937", errorResetAddr: "U0\\G1602", jogSpeedAddr: "D406", newSpeedAddr: "U0\\G1618"
          },
          {
            index: 3, currentPos: "--", currentSpeed: "--", errorCode: "--", warningCode: "--", axisStatus: "--", currentDataNo: "--", lastDataNo: "--", errorReset: "--", jogSpeed: "--", newSpeed: "--",
            currentPosAddr: "D20", currentSpeedAddr: "D24", errorCodeAddr: "U0\\G1006", warningCodeAddr: "U0\\G1007", axisStatusAddr: "U0\\G1014",
            currentDataNoAddr: "U0\\G1035", lastDataNoAddr: "U0\\G1037", errorResetAddr: "U0\\G1702", jogSpeedAddr: "D406", newSpeedAddr: "U0\\G1718"
          },
          {
            index: 4, currentPos: "--", currentSpeed: "--", errorCode: "--", warningCode: "--", axisStatus: "--", currentDataNo: "--", lastDataNo: "--", errorReset: "--", jogSpeed: "--", newSpeed: "--",
            currentPosAddr: "D30", currentSpeedAddr: "D34", errorCodeAddr: "U0\\G1106", warningCodeAddr: "U0\\G1107", axisStatusAddr: "U0\\G1114",
            currentDataNoAddr: "U0\\G1135", lastDataNoAddr: "U0\\G1137", errorResetAddr: "U0\\G1802", jogSpeedAddr: "D406", newSpeedAddr: "U0\\G1818"
          }
        ],
        events: [
          { time: "08:51", kind: "security", tag: "Security", message: "Administrator logged in successfully." }
        ]
      }
    });
  }, 300);
}
