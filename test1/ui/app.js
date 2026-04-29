const host = window.chrome && window.chrome.webview ? window.chrome.webview : null;

const state = {
  view: "control",
  theme: "dark",
  control: {
    connection: { connected: false, ip: "192.168.3.39", port: 3000, banner: "PLC disconnected", meta: "MX Component logical station: 0", buttonText: "CONNECT SYSTEM" },
    coordinates: [],
    velocity: { value: 15, display: "1.5", register: "D406", min: 0, max: 50 },
    integrity: { state: "IDLE", detail: "STOP", tone: "idle" },
    monitorRows: []
  },
  dxf: {
    filePath: "",
    fileName: "",
    bounds: { left: 0, top: 0, width: 100, height: 100 },
    primitives: [],
    points: [],
    selectedPointKey: "",
    assignedPointKeys: {},
    processRows: []
  },
  telemetry: {}
};


const dom = {};
let modalSubmit = null;

// CAD Pan/Zoom state
let cadPanX = 0;
let cadPanY = 0;
let cadZoom = 1;
let isCadPanning = false;
let startCadPanX = 0;
let startCadPanY = 0;

window.app = {
  receive(message) {
    handleHostMessage(message || {});
  }
};

document.addEventListener("DOMContentLoaded", () => {
  cacheDom();
  bindEvents();
  applyTheme(state.theme);
  applyView(state.view);
  post("uiReady");
});

