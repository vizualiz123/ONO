// The web UI is the new product shell; the old Kimodo/Viser app is still the engine backend.
const ENGINE_URL = new URLSearchParams(window.location.search).get("engine") || "http://127.0.0.1:7860";

const state = {
  tool: "select",
  frame: 0,
  fps: 30,
  frameCount: 180,
  playing: false,
  lastTick: 0,
  projectDirty: false,
  objects: [
    { id: "rig-01", name: "SOMA Rig", type: "Rig", active: true },
    { id: "camera-01", name: "Camera", type: "Camera", active: false },
  ],
  prompts: [
    { id: "prompt-01", start: 0, end: 179, text: "A person walks forward." },
  ],
  pathKeys: [0, 45, 90, 135, 179],
  poseKeys: [0, 60, 120, 179],
};

const els = {};

function $(id) {
  return document.getElementById(id);
}

function bindElements() {
  [
    "viewportCanvas",
    "promptInput",
    "modelSelect",
    "seedInput",
    "variantsInput",
    "stepsInput",
    "fpsInput",
    "frameCountInput",
    "frameSlider",
    "frameReadout",
    "timelineSummary",
    "timelineRuler",
    "promptTrack",
    "pathTrack",
    "keyTrack",
    "playhead",
    "objectList",
    "activeToolLabel",
    "viewportStatus",
    "engineStatus",
    "projectStatus",
    "messageDialog",
    "dialogTitle",
    "dialogBody",
  ].forEach((id) => {
    els[id] = $(id);
  });
}

function setDirty(value = true) {
  state.projectDirty = value;
  els.projectStatus.textContent = value ? "Project: unsaved changes" : "Project: saved locally";
}

function showMessage(title, body) {
  els.dialogTitle.textContent = title;
  els.dialogBody.textContent = body;
  els.messageDialog.showModal();
}

function clamp(value, min, max) {
  return Math.min(max, Math.max(min, value));
}

function seconds() {
  return state.frameCount / state.fps;
}

function setFrame(frame) {
  state.frame = clamp(Math.round(frame), 0, state.frameCount - 1);
  els.frameSlider.value = String(state.frame);
  els.frameReadout.textContent = `Frame ${state.frame} / ${state.frameCount - 1}`;
  updatePlayhead();
  drawViewport();
}

function setTool(tool) {
  state.tool = tool;
  document.querySelectorAll("[data-tool]").forEach((button) => {
    button.classList.toggle("active", button.dataset.tool === tool);
  });
  document.querySelectorAll("[data-top-tool]").forEach((button) => {
    button.classList.toggle("active", button.dataset.topTool === tool);
  });
  els.activeToolLabel.textContent = tool[0].toUpperCase() + tool.slice(1);
}

function setButtonIcon(button, icon) {
  const iconNode = button?.querySelector(".button-icon");
  if (iconNode) {
    iconNode.textContent = icon;
  } else if (button) {
    button.textContent = icon;
  }
}

function setRoundIcon(button, icon) {
  const iconNode = button?.querySelector(".round-icon");
  if (iconNode) {
    iconNode.textContent = icon;
  }
}

function activatePanel(panelName) {
  document.querySelectorAll("[data-panel]").forEach((button) => {
    button.classList.toggle("active", button.dataset.panel === panelName);
  });
}

function focusUsdPanel() {
  activatePanel("assets");
  $("usdPanel")?.scrollIntoView({ behavior: "smooth", block: "center" });
}

function renderObjects() {
  els.objectList.innerHTML = "";
  state.objects.forEach((object) => {
    const item = document.createElement("button");
    item.className = `object-item${object.active ? " active" : ""}`;
    item.type = "button";
    item.innerHTML = `<span class="object-dot"></span><span>${object.name}</span><small>${object.type}</small>`;
    item.addEventListener("click", () => {
      state.objects.forEach((obj) => {
        obj.active = obj.id === object.id;
      });
      renderObjects();
      setDirty();
    });
    els.objectList.appendChild(item);
  });
}

function updateTimelineSummary() {
  const count = state.prompts.length;
  const word = count === 1 ? "prompt block" : "prompt blocks";
  els.timelineSummary.textContent = `${count} ${word} · ${seconds().toFixed(1)} sec`;
}

