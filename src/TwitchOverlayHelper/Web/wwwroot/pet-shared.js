"use strict";

/* What the overlay and the pet inspector have to agree about: how a drawing is made safe to show,
   how a spritesheet is measured, and which row of it a behavior is drawn from. Shared so the
   inspector shows a pet exactly as the overlay will.

   A plain script rather than a module: both pages load it before their own, and everything here is
   a global they then share. */

/* Shown when a pet's drawing cannot be fetched at all – an empty patch of ground would look like
   the overlay was broken. */
const FALLBACK_BODY = `<svg viewBox="0 0 100 100" xmlns="http://www.w3.org/2000/svg" aria-hidden="true">
  <circle cx="50" cy="60" r="30" fill="var(--accent)" opacity="0.9" />
  <circle class="eye" cx="41" cy="55" r="4" fill="#141B26" />
  <circle class="eye" cx="59" cy="55" r="4" fill="#141B26" />
  <path d="M43 70 Q50 75 57 70" stroke="#141B26" stroke-width="2.5" fill="none" stroke-linecap="round" />
</svg>`;

/* Hatch-pet sheets are eight cells wide, and a cell is 192×208. Everything below is that grid. */
const SPRITE_COLS = 8;
const SPRITE_CELL_W = 192;
const SPRITE_CELL_H = 208;

/* A drawing is a file in the streamer's own pets folder, but a pet downloaded from someone else is
   a stranger's markup – and the pages that show it carry the dock's access key. Every body is
   therefore parsed into an inert document and stripped of scripts, event handlers and outbound
   references before it is allowed near the DOM. Ordinary drawing markup, animations included,
   passes through untouched. */
const UNSAFE_TAGS = new Set(["script", "foreignobject", "iframe", "object", "embed"]);
const URL_ATTRS = new Set(["href", "xlink:href", "src"]);

/* CSS can fetch too (a background, a mask, a filter, a font, an @import), and a request leaving
   the page hands the streamer's address to whoever wrote the pet. Declarations are scrubbed rather
   than dropped, so a drawing animated by its own <style> block keeps working.

   Comments and backslashes are removed first, because both can hide a reference from a pattern
   match: a comment inside the word, or a hex escape for a letter. The only loss is a stray escape
   in a `content` string. */
const CSS_COMMENT = /\/\*[\s\S]*?\*\//g;
const CSS_BACKSLASH = /\\/g;
const CSS_AT_RULE = /@(import|namespace)[^;{}]*;?/gi;
/* A reference to somewhere inside the drawing (a gradient, a mask, a filter) fetches nothing and
   is kept. The optional quote is skipped by the lookahead, so `url("#grad")` counts as local. */