function cacheDom() {
  dom.html = document.documentElement;
  dom.topViewButtons = Array.from(document.querySelectorAll(".top-nav [data-view]"));
  dom.sideViewButtons = Array.from(document.querySelectorAll(".side-nav [data-view]"));
  dom.placeholderButtons = Array.from(document.querySelectorAll("[data-placeholder]"));
  dom.themeToggle = document.getElementById("theme-toggle");
  dom.connectButton = document.getElementById("connect-button");
  dom.plcIp = document.getElementById("plc-ip");
  dom.plcPort = document.getElementById("plc-port");
  dom.connectionBanner = document.getElementById("connection-banner");
  dom.connectionMeta = document.getElementById("connection-meta");
  dom.sidebarStatus = document.getElementById("sidebar-status");
  dom.velocitySlider = document.getElementById("velocity-slider");
  dom.velocityValue = document.getElementById("velocity-value");
  dom.velocityRaw = document.getElementById("velocity-raw");
  dom.velocitySubtitle = document.getElementById("velocity-subtitle");
  dom.integrityState = document.getElementById("integrity-state");
  dom.integrityDetail = document.getElementById("integrity-detail");
  dom.monitorBody = document.getElementById("monitor-table-body");
  dom.monitorEmpty = document.getElementById("monitor-empty");
  dom.addRegister = document.getElementById("add-register");
  dom.emergencyStop = document.getElementById("emergency-stop");
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
  dom.topViewButtons.forEach((button) => {
    button.addEventListener("click", () => {
      const view = button.dataset.view;
      state.view = view;
      applyView(view);
      post("switchView", { view });
    });
  });

  dom.sideViewButtons.forEach((button) => {
    button.addEventListener("click", () => {
      const view = button.dataset.view;
      state.view = view;
      applyView(view);
      post("switchView", { view });
    });
  });

  dom.placeholderButtons.forEach((button) => {
    button.addEventListener("click", () => {
      showToast("info", button.dataset.placeholder, "Mục này đang để placeholder. CONTROL và DXF RUN đang hoạt động.");
    });
  });

  dom.themeToggle.addEventListener("click", () => {
    state.theme = state.theme === "dark" ? "light" : "dark";
    applyTheme(state.theme);
    post("setTheme", { theme: state.theme });
  });

  dom.connectButton.addEventListener("click", () => {
    post("connectToggle", {
      ip: dom.plcIp.value.trim(),
      port: parseInt(dom.plcPort.value, 10) || 0
    });
  });

  dom.velocitySlider.addEventListener("input", () => {
    const rawValue = parseInt(dom.velocitySlider.value, 10) || 0;
    dom.velocityValue.textContent = (rawValue / 10).toFixed(1);
    dom.velocityRaw.textContent = `Raw: ${rawValue} (${state.control.velocity.register || "D406"})`;
  });

  dom.velocitySlider.addEventListener("change", () => {
    post("setVelocity", {
      value: parseInt(dom.velocitySlider.value, 10) || 0
    });
  });

  dom.addRegister.addEventListener("click", () => {
    openPrompt("Add register", "Enter a PLC register to monitor:", "", (value) => {
      post("addRegister", { register: value });
    });
  });

  document.querySelectorAll("[data-jog-offset]").forEach((button) => {
    const offset = parseInt(button.dataset.jogOffset, 10);
    const stop = () => post("jogStop", { offset });
    button.addEventListener("pointerdown", (event) => {
      if (event.button !== 0) {
        return;
      }

      post("jogStart", { offset });
    });
    button.addEventListener("pointerup", stop);
    button.addEventListener("pointerleave", stop);
    button.addEventListener("pointercancel", stop);
  });

  dom.emergencyStop.addEventListener("click", () => post("emergencyStop"));
  dom.openDxf.addEventListener("click", () => post("openDxf"));

  dom.assignButtons.forEach((button) => {
    button.addEventListener("click", () => {
      if (!state.dxf.selectedPointKey) {
        showToast("info", "DXF", "Hãy chọn một điểm trước khi gán.");
        return;
      }

      post("assignPoint", {
        slot: button.dataset.assignSlot,
        key: state.dxf.selectedPointKey
      });
    });
  });

  dom.processButtons.forEach((button) => {
    button.addEventListener("click", () => {
      const key = button.dataset.processKey;
      const row = state.dxf.processRows.find((item) => item.key === key);
      const currentValue = key === "speed" ? (row ? row.speed : "") : (row ? row.mCodeValue : "");
      const titleMap = {
        zDown: "Độ cao Z hạ",
        zSafe: "Độ cao Z an toàn",
        speed: "Tốc độ"
      };

      openPrompt(titleMap[key] || "Input", "Nhập giá trị:", currentValue || "", (value) => {
        post("setProcessValue", { key, value });
      });
    });
  });

  dom.runButtons.forEach((button) => {
    button.addEventListener("click", () => {
      post("runAction", { command: button.dataset.runAction });
    });
  });

  dom.monitorBody.addEventListener("click", (event) => {
    const target = event.target.closest("[data-remove-register]");
    if (!target) {
      return;
    }

    post("removeRegister", { register: target.dataset.removeRegister });
  });

  dom.pointsBody.addEventListener("click", (event) => {
    const row = event.target.closest("[data-point-key]");
    if (!row) {
      return;
    }

    state.dxf.selectedPointKey = row.dataset.pointKey;
    renderPointsTable();
    renderCadPreview();
    post("selectCadPoint", { key: state.dxf.selectedPointKey });
  });

  dom.processBody.addEventListener("change", (event) => {
    const input = event.target;
    if (input.tagName === "INPUT" && input.dataset.processIndex !== undefined) {
      const index = parseInt(input.dataset.processIndex, 10);
      const field = input.dataset.processField;
      const value = input.value.trim();
      post("setProcessRowValue", { index, field, value });
    }
  });

  dom.cadPreview.addEventListener("click", (event) => {
    // Only select point if we didn't just pan
    const target = event.target.closest("[data-point-key]");
    if (!target) {
      return;
    }

    state.dxf.selectedPointKey = target.dataset.pointKey;
    renderPointsTable();
    renderCadPreview();
    post("selectCadPoint", { key: state.dxf.selectedPointKey });
  });

  // Pan and Zoom logic
  dom.cadPreview.addEventListener("wheel", (event) => {
    event.preventDefault();
    const zoomSensitivity = 0.1;
    const delta = event.deltaY > 0 ? -zoomSensitivity : zoomSensitivity;
    
    // Calculate mouse position relative to SVG
    const rect = dom.cadPreview.getBoundingClientRect();
    const mouseX = event.clientX - rect.left;
    const mouseY = event.clientY - rect.top;

    const oldZoom = cadZoom;
    cadZoom = Math.max(0.1, Math.min(10, cadZoom + delta));

    // Adjust pan so it zooms towards the mouse cursor
    const scaleChange = cadZoom / oldZoom;
    cadPanX = mouseX - (mouseX - cadPanX) * scaleChange;
    cadPanY = mouseY - (mouseY - cadPanY) * scaleChange;
    
    updateCadTransform();
  });

  dom.cadPreview.addEventListener("mousedown", (event) => {
    // Middle click or Left click on background to pan
    if (event.button === 1 || (event.button === 0 && !event.target.closest("[data-point-key]"))) {
      event.preventDefault();
      isCadPanning = true;
      startCadPanX = event.clientX - cadPanX;
      startCadPanY = event.clientY - cadPanY;
      dom.cadPreview.style.cursor = "grabbing";
    }
  });

  dom.cadPreview.addEventListener("mousemove", (event) => {
    if (!isCadPanning) return;
    cadPanX = event.clientX - startCadPanX;
    cadPanY = event.clientY - startCadPanY;
    updateCadTransform();
  });

  dom.cadPreview.addEventListener("mouseup", () => {
    isCadPanning = false;
    dom.cadPreview.style.cursor = "grab";
  });

  dom.cadPreview.addEventListener("mouseleave", () => {
    isCadPanning = false;
    dom.cadPreview.style.cursor = "grab";
  });

  function updateCadTransform() {
    const group = document.getElementById("cad-transform-group");
    if (group) {
      group.setAttribute("transform", `translate(${cadPanX}, ${cadPanY}) scale(${cadZoom})`);
    }
  }

  // telemetry write buffer button
  if (dom.writeBufferButton) {
    dom.writeBufferButton.addEventListener('click', () => {
      if (!state.control || !state.control.connection || !state.control.connection.connected) {
        showToast('error', 'Telemetry', 'Chưa kết nối PLC. Không thể ghi.');
        return;
      }

      const path = dom.writeBufferPath.value.trim();
      const val = parseInt(dom.writeBufferValue.value, 10) || 0;

      // use host message to request write
      post('writeBufferRequest', { path, value: val });
    });
  }

  // Import CAD -> Process button
  const importCadBtn = document.getElementById('import-cad-to-process-button');
  if (importCadBtn) {
    importCadBtn.addEventListener('click', () => {
      // copy CAD points into process rows as endCoordinate (X;Y)
      post('importCadToProcess');
    });
  }

  // send CAD X button in DXF view
  const sendCadXBtn = document.getElementById('send-cad-x-button');
  if (sendCadXBtn) {
    sendCadXBtn.addEventListener('click', () => {
      if (!state.control || !state.control.connection || !state.control.connection.connected) {
        showToast('error', 'Telemetry', 'Chưa kết nối PLC. Không thể gửi CAD.');
        return;
      }

      post('sendCadX');
    });
  }

  if (dom.clearLogsButton) {
    dom.clearLogsButton.addEventListener('click', () => {
      post('clearLogs');
    });
  }

  dom.modalCancel.addEventListener("click", closePrompt);
  dom.modalConfirm.addEventListener("click", submitPrompt);
  dom.modal.addEventListener("click", (event) => {
    if (event.target === dom.modal) {
      closePrompt();
    }
  });
  dom.modalInput.addEventListener("keydown", (event) => {
    if (event.key === "Enter") {
      submitPrompt();
    }
    if (event.key === "Escape") {
      closePrompt();
    }
  });
}

