"use strict";

/* Channel point pets: small creatures that wander the bottom edge of a transparent OBS browser
   source. The app decides who gets a pet and which species it is; this page only animates what
   the server says is alive. */

const KEY = new URLSearchParams(location.search).get("key") || "";
const stage = document.getElementById("stage");

const pets = new Map(); // id -> pet
const catalog = new Map(); // species id -> definition from the server
let settings = { enabled: true, scale: 1, lifetimeMinutes: 5, maxPets: 6, showNames: true };
let duetActive = false;
let nextDuetAt = Date.now() + 12000;

const EDGE = 10;
const BASE_SIZE = 90;

/* ------------------------------------------------------------------ transport */

let socket = null;
let reconnectDelay = 1000;

function connect() {
  socket = new WebSocket(`ws://${location.host}/ws?key=${encodeURIComponent(KEY)}`);
  socket.onopen = () => { reconnectDelay = 1000; };
  socket.onmessage = (event) => handle(JSON.parse(event.data));
  socket.onclose = () => {
    setTimeout(connect, reconnectDelay);
    reconnectDelay = Math.min(15000, reconnectDelay * 1.7);
  };
  socket.onerror = () => socket.close();
}

function handle(frame) {
  if (frame.type === "hello") {
    applySettings(frame.petSettings);
    applyCatalog(frame.petCatalog);
    syncPets(frame.pets || []);
    return;
  }
  if (frame.type === "petSettings") { applySettings(frame.payload); return; }
  if (frame.type === "petCatalog") { applyCatalog(frame.payload); return; }
  if (frame.type === "petSpawn") {
    const { pet, removedId, extended } = frame.payload;
    if (removedId) removePet(removedId, true);
    if (extended && pets.has(pet.id)) extendPet(pet);
    else spawnPet(pet);
  }
  // Chat frames from the shared socket are someone else's business.
}

function applySettings(next) {
  if (!next) return;
  settings = next;
  stage.style.setProperty("--pet-scale", settings.scale);
  stage.classList.toggle("disabled", !settings.enabled);
  stage.classList.toggle("hide-names", settings.showNames === false);
}

/* A reloaded catalog means the user just edited a pet, so the drawings are re-fetched and every
   pet already on screen is redrawn. */
function applyCatalog(list) {
  if (!list) return;
  catalog.clear();
  for (const def of list) catalog.set(def.id, def);
  generation++;
  bodies.clear();
  pending.clear();
  spriteMeta.clear();
  for (const pet of pets.values()) applyBody(pet);
}

/* The server list is the truth: drop pets it no longer knows, add the ones it does. */
function syncPets(list) {
  const alive = new Set(list.map((p) => p.id));
  for (const id of [...pets.keys()]) if (!alive.has(id)) removePet(id, false);
  for (const p of list) {
    if (pets.has(p.id)) {
      const existing = pets.get(p.id);
      existing.expiresAt = p.expiresAt;
      if (p.species && p.species !== existing.species) morphPet(existing, p.species);
      continue;
    }
    spawnPet(p);
  }
}

/* ------------------------------------------------------------------ the drawings

   Every pet is a folder in the user's pets folder, so the bodies are fetched from the app rather
   than kept here. A body is a plain SVG in a 100×100 box that shares a part contract with the CSS:
   .eye blinks, .glow pulses, .arm-left/.arm-right swing and wave, .leg-a/.leg-b walk. A pet
   without legs simply bobs along instead. */

const bodies = new Map(); // species id -> svg markup
const pending = new Map(); // species id -> in-flight fetch
let generation = 0; // bumped on reload, so a fetch started before an edit cannot land after it

/* Shown when a pet's drawing cannot be fetched at all – an empty patch of ground would look like
   the overlay was broken. */
const FALLBACK_BODY = `<svg viewBox="0 0 100 100" xmlns="http://www.w3.org/2000/svg" aria-hidden="true">
  <circle cx="50" cy="60" r="30" fill="var(--accent)" opacity="0.9" />
  <circle class="eye" cx="41" cy="55" r="4" fill="#141B26" />
  <circle class="eye" cx="59" cy="55" r="4" fill="#141B26" />
  <path d="M43 70 Q50 75 57 70" stroke="#141B26" stroke-width="2.5" fill="none" stroke-linecap="round" />
</svg>`;

/* A drawing is a file in the streamer's own pets folder, but a pet downloaded from someone else is
   a stranger's markup – and this page carries the dock's access key. Every body is therefore parsed
   into an inert document and stripped of scripts, event handlers and outbound references before it
   is allowed near the DOM. Ordinary drawing markup, animations included, passes through untouched. */
