"use strict";

/* The pet inspector: one creature, alone, on a background you choose, playing whichever animation
   you point at. Nothing here talks to the overlay, the chat or Twitch – it opens the app's own
   catalog over HTTP, draws the pet with the overlay's stylesheet and its own frame arithmetic, and
   then stays out of the way. Closing the tab is all it takes to be rid of it.

   It exists because a pet is a folder somebody assembles by hand, and the mistakes that folder can
   hold – a row left empty, a drawing sitting half a cell off, a sheet the app reads as nine rows
   when it was drawn as eleven – are invisible on a lawn where six creatures walk past each other
   at 90 pixels tall. */

const KEY = new URLSearchParams(location.search).get("key") || "";
const WANTED = new URLSearchParams(location.search).get("pet") || "";

const stage = document.getElementById("stage");
const stageFrame = document.getElementById("stageFrame");
const statusText = document.getElementById("status");

/* Rows in a hatch-pet sheet, in the app's words rather than the format's: what the overlay reaches
   for each one to do is the useful half when a row turns out to be empty. */
const ROW_NAMES = [
  "Idle – står still",
  "Springer höger",
  "Springer vänster",
  "Vinkar",
  "Hoppar",
  "Misslyckas – förlorat slagsmål",
  "Väntar – sover",
  "Springer – dans och slagsmål",
  "Granskar – lagar mat",
  "Blickar 0–7",
  "Blickar 8–15",
];

/* The behaviors the overlay can put a pet in, as the classes it actually sets. Same classes, same
   stylesheet, same sprite rows – that is the whole point of the view. */
const BEHAVIORS = [
  { id: "idle", label: "Står still", classes: [] },
  { id: "walk", label: "Går", classes: ["walk"] },
  { id: "wave", label: "Vinkar", classes: ["wave"], oneShot: true },
  { id: "jump", label: "Hoppar", classes: ["jump"], oneShot: true },
  { id: "dance", label: "Dansar", classes: ["dance"], oneShot: true },
  { id: "sleep", label: "Sover", classes: ["sleep"] },
  { id: "fight", label: "Slåss", classes: ["fight"] },
  { id: "cook", label: "Lagar mat", classes: ["cook"] },
  { id: "sad", label: "Ledsen – förlorat slagsmål", classes: ["sad"], oneShot: true },
  { id: "lean-right", label: "Myser åt höger", classes: ["lean-right"] },
  { id: "lean-left", label: "Myser åt vänster", classes: ["lean-left"] },
  { id: "spawn", label: "Kommer in", classes: ["spawn"], oneShot: true },
  { id: "despawn", label: "Åker hem", classes: ["despawn"], oneShot: true },
];

const BACKGROUNDS = [
  { id: "checker", label: "Rutmönster – genomskinligt", color: "" },
  { id: "black", label: "Svart", color: "#000000" },
  { id: "white", label: "Vitt", color: "#FFFFFF" },
  { id: "grey", label: "Grått", color: "#808080" },
  { id: "green", label: "Greenscreen", color: "#00B140" },
  { id: "night", label: "Mörk spelbild", color: "#141A2E" },
  { id: "warm", label: "Ljus spelbild", color: "#C9B49A" },
];

const state = {
  catalog: [],
  def: null,
  meta: null, // what measureSprite read out of the sheet
  grid: null, // used[row][col], the inspector's own reading – the sheet map and the gap warnings
  petEl: null,
  spriteEl: null,
  playing: true,
  frame: 0,
  animStart: 0, // when the behavior on screen began, for the one-shots timed from their own start
  manualRow: -1, // -1 = whichever row the behavior asks for
  lookDir: -1, // -1 = not looking anywhere
  facingLeft: false,
  behavior: BEHAVIORS[0],
  fps: 10,
  animationSpeed: 1, // the streamer's own, read from the app so the view plays at the lawn's pace
  scale: 3,
  generation: 0, // bumped on reload, so an edited sheet is fetched rather than remembered
  bodyReport: null,
  sheetSize: null, // the sheet's own pixels, once it has loaded
  sheetError: false, // the sheet never arrived, so the report says so rather than reading for ever
};