function handleHostMessage(message) {
  if (!message || !message.type) {
    return;
  }

  switch (message.type) {
    case "controlState":
      state.control = message.payload || state.control;
      state.view = state.control.view || state.view;
      state.theme = state.control.theme || state.theme;
      applyTheme(state.theme);
      applyView(state.view);
      renderControl();
      break;

    case "dxfState":
      state.dxf = message.payload || state.dxf;
      state.view = state.dxf.view || state.view;
      state.theme = state.dxf.theme || state.theme;
      applyTheme(state.theme);
      applyView(state.view);
      renderDxf();
      break;

    case "telemetry":
      state.telemetry = message.payload || state.telemetry || {};
      renderTelemetry();
      break;

    case "logsState":
      state.logs = (message.payload && message.payload.logs) || [];
      renderLogs();
      break;

    case "notify":
      showToast(message.payload.kind, message.payload.title, message.payload.message);
      break;
  }
}

function renderControl() {
  const connection = state.control.connection || {};
  syncInputValue(dom.plcIp, connection.ip || "");
  syncInputValue(dom.plcPort, connection.port != null ? String(connection.port) : "");
  dom.connectButton.textContent = connection.buttonText || "CONNECT SYSTEM";
  dom.connectionBanner.textContent = (connection.banner || "PLC disconnected").toUpperCase();
  dom.connectionBanner.classList.toggle("connected", !!connection.connected);
  dom.connectionBanner.classList.toggle("disconnected", !connection.connected);
  dom.connectionMeta.textContent = connection.meta || "";
  dom.sidebarStatus.textContent = connection.banner || "PLC disconnected";

  (state.control.coordinates || []).forEach((coordinate) => {
    const key = coordinate.key;
    setText(`coord-${key}-value`, coordinate.display || "0.00");
    setText(`coord-${key}-raw`, `Raw: ${coordinate.raw || 0} (${coordinate.register || ""})`);
  });

  const velocity = state.control.velocity || {};
  dom.velocitySlider.min = velocity.min != null ? velocity.min : 0;
  dom.velocitySlider.max = velocity.max != null ? velocity.max : 50;
  dom.velocitySlider.value = velocity.value != null ? velocity.value : 0;
  dom.velocityValue.textContent = velocity.display || "0.0";
  dom.velocityRaw.textContent = `Raw: ${velocity.value || 0} (${velocity.register || "D406"})`;
  dom.velocitySubtitle.textContent = `Target write velocity (${velocity.register || "D406"})`;

  const integrity = state.control.integrity || {};
  dom.integrityState.textContent = integrity.state || "IDLE";
  dom.integrityState.className = `integrity-state ${integrity.tone || "idle"}`;
  dom.integrityDetail.textContent = integrity.detail || "STOP";

  renderMonitorTable();
  updateNavState();
}

