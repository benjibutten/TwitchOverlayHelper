"use strict";

/* Draws the inside of a chat line (words, emotes, links, markers) for both pages that show one:
   the dock and the stream overlay, so a message looks the same in both.

   Everything here is a pure function of a message and a settings object; nothing touches the page,
   the socket or shared state.

   The settings object needs five things: calmShouting, showEmotes, showGifs, giantEmotes and
   collapseLinks. Both views' settings carry them under those names. */

/* Links in chat are mostly written without a scheme ("linktr.ee/perralinks"), so a bare host
   counts too, but only when its last part is a recognised suffix: that keeps "t.ex", "3.5" and
   "kl.20" out. */
const URL_PATTERN =
  /(?:https?:\/\/|www\.)[^\s]+|[a-z0-9][a-z0-9-]*(?:\.[a-z0-9][a-z0-9-]*)*\.[a-z]{2,24}(?::\d{2,5})?(?:\/[^\s]*)?/gi;

const LINK_SUFFIXES = new Set((
  "com net org edu gov int mil info biz name pro io gg tv me ly co cc to sh ws am fm " +
  "se no dk fi is ee lv lt de at ch nl be fr es it pt pl cz sk hu ro gr ie uk eu ru ua " +
  "us ca mx br ar cl au nz jp kr cn in id tr za il ae " +
  "app dev art blog chat cloud club design digital email fun games game group host life link " +
  "live media music news one online page pro shop site social space store stream studio team " +
  "tech today top tube video wiki work world xyz zone"
).split(" "));

/* Trailing punctuation belongs to the sentence, not to the address. */
const LINK_TAIL = /[.,;:!?)\]}'"»…]+$/u;