const CSS_URL = /url\((?!\s*['"]?\s*#)[^)]*\)/gi;

function sanitizeCss(text) {
  return text
    .replace(CSS_COMMENT, "")
    .replace(CSS_BACKSLASH, "")
    .replace(CSS_AT_RULE, "")
    // Left pointing at nothing rather than taken out: a paint or a mask whose declaration vanished
    // falls back to something visible, and a drawing missing half its fills reads as a broken pet
    // rather than a safe one.
    .replace(CSS_URL, "url(#)");
}

/* One attribute's CSS made safe, or null when nothing in it needed changing. Applies to the style
   attribute and to any value with a url( or a backslash: fill, filter, mask and clip-path reach
   the network the same way. */
function safeCssAttr(name, value) {
  if (name !== "style" && !value.includes("url(") && !value.includes("\\")) return null;
  const safe = sanitizeCss(value);
  return safe === value ? null : safe;
}

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
      continue;
    }
    const safe = safeCssAttr(name, attr.value);
    if (safe !== null) el.setAttribute(attr.name, safe);
  }
  for (const child of [...el.children]) {
    const tag = child.tagName.toLowerCase();
    if (UNSAFE_TAGS.has(tag)) { child.remove(); continue; }
    // A stylesheet is text rather than markup, so the scrub has to reach inside it.
    if (tag === "style") child.textContent = sanitizeCss(child.textContent);
    scrub(child);
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

/* Which halo a species wears. The tiers are written by hand into pet.json, so anything
   unrecognised, and every common pet, is left plain. */
const RARITY_CLASSES = {
  "ovanlig": "rarity-uncommon",
  "sällsynt": "rarity-rare",
  "legendarisk": "rarity-legendary",
};

/* What a sheet actually holds: how many rows, how many drawn frames each row carries, and which of
   the sixteen look directions were filled in. A short animation leaves the rest of its row empty,
   so the frame count is read out of the pixels rather than assumed. */
function measureSprite(img, fallbackRows) {
  // Cells are 192×208, so the row count falls out of the sheet's own proportions; the version in
  // pet.json only settles a sheet whose measurements say something else entirely.
  const measured = Math.round((img.height * SPRITE_COLS * SPRITE_CELL_W) / (img.width * SPRITE_CELL_H));
  const rows = measured === 9 || measured === 11 ? measured : fallbackRows;

  const canvas = document.createElement("canvas");
  canvas.width = img.width;
  canvas.height = img.height;
  const ctx = canvas.getContext("2d", { willReadFrequently: true });
  ctx.drawImage(img, 0, 0);
  const cellW = img.width / SPRITE_COLS;
  const cellH = img.height / rows;

  const used = (row, col) => {
    const data = ctx.getImageData(Math.round(col * cellW), Math.round(row * cellH), Math.floor(cellW), Math.floor(cellH)).data;
    for (let i = 3; i < data.length; i += 4) if (data[i] > 0) return true;
    return false;
  };

  const frames = [];
  for (let row = 0; row < Math.min(rows, 9); row++) {
    let count = 0;
    for (let col = 0; col < SPRITE_COLS; col++) if (used(row, col)) count = col + 1;
    frames.push(count);
  }

  const lookUsed = [];
  if (rows >= 11) for (let dir = 0; dir < 16; dir++) lookUsed.push(used(9 + (dir >> 3), dir & SPRITE_COLS - 1));
  return { rows, frames, lookUsed, hasLook: lookUsed.some(Boolean) };
}

/* What a behavior does to a spritesheet: the row it draws from, the pace that row is played at, and
   whether it loops or plays through once and holds its last frame. First match wins, so the order
   is the precedence of the overlay's classes.

   A pace left out is the sheet's own fps. The three that name one (a wave, a slump, a wait) run
   at roughly half pace. These are the defaults for every pet. */
const SPRITE_BEHAVIORS = [
  { cls: "sleep", row: 6, fps: 4, loop: true },
  { cls: "sad", row: 5, fps: 4 },
  { cls: "walk", row: 1, loop: true }, // row 2 when it faces the other way; see spriteRowFor
  { cls: "wave", row: 3, fps: 5 },
  { cls: "jump", row: 4 },
  // Dancing and fighting share the running row, and a run cycle is drawn to be looped: playing it
  // through once would read as a twitch rather than a dance.
  { cls: "dance", row: 7, loop: true },
  { cls: "fight", row: 7, loop: true },
  { cls: "cook", row: 8, loop: true },
];

/* Standing still has no class, so idle is a behavior of its own; rows that fall back to idle use
   it too. */
const IDLE_BEHAVIOR = { cls: "", row: 0, loop: true };

const SPRITE_BEHAVIOR_BY_CLASS = new Map(SPRITE_BEHAVIORS.map((entry) => [entry.cls, entry]));

function spriteBehaviorFor(classList) {
  for (const entry of SPRITE_BEHAVIORS) if (classList.contains(entry.cls)) return entry;
  return IDLE_BEHAVIOR;
}

/* The row a behavior is drawn from. Takes the classes rather than a pet, so the inspector can
   call it too. */
function spriteRowFor(classList, facingLeft) {
  const behavior = spriteBehaviorFor(classList);
  if (behavior.cls === "walk") return facingLeft ? 2 : 1;
  return behavior.row;
}

/* Which frame of a row is on screen.

   Counted from the moment the behavior began, so a one-shot starts on frame 0 and pets of the same
   species do not animate in step. A loop wraps round; a one-shot holds its last frame. */
function spriteFrameAt(behavior, frames, sheetFps, nowMs, startedMs, speed) {
  const count = Math.max(1, frames || 1);
  const step = Math.floor((Math.max(0, nowMs - (startedMs || 0)) / 1000) * spriteFps(behavior, sheetFps, speed));
  return behavior.loop ? step % count : Math.min(count - 1, step);
}

/* The pace a row is played at: the behavior's own if it has one, the sheet's otherwise, scaled by
   the streamer's animation speed. Never below 0.5 fps, so no setting can freeze a pet mid-step. */
function spriteFps(behavior, sheetFps, speed) {
  return Math.max(0.5, (behavior.fps || sheetFps || 10) * (speed > 0 ? speed : 1));
}

/* How long one pass of a row takes, in milliseconds. A one-shot behavior keeps its class for
   exactly this long. */
function spriteRunMs(behavior, frames, sheetFps, speed) {
  return Math.round((Math.max(1, frames || 1) / spriteFps(behavior, sheetFps, speed)) * 1000);
}