function renderMonitorTable() {
  const rows = state.control.monitorRows || [];
  dom.monitorBody.innerHTML = rows.map((row) => `
    <tr>
      <td>${escapeHtml(row.register || "")}</td>
      <td>${escapeHtml(row.value || "-")}</td>
      <td>${escapeHtml(row.status || "")}</td>
      <td><button class="row-action" data-remove-register="${escapeHtml(row.register || "")}">x</button></td>
    </tr>
  `).join("");

  dom.monitorEmpty.classList.toggle("hidden", rows.length > 0);
}

function renderDxf() {
  syncInputValue(dom.cadPath, state.dxf.filePath || '');
  syncInputValue(dom.cadFile, state.dxf.fileName || '');

  const sendBtn = document.getElementById('send-cad-x-button');
  if (sendBtn) sendBtn.disabled = !(state.control && state.control.connection && state.control.connection.connected);

  renderPointsTable();
  renderProcessTable();
  renderCadPreview();
  updateNavState();
}

function renderPointsTable() {
  const points = state.dxf.points || [];
  const primitives = state.dxf.primitives || [];
  const rows = [];
  
  function findPointKey(x, y) {
    const eps = 1e-3;
    const pt = points.find(p => Math.abs((p.x || 0) - x) < eps && Math.abs((p.y || 0) - y) < eps);
    return pt ? pt.key : '';
  }

  function findPointIndex(x, y) {
    const eps = 1e-3;
    const pt = points.find(p => Math.abs((p.x || 0) - x) < eps && Math.abs((p.y || 0) - y) < eps);
    return pt && pt.index != null ? pt.index : '';
  }

  let autoIndex = 1;

  for (let i = 0; i < primitives.length; i++) {
    const prim = primitives[i];
    if (!prim.points || prim.points.length === 0) continue;

    let displayType = 'Line';
    if ((prim.sourceType || '').toLowerCase().includes('arc')) displayType = 'Cung tròn';
    if ((prim.sourceType || '').toLowerCase().includes('circle')) displayType = 'Hình tròn';
    
    let cx = '', cy = '';
    if (prim.center) {
        cx = Number(prim.center.x).toFixed(3);
        cy = Number(prim.center.y).toFixed(3);
    }
    
    if (displayType === 'Line') {
       // Loop through all points in the polyline/line, creating a segment for each pair
       for (let j = 0; j < prim.points.length - 1; j++) {
           const s = prim.points[j];
           const e = prim.points[j + 1];
           
           const key = findPointKey(s.x, s.y);
           const stt = findPointIndex(s.x, s.y) || autoIndex++;
           
           const sx = Number(s.x).toFixed(3);
           const sy = Number(s.y).toFixed(3);
           const ex = Number(e.x).toFixed(3);
           const ey = Number(e.y).toFixed(3);

           rows.push(`
             <tr data-point-key="${escapeHtml(key)}">
               <td>${escapeHtml(stt)}</td>
               <td>${escapeHtml(displayType)}</td>
               <td>${escapeHtml(sx)}</td>
               <td>${escapeHtml(sy)}</td>
               <td>${escapeHtml(ex)}</td>
               <td>${escapeHtml(ey)}</td>
               <td></td>
               <td></td>
             </tr>
           `);
       }
    } else {
       // Arc/Circle: output ONE row showing start and end
       const s = prim.points[0];
       const e = prim.points[prim.points.length - 1];
       
       const key = findPointKey(s.x, s.y);
       const stt = findPointIndex(s.x, s.y) || autoIndex++;
       
       const sx = Number(s.x).toFixed(3);
       const sy = Number(s.y).toFixed(3);
       const ex = Number(e.x).toFixed(3);
       const ey = Number(e.y).toFixed(3);

       rows.push(`
         <tr data-point-key="${escapeHtml(key)}">
           <td>${escapeHtml(stt)}</td>
           <td>${escapeHtml(displayType)}</td>
           <td>${escapeHtml(sx)}</td>
           <td>${escapeHtml(sy)}</td>
           <td>${escapeHtml(ex)}</td>
           <td>${escapeHtml(ey)}</td>
           <td>${escapeHtml(cx)}</td>
           <td>${escapeHtml(cy)}</td>
         </tr>
       `);
    }
  }

  dom.pointsBody.innerHTML = rows.join('');
}



