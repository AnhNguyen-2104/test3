/**
 * cad3d.js — Three.js 3D CAD Preview for Gantry SCADA
 * Renders primitives (lines, arcs, rapid moves) in a 3D scene
 * with orbit controls, axis helpers, and grid.
 */
(function () {
  'use strict';

  let scene, camera, renderer, controls, cadGroup;
  let is3DMode = false;
  let initialized = false;
  let animFrameId = null;

  // Colors
  const COL_LINE    = 0x00e5ff;    // cyan for cutting moves
  const COL_RAPID   = 0xff9800;    // orange for rapid/G0
  const COL_ARC     = 0x8be9fd;    // light cyan for arcs
  const COL_POINT   = 0xfff0a8;    // warm yellow
  const COL_POINT_SEL = 0xff9d2f;  // orange selected
  const COL_BG      = 0x08111f;

  // Workspace limit (170x170mm)
  const WS_X = 170, WS_Y = 170;

  function getCanvas() { return document.getElementById('cad-3d-canvas'); }
  function getSvg()    { return document.getElementById('cad-preview'); }
  function getState()  { return window.cadState || {}; }

  // ─── Initialize Three.js scene ─────────────────────────────────────────────
  function initScene() {
    if (initialized) return;
    const canvas = getCanvas();
    if (!canvas) return;

    // Renderer
    renderer = new THREE.WebGLRenderer({ canvas, antialias: true, alpha: false });
    renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
    renderer.setClearColor(COL_BG, 1);

    // Scene
    scene = new THREE.Scene();
    scene.fog = new THREE.FogExp2(COL_BG, 0.0006);

    // Camera
    camera = new THREE.PerspectiveCamera(50, 1, 0.1, 10000);
    camera.position.set(120, -80, 200);
    camera.up.set(0, 0, 1); // Z is up in CNC coordinate system

    // Orbit controls
    controls = new THREE.OrbitControls(camera, canvas);
    controls.enableDamping = true;
    controls.dampingFactor = 0.08;
    controls.rotateSpeed = 0.8;
    controls.zoomSpeed = 1.2;
    controls.panSpeed = 0.8;
    controls.target.set(85, 85, 0);
    controls.update();

    // Lights
    scene.add(new THREE.AmbientLight(0xffffff, 0.6));
    const dirLight = new THREE.DirectionalLight(0xffffff, 0.5);
    dirLight.position.set(100, 100, 200);
    scene.add(dirLight);

    // Static elements
    buildGrid();
    buildAxes();
    buildWorkspaceBounds();

    // Group for CAD data
    cadGroup = new THREE.Group();
    cadGroup.name = 'cadData';
    scene.add(cadGroup);

    initialized = true;
    startRender();
  }

  // ─── Grid ──────────────────────────────────────────────────────────────────
  function buildGrid() {
    const size = 300, divisions = 30;
    const gridHelper = new THREE.GridHelper(size, divisions, 0x1e3350, 0x111d33);
    // Rotate to XY plane (Three.js GridHelper is on XZ by default, we need XY with Z-up)
    gridHelper.rotation.x = Math.PI / 2;
    gridHelper.position.set(size / 2, size / 2, 0);
    scene.add(gridHelper);
  }

  // ─── Axes Arrows ───────────────────────────────────────────────────────────
  function buildAxes() {
    const len = 50;

    // X axis — Red
    addAxisLine(
      [new THREE.Vector3(0, 0, 0), new THREE.Vector3(len, 0, 0)],
      0xff4444
    );
    addArrowhead(len, 0, 0, 0xff4444, 'x');
    addTextSprite('X', len + 8, 0, 0, '#ff4444');

    // Y axis — Green
    addAxisLine(
      [new THREE.Vector3(0, 0, 0), new THREE.Vector3(0, len, 0)],
      0x44ff44
    );
    addArrowhead(0, len, 0, 0x44ff44, 'y');
    addTextSprite('Y', 0, len + 8, 0, '#44ff44');

    // Z axis — Blue
    addAxisLine(
      [new THREE.Vector3(0, 0, 0), new THREE.Vector3(0, 0, len)],
      0x4488ff
    );
    addArrowhead(0, 0, len, 0x4488ff, 'z');
    addTextSprite('Z', 0, 0, len + 8, '#4488ff');

    // Origin sphere
    const originGeom = new THREE.SphereGeometry(2.5, 16, 16);
    const originMat = new THREE.MeshBasicMaterial({ color: 0xffff00 });
    scene.add(new THREE.Mesh(originGeom, originMat));

    // Origin label
    addTextSprite('O (0,0,0)', 12, -8, 0, '#ffff00', 10);
  }

  function addAxisLine(points, color) {
    const geom = new THREE.BufferGeometry().setFromPoints(points);
    const mat = new THREE.LineBasicMaterial({ color, linewidth: 2 });
    scene.add(new THREE.Line(geom, mat));
  }

  function addArrowhead(x, y, z, color, axis) {
    const coneGeom = new THREE.ConeGeometry(2, 7, 8);
    const coneMat = new THREE.MeshBasicMaterial({ color });
    const cone = new THREE.Mesh(coneGeom, coneMat);
    cone.position.set(x, y, z);
    // Cone default orientation is along +Y local axis
    if (axis === 'x') cone.rotation.z = -Math.PI / 2;
    else if (axis === 'z') cone.rotation.x = Math.PI / 2;  // no rotation needed if along Y but we want Z
    // For Y: default is correct (cone along +Y)
    scene.add(cone);
  }

  function addTextSprite(text, x, y, z, color, fontSize) {
    const size = 128;
    const canvas = document.createElement('canvas');
    canvas.width = size; canvas.height = size;
    const ctx = canvas.getContext('2d');
    ctx.font = `bold ${fontSize || 48}px "Segoe UI", Arial, sans-serif`;
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    ctx.fillStyle = color;
    ctx.fillText(text, size / 2, size / 2);

    const texture = new THREE.CanvasTexture(canvas);
    const spriteMat = new THREE.SpriteMaterial({ map: texture, sizeAttenuation: false, transparent: true });
    const sprite = new THREE.Sprite(spriteMat);
    sprite.position.set(x, y, z);
    sprite.scale.set(0.06, 0.06, 1);
    scene.add(sprite);
  }

  // ─── Workspace boundary ────────────────────────────────────────────────────
  function buildWorkspaceBounds() {
    const corners = [
      new THREE.Vector3(0, 0, 0),
      new THREE.Vector3(WS_X, 0, 0),
      new THREE.Vector3(WS_X, WS_Y, 0),
      new THREE.Vector3(0, WS_Y, 0),
      new THREE.Vector3(0, 0, 0),
    ];
    const geom = new THREE.BufferGeometry().setFromPoints(corners);
    const mat = new THREE.LineDashedMaterial({
      color: 0x32c8ff, dashSize: 6, gapSize: 4, opacity: 0.5, transparent: true
    });
    const line = new THREE.Line(geom, mat);
    line.computeLineDistances();
    scene.add(line);

    // Semi-transparent workspace floor
    const floorGeom = new THREE.PlaneGeometry(WS_X, WS_Y);
    const floorMat = new THREE.MeshBasicMaterial({
      color: 0x3b82f6, opacity: 0.04, transparent: true, side: THREE.DoubleSide
    });
    const floor = new THREE.Mesh(floorGeom, floorMat);
    floor.position.set(WS_X / 2, WS_Y / 2, 0);
    scene.add(floor);
  }

  // ─── Render loop ───────────────────────────────────────────────────────────
  function startRender() {
    if (animFrameId) return;
    function loop() {
      animFrameId = requestAnimationFrame(loop);
      if (!is3DMode) return;
      controls.update();
      resizeIfNeeded();
      renderer.render(scene, camera);
    }
    loop();
  }

  function resizeIfNeeded() {
    const canvas = getCanvas();
    if (!canvas) return;
    const parent = canvas.parentElement;
    if (!parent) return;
    const w = parent.clientWidth, h = parent.clientHeight;
    if (canvas.width !== w || canvas.height !== h) {
      renderer.setSize(w, h, false);
      camera.aspect = w / h;
      camera.updateProjectionMatrix();
    }
  }

  // ─── Update 3D scene from state ────────────────────────────────────────────
  function update3DScene() {
    if (!initialized || !cadGroup) return;
    if (!is3DMode) return;

    // Clear previous CAD data
    while (cadGroup.children.length) {
      const c = cadGroup.children[0];
      if (c.geometry) c.geometry.dispose();
      if (c.material && c.material.dispose) c.material.dispose();
      cadGroup.remove(c);
    }

    const appState = getState();
    const dxf = appState.dxf;
    if (!dxf) return;

    const primitives = dxf.primitives || [];
    const points = dxf.points || [];

    if (!primitives.length && !points.length) return;

    // ── Draw primitives (toolpath lines) ──
    for (const prim of primitives) {
      const pts = prim.points || [];
      if (pts.length < 2) continue;

      const st = (prim.sourceType || '').toLowerCase();
      const isRapid = st.includes('g0') || st.includes('rapid');
      const isArc = st.includes('arc') || st.includes('circle');

      const vertices = pts.map(p => new THREE.Vector3(
        p.x || 0,
        p.y || 0,
        p.z != null ? Number(p.z) : 0
      ));

      const geom = new THREE.BufferGeometry().setFromPoints(vertices);

      let line;
      if (isRapid) {
        const mat = new THREE.LineDashedMaterial({
          color: COL_RAPID, dashSize: 4, gapSize: 3, opacity: 0.7, transparent: true
        });
        line = new THREE.Line(geom, mat);
        line.computeLineDistances();
      } else {
        const color = isArc ? COL_ARC : COL_LINE;
        const mat = new THREE.LineBasicMaterial({ color, linewidth: 2 });
        line = new THREE.Line(geom, mat);
      }
      cadGroup.add(line);

      // Shadow projection on Z=0 plane for depth perception
      const shadowVerts = vertices.map(v => new THREE.Vector3(v.x, v.y, 0));
      const shadowGeom = new THREE.BufferGeometry().setFromPoints(shadowVerts);
      const shadowMat = new THREE.LineBasicMaterial({
        color: isRapid ? COL_RAPID : COL_LINE,
        opacity: 0.1,
        transparent: true
      });
      cadGroup.add(new THREE.Line(shadowGeom, shadowMat));

      // Vertical drop lines for Z-elevated points
      for (const v of vertices) {
        if (Math.abs(v.z) > 0.01) {
          const dropGeom = new THREE.BufferGeometry().setFromPoints([
            new THREE.Vector3(v.x, v.y, v.z),
            new THREE.Vector3(v.x, v.y, 0)
          ]);
          const dropMat = new THREE.LineDashedMaterial({
            color: 0x4488ff, dashSize: 2, gapSize: 2, opacity: 0.15, transparent: true
          });
          const dropLine = new THREE.Line(dropGeom, dropMat);
          dropLine.computeLineDistances();
          cadGroup.add(dropLine);
        }
      }
    }

    // ── Draw vertex points ──
    const isGcode = (dxf.fileKind || '').toUpperCase() === 'GCODE';
    if (!isGcode) {
      const selectedKey = dxf.selectedPointKey || '';
      for (const p of points) {
        const isSelected = p.key === selectedKey;
        const size = isSelected ? 3.2 : 1.8;
        const color = isSelected ? COL_POINT_SEL : COL_POINT;

        const geom = new THREE.SphereGeometry(size, 12, 12);
        const mat = new THREE.MeshBasicMaterial({ color });
        const mesh = new THREE.Mesh(geom, mat);
        mesh.position.set(p.x || 0, p.y || 0, p.z != null ? Number(p.z) : 0);
        mesh.userData = { pointKey: p.key };
        cadGroup.add(mesh);
      }
    }

    // ── Auto-fit camera on first load ──
    fitCameraToBounds(dxf.bounds);
  }

  function fitCameraToBounds(bounds) {
    if (!bounds || !camera || !controls) return;
    const cx = (bounds.left || 0) + (bounds.width || 100) / 2;
    const cy = (bounds.top || 0) + (bounds.height || 100) / 2;
    const zMid = ((bounds.minZ || 0) + (bounds.maxZ || 0)) / 2;
    const maxDim = Math.max(bounds.width || 100, bounds.height || 100, Math.abs((bounds.maxZ || 0) - (bounds.minZ || 0)) || 10);
    const dist = maxDim * 1.8;

    if (!cadGroup._fitted) {
      controls.target.set(cx, cy, zMid);
      camera.position.set(cx + dist * 0.5, cy - dist * 0.4, zMid + dist * 0.7);
      controls.update();
      cadGroup._fitted = true;
    }
  }

  // ─── Reset camera view ─────────────────────────────────────────────────────
  function resetCamera() {
    if (!camera || !controls) return;
    const appState = getState();
    const dxf = appState.dxf;
    const bounds = dxf ? dxf.bounds : null;
    const cx = bounds ? (bounds.left || 0) + (bounds.width || 100) / 2 : 85;
    const cy = bounds ? (bounds.top || 0) + (bounds.height || 100) / 2 : 85;
    const zMid = bounds ? ((bounds.minZ || 0) + (bounds.maxZ || 0)) / 2 : 0;
    const maxDim = bounds ? Math.max(bounds.width || 100, bounds.height || 100) : 170;
    const dist = maxDim * 1.8;

    const startPos = camera.position.clone();
    const endPos = new THREE.Vector3(cx + dist * 0.5, cy - dist * 0.4, zMid + dist * 0.7);
    const startTarget = controls.target.clone();
    const endTarget = new THREE.Vector3(cx, cy, zMid);
    const duration = 600;
    const startTime = performance.now();

    function animate(now) {
      const t = Math.min((now - startTime) / duration, 1);
      const ease = t < 0.5 ? 2 * t * t : -1 + (4 - 2 * t) * t;
      camera.position.lerpVectors(startPos, endPos, ease);
      controls.target.lerpVectors(startTarget, endTarget, ease);
      controls.update();
      if (t < 1) requestAnimationFrame(animate);
    }
    requestAnimationFrame(animate);
  }

  // ─── Preset camera views ──────────────────────────────────────────────────
  function setCameraPreset(preset) {
    if (!camera || !controls) return;
    const appState = getState();
    const dxf = appState.dxf;
    const bounds = dxf ? dxf.bounds : null;
    const cx = bounds ? (bounds.left || 0) + (bounds.width || 100) / 2 : 85;
    const cy = bounds ? (bounds.top || 0) + (bounds.height || 100) / 2 : 85;
    const zMid = bounds ? ((bounds.minZ || 0) + (bounds.maxZ || 0)) / 2 : 0;
    const maxDim = bounds ? Math.max(bounds.width || 100, bounds.height || 100) : 170;
    const dist = maxDim * 1.6;

    let endPos;
    switch (preset) {
      case 'top':   endPos = new THREE.Vector3(cx, cy, dist); break;
      case 'front': endPos = new THREE.Vector3(cx, cy - dist, zMid); break;
      case 'right': endPos = new THREE.Vector3(cx + dist, cy, zMid); break;
      case 'iso':
      default:      endPos = new THREE.Vector3(cx + dist * 0.5, cy - dist * 0.4, zMid + dist * 0.7); break;
    }

    const startPos = camera.position.clone();
    const startTarget = controls.target.clone();
    const endTarget = new THREE.Vector3(cx, cy, zMid);
    const duration = 500;
    const startTime = performance.now();

    function animate(now) {
      const t = Math.min((now - startTime) / duration, 1);
      const ease = t < 0.5 ? 2 * t * t : -1 + (4 - 2 * t) * t;
      camera.position.lerpVectors(startPos, endPos, ease);
      controls.target.lerpVectors(startTarget, endTarget, ease);
      controls.update();
      if (t < 1) requestAnimationFrame(animate);
    }
    requestAnimationFrame(animate);
  }

  // ─── View mode switching ───────────────────────────────────────────────────
  function setViewMode(mode) {
    try {
      if (typeof THREE === 'undefined') {
        console.error('[cad3d] THREE.js not loaded!');
        return;
      }
      is3DMode = (mode === '3d');
      const svgEl = getSvg();
      const canvasEl = getCanvas();
      const btn2d = document.getElementById('cad-view-2d');
      const btn3d = document.getElementById('cad-view-3d');
      const label = document.getElementById('cad-view-label');

      if (!svgEl || !canvasEl) {
        console.error('[cad3d] SVG or canvas element not found');
        return;
      }

      if (is3DMode) {
        svgEl.style.display = 'none';
        canvasEl.style.display = 'block';
        if (btn2d) btn2d.classList.remove('is-active');
        if (btn3d) btn3d.classList.add('is-active');
        if (label) label.textContent = '3D Perspective — double-click to reset camera';
        initScene();
        if (cadGroup) cadGroup._fitted = false;
        update3DScene();
        setTimeout(function() { resizeIfNeeded(); if (renderer) renderer.render(scene, camera); }, 50);
      } else {
        svgEl.style.display = 'block';
        canvasEl.style.display = 'none';
        if (btn2d) btn2d.classList.add('is-active');
        if (btn3d) btn3d.classList.remove('is-active');
        if (label) label.textContent = 'XY Projection';
      }
    } catch (err) {
      console.error('[cad3d] setViewMode error:', err);
      alert('Lỗi khởi tạo 3D (có thể máy tính không hỗ trợ WebGL): ' + err.message);
    }
  }

  // ─── Bind events ───────────────────────────────────────────────────────────
  function bindEvents() {
    const btn2d = document.getElementById('cad-view-2d');
    const btn3d = document.getElementById('cad-view-3d');

    if (btn2d) btn2d.addEventListener('click', () => setViewMode('2d'));
    if (btn3d) btn3d.addEventListener('click', () => setViewMode('3d'));

    // Double-click on 3D canvas to reset camera
    const canvas = getCanvas();
    if (canvas) canvas.addEventListener('dblclick', resetCamera);
  }

  // Run after DOM is ready — handles both before and after DOMContentLoaded
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', bindEvents);
  } else {
    bindEvents();
  }

  // ─── Expose API ────────────────────────────────────────────────────────────
  window.cad3d = {
    setViewMode,
    update3DScene,
    resetCamera,
    setCameraPreset,
    is3DMode: () => is3DMode
  };
})();