function renderRuler() {
  els.timelineRuler.innerHTML = "";
  const step = state.frameCount <= 180 ? 30 : 60;
  for (let frame = 0; frame < state.frameCount; frame += step) {
    const tick = document.createElement("span");
    tick.className = "ruler-tick";
    tick.style.left = `${(frame / (state.frameCount - 1)) * 100}%`;
    tick.textContent = String(frame);
    els.timelineRuler.appendChild(tick);
  }
}

function renderPromptTrack() {
  els.promptTrack.innerHTML = "";
  state.prompts.forEach((prompt, index) => {
    const clip = document.createElement("button");
    clip.className = "clip";
    clip.type = "button";
    clip.textContent = prompt.text;
    clip.title = "Клик: редактировать prompt в нижнем окне";
    const startPct = (prompt.start / (state.frameCount - 1)) * 100;
    const widthPct = ((prompt.end - prompt.start + 1) / state.frameCount) * 100;
    clip.style.left = `${startPct}%`;
    clip.style.width = `${Math.max(8, widthPct)}%`;
    clip.addEventListener("click", () => {
      els.promptInput.value = prompt.text;
      els.promptInput.focus();
      els.promptInput.dataset.promptId = prompt.id;
    });
    clip.addEventListener("dblclick", () => {
      const nextText = window.prompt("Prompt", prompt.text);
      if (nextText !== null) {
        prompt.text = nextText.trim() || prompt.text;
        if (index === 0) {
          els.promptInput.value = prompt.text;
        }
        renderTimeline();
        setDirty();
      }
    });
    els.promptTrack.appendChild(clip);
  });
}

function renderKeyTrack(track, frames, className) {
  track.innerHTML = "";
  frames.forEach((frame) => {
    const node = document.createElement("button");
    node.className = className;
    node.type = "button";
    node.title = `Frame ${frame}`;
    node.style.left = `${(frame / (state.frameCount - 1)) * 100}%`;
    node.addEventListener("click", () => setFrame(frame));
    track.appendChild(node);
  });
}

function updatePlayhead() {
  const left = state.frameCount <= 1 ? 0 : (state.frame / (state.frameCount - 1)) * 100;
  els.playhead.style.left = `${left}%`;
}

function renderTimeline() {
  renderRuler();
  renderPromptTrack();
  renderKeyTrack(els.pathTrack, state.pathKeys, "path-dot");
  renderKeyTrack(els.keyTrack, state.poseKeys, "key");
  updateTimelineSummary();
  updatePlayhead();
}

function applyPromptToTrack() {
  const text = els.promptInput.value.trim();
  if (!text) {
    showMessage("Prompt пустой", "Напиши движение в поле Prompt. Поле теперь обычное редактируемое textarea.");
    return;
  }
  const promptId = els.promptInput.dataset.promptId || state.prompts[0]?.id;
  const target = state.prompts.find((prompt) => prompt.id === promptId) || state.prompts[0];
  target.text = text;
  renderTimeline();
  drawViewport();
  setDirty();
}

function addPromptBlock() {
  const start = state.prompts.length ? state.prompts[state.prompts.length - 1].end + 1 : 0;
  if (start >= state.frameCount - 1) {
    showMessage("Timeline full", "Увеличь число кадров, чтобы добавить еще один prompt block.");
    return;
  }
  const remaining = state.frameCount - start;
  const end = Math.min(state.frameCount - 1, start + Math.max(30, Math.floor(remaining / 2)) - 1);
  const text = els.promptInput.value.trim() || "New motion prompt.";
  state.prompts.push({
    id: `prompt-${Date.now()}`,
    start,
    end,
    text,
  });
  renderTimeline();
  setDirty();
}

function resizeCanvas() {
  const canvas = els.viewportCanvas;
  const rect = canvas.getBoundingClientRect();
  const dpr = window.devicePixelRatio || 1;
  const nextWidth = Math.max(1, Math.floor(rect.width * dpr));
  const nextHeight = Math.max(1, Math.floor(rect.height * dpr));
  if (canvas.width !== nextWidth || canvas.height !== nextHeight) {
    canvas.width = nextWidth;
    canvas.height = nextHeight;
  }
  drawViewport();
}