function renderProcessTable() {
  const rows = state.dxf.processRows || [];
  dom.processBody.innerHTML = rows.map((row, index) => `
    <tr>
      <td>${escapeHtml(row.motionType || "")}</td>
      <td><input type="text" class="text-input compact" style="margin:0; width:100%; min-width:80px;" data-process-index="${index}" data-process-field="mcode" value="${escapeHtml(row.mCodeValue || "")}"></td>
      <td><input type="text" class="text-input compact" style="margin:0; width:100%; min-width:60px;" data-process-index="${index}" data-process-field="dwell" value="${escapeHtml(row.dwell || "")}"></td>
      <td><input type="text" class="text-input compact" style="margin:0; width:100%; min-width:60px;" data-process-index="${index}" data-process-field="speed" value="${escapeHtml(row.speed || "")}"></td>
      <td>${escapeHtml(row.endCoordinate || "")}</td>
      <td>${escapeHtml(row.centerCoordinate || "")}</td>
    </tr>
  `).join("");
}

function renderCadPreview() {
  const primitives = state.dxf.primitives || [];
  const points = state.dxf.points || [];
  const bounds = state.dxf.bounds || { left: 0, top: 0, width: 100, height: 100 };

  if (!primitives.length) {
    dom.cadPreview.innerHTML = "";
    dom.cadPlaceholder.classList.remove("hidden");
    return;
  }

  dom.cadPlaceholder.classList.add("hidden");

  const width = 1000;
  const height = 560;
  const padding = 28;
  const worldWidth = Math.max(bounds.width || 0, 1);
  const worldHeight = Math.max(bounds.height || 0, 1);
  const scale = Math.min((width - padding * 2) / worldWidth, (height - padding * 2) / worldHeight);
  const offsetX = (width - worldWidth * scale) / 2;
  const offsetY = (height - worldHeight * scale) / 2;

  const project = (point) => {
    const x = offsetX + (point.x - bounds.left) * scale;
    const y = height - offsetY - (point.y - bounds.top) * scale;
    return { x, y };
  };

  const polylineMarkup = primitives.map((primitive) => {
    const pointsAttr = (primitive.points || []).map((point) => {
      const projected = project(point);
      return `${projected.x.toFixed(2)},${projected.y.toFixed(2)}`;
    }).join(" ");
    return `<polyline class="cad-line" points="${pointsAttr}"></polyline>`;
  }).join("");

  const pointMarkup = points.map((point) => {
    const projected = project(point);
    const selectedClass = point.key === state.dxf.selectedPointKey ? "is-selected" : "";
    return `
      <circle
        class="cad-point ${selectedClass}"
        cx="${projected.x.toFixed(2)}"
        cy="${projected.y.toFixed(2)}"
        r="4.8"
        data-point-key="${escapeHtml(point.key || "")}">
      </circle>
    `;
  }).join("");

  const assignmentMarkup = Object.entries(state.dxf.assignedPointKeys || {}).map(([slot, key]) => {
    const point = points.find((item) => item.key === key);
    if (!point) {
      return "";
    }

    const projected = project(point);
    const tone = getAssignmentTone(slot);
    return `
      <circle cx="${projected.x.toFixed(2)}" cy="${projected.y.toFixed(2)}" r="10.5" fill="${tone.fill}" stroke="white" stroke-width="1.8"></circle>
      <text class="cad-assignment-text" x="${projected.x.toFixed(2)}" y="${projected.y.toFixed(2)}">${tone.label}</text>
    `;
  }).join("");

  dom.cadPreview.innerHTML = `
    <g id="cad-transform-group" transform="translate(${cadPanX}, ${cadPanY}) scale(${cadZoom})">
      <g>${polylineMarkup}</g>
      <g>${pointMarkup}</g>
      <g>${assignmentMarkup}</g>
    </g>
  `;
}