const ui = {
  petPick: document.getElementById("petPick"),
  animPick: document.getElementById("animPick"),
  rowPick: document.getElementById("rowPick"),
  rowField: document.getElementById("rowField"),
  lookField: document.getElementById("lookField"),
  lookSlider: document.getElementById("lookSlider"),
  lookRead: document.getElementById("lookRead"),
  fpsInput: document.getElementById("fpsInput"),
  faceLeft: document.getElementById("faceLeft"),
  scaleSlider: document.getElementById("scaleSlider"),
  scaleRead: document.getElementById("scaleRead"),
  rarityPick: document.getElementById("rarityPick"),
  rarityFx: document.getElementById("rarityFx"),
  rarityFaded: document.getElementById("rarityFaded"),
  showName: document.getElementById("showName"),
  showShadow: document.getElementById("showShadow"),
  guides: document.getElementById("guides"),
  playBtn: document.getElementById("playBtn"),
  prevBtn: document.getElementById("prevBtn"),
  nextBtn: document.getElementById("nextBtn"),
  replayBtn: document.getElementById("replayBtn"),
  frameRead: document.getElementById("frameRead"),
  frameSlider: document.getElementById("frameSlider"),
  swatches: document.getElementById("swatches"),
  bgColor: document.getElementById("bgColor"),
  bubbleBtn: document.getElementById("bubbleBtn"),
  sparkleBtn: document.getElementById("sparkleBtn"),
  sheetCard: document.getElementById("sheetCard"),
  sheetImg: document.getElementById("sheetImg"),
  sheetGrid: document.getElementById("sheetGrid"),
  sheetRows: document.getElementById("sheetRows"),
  reportBody: document.getElementById("reportBody"),
  notes: document.getElementById("notes"),
  title: document.getElementById("petTitle"),
  reload: document.getElementById("reloadBtn"),
};

/* ------------------------------------------------------------------ the catalog */

/* The same list the overlay is given, over plain HTTP: this page has no socket and no business on
   one. A refused answer is almost always the app's server being off, so it is said in those words
   rather than as a status code. */

/* The overlay's animation speed, which is a slider in the app rather than anything the sheet knows
   about. Fetched once: the inspector is opened to look at a pet, not to watch the settings change,
   and a speed that could not be read is simply the unscaled one – the same pace the app ships. */
async function loadSpeed() {
  try {
    const response = await fetch(`/api/pets/settings?key=${encodeURIComponent(KEY)}`);
    if (!response.ok) return;
    const petSettings = await response.json();
    if (petSettings.animationSpeed > 0) state.animationSpeed = petSettings.animationSpeed;
  } catch { /* the unscaled pace carries it */ }
  applySpeed();
}

/* The same variable the overlay sets, for the same reason: an SVG pet is animated by pets.css, so
   the speed has to reach the stylesheet rather than only the frame arithmetic. */
function applySpeed() {
  stage.style.setProperty("--pet-anim-speed", state.animationSpeed);
}

async function loadCatalog() {
  try {
    const response = await fetch(`/api/pets/catalog?key=${encodeURIComponent(KEY)}`);
    if (!response.ok) throw new Error(String(response.status));
    state.catalog = await response.json();
  } catch {
    statusText.textContent = "Kunde inte hämta pets från appen. Kör chattservern och öppna vyn från appen igen.";
    return;
  }

  ui.petPick.innerHTML = "";
  for (const def of state.catalog) {
    const option = document.createElement("option");
    option.value = def.id;
    option.textContent = `${def.name} (${def.id})`;
    ui.petPick.appendChild(option);
  }

  const wanted = state.catalog.find((def) => def.id === WANTED) || state.catalog[0];
  if (!wanted) {
    statusText.textContent = "Det finns inga pets i pets-mappen.";
    return;
  }
  ui.petPick.value = wanted.id;
  showPet(wanted.id);
}

function showPet(id) {
  const def = state.catalog.find((entry) => entry.id === id);
  if (!def) return;
  state.def = def;
  state.meta = null;
  state.grid = null;
  state.bodyReport = null;
  state.sheetSize = null;
  state.sheetError = false;
  state.fps = def.fps || 10;
  ui.fpsInput.value = state.fps;
  ui.title.textContent = def.name;
  document.title = `${def.name} – Pet-inspektör`;
  statusText.textContent = "";
  buildPet();
  buildRowPicker();
  renderReport();
}

/* ------------------------------------------------------------------ the creature

   The same markup the overlay builds for a pet, so every selector in pets.css lands the same way
   here. The guide box is the one addition, and it is a sibling nothing in the stylesheet reaches. */