function drawGrid(ctx, width, height, dpr) {
  ctx.save();
  ctx.translate(width / 2, height * 0.62);
  ctx.scale(dpr, dpr);
  const spacing = 34;
  ctx.strokeStyle = "rgba(72, 221, 255, 0.12)";
  ctx.lineWidth = 1;
  for (let i = -36; i <= 36; i += 1) {
    ctx.beginPath();
    ctx.moveTo(-900, i * spacing * 0.35);
    ctx.lineTo(900, i * spacing * 0.35);
    ctx.stroke();
    ctx.beginPath();
    ctx.moveTo(i * spacing, -240);
    ctx.lineTo(i * spacing * 0.35, 210);
    ctx.stroke();
  }
  ctx.strokeStyle = "rgba(84, 242, 180, 0.22)";
  ctx.beginPath();
  ctx.moveTo(-900, 0);
  ctx.lineTo(900, 0);
  ctx.stroke();
  ctx.strokeStyle = "rgba(255, 93, 115, 0.2)";
  ctx.beginPath();
  ctx.moveTo(0, -260);
  ctx.lineTo(0, 220);
  ctx.stroke();
  ctx.restore();
}

function drawRig(ctx, width, height, dpr) {
  const t = (state.frame / Math.max(1, state.frameCount - 1)) * Math.PI * 2;
  const cx = width / 2;
  const cy = height * 0.49;
  const scale = Math.min(width, height) / 720;
  const bob = Math.sin(t * 2) * 8 * scale;
  const walk = Math.sin(t) * 26 * scale;

  const pts = {
    head: [cx, cy - 126 * scale + bob],
    neck: [cx, cy - 86 * scale + bob],
    chest: [cx, cy - 48 * scale + bob],
    hips: [cx, cy + 16 * scale + bob],
    lHand: [cx - 72 * scale - walk * 0.35, cy - 32 * scale - bob],
    rHand: [cx + 72 * scale - walk * 0.35, cy - 32 * scale + bob],
    lKnee: [cx - 30 * scale + walk, cy + 86 * scale],
    rKnee: [cx + 30 * scale - walk, cy + 86 * scale],
    lFoot: [cx - 52 * scale - walk, cy + 150 * scale],
    rFoot: [cx + 52 * scale + walk, cy + 150 * scale],
  };

  const lines = [
    ["head", "neck"],
    ["neck", "chest"],
    ["chest", "hips"],
    ["chest", "lHand"],
    ["chest", "rHand"],
    ["hips", "lKnee"],
    ["hips", "rKnee"],
    ["lKnee", "lFoot"],
    ["rKnee", "rFoot"],
  ];

  ctx.save();
  ctx.lineCap = "round";
  ctx.lineJoin = "round";
  ctx.shadowColor = "rgba(72, 221, 255, 0.45)";
  ctx.shadowBlur = 18 * dpr;
  ctx.strokeStyle = "#48ddff";
  ctx.lineWidth = 5 * scale;
  lines.forEach(([a, b]) => {
    ctx.beginPath();
    ctx.moveTo(pts[a][0], pts[a][1]);
    ctx.lineTo(pts[b][0], pts[b][1]);
    ctx.stroke();
  });
  Object.values(pts).forEach(([x, y]) => {
    ctx.beginPath();
    ctx.fillStyle = "#081117";
    ctx.strokeStyle = "#54f2b4";
    ctx.lineWidth = 2 * scale;
    ctx.arc(x, y, 8 * scale, 0, Math.PI * 2);
    ctx.fill();
    ctx.stroke();
  });
  ctx.restore();
}

function drawViewport() {
  const canvas = els.viewportCanvas;
  const ctx = canvas.getContext("2d");
  const width = canvas.width;
  const height = canvas.height;
  const dpr = window.devicePixelRatio || 1;
  ctx.clearRect(0, 0, width, height);

  const bg = ctx.createLinearGradient(0, 0, 0, height);
  bg.addColorStop(0, "#081018");
  bg.addColorStop(1, "#020305");
  ctx.fillStyle = bg;
  ctx.fillRect(0, 0, width, height);

  drawGrid(ctx, width, height, dpr);
  drawRig(ctx, width, height, dpr);

  ctx.save();
  ctx.fillStyle = "rgba(255, 255, 255, 0.58)";
  ctx.font = `${12 * dpr}px Segoe UI, Arial`;
  ctx.fillText(`Prompt: ${state.prompts[0]?.text || "empty"}`, 18 * dpr, height - 26 * dpr);
  ctx.restore();
}