const UNSAFE_TAGS = new Set(["script", "foreignobject", "iframe", "object", "embed"]);
const URL_ATTRS = new Set(["href", "xlink:href", "src"]);

function sanitizeBody(markup) {
  // Parsed as HTML rather than XML: nothing runs and nothing is fetched either way, but the HTML
  // parser forgives the hand-edited files a streamer is invited to write.
  const svg = new DOMParser().parseFromString(markup, "text/html").body.querySelector("svg");
  if (!svg) return "";
  scrub(svg);
  return svg.outerHTML;
}

function scrub(el) {
  for (const attr of [...el.attributes]) {
    const name = attr.name.toLowerCase();
    const value = attr.value.trim();
    if (name.startsWith("on") ||
        // <set attributeName="onclick" …> would otherwise smuggle a handler back in.
        (name === "attributename" && value.toLowerCase().startsWith("on")) ||
        // Only local fragments survive, so no javascript: link and no call home for a tracking pixel.
        (URL_ATTRS.has(name) && !value.startsWith("#"))) {
      el.removeAttribute(attr.name);
    }
  }
  for (const child of [...el.children]) {
    if (UNSAFE_TAGS.has(child.tagName.toLowerCase())) child.remove();
    else scrub(child);
  }
}

/* Ids inside a body are local to that pet: two pets copied from the same file would otherwise
   fight over the same gradient, and the first one in the DOM would win for both. */
function scopeIds(svg, species) {
  return svg
    .replace(/id="([^"]*)"/g, `id="${species}--$1"`)
    .replace(/url\(#([^)]*)\)/g, `url(#${species}--$1)`)
    .replace(/href="#([^"]*)"/g, `href="#${species}--$1"`);
}

function loadBody(species) {
  const cached = bodies.get(species);
  if (cached !== undefined) return Promise.resolve(cached);

  let job = pending.get(species);
  if (!job) {
    const def = catalog.get(species);
    const url = (def && def.bodyUrl) || `/pets/body/${encodeURIComponent(species)}`;
    const started = generation;
    job = fetch(url)
      .then((response) => (response.ok ? response.text() : ""))
      .catch(() => "")
      .then((svg) => {
        const safe = sanitizeBody(svg);
        const body = safe.length > 0 ? scopeIds(safe, species) : FALLBACK_BODY;
        if (started === generation) {
          bodies.set(species, body);
          pending.delete(species);
        }
        return body;
      });
    pending.set(species, job);
  }
  return job;
}

function flavorEmoji(pet) {
  const emoji = catalog.get(pet.species)?.emoji;
  return emoji && emoji.length ? pick(emoji) : "💬";
}