function buildPet() {
  const def = state.def;
  const previous = stage.querySelector(".pet");
  if (previous) previous.remove();

  const el = document.createElement("div");
  el.className = "pet";
  el.innerHTML =
    `<div class="bubble"></div><div class="aura"></div><div class="shadow"></div>` +
    `<div class="body-wrap"></div><span class="spark"></span><span class="spark"></span>` +
    `<div class="name"></div><div class="guide"></div>`;
  el.querySelector(".name").textContent = def.name;
  el.style.setProperty("--accent", accentFor(def.id));
  stage.appendChild(el);
  state.petEl = el;
  state.spriteEl = null;

  const wrap = el.querySelector(".body-wrap");
  if (def.kind === "sprite" && def.spriteUrl) {
    const url = `${def.spriteUrl}?g=${state.generation}`;
    wrap.innerHTML = `<div class="sprite" style="background-image:url('${url}')"></div>`;
    state.spriteEl = wrap.querySelector(".sprite");
    el.classList.add("is-sprite");
    const rows = def.spriteVersion === 2 ? 11 : 9;
    setSpriteRows(rows);
    state.spriteEl.style.setProperty("--sprite-mask", `url('${url}')`);
    // Emptied before the new sheet is asked for rather than when it lands: a sheet that never
    // arrives lands never, and the map left standing would be the last pet's picture and the last
    // pet's cells sitting under this one's name.
    clearSheetMap();
    loadSheet(url, rows);
    ui.sheetCard.style.display = "";
    ui.rowField.style.display = "";
  } else {
    el.classList.remove("is-sprite");
    clearSheetMap();
    ui.sheetCard.style.display = "none";
    ui.rowField.style.display = "none";
    ui.lookField.style.display = "none";
    loadDrawing(def, wrap);
  }

  applyLook();
  applyBehavior();
  applyRarityTier();
  // An SVG pet has no frames to step through – the stylesheet animates it – so the transport is
  // taken away rather than left there doing nothing.
  const framed = state.spriteEl !== null;
  for (const control of [ui.playBtn, ui.prevBtn, ui.nextBtn, ui.frameSlider]) control.disabled = !framed;
  // The toggles are the view's, not the pet's: a creature swapped in under them keeps whatever was
  // already switched on rather than quietly resetting the panel.
  el.querySelector(".shadow").style.display = ui.showShadow.checked ? "" : "none";
  place();
}

/* An accent of the pet's own, the same way the overlay derives one when a pet has no colour: the
   name plate and the fallback drawing both use it. */
function accentFor(id) {
  let hash = 0;
  for (const ch of id) hash = (hash * 31 + ch.codePointAt(0)) >>> 0;
  return `hsl(${hash % 360}, 78%, 62%)`;
}

function setSpriteRows(rows) {
  state.rows = rows;
  if (!state.spriteEl) return;
  state.spriteEl.style.backgroundSize = `800% ${rows * 100}%`;
  state.spriteEl.style.setProperty("--sprite-size", `800% ${rows * 100}%`);
}

/* The drawing, fetched and made safe exactly as the overlay does it – and then looked over, since
   an SVG that renders is not the same thing as an SVG the overlay can animate. */
function loadDrawing(def, wrap) {
  const url = `${def.bodyUrl || `/pets/body/${encodeURIComponent(def.id)}`}?g=${state.generation}`;
  const forPet = def.id;
  // The pet on screen is not enough to know this answer is still wanted: "Ladda om" on the pet you
  // are already looking at asks for the same id again, and the drawing that lands second is not
  // necessarily the one that was asked for second. The generation says which request this was.
  const started = state.generation;
  fetch(url)
    .then((response) => (response.ok ? response.text() : ""))
    .catch(() => "")
    .then((markup) => {
      if (state.def?.id !== forPet || started !== state.generation) return;
      const safe = sanitizeBody(markup);
      state.bodyReport = reviewDrawing(markup, safe);
      wrap.innerHTML = safe.length > 0 ? scopeIds(safe, forPet) : FALLBACK_BODY;
      renderReport();
    });
}

/* What the app will and will not be able to do with this drawing. The part names are a contract
   with pets.css: no .eye and the pet never blinks, no legs and it slides along rather than walks. */
function reviewDrawing(markup, safe) {
  const doc = new DOMParser().parseFromString(markup, "text/html");
  const svg = doc.body.querySelector("svg");
  if (!svg) return { missing: true };

  let stripped = 0;
  for (const el of svg.querySelectorAll("*")) {
    const tag = el.tagName.toLowerCase();
    if (UNSAFE_TAGS.has(tag)) { stripped++; continue; }
    // A stylesheet counts once however much came out of it: the number is meant to say that the
    // drawing held something the overlay will not run, not to tally declarations.
    if (tag === "style" && sanitizeCss(el.textContent) !== el.textContent) stripped++;
    for (const attr of el.attributes) {
      const name = attr.name.toLowerCase();
      if (name.startsWith("on") || (URL_ATTRS.has(name) && !attr.value.trim().startsWith("#"))) stripped++;
      // The same reach as the overlay's: CSS that fetches counts too, wherever it was written.
      else if (safeCssAttr(name, attr.value) !== null) stripped++;
    }
  }

  const has = (selector) => svg.querySelector(selector) !== null;
  return {
    missing: false,
    empty: safe.length === 0,
    viewBox: svg.getAttribute("viewBox") || "",
    elements: svg.querySelectorAll("*").length,
    stripped,
    parts: {
      eye: has(".eye"),
      glow: has(".glow"),
      arms: has(".arm-left") || has(".arm-right"),
      legs: has(".leg-a") || has(".leg-b"),
    },
  };
}