function togglePlayback(force) {
  state.playing = typeof force === "boolean" ? force : !state.playing;
  setButtonIcon($("playTimeline"), state.playing ? "Ⅱ" : "▸");
  $("playTimelineBottom").textContent = state.playing ? "Ⅱ" : "▶";
  document.querySelectorAll('[data-top-action="play"]').forEach((button) => {
    setRoundIcon(button, state.playing ? "Ⅱ" : "▸");
  });
  if (state.playing) {
    state.lastTick = performance.now();
    requestAnimationFrame(playLoop);
  }
}

function playLoop(now) {
  if (!state.playing) {
    return;
  }
  const delta = (now - state.lastTick) / 1000;
  state.lastTick = now;
  const nextFrame = state.frame + delta * state.fps;
  setFrame(nextFrame >= state.frameCount - 1 ? 0 : nextFrame);
  requestAnimationFrame(playLoop);
}

function saveProject() {
  applyPromptToTrack();
  const project = {
    app: "Nein3D Studio",
    version: 1,
    savedAt: new Date().toISOString(),
    engineUrl: ENGINE_URL,
    settings: {
      model: els.modelSelect.value,
      seed: Number(els.seedInput.value),
      variants: Number(els.variantsInput.value),
      steps: Number(els.stepsInput.value),
      fps: state.fps,
      frameCount: state.frameCount,
    },
    objects: state.objects,
    prompts: state.prompts,
    pathKeys: state.pathKeys,
    poseKeys: state.poseKeys,
  };
  localStorage.setItem("nein3d.project", JSON.stringify(project, null, 2));
  const blob = new Blob([JSON.stringify(project, null, 2)], { type: "application/json" });
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = "project.nein3d.json";
  link.click();
  URL.revokeObjectURL(url);
  setDirty(false);
}

function loadLocalProject() {
  const raw = localStorage.getItem("nein3d.project");
  if (!raw) {
    return;
  }
  try {
    const project = JSON.parse(raw);
    if (Array.isArray(project.prompts) && project.prompts.length) {
      state.prompts = project.prompts;
      els.promptInput.value = state.prompts[0].text;
    }
    if (Array.isArray(project.objects)) {
      state.objects = project.objects;
    }
    if (project.settings) {
      state.fps = Number(project.settings.fps || state.fps);
      state.frameCount = Number(project.settings.frameCount || state.frameCount);
      els.fpsInput.value = String(state.fps);
      els.frameCountInput.value = String(state.frameCount);
    }
  } catch {
    localStorage.removeItem("nein3d.project");
  }
}

function addRig() {
  state.objects.forEach((obj) => {
    obj.active = false;
  });
  state.objects.push({
    id: `rig-${Date.now()}`,
    name: `Rig ${state.objects.filter((obj) => obj.type === "Rig").length + 1}`,
    type: "Rig",
    active: true,
  });
  renderObjects();
  setDirty();
}

function clearScene() {
  state.objects = [{ id: "camera-01", name: "Camera", type: "Camera", active: true }];
  state.pathKeys = [];
  state.poseKeys = [state.frame];
  renderObjects();
  renderTimeline();
  drawViewport();
  setDirty();
}

function loadUsd() {
  const path = $("usdPathInput").value.trim() || "asset.usd";
  state.objects.forEach((obj) => {
    obj.active = false;
  });
  state.objects.push({
    id: `usd-${Date.now()}`,
    name: path.split(/[\\/]/).pop(),
    type: "USD",
    active: true,
  });
  renderObjects();
  setDirty();
  showMessage("USD добавлен в web scene", "Это новый web-интерфейс. Реальная загрузка USD в движок остается на backend-этапе.");
}

function clearUsd() {
  state.objects = state.objects.filter((object) => object.type !== "USD");
  if (!state.objects.some((object) => object.active) && state.objects[0]) {
    state.objects[0].active = true;
  }
  renderObjects();
  setDirty();
}

function updateFrameSettings() {
  state.fps = clamp(Number(els.fpsInput.value) || 30, 1, 120);
  state.frameCount = clamp(Number(els.frameCountInput.value) || 180, 30, 900);
  els.fpsInput.value = String(state.fps);
  els.frameCountInput.value = String(state.frameCount);
  els.frameSlider.max = String(state.frameCount - 1);
  state.prompts[state.prompts.length - 1].end = state.frameCount - 1;
  setFrame(clamp(state.frame, 0, state.frameCount - 1));
  renderTimeline();
  setDirty();
}

