"use strict";

/* Channel point pets: small creatures that wander the bottom edge of a transparent OBS browser
   source. The app decides who gets a pet and which species it is; this page only animates what
   the server says is alive. */

const KEY = new URLSearchParams(location.search).get("key") || "";
/* The species pet-preview.html shows alone. Empty on the real overlay. */
const PREVIEW = new URLSearchParams(location.search).get("preview") || "";
const stage = document.getElementById("stage");

const pets = new Map(); // id -> pet
const catalog = new Map(); // species id -> definition from the server
let settings = {
  enabled: true, scale: 1, lifetimeMinutes: 5, maxPets: 6,
  showNames: true, rarityEffects: true, rarityFadeSeconds: 10, animationSpeed: 1,
};
let duetActive = false;
let nextDuetAt = Date.now() + 12000;

const EDGE = 10;
const BASE_SIZE = 90;

/* Transport */

let socket = null;
let reconnectDelay = 1000;

/* view=pets tells the app this is the pets overlay rather than a dock: the greeting is cut to the
   pets, and a refundable redemption knows whether anyone will see the pet it bought. */
function connect() {
  socket = new WebSocket(`ws://${location.host}/ws?view=pets&key=${encodeURIComponent(KEY)}`);
  socket.onopen = () => { reconnectDelay = 1000; };
  socket.onmessage = (event) => handle(JSON.parse(event.data));
  socket.onclose = () => {
    setTimeout(connect, reconnectDelay);
    reconnectDelay = Math.min(15000, reconnectDelay * 1.7);
  };
  socket.onerror = () => socket.close();
}

/* Tells the app a pet is in the DOM, so it can refund one nobody saw: a browser source that failed
   to come up accepts the frame and renders nothing. Best effort; a closed socket says the same. */
function reportShown(id) {
  if (!socket || socket.readyState !== WebSocket.OPEN) return;
  try { socket.send(JSON.stringify({ type: "petShown", id })); } catch { /* the reconnect answers for it */ }
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
  if (frame.type === "petRemove") { removePet(frame.payload.id, true); return; }
  // The app left the channel these pets were bought in; none of them belong on the new one.
  if (frame.type === "petsClear") { for (const id of [...pets.keys()]) removePet(id, true); return; }
  if (frame.type === "petSpawn") {
    const { pet, removedId, extended } = frame.payload;
    if (removedId) removePet(removedId, true);
    if (extended && pets.has(pet.id)) extendPet(pet);
    else spawnPet(pet);
    reportShown(pet.id);
    return;
  }
  if (frame.type === "spin") { runSpin(frame.payload); return; }
  // Chat frames from the shared socket are someone else's business.
}

/* The streamer's animation speed, as a multiplier. Read when needed rather than cached per pet,
   so a pet already walking follows the slider. */
function animationSpeed() {
  const speed = settings.animationSpeed;
  return speed > 0 ? speed : 1;
}