/* ------------------------------------------------------------------ the sheet

   Measured the way the overlay measures it, plus a cell-by-cell reading of its own. The overlay
   only ever needs to know how many frames a row carries; the inspector wants to point at the third
   cell of the fourth row and say that this one is empty. */
function loadSheet(url, fallbackRows) {
  const forPet = state.def.id;
  // As in loadDrawing: a reload of the same pet is a second request for the same id, and the older
  // image finishing last would otherwise overwrite the newer sheet, its map and its report.
  const started = state.generation;
  const img = new Image();
  img.onload = () => {
    if (state.def?.id !== forPet || started !== state.generation) return;
    try {
      state.meta = measureSprite(img, fallbackRows);
      state.grid = readGrid(img, state.meta.rows);
      setSpriteRows(state.meta.rows);
    } catch {
      // A sheet the browser will not let us read pixel by pixel still animates; only the map and
      // the warnings are lost, so the view carries on with what pet.json claimed.
      state.meta = null;
      state.grid = null;
    }
    state.sheetSize = { width: img.naturalWidth, height: img.naturalHeight };
    buildSheetMap(url);
    buildRowPicker();
    applyLook();
    renderReport();
  };
  img.onerror = () => {
    if (state.def?.id !== forPet || started !== state.generation) return;
    // Nothing was read, so nothing is shown: the map stays empty and the report says the sheet is
    // missing rather than reading for ever.
    state.sheetError = true;
    clearSheetMap();
    renderReport();
    statusText.textContent = "Spritesheeten kunde inte läsas in. Kontrollera filen i petens mapp.";
  };
  img.src = url;
}

function readGrid(img, rows) {
  const canvas = document.createElement("canvas");
  canvas.width = img.naturalWidth;
  canvas.height = img.naturalHeight;
  const ctx = canvas.getContext("2d", { willReadFrequently: true });
  ctx.drawImage(img, 0, 0);
  const cellW = img.naturalWidth / SPRITE_COLS;
  const cellH = img.naturalHeight / rows;

  const grid = [];
  for (let row = 0; row < rows; row++) {
    const used = [];
    for (let col = 0; col < SPRITE_COLS; col++) {
      const data = ctx.getImageData(Math.round(col * cellW), Math.round(row * cellH), Math.floor(cellW), Math.floor(cellH)).data;
      let filled = false;
      for (let at = 3; at < data.length && !filled; at += 4) filled = data[at] > 0;
      used.push(filled);
    }
    grid.push(used);
  }
  return grid;
}

/* The map belongs to the one sheet it was read from. Whenever another sheet is asked for – or the
   one asked for turns out not to be there – it goes, rather than being left to be overwritten by a
   picture that may never come. */
function clearSheetMap() {
  ui.sheetImg.removeAttribute("src");
  ui.sheetGrid.innerHTML = "";
  ui.sheetRows.innerHTML = "";
  state.litCell = undefined;
}

function buildSheetMap(url) {
  const rows = state.meta ? state.meta.rows : state.rows;
  ui.sheetImg.src = url;
  ui.sheetGrid.style.gridTemplateColumns = `repeat(${SPRITE_COLS}, 1fr)`;
  ui.sheetGrid.style.gridTemplateRows = `repeat(${rows}, 1fr)`;
  ui.sheetRows.style.gridTemplateRows = `repeat(${rows}, 1fr)`;

  ui.sheetRows.innerHTML = "";
  for (let row = 0; row < rows; row++) {
    const label = document.createElement("div");
    label.textContent = `${row} · ${ROW_NAMES[row] || "okänd rad"}`;
    ui.sheetRows.appendChild(label);
  }

  ui.sheetGrid.innerHTML = "";
  state.litCell = undefined;
  for (let row = 0; row < rows; row++) {
    for (let col = 0; col < SPRITE_COLS; col++) {
      const cell = document.createElement("div");
      cell.className = "sheet-cell";
      if (state.grid && !state.grid[row][col]) cell.classList.add("empty");
      cell.title = `Rad ${row}, ruta ${col}`;
      cell.dataset.row = String(row);
      cell.dataset.col = String(col);
      // Clicking a cell is the quickest way to ask "what is actually drawn here" – so it freezes
      // the pet on exactly that frame rather than merely selecting the row.
      cell.addEventListener("click", () => {
        state.manualRow = row;
        ui.rowPick.value = String(row);
        setPlaying(false);
        state.frame = col;
        ui.frameSlider.value = String(col);
      });
      ui.sheetGrid.appendChild(cell);
    }
  }
}

/* ------------------------------------------------------------------ controls */