function bindEvents() {
  document.querySelectorAll("[data-tool]").forEach((button) => {
    button.addEventListener("click", () => setTool(button.dataset.tool));
  });
  document.querySelectorAll("[data-top-tool]").forEach((button) => {
    button.addEventListener("click", () => setTool(button.dataset.topTool));
  });
  document.querySelectorAll("[data-top-action]").forEach((button) => {
    button.addEventListener("click", () => {
      const action = button.dataset.topAction;
      if (action === "usd") {
        focusUsdPanel();
      } else if (action === "generate") {
        $("generateMotion").click();
      } else if (action === "play") {
        togglePlayback();
      } else if (action === "save") {
        saveProject();
      } else if (action === "engine") {
        window.open(ENGINE_URL, "_blank", "noopener");
      }
    });
  });
  document.querySelectorAll("[data-menu]").forEach((button) => {
    button.addEventListener("click", () => {
      if (button.dataset.menu === "usd") {
        focusUsdPanel();
      }
    });
  });
  document.querySelectorAll("[data-left-tool]").forEach((button) => {
    button.addEventListener("click", () => {
      document.querySelectorAll("[data-left-tool]").forEach((item) => item.classList.remove("active"));
      button.classList.add("active");
      if (button.dataset.leftTool === "usd") {
        focusUsdPanel();
      }
    });
  });
  document.querySelectorAll("[data-panel]").forEach((button) => {
    button.addEventListener("click", () => {
      document.querySelectorAll("[data-panel]").forEach((item) => item.classList.remove("active"));
      button.classList.add("active");
    });
  });

  els.promptInput.addEventListener("input", () => {
    els.viewportStatus.textContent = "Prompt editing";
    setDirty();
  });
  $("applyPrompt").addEventListener("click", applyPromptToTrack);
  $("addPromptBlock").addEventListener("click", addPromptBlock);
  $("generateMotion").addEventListener("click", () => {
    applyPromptToTrack();
    // TODO: replace this placeholder with a backend call once /api/generate exists.
    showMessage("Generate готов к подключению", "Prompt редактируется в web UI. Следующий шаг - связать эту кнопку с backend генерацией напрямую.");
  });
  $("playTimeline").addEventListener("click", () => togglePlayback());
  $("playTimelineBottom").addEventListener("click", () => togglePlayback());
  $("stopTimeline").addEventListener("click", () => {
    togglePlayback(false);
    setFrame(0);
  });
  $("prevFrame").addEventListener("click", () => setFrame(state.frame - 1));
  $("nextFrame").addEventListener("click", () => setFrame(state.frame + 1));
  els.frameSlider.addEventListener("input", () => setFrame(Number(els.frameSlider.value)));
  els.fpsInput.addEventListener("change", updateFrameSettings);
  els.frameCountInput.addEventListener("change", updateFrameSettings);
  $("saveProjectTop").addEventListener("click", saveProject);
  $("openEngine").addEventListener("click", () => window.open(ENGINE_URL, "_blank", "noopener"));
  $("undoAction").addEventListener("click", () => showMessage("Undo", "История действий будет подключена на следующем backend-этапе."));
  $("redoAction").addEventListener("click", () => showMessage("Redo", "История действий будет подключена на следующем backend-этапе."));
  document.querySelectorAll('[data-toolbar-action="usd"]').forEach((button) => {
    button.addEventListener("click", focusUsdPanel);
  });
  $("addRig").addEventListener("click", addRig);
  $("clearScene").addEventListener("click", clearScene);
  $("loadUsd").addEventListener("click", loadUsd);
  $("clearUsd").addEventListener("click", clearUsd);
  window.addEventListener("resize", resizeCanvas);
  window.addEventListener("keydown", (event) => {
    if (event.code === "Space" && document.activeElement !== els.promptInput) {
      event.preventDefault();
      togglePlayback();
    }
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "s") {
      event.preventDefault();
      saveProject();
    }
  });
}

async function checkEngine() {
  try {
    await fetch(ENGINE_URL, { mode: "no-cors", cache: "no-store" });
    els.engineStatus.textContent = `Engine: online ${ENGINE_URL}`;
  } catch {
    els.engineStatus.textContent = `Engine: offline ${ENGINE_URL}`;
  }
}

function boot() {
  bindElements();
  loadLocalProject();
  bindEvents();
  renderObjects();
  renderTimeline();
  updateFrameSettings();
  setTool("select");
  resizeCanvas();
  checkEngine();
  setInterval(checkEngine, 12000);
}

boot();