function renderTelemetry() {
  const t = state.telemetry || {};
  const connected = t.connected;
  const dValues = t.dValues || [];
  const buffers = t.buffers || [];

  // ensure controls reflect connection state
  if (dom.writeBufferButton) dom.writeBufferButton.disabled = !connected;
  // keep path and value inputs enabled so users can prepare data
  if (dom.writeBufferPath) dom.writeBufferPath.disabled = false;
  if (dom.writeBufferValue) dom.writeBufferValue.disabled = false;

  // enable/disable send cad button
  const sendCadBtn = document.getElementById('send-cad-x-button');
  if (sendCadBtn) sendCadBtn.disabled = !connected;

  if (!dom.telemetryContent) return;

  const rows = [];
  rows.push(`<div class="telemetry-header">Telemetry (${connected ? 'connected' : 'disconnected'})</div>`);

  if (dValues.length) {
    rows.push('<div class="telemetry-section"><div class="telemetry-title">D registers</div><table class="telemetry-table"><thead><tr><th>Register</th><th>Value</th><th>Status</th></tr></thead><tbody>');
    dValues.forEach((item) => {
      rows.push(`<tr><td>${escapeHtml(item.register || '')}</td><td>${escapeHtml(item.value != null ? String(item.value) : '')}</td><td>${escapeHtml(item.ok ? 'OK' : (item.error || 'ERR'))}</td></tr>`);
    });
    rows.push('</tbody></table></div>');
  }

  if (buffers.length) {
    rows.push('<div class="telemetry-section"><div class="telemetry-title">Buffers (Un\\Gx)</div>');
    buffers.forEach((b) => {
      const vals = (b.values || []).map(v => escapeHtml(String(v))).join(', ');
      rows.push(`<div class="telemetry-buffer"><div class="buffer-path">${escapeHtml(b.path || '')}</div><div class="buffer-values">[${vals}]</div><div class="buffer-status">${b.ok ? 'OK' : (b.error || 'ERR')}</div></div>`);
    });
    rows.push('</div>');
  }

  dom.telemetryContent.innerHTML = rows.join('');
}

function renderLogs() {
  const rows = state.logs || [];
  if (!dom.logsBody) return;

  dom.logsBody.innerHTML = rows.map((r) => `
    <tr>
      <td>${escapeHtml(r.timestamp || '')}</td>
      <td>${escapeHtml(r.direction || '')}</td>
      <td>${escapeHtml(r.address || '')}</td>
      <td>${escapeHtml(r.value != null ? String(r.value) : '')}</td>
      <td>${escapeHtml(r.status || '')}</td>
      <td>${escapeHtml(r.message || '')}</td>
    </tr>
  `).join('');

  dom.logsEmpty.classList.toggle('hidden', rows.length > 0);
}

function applyTheme(theme) {
  dom.html.classList.toggle("theme-dark", theme === "dark");
  dom.html.classList.toggle("theme-light", theme !== "dark");
  dom.themeToggle.textContent = theme === "dark" ? "◐" : "◑";
}

function applyView(view) {
  state.view = view;
  // toggle known views explicitly
  dom.viewControl && dom.viewControl.classList.toggle("is-active", view === "control");
  dom.viewLogs && dom.viewLogs.classList.toggle("is-active", view === "logs");
  dom.viewTelemetry && dom.viewTelemetry.classList.toggle("is-active", view === "telemetry");
  dom.viewDxf && dom.viewDxf.classList.toggle("is-active", view === "dxf");

  updateNavState();
}