function buildBehaviorPicker() {
  for (const behavior of BEHAVIORS) {
    const option = document.createElement("option");
    option.value = behavior.id;
    option.textContent = behavior.label;
    ui.animPick.appendChild(option);
  }
}

function buildRowPicker() {
  const rows = state.meta ? state.meta.rows : state.rows || 9;
  const previous = ui.rowPick.value;
  ui.rowPick.innerHTML = "";

  const auto = document.createElement("option");
  auto.value = "-1";
  auto.textContent = "Auto – den rad beteendet spelar";
  ui.rowPick.appendChild(auto);

  for (let row = 0; row < rows; row++) {
    const option = document.createElement("option");
    option.value = String(row);
    const frames = state.meta && row < 9 ? state.meta.frames[row] : null;
    const count = frames === null ? "" : frames === 0 ? " · tom" : ` · ${frames} rutor`;
    option.textContent = `${row} · ${ROW_NAMES[row] || "okänd"}${count}`;
    ui.rowPick.appendChild(option);
  }
  ui.rowPick.value = previous && Number(previous) < rows ? previous : "-1";
  state.manualRow = Number(ui.rowPick.value);
}

function applyLook() {
  const canLook = !!(state.meta && state.meta.hasLook);
  ui.lookField.style.display = state.spriteEl && canLook ? "" : "none";
  if (!canLook && state.lookDir >= 0) {
    state.lookDir = -1;
    ui.lookSlider.value = "-1";
  }
  ui.lookRead.textContent = state.lookDir < 0 ? "av" : `${state.lookDir * 22.5}°`;
}

/* The classes the overlay would have set, and nothing else: a behavior is not re-implemented here,
   it is played. */
function applyBehavior() {
  const el = state.petEl;
  if (!el) return;
  for (const behavior of BEHAVIORS) el.classList.remove(...behavior.classes);
  el.classList.add(...state.behavior.classes);
  el.classList.toggle("face-left", state.facingLeft);
  state.animStart = performance.now();
}

/* One-shot animations – a wave, an entrance – have played out by the time anyone looks at them
   twice. Taking the class off and putting it back is what the overlay's own replays do, and on a
   spritesheet it is applyBehavior's restarted clock that makes the row play from its first frame
   again rather than the stylesheet. */
function replay() {
  const el = state.petEl;
  if (!el) return;
  for (const behavior of BEHAVIORS) el.classList.remove(...behavior.classes);
  void el.offsetWidth;
  applyBehavior();
}

function setPlaying(playing) {
  state.playing = playing;
  ui.playBtn.textContent = playing ? "⏸ Pausa" : "▶ Spela";
}

function place() {
  const el = state.petEl;
  if (!el) return;
  // Centred by hand rather than with a transform: the entrance and exit animations own .pet's
  // transform, and a translate here would be thrown away the moment one of them plays.
  el.style.left = `${Math.round(stageFrame.clientWidth / 2 - 45 * state.scale)}px`;
}

function applyScale() {
  stage.style.setProperty("--pet-scale", state.scale);
  ui.scaleRead.textContent = `${Math.round(state.scale * 100)} %`;
  place();
}

function applyRarityTier() {
  const el = state.petEl;
  if (!el) return;
  const chosen = ui.rarityPick.value || state.def?.rarity || "";
  const tier = RARITY_CLASSES[chosen.toLowerCase()] || "";
  el.classList.remove("rarity-uncommon", "rarity-rare", "rarity-legendary");
  if (tier) el.classList.add(tier);
  el.classList.toggle("rarity-faded", ui.rarityFaded.checked);
}

function applyBackground(entry) {
  stageFrame.classList.toggle("checker", entry.color === "");
  stageFrame.style.backgroundColor = entry.color || "";
  for (const button of ui.swatches.children) button.classList.toggle("on", button.dataset.bg === entry.id);
}

function buildSwatches() {
  for (const entry of BACKGROUNDS) {
    const button = document.createElement("button");
    button.className = "swatch";
    button.dataset.bg = entry.id;
    button.title = entry.label;
    button.style.background = entry.color || "repeating-conic-gradient(#2A2E3D 0% 25%, #1B1F2C 0% 50%) 0 0 / 12px 12px";
    button.addEventListener("click", () => applyBackground(entry));
    ui.swatches.appendChild(button);
  }
  applyBackground(BACKGROUNDS[0]);
}

/* ------------------------------------------------------------------ the report

   The report is built as markup, and half of what goes in it comes out of a pet.json and an SVG
   somebody else wrote: a name, a rarity, a handful of emoji, the viewBox off a drawing. A pet is
   downloaded and dropped in a folder, so those are a stranger's strings, and this page carries the
   dock key in its own URL – markup smuggled through a display name would be running here with it.

   So every value is escaped on its way in. The `html` tag is what makes that the default rather
   than something to remember: the literal parts of the template are markup, everything interpolated
   into it is text. Composing one tagged result inside another works, because what comes back is
   already escaped. */