function applySettings(next) {
  if (!next) return;
  settings = next;
  stage.style.setProperty("--pet-scale", settings.scale);
  // SVG pets are animated by pets.css, which reads the speed from this property. Spritesheet pets
  // are paced by spriteFrameAt.
  stage.style.setProperty("--pet-anim-speed", animationSpeed());
  stage.classList.toggle("disabled", !settings.enabled);
  stage.classList.toggle("hide-names", settings.showNames === false);
  stage.classList.toggle("rarity-fx", settings.rarityEffects !== false);
  // Pets already on the lawn get their full rarity moment again under the changed settings.
  for (const pet of pets.values()) armRarityFade(pet);
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

/* The server list is the truth: drop pets it no longer knows, add the ones it does. Every pet on
   screen is reported, including ones that came back after an OBS reload. */
function syncPets(list) {
  const alive = new Set(list.map((p) => p.id));
  for (const id of [...pets.keys()]) if (!alive.has(id)) removePet(id, false);
  for (const p of list) {
    if (pets.has(p.id)) {
      const existing = pets.get(p.id);
      existing.expiresAt = p.expiresAt;
      if (p.species && p.species !== existing.species) morphPet(existing, p.species);
    } else {
      spawnPet(p);
    }
    reportShown(p.id);
  }
}

/* The drawings

   Every pet is a folder in the user's pets folder, so bodies are fetched from the app. A body is a
   plain SVG in a 100×100 box that shares a part contract with the CSS: .eye blinks, .glow pulses,
   .arm-left/.arm-right swing and wave, .leg-a/.leg-b walk. A pet without legs bobs along instead. */

const bodies = new Map(); // species id -> svg markup
const pending = new Map(); // species id -> in-flight fetch
let generation = 0; // bumped on reload, so a fetch started before an edit cannot land after it

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

/* Only the two rarest sweep a sheen across themselves, and only sprite pets can: the light is
   masked with the sheet's current cell, and an inline SVG has no image to mask with. */
function applyRarity(pet) {
  const tier = RARITY_CLASSES[(catalog.get(pet.species)?.rarity || "").toLowerCase()] || "";
  pet.el.classList.remove("rarity-uncommon", "rarity-rare", "rarity-legendary");
  if (tier) pet.el.classList.add(tier);
  pet.shimmer = tier === "rarity-rare" || tier === "rarity-legendary";
  armRarityFade(pet);
}

/* The aura marks a pet's arrival and fades after a short while; zero seconds keeps it lit.

   Timed from now rather than from the redemption: a browser source reload or a pets folder edit
   redraws the pet, which counts as arriving again. */
function armRarityFade(pet) {
  clearTimeout(pet.fadeTimer);
  pet.fadeTimer = 0;
  pet.el.classList.remove("rarity-faded");
  const seconds = settings.rarityFadeSeconds || 0;
  if (seconds <= 0) return;
  pet.fadeTimer = setTimeout(() => {
    if (!pet.removed) pet.el.classList.add("rarity-faded");
  }, seconds * 1000);
}

function accentFor(pet) {
  if (pet.color && /^#[0-9a-f]{6}$/i.test(pet.color)) return pet.color;
  let hash = 0;
  for (const ch of pet.id) hash = (hash * 31 + ch.codePointAt(0)) >>> 0;
  return `hsl(${hash % 360}, 78%, 62%)`;
}

/* Lifecycle */

function petWidth() { return BASE_SIZE * (settings.scale || 1); }
function clampX(x) { return Math.min(Math.max(x, EDGE), Math.max(EDGE, innerWidth - petWidth() - EDGE)); }
function rand(min, max) { return min + Math.random() * (max - min); }
function pick(list) { return list[Math.floor(Math.random() * list.length)]; }
function sleep(ms) { return new Promise((resolve) => setTimeout(resolve, ms)); }

function applyBody(pet) {
  const wrap = pet.el.querySelector(".body-wrap");
  const def = catalog.get(pet.species);
  applyRarity(pet);
  pet.spriteEl = null;
  pet.spriteRow = -1;
  pet.spriteFrame = -1;
  pet.spriteBehavior = null;
  pet.spriteStart = 0;
  pet.lookAngle = null;

  if (def && def.kind === "sprite" && def.spriteUrl) {
    // The generation in the URL makes "Ladda om pets" bypass the browser's cached sheet.
    const url = `${def.spriteUrl}?g=${generation}`;
    wrap.innerHTML = `<div class="sprite" style="background-image:url('${url}')"></div>`;
    pet.spriteEl = wrap.querySelector(".sprite");
    pet.spriteFps = def.fps || 10;
    pet.spriteRows = def.spriteVersion === 2 ? 11 : 9;
    pet.spriteEl.style.backgroundSize = `800% ${pet.spriteRows * 100}%`;
    // The sheen's mask is the sheet itself. Set for every sprite pet; only the rarity class uses it.
    pet.spriteEl.style.setProperty("--sprite-mask", `url('${url}')`);
    pet.spriteEl.style.setProperty("--sprite-size", `800% ${pet.spriteRows * 100}%`);
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
  el.innerHTML =
    `<div class="bubble"></div><div class="aura"></div><div class="shadow"></div>` +
    `<div class="body-wrap"></div><span class="spark"></span><span class="spark"></span><div class="name"></div>`;
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
    spriteBehavior: null, // the entry from SPRITE_BEHAVIORS this pet is playing
    spriteStart: 0, // when it started, for the one-shots that are timed from their own beginning
    lookAngle: null,
    shimmer: false,
    fadeTimer: 0,
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
  clearTimeout(pet.fadeTimer);
  if (pet.walkResolve) pet.walkResolve();

  if (!withGoodbye) { pet.el.remove(); return; }
  showBubble(pet, "👋");
  pet.el.classList.remove("walk", "sleep", "fight", "cook", "sad", "lean-left", "lean-right");
  pet.el.classList.add("despawn");
  sparkle(pet.x + petWidth() / 2, petWidth() * 0.7, ["✨", "💫"]);
  setTimeout(() => pet.el.remove(), 950);
}

/* Behaviors */

function showBubble(pet, text) {
  pet.bubble.textContent = text;
  pet.bubble.classList.remove("show");
  void pet.bubble.offsetWidth; // restart the pop animation
  pet.bubble.classList.add("show");
}

/* Adds a behavior class and removes it when the animation is done: after the written duration for
   an SVG pet, after one measured pass of the row for a sprite pet. */
function playClass(pet, name, ms) {
  pet.el.classList.add(name);
  setTimeout(() => pet.el.classList.remove(name), playMs(pet, name, ms));
}

/* How long one pass of the behavior takes for this pet. A pet with no sheet, a looping behavior or
   a row the sheet left empty falls back to the written duration, scaled by the animation speed
   because pets.css divides its durations by the same setting. */
function playMs(pet, name, fallbackMs) {
  const behavior = SPRITE_BEHAVIOR_BY_CLASS.get(name);
  const meta = pet.spriteEl ? spriteMeta.get(pet.species) : null;
  if (!behavior || behavior.loop || !meta || !meta.frames[behavior.row]) {
    return Math.round(fallbackMs / animationSpeed());
  }
  return spriteRunMs(behavior, meta.frames[behavior.row], pet.spriteFps, animationSpeed());
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

/* A glance or two, usually at a neighbour when one is around. Only changes which look frame is
   shown, so a walk or a duet simply plays over it. */
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

/* Duets */

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

/* Sprite frames

   Hatch-pet spritesheets carry 8 columns of animation frames per row. Version 1 has nine rows –
   idle, running-right, running-left, waving, jumping, failed, waiting, running, review – and
   version 2 two more, holding sixteen look directions: one still frame per 22.5°, 0° being
   straight up and the angle growing clockwise. A short animation leaves the rest of its row
   empty, so the true frame count per row is measured from the pixels, once per species.

   Pace and one-shot playback belong to the behavior and live in SPRITE_BEHAVIORS, which the
   inspector shares. */

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

function canLook(pet) {
  const meta = spriteMeta.get(pet.species);
  return !!(meta && meta.hasLook);
}

function updateSprite(pet, nowMs) {
  const meta = spriteMeta.get(pet.species);
  // The measured sheet outranks the manifest: a version 2 sheet whose pet.json forgot to say so
  // would otherwise be squeezed into nine rows.
  if (meta && meta.rows !== pet.spriteRows) {
    pet.spriteRows = meta.rows;
    pet.spriteEl.style.backgroundSize = `800% ${meta.rows * 100}%`;
    pet.spriteEl.style.setProperty("--sprite-size", `800% ${meta.rows * 100}%`);
  }
  const rows = pet.spriteRows;
  let behavior = spriteBehaviorFor(pet.el.classList);
  // A behavior starts when its class appears. Watching the classes means no caller has to announce
  // it, and tells a dance from a fight, which share a row but not an ending.
  if (behavior !== pet.spriteBehavior) {
    pet.spriteBehavior = behavior;
    pet.spriteStart = nowMs;
  }
  let row = spriteRowFor(pet.el.classList, pet.facingLeft);
  // An animation this sheet does not carry falls back to idle, pace included.
  if (meta && !meta.frames[row]) { row = 0; behavior = IDLE_BEHAVIOR; }
  let frame = null;

  // The look rows are indexed by direction rather than played over time.
  if (row === 0 && pet.lookAngle !== null && meta && meta.hasLook) {
    const dir = Math.round((((pet.lookAngle % 360) + 360) % 360) / 22.5) % 16;
    if (meta.lookUsed[dir]) { row = 9 + (dir >> 3); frame = dir & 7; }
  }
  if (frame === null) {
    frame = spriteFrameAt(behavior, (meta && meta.frames[row]) || 8, pet.spriteFps, nowMs, pet.spriteStart, animationSpeed());
  }

  if (row === pet.spriteRow && frame === pet.spriteFrame) return;
  pet.spriteRow = row;
  pet.spriteFrame = frame;
  const position = `${(frame * 100) / 7}% ${(row * 100) / (rows - 1)}%`;
  pet.spriteEl.style.backgroundPosition = position;
  // The sheen is masked with the cell on screen, so the mask follows the animation frame by frame.
  if (pet.shimmer) pet.spriteEl.style.setProperty("--sprite-pos", position);
}

/* Lyckosnurren

   A reel of portraits that slides in from one edge, loops through everything winnable, slows down
   and lands on the winner. Theatre only: the app drew and saved the winner before sending the
   frame, so nothing here can change or lose the prize.

   The portrait is the first cell of a hatch-pet sheet (row 0, column 0), the standing still frame
   every sheet carries. SVG pets draw their whole body instead. */

let spinBusy = false;

/* Spins waiting for the stage. The app sends one at a time, but several overlays share one
   broadcast and a slower one can still be playing when the next spin is released. Queued rather
   than dropped, so every spin is played and acknowledged. */
const spinQueue = [];

/* Passes of the full list before the winner comes up; enough that the pets blur mid-spin. */
const SPIN_LOOPS = 6;

function reportSpinDone(id) {
  if (!socket || socket.readyState !== WebSocket.OPEN) return;
  try { socket.send(JSON.stringify({ type: "spinDone", id })); } catch { /* the app's own timeout answers for it */ }
}

/* One portrait cell: a sprite pet's first frame held still, or an SVG pet's drawing. */
function portrait(species) {
  const cell = document.createElement("div");
  const def = catalog.get(species);
  cell.className = `spin-cell ${RARITY_CLASSES[(def?.rarity || "").toLowerCase()] || ""}`.trim();

  if (def && def.kind === "sprite" && def.spriteUrl) {
    const rows = def.spriteVersion === 2 ? 11 : 9;
    const art = document.createElement("div");
    art.className = "spin-art sprite-portrait";
    art.style.backgroundImage = `url('${def.spriteUrl}?g=${generation}')`;
    art.style.backgroundSize = `800% ${rows * 100}%`;
    art.style.backgroundPosition = "0% 0%";
    cell.appendChild(art);
    return cell;
  }

  const art = document.createElement("div");
  art.className = "spin-art";
  cell.appendChild(art);
  loadBody(species).then((svg) => { art.innerHTML = svg; });
  return cell;
}

/* Fetches the sheets before the reel starts, so no blank cell goes past mid-spin. Gives up after
   a moment so a slow or missing file only delays the show. */
function preload(speciesList) {
  const jobs = speciesList.map((species) => {
    const def = catalog.get(species);
    if (!(def && def.kind === "sprite" && def.spriteUrl)) return loadBody(species);
    return new Promise((resolve) => {
      const img = new Image();
      img.onload = img.onerror = () => resolve();
      img.src = `${def.spriteUrl}?g=${generation}`;
    });
  });
  return Promise.race([Promise.all(jobs), sleep(2500)]);
}

function runSpin(spin) {
  spinQueue.push(spin);
  drainSpins();
}

function drainSpins() {
  if (spinBusy || spinQueue.length === 0) return;
  spinBusy = true;
  playSpin(spinQueue.shift());
}

async function playSpin(spin) {
  const panel = document.createElement("div");
  panel.className = `spin from-${spin.side || "right"}`;
  panel.innerHTML =
    `<div class="spin-title"></div>` +
    `<div class="spin-window"><div class="spin-reel"></div><div class="spin-glare"></div><div class="spin-marker"></div></div>` +
    `<div class="spin-winner"></div>`;
  panel.querySelector(".spin-title").textContent = spin.title || "Lyckosnurren";
  stage.appendChild(panel);

  try {
    const reel = panel.querySelector(".spin-reel");
    const list = spin.petIds && spin.petIds.length ? spin.petIds : [spin.winnerPetId];

    await preload(list);

    // The strip is the list a few times over with the winner as the final cell, so the reel stops on
    // the last one and cannot disagree with the app.
    const cells = [];
    for (let loop = 0; loop < SPIN_LOOPS; loop++) for (const id of list) cells.push(id);
    cells.push(spin.winnerPetId);
    for (const id of cells) reel.appendChild(portrait(id));

    // A gap between sliding in and spinning, so the reel does not arrive already running.
    await nextFrame();
    panel.classList.add("in");
    await sleep(700);

    const cellHeight = reel.firstElementChild ? reel.firstElementChild.offsetHeight : 0;
    const distance = cellHeight * (cells.length - 1);
    const seconds = Math.max(2, spin.seconds || 8);
    reel.style.transition = `transform ${seconds}s cubic-bezier(.12,.62,.16,1)`;
    reel.style.transform = `translateY(-${distance}px)`;
    panel.classList.add("spinning");
    await sleep(seconds * 1000);

    panel.classList.remove("spinning");
    panel.classList.add("landed", spin.duplicate ? "is-duplicate" : "is-win");
    // The window lights up in the winner's rarity tier.
    const tier = RARITY_CLASSES[(catalog.get(spin.winnerPetId)?.rarity || "").toLowerCase()];
    if (tier) panel.classList.add(tier);
    const winner = panel.querySelector(".spin-winner");
    winner.textContent = spin.winnerName || "";
    // Confetti for a fresh win only; a duplicate's story carries on in chat.
    if (!spin.duplicate) celebrate(panel);
    await sleep(2600);

    panel.classList.remove("in");
    await sleep(700);
  } catch {
    // The prize is already the viewer's. Falling through to the curtain call keeps one bad spin
    // from blocking the ones after it.
  } finally {
    panel.remove();
    spinBusy = false;
    reportSpinDone(spin.id);
    drainSpins();
  }
}

/* Confetti over the panel only, not the whole screen. */
function celebrate(panel) {
  const colors = ["#FFD166", "#EF476F", "#06D6A0", "#7EF0FF", "#C77DFF"];
  for (let i = 0; i < 26; i++) {
    const bit = document.createElement("span");
    bit.className = "spin-confetti";
    bit.style.left = `${rand(4, 96)}%`;
    bit.style.background = pick(colors);
    bit.style.animationDelay = `${rand(0, 500)}ms`;
    bit.style.setProperty("--drift", `${rand(-40, 40)}px`);
    bit.style.setProperty("--spin", `${rand(-540, 540)}deg`);
    bit.addEventListener("animationend", () => bit.remove());
    panel.appendChild(bit);
  }
}

function nextFrame() { return new Promise((resolve) => requestAnimationFrame(() => resolve())); }

/* Preview

   pet-preview.html runs this file with one species named in its address, so the creature is drawn,
   sized and paced exactly as on stream. It never opens the socket; settings and catalog are read
   once over HTTP. */

const PREVIEW_ID = "preview";
let previewReturning = false;

async function startPreview() {
  const status = document.getElementById("previewStatus");
  const get = (path) => fetch(`${path}?key=${encodeURIComponent(KEY)}`)
    .then((response) => (response.ok ? response.json() : Promise.reject(new Error(String(response.status)))));
  try {
    const [petSettings, list] = await Promise.all([get("/api/pets/settings"), get("/api/pets/catalog")]);
    // Shown even with the pets switched off in the app: looking at one is the page's whole purpose.
    applySettings({ ...petSettings, enabled: true });
    applyCatalog(list);
  } catch {
    status.textContent = "Kunde inte hämta pets från appen. Kör chattservern och öppna sidan från appen igen.";
    return;
  }

  const def = catalog.get(PREVIEW);
  if (!def) {
    status.textContent = `Det finns ingen pet som heter "${PREVIEW}" längre.`;
    return;
  }
  document.title = `${def.name} – som på streamen`;
  document.getElementById("previewName").textContent = def.name;
  spawnPreview();
}

function spawnPreview() {
  const def = catalog.get(PREVIEW);
  if (def) spawnPet({ id: PREVIEW_ID, name: def.name, species: def.id, expiresAt: Infinity });
}

/* Makes the pet leave and arrive again, so the entrance can be watched repeatedly. */
function respawnPreview() {
  if (previewReturning) return;
  if (!pets.has(PREVIEW_ID)) { spawnPreview(); return; }
  previewReturning = true;
  removePet(PREVIEW_ID, true);
  setTimeout(() => { previewReturning = false; spawnPreview(); }, 1100);
}

const BUDDY_ID = "preview-buddy";
let buddyArriving = false;

/* A random other species beside the previewed one, so a fight, a cuddle or a meal together can be
   watched. A second click swaps the buddy for a new one. */
function spawnBuddy() {
  if (buddyArriving) return;
  const others = [...catalog.values()].filter((def) => def.id !== PREVIEW);
  const def = pick(others.length > 0 ? others : [...catalog.values()]);
  if (!def) return;

  const arrive = () => {
    buddyArriving = false;
    spawnPet({ id: BUDDY_ID, name: def.name, species: def.id, expiresAt: Infinity });
    // On the lawn a duet waits up to half a minute; here the meeting is the point of the button.
    nextDuetAt = Date.now() + 2500;
  };
  if (!pets.has(BUDDY_ID)) { arrive(); return; }
  buddyArriving = true;
  removePet(BUDDY_ID, true);
  setTimeout(arrive, 1100);
}

/* Main loop */

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
      // Ground speed follows both size and animation speed, so the feet do not skate against the walk
      // cycle.
      //
      // The step is clamped at the target: an overshoot would flip the direction and the pet would tick
      // back and forth across its target without ever finishing the walk.
      const step = pet.speed * (settings.scale || 1) * animationSpeed() * dt;
      pet.x = dir < 0 ? Math.max(pet.targetX, pet.x - step) : Math.min(pet.targetX, pet.x + step);
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

if (PREVIEW) startPreview(); else connect();
requestAnimationFrame(tick);