function updateNavState() {
  const setActive = (button) => {
    const active = button.dataset.view === state.view;
    button.classList.toggle("is-active", active);
  };

  dom.topViewButtons.forEach(setActive);
  dom.sideViewButtons.forEach(setActive);
}

function openPrompt(title, label, currentValue, onSubmit) {
  modalSubmit = onSubmit;
  dom.modalTitle.textContent = title;
  dom.modalLabel.textContent = label;
  dom.modalInput.value = currentValue || "";
  dom.modal.classList.remove("hidden");
  dom.modalInput.focus();
  dom.modalInput.select();
}

function closePrompt() {
  modalSubmit = null;
  dom.modal.classList.add("hidden");
}

function submitPrompt() {
  if (typeof modalSubmit === "function") {
    modalSubmit(dom.modalInput.value.trim());
  }
  closePrompt();
}

function showToast(kind, title, message) {
  const toast = document.createElement("div");
  toast.className = `toast ${kind || "info"}`;
  toast.innerHTML = `
    <div class="toast-title">${escapeHtml(title || "Message")}</div>
    <div class="toast-message">${escapeHtml(message || "")}</div>
  `;
  dom.toastContainer.appendChild(toast);
  window.setTimeout(() => {
    toast.remove();
  }, 4200);
}

function post(action, payload = {}) {
  if (host) {
    host.postMessage({ action, payload });
  }
}

function syncInputValue(input, value) {
  if (!input || document.activeElement === input) {
    return;
  }

  input.value = value;
}

function setText(id, value) {
  const element = document.getElementById(id);
  if (element) {
    element.textContent = value;
  }
}

function getAssignmentTone(slot) {
  switch (slot) {
    case "start":
      return { fill: "#22c55e", label: "S" };
    case "glueStart":
      return { fill: "#f59e0b", label: "B" };
    case "glueEnd":
      return { fill: "#ef4444", label: "E" };
    default:
      return { fill: "#94a3b8", label: "?" };
  }
}

function escapeHtml(value) {
  return String(value)
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;");
}

// Development Mode Mock Data
// Tự động nạp dữ liệu mẫu nếu mở index.html trực tiếp trên trình duyệt (không thông qua C#)
if (!host) {
  setTimeout(() => {
    handleHostMessage({
      type: "state",
      state: {
        view: "dxf",
        dxf: {
          filePath: "C:\\MOCK\\DEMO_CAD.dxf",
          fileName: "DEMO_CAD.dxf",
          bounds: { left: 0, top: 0, width: 300, height: 250 },
          primitives: [
            {
              sourceType: "Line",
              points: [
                { x: 50, y: 50 },
                { x: 250, y: 50 },
                { x: 250, y: 150 },
                { x: 50, y: 150 },
                { x: 50, y: 50 }
              ]
            },
            {
              sourceType: "Circle",
              center: { x: 150, y: 100 },
              radius: 40,
              points: Array.from({ length: 37 }, (_, i) => ({
                x: 150 + 40 * Math.cos(i * 10 * Math.PI / 180),
                y: 100 + 40 * Math.sin(i * 10 * Math.PI / 180)
              }))
            }
          ],
          points: [
            { x: 50, y: 50, key: "50_50", index: 1 },
            { x: 250, y: 50, key: "250_50", index: 2 },
            { x: 250, y: 150, key: "250_150", index: 3 },
            { x: 50, y: 150, key: "50_150", index: 4 }
          ],
          processRows: [
            { motionType: "Line (Continuous Path)", mCodeValue: "1", dwell: "100", speed: "15", endCoordinate: "250;50", centerCoordinate: "" },
            { motionType: "Line (Continuous Path)", mCodeValue: "", dwell: "", speed: "15", endCoordinate: "250;150", centerCoordinate: "" },
            { motionType: "Line (Continuous Path)", mCodeValue: "", dwell: "", speed: "15", endCoordinate: "50;150", centerCoordinate: "" },
            { motionType: "Line (Continuous Positioning)", mCodeValue: "2", dwell: "0", speed: "15", endCoordinate: "50;50", centerCoordinate: "" },
            { motionType: "Arc CCW (End)", mCodeValue: "", dwell: "500", speed: "10", endCoordinate: "190;100", centerCoordinate: "150;100" }
          ]
        }
      }
    });
  }, 300);
}