function escapeHtml(value) {
  return String(value)
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;");
}

function html(strings, ...values) {
  let out = strings[0];
  for (let i = 0; i < values.length; i++) out += escapeHtml(values[i]) + strings[i + 1];
  return out;
}

/* Both halves are markup the caller has already made safe with `html`. */
function row(label, value) {
  return `<tr><td>${label}</td><td>${value}</td></tr>`;
}

function renderReport() {
  const def = state.def;
  if (!def) return;

  const lines = [
    row("Id", html`<code>${def.id}</code>`),
    row("Namn", escapeHtml(def.name)),
    row("Typ", def.kind === "sprite" ? "Spritesheet" : "SVG-ritning"),
    row("Sällsynthet", escapeHtml(def.rarity || "vanlig")),
    row("Pratbubblor", escapeHtml((def.emoji || []).join(" ")) || "–"),
  ];
  const notes = [];

  if (def.kind === "sprite") {
    const size = state.sheetSize;
    const rows = state.meta ? state.meta.rows : state.rows;
    const declared = def.spriteVersion === 2 ? 11 : 9;
    lines.push(row("Bildstorlek", size ? html`${size.width} × ${size.height} px` : state.sheetError ? "kunde inte läsas" : "läser …"));
    if (state.sheetError) notes.push(["bad", "Spritesheeten kunde inte hämtas. Kontrollera att filen ligger i petens mapp och att webbläsaren klarar formatet."]);
    if (size) {
      const cellW = size.width / SPRITE_COLS;
      const cellH = size.height / rows;
      lines.push(row("Cellstorlek", html`${round(cellW)} × ${round(cellH)} px`));
      lines.push(row("Rader", html`${rows} (pet.json: version ${def.spriteVersion || 1}, ${declared})`));
      lines.push(row("Bildrutor per sekund", html`${def.fps || 10}`));

      if (rows !== declared) {
        notes.push(["warn", html`Arket mäter ${rows} rader men pet.json säger version ${def.spriteVersion || 1} (${declared} rader). Appen litar på mätningen – rätta gärna <code>spriteVersion</code> ändå.`]);
      }
      if (size.width % SPRITE_COLS !== 0) {
        notes.push(["warn", html`Bredden (${size.width} px) går inte jämnt ut på ${SPRITE_COLS} kolumner, så rutorna glider någon pixel i sidled.`]);
      }
      if (size.height % rows !== 0) {
        notes.push(["warn", html`Höjden (${size.height} px) går inte jämnt ut på ${rows} rader, så rutorna glider någon pixel i höjdled.`]);
      }
      if (Math.abs(cellW / cellH - SPRITE_CELL_W / SPRITE_CELL_H) > 0.02) {
        notes.push(["warn", html`Cellerna är ${round(cellW)} × ${round(cellH)} px i stället för ${SPRITE_CELL_W} × ${SPRITE_CELL_H}. Peten blir utdragen eller hoptryckt.`]);
      }
    }

    if (state.meta) {
      const frames = state.meta.frames;
      if (!frames[0]) notes.push(["bad", "Idle-raden är tom. En pet som står still syns inte alls – det är den rad allt annat faller tillbaka på."]);

      const empty = [];
      for (let index = 1; index < frames.length; index++) if (!frames[index]) empty.push(`${index} · ${ROW_NAMES[index]}`);
      if (empty.length) notes.push(["warn", html`Tomma rader, som spelas som idle i stället: ${empty.join(", ")}.`]);

      if (state.grid) {
        const gaps = [];
        for (let index = 0; index < Math.min(frames.length, state.grid.length); index++) {
          for (let col = 0; col < frames[index]; col++) if (!state.grid[index][col]) gaps.push(`rad ${index}, ruta ${col}`);
        }
        if (gaps.length) notes.push(["warn", html`Tomma rutor mitt i en animation: ${gaps.join(", ")}. Peten blinkar bort där.`]);
      }

      if (state.meta.rows >= 11) {
        const looks = state.meta.lookUsed.filter(Boolean).length;
        lines.push(row("Blickrutor", html`${looks} av 16`));
        if (looks === 0) notes.push(["warn", "De två extra raderna är tomma, så peten tittar sig aldrig omkring."]);
        else if (looks < 16) notes.push(["warn", html`${16 - looks} av 16 blickriktningar saknas – de riktningarna visas som idle.`]);
        else notes.push(["ok", "Alla sexton blickriktningar är ifyllda."]);
      } else {
        notes.push(["ok", "Nio rader: peten går, vinkar och hoppar, men tittar sig inte omkring."]);
      }

      if (!notes.some(([kind]) => kind !== "ok")) notes.push(["ok", "Inget att anmärka på i arket."]);
    }
  } else {
    const report = state.bodyReport;
    if (!report) lines.push(row("Ritning", "hämtar …"));
    else if (report.missing || report.empty) {
      notes.push(["bad", "Ritningen kunde inte läsas som SVG. Overlayen visar reservfiguren i stället."]);
    } else {
      lines.push(row("viewBox", report.viewBox ? html`<code>${report.viewBox}</code>` : "saknas"));
      lines.push(row("Element", html`${report.elements}`));
      if (!report.viewBox) notes.push(["warn", "Ritningen saknar viewBox, så den skalas inte med peten."]);
      if (report.stripped > 0) notes.push(["warn", html`${report.stripped} sak(er) plockades bort av säkerhetsskäl – skript, händelser eller adresser utanför filen. Overlayen gör likadant.`]);
      notes.push([report.parts.eye ? "ok" : "warn", report.parts.eye ? "<code>.eye</code> finns – peten blinkar." : "Ingen <code>.eye</code>: peten blinkar aldrig och sover med öppna ögon."]);
      notes.push([report.parts.arms ? "ok" : "warn", report.parts.arms ? "<code>.arm-left</code>/<code>.arm-right</code> finns – peten vinkar." : "Inga armar (<code>.arm-left</code>/<code>.arm-right</code>): vinkningen syns inte."]);
      notes.push([report.parts.legs ? "ok" : "warn", report.parts.legs ? "<code>.leg-a</code>/<code>.leg-b</code> finns – peten går med bensving." : "Inga ben (<code>.leg-a</code>/<code>.leg-b</code>): peten guppar i stället för att gå."]);
      if (report.parts.glow) notes.push(["ok", "<code>.glow</code> finns – den delen pulserar."]);
    }
  }

  ui.reportBody.innerHTML = lines.join("");
  // `kind` is one of this file's own three words and `text` came through `html`, so both are markup
  // by the time they get here.
  ui.notes.innerHTML = notes.map(([kind, text]) => `<li class="${kind}">${text}</li>`).join("");
}