function linksIn(text) {
  const found = [];
  for (const match of text.matchAll(URL_PATTERN)) {
    const before = match.index > 0 ? text[match.index - 1] : "";
    // Skip anything glued to a word, which is mostly the domain half of an e-mail address.
    if (before && /[\w.@/:-]/.test(before)) continue;

    const value = match[0].replace(LINK_TAIL, "");
    if (!value) continue;

    if (!/^(?:https?:\/\/|www\.)/i.test(value)) {
      const host = value.split(/[/:?#]/)[0];
      if (!LINK_SUFFIXES.has(host.slice(host.lastIndexOf(".") + 1).toLowerCase())) continue;
    }
    found.push({ start: match.index, length: value.length });
  }
  return found;
}

function isShouting(text) {
  const letters = text.replace(/[^A-Za-zÅÄÖåäö]/g, "");
  if (letters.length < 8) return false;
  const upper = letters.replace(/[^A-ZÅÄÖ]/g, "").length;
  return upper / letters.length > 0.7;
}

/* Collapsing only changes the label; the address stays clickable. On the stream overlay nothing
   is clickable, but the chip keeps a readable address off the broadcast. */
function appendLink(target, raw, collapse) {
  const anchor = document.createElement("a");
  anchor.className = collapse ? "link-chip" : "link";
  anchor.textContent = collapse ? "🔗 länk" : raw;
  anchor.href = /^https?:\/\//i.test(raw) ? raw : `https://${raw}`;
  anchor.target = "_blank";
  anchor.rel = "noreferrer noopener";
  anchor.title = raw;
  target.appendChild(anchor);
}

function appendText(target, text, calm, collapse) {
  const source = calm ? text.toLowerCase() : text;

  let cursor = 0;
  for (const link of linksIn(source)) {
    if (link.start > cursor) target.appendChild(document.createTextNode(source.slice(cursor, link.start)));
    // Calming a shout must not reach into an address: a path can be case-sensitive.
    appendLink(target, text.substr(link.start, link.length), collapse);
    cursor = link.start + link.length;
  }
  if (cursor < source.length) target.appendChild(document.createTextNode(source.slice(cursor)));
}

/* "default" rather than "static" so an animated emote animates, the same choice the picker makes. */
const emoteUrl = (id, size) => `https://static-cdn.jtvnw.net/emoticons/v2/${encodeURIComponent(id)}/default/dark/${size}`;

/* The same ceiling the desktop overlay puts on a chat GIF – see MaxGifBytes in OverlayWindow.
   The GIF is picked by a viewer, and CSS only limits how big it is drawn, not how much is
   downloaded. */
const MAX_GIF_BYTES = 6 * 1024 * 1024;

/* Reads the body and throws once it passes the limit, without trusting the declared length.
   Cancelling the reader closes the connection. */
async function readWithinLimit(response, limit) {
  const type = response.headers.get("content-type") || "image/gif";
  if (!response.body) {
    const whole = await response.blob();
    if (whole.size > limit) throw new Error("för stor");
    return whole;
  }

  const reader = response.body.getReader();
  const chunks = [];
  let total = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    total += value.byteLength;
    if (total > limit) {
      await reader.cancel();
      throw new Error("för stor");
    }
    chunks.push(value);
  }
  return new Blob(chunks, { type });
}

/* Fetched rather than set on the img, which is the only way to see the size before the picture
   is shown. Giphy, where the power-up takes them from, answers with
   "access-control-allow-origin: *"; the address goes out unrewritten, signed query and all. */
async function loadGifWithinLimit(url) {
  const response = await fetch(url, { mode: "cors", credentials: "omit", referrerPolicy: "no-referrer" });
  if (!response.ok) throw new Error(`HTTP ${response.status}`);

  // Believed when it is over the ceiling, so an oversized GIF is dropped before a byte of it is read.
  const declared = Number(response.headers.get("content-length"));
  if (Number.isFinite(declared) && declared > MAX_GIF_BYTES) throw new Error("för stor");

  return URL.createObjectURL(await readWithinLimit(response, MAX_GIF_BYTES));
}

/* One picture a tier 2 or tier 3 subscriber sent. The description Twitch wrote into the message
   becomes the alt text, so a GIF that never loads still says what it was. */
function appendGif(target, gif, description) {
  const image = document.createElement("img");
  image.className = "chat-gif";
  image.alt = description;
  image.title = description;
  target.appendChild(image);

  /* An image that failed to load, or was too big, falls back to the description Twitch wrote. */
  const fallBackToDescription = () => image.replaceWith(document.createTextNode(description));
  image.addEventListener("error", fallBackToDescription, { once: true });

  loadGifWithinLimit(gif.url).then((objectUrl) => {
    // Released once the picture is decoded; the drawn image lives on without it. A stream runs for
    // hours, so nothing is held longer than needed.
    const release = () => URL.revokeObjectURL(objectUrl);
    image.addEventListener("load", release, { once: true });
    image.addEventListener("error", release, { once: true });
    image.src = objectUrl;
  }, fallBackToDescription);
}

/* Merges emotes and GIFs, two lists of ranges over the same text, into one list in reading order
   so renderBody walks the text in a single pass. */
function spansOf(emotes, gifs) {
  const spans = [];
  for (let i = 0; i < emotes.length; i++) spans.push({ kind: "emote", value: emotes[i], index: i });
  for (const gif of gifs) spans.push({ kind: "gif", value: gif, index: -1 });
  spans.sort((a, b) => a.value.start - b.value.start);
  return spans;
}

function renderBody(message, view) {
  const body = document.createElement("span");
  body.className = "msg-text";
  if (message.isAction) body.dataset.action = "true";

  // Lower-casing preserves length, so emote spans stay valid after calming a shout.
  const calm = view.calmShouting && isShouting(message.text);
  const collapse = view.collapseLinks;
  /* Twitch sends no emote spans for our own lines; the app fills them in before either view sees
     the message. */
  const emotes = view.showEmotes ? message.emotes : [];
  /* Which span the Gigantify an Emote power-up enlarged. The desktop app decides it, so every view
     enlarges the same emote; showing it big is a setting of its own. */
  const giant = view.giantEmotes ? message.giantEmote : undefined;
  const gifs = view.showGifs !== false ? (message.gifs || []) : [];

  let cursor = 0;
  for (const span of spansOf(emotes, gifs)) {
    const { start, length } = span.value;
    if (start < cursor || start + length > message.text.length) continue;
    if (start > cursor) appendText(body, message.text.slice(cursor, start), calm, collapse);
    const covered = message.text.substr(start, length);
    cursor = start + length;

    if (span.kind === "gif") {
      appendGif(body, span.value, covered);
      continue;
    }

    const image = document.createElement("img");
    image.className = "emote";
    image.loading = "lazy";
    image.alt = covered;
    image.title = image.alt;
    // The 3.0 variant is the only one with the pixels for it; 2.0 scaled up is a blurry mess.
    const size = span.index === giant ? "3.0" : "2.0";
    if (span.index === giant) image.dataset.giant = "true";
    image.src = emoteUrl(span.value.id, size);
    body.appendChild(image);
  }
  if (cursor < message.text.length) appendText(body, message.text.slice(cursor), calm, collapse);
  return body;
}

function tag(text, kind) {
  const span = document.createElement("span");
  span.className = "tag";
  span.dataset.kind = kind;
  span.textContent = text;
  return span;
}

/* The badges Twitch has no image for, shown as a word instead. */
const BADGE_WORDS = { broadcaster: "live", moderator: "mod", lead_moderator: "mod", vip: "vip", subscriber: "sub" };

/* Draws a row of badges: the image where Twitch has one, otherwise the word for a known role. */
function appendBadges(head, badges) {
  for (const badge of badges) {
    if (badge.imageUrl) {
      const image = document.createElement("img");
      image.className = "badge";
      image.src = badge.imageUrl;
      image.alt = badge.title || badge.setId;
      image.title = image.alt;
      head.appendChild(image);
    } else if (BADGE_WORDS[badge.setId]) {
      head.appendChild(tag(BADGE_WORDS[badge.setId], badge.setId));
    }
  }
}

/* Icons show the type at a glance; the headline always says the same thing in words. */
const EVENT_ICONS = {
  subscription: "★", subGift: "🎁", communityGift: "🎁", subUpgrade: "★",
  raid: "🚚", unraid: "↩", announcement: "📣", bitsBadge: "💎",
  watchStreak: "🔥", newChatter: "👋", other: "✨",
  reward: "🔮", shoutoutSent: "📢", shoutoutReceived: "📢",
  celebration: "🎉",
};

const clockOf = (at) => new Date(at).toLocaleTimeString("sv-SE", { hour: "2-digit", minute: "2-digit" });