function accentFor(pet) {
  if (pet.color && /^#[0-9a-f]{6}$/i.test(pet.color)) return pet.color;
  let hash = 0;
  for (const ch of pet.id) hash = (hash * 31 + ch.codePointAt(0)) >>> 0;
  return `hsl(${hash % 360}, 78%, 62%)`;
}

/* ------------------------------------------------------------------ lifecycle */

function petWidth() { return BASE_SIZE * (settings.scale || 1); }
function clampX(x) { return Math.min(Math.max(x, EDGE), Math.max(EDGE, innerWidth - petWidth() - EDGE)); }
function rand(min, max) { return min + Math.random() * (max - min); }
function pick(list) { return list[Math.floor(Math.random() * list.length)]; }
function sleep(ms) { return new Promise((resolve) => setTimeout(resolve, ms)); }

function applyBody(pet) {
  const wrap = pet.el.querySelector(".body-wrap");
  const def = catalog.get(pet.species);
  pet.spriteEl = null;
  pet.spriteRow = -1;
  pet.spriteFrame = -1;
  pet.lookAngle = null;

  if (def && def.kind === "sprite" && def.spriteUrl) {
    // The generation in the URL is what makes "Ladda om pets" reach an edited sheet: the same
    // address again could hand back the image the browser already holds.
    const url = `${def.spriteUrl}?g=${generation}`;
    wrap.innerHTML = `<div class="sprite" style="background-image:url('${url}')"></div>`;
    pet.spriteEl = wrap.querySelector(".sprite");
    pet.spriteFps = def.fps || 10;
    pet.spriteRows = def.spriteVersion === 2 ? 11 : 9;
    pet.spriteEl.style.backgroundSize = `800% ${pet.spriteRows * 100}%`;
    pet.el.classList.add("is-sprite");
    loadSpriteMeta(pet.species, url, pet.spriteRows);
    return;
  }

  pet.el.classList.remove("is-sprite");
  const species = pet.species;
  const cached = bodies.get(species);
  if (cached !== undefined) { wrap.innerHTML = cached; return; }

  // First pet of this species on screen: the drawing arrives a moment later.
  wrap.innerHTML = "";
  const started = generation;
  loadBody(species).then((svg) => {
    if (!pet.removed && pet.species === species && started === generation) wrap.innerHTML = svg;
  });
}

function spawnPet(data) {
  if (pets.has(data.id)) { extendPet(data); return; }

  const el = document.createElement("div");
  el.className = "pet spawn";
  el.style.setProperty("--accent", accentFor(data));
  el.innerHTML = `<div class="bubble"></div><div class="shadow"></div><div class="body-wrap"></div><div class="name"></div>`;
  el.querySelector(".name").textContent = data.name;
  stage.appendChild(el);

  const pet = {
    id: data.id,
    name: data.name,
    species: data.species || "robo",
    el,
    bubble: el.querySelector(".bubble"),
    spriteEl: null,
    spriteFps: 10,
    spriteRows: 9,
    spriteRow: -1,
    spriteFrame: -1,
    lookAngle: null,
    x: clampX(rand(EDGE, innerWidth - petWidth() - EDGE)),
    targetX: null,
    walkResolve: null,
    speed: 60,
    facingLeft: false,
    busy: false,
    sleepy: false,
    removed: false,
    nextDecideAt: Date.now() + 1400,
    expiresAt: data.expiresAt,
  };
  applyBody(pet);
  // Positioned via left, never transform: the behavior animations own the transform.
  el.style.left = `${pet.x}px`;
  el.addEventListener("animationend", (e) => { if (e.target === el) el.classList.remove("spawn"); });
  pets.set(pet.id, pet);

  sparkle(pet.x + petWidth() / 2, petWidth() * 0.8, ["✨", "✨", "⭐"]);
  setTimeout(() => { if (!pet.removed) showBubble(pet, "👋"); }, 900);
}

/* Re-redeeming with another species name transforms the pet in place. */
function morphPet(pet, species) {
  pet.species = species;
  applyBody(pet);
  sparkle(pet.x + petWidth() / 2, petWidth() * 0.7, ["✨", "💫", "🌟"]);
}

function extendPet(data) {
  const pet = pets.get(data.id);
  if (!pet) { spawnPet(data); return; }
  pet.expiresAt = Math.max(pet.expiresAt, data.expiresAt);
  pet.sleepy = false;
  pet.el.classList.remove("sleep");
  pet.name = data.name;
  pet.el.querySelector(".name").textContent = data.name;
  if (data.species && data.species !== pet.species) morphPet(pet, data.species);
  showBubble(pet, "⏰💜");
  playClass(pet, "jump", 700);
}

function removePet(id, withGoodbye) {
  const pet = pets.get(id);
  if (!pet) return;
  pet.removed = true;
  pets.delete(id);
  if (pet.walkResolve) pet.walkResolve();

  if (!withGoodbye) { pet.el.remove(); return; }
  showBubble(pet, "👋");
  pet.el.classList.remove("walk", "sleep", "fight", "cook", "sad", "lean-left", "lean-right");
  pet.el.classList.add("despawn");
  sparkle(pet.x + petWidth() / 2, petWidth() * 0.7, ["✨", "💫"]);
  setTimeout(() => pet.el.remove(), 950);
}

/* ------------------------------------------------------------------ behaviors */

function showBubble(pet, text) {
  pet.bubble.textContent = text;
  pet.bubble.classList.remove("show");
  void pet.bubble.offsetWidth; // restart the pop animation
  pet.bubble.classList.add("show");
}

function playClass(pet, name, ms) {
  pet.el.classList.add(name);
  setTimeout(() => pet.el.classList.remove(name), ms);
}

function walkTo(pet, x, speed) {
  pet.speed = speed || 60;
  pet.targetX = clampX(x);
  pet.el.classList.remove("sleep");
  pet.el.classList.add("walk");
  return new Promise((resolve) => { pet.walkResolve = resolve; });
}

function setFacing(pet, left) {
  pet.facingLeft = left;
  pet.el.classList.toggle("face-left", left);
}

function decide(pet) {
  const roll = Math.random();
  if (roll < 0.4) {
    walkTo(pet, rand(EDGE, innerWidth - petWidth() - EDGE)).then(() => {
      if (!pet.removed) pet.nextDecideAt = Date.now() + rand(600, 1800);
    });
    return;
  }
  if (roll < 0.55) { pet.nextDecideAt = Date.now() + rand(1500, 3500); return; }
  // Only pets whose sheet carries the look rows draw this card; for the rest the same roll waves.
  if (roll < 0.63 && canLook(pet)) { lookAround(pet); return; }
  if (roll < 0.66) { playClass(pet, "wave", 1600); if (Math.random() < 0.5) showBubble(pet, "👋"); pet.nextDecideAt = Date.now() + 2200; return; }
  if (roll < 0.75) { playClass(pet, "jump", 700); pet.nextDecideAt = Date.now() + 1400; return; }
  if (roll < 0.86) { playClass(pet, "dance", 1400); showBubble(pet, "🎵"); pet.nextDecideAt = Date.now() + 2200; return; }
  if (roll < 0.93) { showBubble(pet, flavorEmoji(pet)); pet.nextDecideAt = Date.now() + 2000; return; }
  // A short nap.
  pet.el.classList.add("sleep");
  showBubble(pet, "💤");
  const wake = Date.now() + rand(3000, 5000);
  pet.nextDecideAt = wake;
  setTimeout(() => { if (!pet.removed && !pet.busy) pet.el.classList.remove("sleep"); }, wake - Date.now());
}

/* A glance or two: usually at a neighbour when one is around, otherwise at nothing in
   particular. Purely a matter of which look frame is shown, so anything real – a walk, a duet –
   simply plays over it. */
async function lookAround(pet) {
  pet.nextDecideAt = Date.now() + 60000; // released below
  const glances = 1 + Math.floor(Math.random() * 2);
  for (let i = 0; i < glances; i++) {
    const other = nearestOther(pet);
    pet.lookAngle = other && Math.random() < 0.6 ? angleTo(pet, other) : rand(0, 360);
    await sleep(rand(900, 1700));
    if (pet.removed || pet.busy) { pet.lookAngle = null; return; }
  }
  pet.lookAngle = null;
  pet.nextDecideAt = Date.now() + rand(800, 2000);
}

function nearestOther(pet) {
  let best = null;
  for (const other of pets.values()) {
    if (other === pet || other.removed) continue;
    if (!best || Math.abs(other.x - pet.x) < Math.abs(best.x - pet.x)) best = other;
  }
  return best;
}

/* Look angles follow the sheet: 0° is straight up, clockwise. A neighbour stands on the same
   ground, so the gaze is sideways, tipping downwards when they are close. */
function angleTo(pet, other) {
  const dx = other.x - pet.x;
  const tilt = Math.abs(dx) < petWidth() * 1.4 ? 22.5 : 0;
  return dx >= 0 ? 90 + tilt : 270 - tilt;
}

/* ------------------------------------------------------------------ duets */

async function runDuet(a, b) {
  duetActive = true;
  a.busy = b.busy = true;
  try {
    const w = petWidth();
    const gap = w * 0.95;
    const mid = Math.min(Math.max((a.x + b.x) / 2 + w / 2, EDGE + gap), innerWidth - EDGE - gap);
    const left = a.x <= b.x ? a : b;
    const right = left === a ? b : a;

    await Promise.all([
      walkTo(left, mid - gap / 2 - w / 2, 85),
      walkTo(right, mid + gap / 2 - w / 2, 85),
    ]);
    if (a.removed || b.removed) return;

    setFacing(left, false);
    setFacing(right, true);
    // Sprite pets with look rows meet each other's eyes; it carries through a cuddle, where
    // nothing else claims their frames.
    if (canLook(left)) left.lookAngle = angleTo(left, right);
    if (canLook(right)) right.lookAngle = angleTo(right, left);
    await sleep(350);

    const act = pick(["fight", "cuddle", "cook"]);
    if (act === "fight") await actFight(left, right, mid);
    else if (act === "cuddle") await actCuddle(left, right, mid);
    else await actCook(left, right, mid);
  } finally {
    for (const pet of [a, b]) {
      pet.el.classList.remove("fight", "cook", "lean-left", "lean-right");
      pet.lookAngle = null;
      pet.busy = false;
      pet.nextDecideAt = Date.now() + rand(400, 1200);
    }
    duetActive = false;
    nextDuetAt = Date.now() + rand(15000, 32000);
  }
}

async function actFight(left, right, mid) {
  left.el.classList.add("fight");
  right.el.classList.add("fight");
  const y = petWidth() * 0.55;
  for (let i = 0; i < 8; i++) {
    sparkle(mid, y + rand(-8, 14), ["💥", "⚡", "👊", "💢"]);
    await sleep(260);
    if (left.removed || right.removed) return;
  }
  left.el.classList.remove("fight");
  right.el.classList.remove("fight");
  const winner = Math.random() < 0.5 ? left : right;
  const loser = winner === left ? right : left;
  showBubble(winner, "😤🏆");
  showBubble(loser, "🤕");
  playClass(loser, "sad", 1900); // the "failed" row on a sprite pet, a slump on an SVG one
  await Promise.all([
    walkTo(left, left.x - 70, 120),
    walkTo(right, right.x + 70, 120),
  ]);
}

async function actCuddle(left, right, mid) {
  left.el.classList.add("lean-right");
  right.el.classList.add("lean-left");
  const y = petWidth() * 0.8;
  for (let i = 0; i < 9; i++) {
    sparkle(mid + rand(-14, 14), y + rand(-6, 10), ["❤️", "💗", "💞"]);
    await sleep(340);
    if (left.removed || right.removed) return;
  }
  showBubble(left, "🥰");
  showBubble(right, "🥰");
}

async function actCook(left, right, mid) {
  const pot = document.createElement("div");
  pot.className = "prop";
  pot.textContent = "🍲";
  stage.appendChild(pot);
  pot.style.left = `${mid - 15 * (settings.scale || 1)}px`;
  try {
    left.el.classList.add("cook");
    right.el.classList.add("cook");
    const y = petWidth() * 0.5;
    for (let i = 0; i < 9; i++) {
      sparkle(mid + rand(-10, 10), y + rand(0, 12), ["♨️", "✨", "🧂"]);
      await sleep(340);
      if (left.removed || right.removed) return;
    }
    showBubble(left, "😋");
    showBubble(right, "😋");
    await sleep(600);
  } finally { pot.remove(); }
}

function sparkle(x, y, emojis) {
  const span = document.createElement("span");
  span.className = "float";
  span.textContent = pick(emojis);
  span.style.left = `${x + rand(-10, 10)}px`;
  span.style.bottom = `${y}px`;
  span.addEventListener("animationend", () => span.remove());
  stage.appendChild(span);
}

/* ------------------------------------------------------------------ sprite frames

   Hatch-pet spritesheets carry 8 columns of animation frames per row. Version 1 has nine rows –
   idle, running-right, running-left, waving, jumping, failed, waiting, running, review – and
   version 2 two more, holding sixteen look directions: one still frame per 22.5°, 0° being
   straight up and the angle growing clockwise. A short animation leaves the rest of its row
   empty, so the true frame count per row is measured from the pixels, once per species. */

const spriteMeta = new Map(); // species id -> { rows, frames per row, lookUsed per direction, hasLook }

function loadSpriteMeta(species, url, fallbackRows) {
  if (spriteMeta.has(species)) return;
  const started = generation;
  const img = new Image();
  img.onload = () => {
    if (started !== generation || spriteMeta.has(species)) return;
    try { spriteMeta.set(species, measureSprite(img, fallbackRows)); } catch { /* the fallback rows carry it */ }
  };
  img.src = url;
}

function measureSprite(img, fallbackRows) {
  // Cells are 192×208, so the row count falls out of the sheet's own proportions; the version in
  // pet.json only settles a sheet whose measurements say something else entirely.
  const measured = Math.round((img.height * 8 * 192) / (img.width * 208));
  const rows = measured === 9 || measured === 11 ? measured : fallbackRows;

  const canvas = document.createElement("canvas");
  canvas.width = img.width;
  canvas.height = img.height;
  const ctx = canvas.getContext("2d", { willReadFrequently: true });
  ctx.drawImage(img, 0, 0);
  const cellW = img.width / 8;
  const cellH = img.height / rows;

  const used = (row, col) => {
    const data = ctx.getImageData(Math.round(col * cellW), Math.round(row * cellH), Math.floor(cellW), Math.floor(cellH)).data;
    for (let i = 3; i < data.length; i += 4) if (data[i] > 0) return true;
    return false;
  };

  const frames = [];
  for (let row = 0; row < Math.min(rows, 9); row++) {
    let count = 0;
    for (let col = 0; col < 8; col++) if (used(row, col)) count = col + 1;
    frames.push(count);
  }

  const lookUsed = [];
  if (rows >= 11) for (let dir = 0; dir < 16; dir++) lookUsed.push(used(9 + (dir >> 3), dir & 7));
  return { rows, frames, lookUsed, hasLook: lookUsed.some(Boolean) };
}

function canLook(pet) {
  const meta = spriteMeta.get(pet.species);
  return !!(meta && meta.hasLook);
}

function spriteRowFor(pet) {
  const cls = pet.el.classList;
  if (cls.contains("sleep")) return 6;
  if (cls.contains("sad")) return 5;
  if (cls.contains("walk")) return pet.facingLeft ? 2 : 1;
  if (cls.contains("wave")) return 3;
  if (cls.contains("jump")) return 4;
  if (cls.contains("dance")) return 7;
  if (cls.contains("fight")) return 7;
  if (cls.contains("cook")) return 8;
  return 0;
}

function updateSprite(pet, nowMs) {
  const meta = spriteMeta.get(pet.species);
  // The measured sheet outranks the manifest: a version 2 sheet whose pet.json forgot to say so
  // would otherwise be squeezed into nine rows.
  if (meta && meta.rows !== pet.spriteRows) {
    pet.spriteRows = meta.rows;
    pet.spriteEl.style.backgroundSize = `800% ${meta.rows * 100}%`;
  }
  const rows = pet.spriteRows;
  let row = spriteRowFor(pet);
  if (meta && !meta.frames[row]) row = 0; // an animation this sheet does not carry falls back to idle
  let frame = null;

  // The look rows are indexed by direction rather than played over time.
  if (row === 0 && pet.lookAngle !== null && meta && meta.hasLook) {
    const dir = Math.round((((pet.lookAngle % 360) + 360) % 360) / 22.5) % 16;
    if (meta.lookUsed[dir]) { row = 9 + (dir >> 3); frame = dir & 7; }
  }
  if (frame === null) frame = Math.floor((nowMs / 1000) * pet.spriteFps) % ((meta && meta.frames[row]) || 8);

  if (row === pet.spriteRow && frame === pet.spriteFrame) return;
  pet.spriteRow = row;
  pet.spriteFrame = frame;
  pet.spriteEl.style.backgroundPosition = `${(frame * 100) / 7}% ${(row * 100) / (rows - 1)}%`;
}

/* ------------------------------------------------------------------ main loop */

let lastTick = performance.now();

function tick(now) {
  requestAnimationFrame(tick);
  const dt = Math.min(0.1, (now - lastTick) / 1000);
  lastTick = now;
  const time = Date.now();

  for (const pet of pets.values()) {
    // Lifetime: yawn shortly before the end, then beam home.
    if (!pet.sleepy && time > pet.expiresAt - 8000) {
      pet.sleepy = true;
      if (!pet.busy) { pet.el.classList.add("sleep"); showBubble(pet, "💤"); }
    }
    if (time > pet.expiresAt) { removePet(pet.id, true); continue; }

    if (pet.spriteEl) updateSprite(pet, time);

    if (pet.targetX !== null) {
      const dir = Math.sign(pet.targetX - pet.x);
      setFacing(pet, dir < 0);
      pet.x += dir * pet.speed * (settings.scale || 1) * dt;
      pet.el.style.left = `${pet.x}px`;
      if (Math.abs(pet.targetX - pet.x) < 3) {
        pet.x = pet.targetX;
        pet.targetX = null;
        pet.el.classList.remove("walk");
        const resolve = pet.walkResolve;
        pet.walkResolve = null;
        if (resolve) resolve();
      }
    } else if (!pet.busy && !pet.sleepy && time >= pet.nextDecideAt) {
      pet.nextDecideAt = time + 60000; // decide() always sets the real value
      decide(pet);
    }
  }
}

setInterval(() => {
  if (duetActive || Date.now() < nextDuetAt || !settings.enabled) return;
  const free = [...pets.values()].filter((pet) => !pet.busy && !pet.removed && !pet.sleepy && pet.targetX === null);
  if (free.length < 2) return;
  const a = pick(free);
  let b = pick(free);
  while (b === a) b = pick(free);
  runDuet(a, b);
}, 3000);

addEventListener("resize", () => {
  for (const pet of pets.values()) {
    pet.x = clampX(pet.x);
    if (pet.targetX !== null) pet.targetX = clampX(pet.targetX);
    pet.el.style.left = `${pet.x}px`;
  }
});

connect();
requestAnimationFrame(tick);