function round(value) { return Math.round(value * 10) / 10; }

/* ------------------------------------------------------------------ the frame loop

   The same arithmetic as the overlay's updateSprite, with the manual controls layered on top: a
   chosen row wins over the behavior's, a chosen look direction wins over both, and a paused view
   shows the frame the slider is on. */
function tick() {
  requestAnimationFrame(tick);
  const sprite = state.spriteEl;
  if (!sprite) {
    ui.frameRead.textContent = state.def
      ? `SVG – animeras av stilmallen · ${round1(state.animationSpeed * 100)} % fart`
      : "–";
    return;
  }

  const meta = state.meta;
  const rows = meta ? meta.rows : state.rows;
  // A row picked by hand is looked at rather than acted out, so it is played at the sheet's own pace
  // on a loop – holding the last frame of a row nobody chose a behavior for would just look stuck.
  let behavior = state.manualRow >= 0 ? IDLE_BEHAVIOR : spriteBehaviorFor(state.petEl.classList);
  let row = state.manualRow >= 0 ? state.manualRow : spriteRowFor(state.petEl.classList, state.facingLeft);
  if (state.manualRow < 0 && meta && row < meta.frames.length && !meta.frames[row]) {
    row = 0;
    behavior = IDLE_BEHAVIOR;
  }

  let frames = row < 9 ? (meta && meta.frames[row]) || SPRITE_COLS : SPRITE_COLS;
  let frame;

  if (state.lookDir >= 0 && state.manualRow < 0 && meta && meta.hasLook) {
    row = 9 + (state.lookDir >> 3);
    frame = state.lookDir & (SPRITE_COLS - 1);
    frames = SPRITE_COLS;
  } else if (state.playing) {
    frame = spriteFrameAt(behavior, frames, state.fps, performance.now(), state.animStart, state.animationSpeed);
    state.frame = frame;
    ui.frameSlider.value = String(frame);
  } else {
    frame = Math.min(state.frame, Math.max(0, frames - 1));
  }

  ui.frameSlider.max = String(Math.max(0, frames - 1));
  const position = `${(frame * 100) / (SPRITE_COLS - 1)}% ${(row * 100) / (rows - 1)}%`;
  sprite.style.backgroundPosition = position;
  sprite.style.setProperty("--sprite-pos", position);

  const drawn = state.grid && state.grid[row] ? state.grid[row][frame] : true;
  // The pace is worth saying out loud: a row with a pace of its own is the reason a wave looks
  // slower here than the sheet's own number suggests, and it should not have to be guessed at.
  const pace = `${round1(spriteFps(behavior, state.fps, state.animationSpeed))} b/s${behavior.loop ? "" : " · en gång"}`;
  ui.frameRead.textContent = `rad ${row} · ruta ${frame + 1}/${frames} · ${pace}${drawn ? "" : " · TOM"}`;
  highlightCell(row, frame);
}

/* The scaled pace lands on numbers like 4.5; a whole one where there is one keeps the readout from
   saying "5.0 b/s" for a sheet that plainly runs at five. */
function round1(value) {
  return Math.round(value * 10) / 10;
}

function highlightCell(row, frame) {
  const index = row * SPRITE_COLS + frame;
  const cells = ui.sheetGrid.children;
  if (state.litCell === index || index >= cells.length) return;
  if (state.litCell !== undefined && cells[state.litCell]) cells[state.litCell].classList.remove("on");
  cells[index].classList.add("on");
  state.litCell = index;
}

/* ------------------------------------------------------------------ wiring */

ui.petPick.addEventListener("change", () => showPet(ui.petPick.value));

ui.reload.addEventListener("click", async () => {
  // A reload here is the same reload the app does: the folder is read again and the pictures are
  // fetched past whatever the browser is holding.
  state.generation++;
  const current = state.def?.id || WANTED;
  await loadCatalog();
  if (state.catalog.some((def) => def.id === current)) {
    ui.petPick.value = current;
    showPet(current);
  }
});

ui.animPick.addEventListener("change", () => {
  state.behavior = BEHAVIORS.find((entry) => entry.id === ui.animPick.value) || BEHAVIORS[0];
  applyBehavior();
});

ui.rowPick.addEventListener("change", () => { state.manualRow = Number(ui.rowPick.value); });

ui.lookSlider.addEventListener("input", () => {
  state.lookDir = Number(ui.lookSlider.value);
  applyLook();
});

ui.fpsInput.addEventListener("change", () => {
  const fps = Number(ui.fpsInput.value);
  state.fps = fps >= 1 && fps <= 60 ? fps : state.def?.fps || 10;
  ui.fpsInput.value = state.fps;
});

ui.faceLeft.addEventListener("change", () => {
  state.facingLeft = ui.faceLeft.checked;
  applyBehavior();
});

ui.scaleSlider.addEventListener("input", () => {
  state.scale = Number(ui.scaleSlider.value);
  applyScale();
});

ui.rarityPick.addEventListener("change", applyRarityTier);
ui.rarityFaded.addEventListener("change", applyRarityTier);
ui.rarityFx.addEventListener("change", () => stage.classList.toggle("rarity-fx", ui.rarityFx.checked));
ui.showName.addEventListener("change", () => stage.classList.toggle("hide-names", !ui.showName.checked));
ui.showShadow.addEventListener("change", () => {
  const shadow = state.petEl?.querySelector(".shadow");
  if (shadow) shadow.style.display = ui.showShadow.checked ? "" : "none";
});
ui.guides.addEventListener("change", () => stage.classList.toggle("guides-on", ui.guides.checked));

ui.playBtn.addEventListener("click", () => setPlaying(!state.playing));
ui.prevBtn.addEventListener("click", () => step(-1));
ui.nextBtn.addEventListener("click", () => step(1));
ui.replayBtn.addEventListener("click", replay);
ui.frameSlider.addEventListener("input", () => {
  setPlaying(false);
  state.frame = Number(ui.frameSlider.value);
});

function step(delta) {
  setPlaying(false);
  const max = Number(ui.frameSlider.max) + 1;
  state.frame = (state.frame + delta + max) % max;
  ui.frameSlider.value = String(state.frame);
}

ui.bgColor.addEventListener("input", () => applyBackground({ id: "custom", color: ui.bgColor.value }));

ui.bubbleBtn.addEventListener("click", () => {
  const bubble = state.petEl?.querySelector(".bubble");
  if (!bubble) return;
  const emoji = state.def?.emoji || [];
  bubble.textContent = emoji.length ? emoji[Math.floor(Math.random() * emoji.length)] : "💬";
  bubble.classList.remove("show");
  void bubble.offsetWidth;
  bubble.classList.add("show");
});

ui.sparkleBtn.addEventListener("click", () => {
  const el = state.petEl;
  if (!el) return;
  const span = document.createElement("span");
  span.className = "float";
  span.textContent = ["✨", "💫", "⭐"][Math.floor(Math.random() * 3)];
  span.style.left = `${el.offsetLeft + 45 * state.scale}px`;
  span.style.bottom = `${70 * state.scale}px`;
  span.addEventListener("animationend", () => span.remove());
  stage.appendChild(span);
});

addEventListener("resize", place);

buildBehaviorPicker();
buildSwatches();
applyScale();
applySpeed();
loadSpeed();
loadCatalog();
requestAnimationFrame(tick);
