"use strict";

const KEY = new URLSearchParams(location.search).get("key") || "";
const $ = (id) => document.getElementById(id);

const el = {
  chat: $("chat"), pinned: $("pinned"), statusDot: $("statusDot"), statusText: $("statusText"),
  jump: $("jumpBtn"), jumpCount: $("jumpCount"), pause: $("pauseBtn"), refresh: $("refreshBtn"),
  raidBtn: $("raidBtn"),
  composer: $("composer"), composerInput: $("composerInput"), composerSend: $("composerSend"),
  toast: $("toast"), toastText: $("toastText"), toastAction: $("toastAction"),
  userSheet: $("userSheet"), sheetName: $("sheetName"), sheetQuote: $("sheetQuote"),
  sheetActions: $("sheetActions"), sheetLocked: $("sheetLocked"),
  sheetPin: $("sheetPin"), sheetPinChat: $("sheetPinChat"),
  sheetConfirm: $("sheetConfirm"), sheetConfirmText: $("sheetConfirmText"),
  raidPanel: $("raidPanel"), raidList: $("raidList"), raidSearch: $("raidSearch"),
  hype: $("hype"), hypeHeadline: $("hypeHeadline"), hypeDetail: $("hypeDetail"),
  hypeBar: $("hypeBar"), hypeFill: $("hypeFill"), hypeTop: $("hypeTop"),
};

const state = {
  settings: null,
  auth: { loggedIn: false, canSend: false, canRaid: false },
  speech: false,       // the app has DeepSeek + ElevenLabs set up, so names can be read aloud
  mentionName: "",
  queue: [],
  paused: false,
  missed: 0,
  target: null,        // chatter the bottom sheet is acting on
  raidCandidates: [],
  toastTimer: 0,
  removed: new Map(),  // message id -> note, so deletions survive a re-render
  stick: true,         // follow the newest message until the reader scrolls up
  pins: [],            // what the strip at the top is holding, mentions and manual pins alike
  sharedPin: "",       // the message this dock last pinned for the viewers, so it can take it down
  channel: "",         // which chat that claim belongs to
  hypeTrain: null,     // last state Twitch sent, kept so the strip survives a settings change
};

/* Whether a kind of event card is switched on in the app. Each event arrives carrying the name of
   the group it belongs to, so this never has to know what a submysterygift is. An older app that
   sends no list at all means everything shows, which is what the dock did before the switches. */
function showsEvent(chatEvent) {
  const groups = state.settings && state.settings.events;
  return !groups || groups[chatEvent.group] !== false;
}

/* ------------------------------------------------------------------ transport */

function api(path, options = {}) {
  const separator = path.includes("?") ? "&" : "?";
  return fetch(`${path}${separator}key=${encodeURIComponent(KEY)}`, {
    ...options,
    headers: { "Content-Type": "application/json", ...(options.headers || {}) },
  }).then(async (response) => {
    const body = await response.text();
    const parsed = body ? JSON.parse(body) : null;
    if (!response.ok) throw new Error((parsed && parsed.error) || "Åtgärden gick inte igenom.");
    return parsed;
  });
}

let socket = null;
let reconnectDelay = 1000;

function connect() {
  socket = new WebSocket(`ws://${location.host}/ws?key=${encodeURIComponent(KEY)}`);

  socket.onopen = () => { reconnectDelay = 1000; };
  socket.onmessage = (event) => handle(JSON.parse(event.data));
  socket.onclose = () => {
    setStatus("Tappade kontakten med appen …", "error");
    setTimeout(connect, reconnectDelay);
    reconnectDelay = Math.min(15000, reconnectDelay * 1.7);
  };
  socket.onerror = () => socket.close();
}

function handle(frame) {
  if (frame.type === "hello") {
    state.mentionName = frame.mentionName || "";
    state.speech = frame.speechEnabled === true;
    applySettings(frame.settings);
    applyAuth(frame.auth);
    setStatus(frame.status.text, frame.status.state);

    // A hello is a fresh start, and after a reconnect the history already contains whatever was
    // still queued here. Anything left over would be shown a second time.
    el.chat.replaceChildren();
    clearPins();
    // A pin belongs to the chat it was made in, so the claim is only picked back up when the dock
    // has come back to the same channel. A reload is the whole reason it is written down at all.
    state.channel = frame.channel || "";
    state.sharedPin = recallSharedPin(state.channel);
    state.queue.length = 0;
    state.missed = 0;
    updateJump();
    // A train that is still running when OBS restarts should be there when the dock comes back.
    applyHypeTrain(frame.hypeTrain || null);

    // History is already read; it should appear at once rather than trickle through the pacer.
    // The app keeps every event in its history, so a kind switched back on is here again after a
    // reload even though the cards already on screen were not brought back when it was switched on.
    frame.history
      .filter((item) => item.type !== "event" || showsEvent(item.event))
      .forEach((item) => appendItem(item.type === "event" ? evt(item.event) : msg(item.message), true));
    scrollToEnd();
    return;
  }
  if (frame.type === "message") { state.queue.push(msg(frame.payload)); return; }
  // Dropped here rather than at render, so a hidden card never counts as something the reader is
  // behind on: the jump button counts what is waiting in the queue.
  if (frame.type === "event") { if (showsEvent(frame.payload)) state.queue.push(evt(frame.payload)); return; }
  if (frame.type === "messageUpdate") { applyMessageUpdate(frame.payload); return; }
  // No payload means there is no train. Deliberately not folded into the clear frame below: that
  // one also fires when the first real line replaces the samples, which says nothing about a train.
  if (frame.type === "hypeTrain") { applyHypeTrain(frame.payload || null); return; }
  // Both things that send a clear – switching channel, and the first real line replacing the
  // samples – take the whole column with them, and every pin is a copy of a line that was in it.
  // Leaving the strip alone would nail the previous channel's messages above a chat they are not
  // from. Deliberately unlike the hype train, which is a state of its own and has its own frame.
  if (frame.type === "clear") {
    el.chat.replaceChildren();
    clearPins();
    forgetSharedPin();
    state.queue.length = 0;
    state.missed = 0;
    return;
  }
  if (frame.type === "moderation") { applyModeration(frame.payload); return; }
  if (frame.type === "status") { setStatus(frame.payload.text, frame.payload.state); return; }
  if (frame.type === "settings") { applySettings(frame.payload); return; }
  if (frame.type === "auth") { applyAuth(frame.payload); return; }
  if (frame.type === "speech") { applySpeech(frame.payload.enabled); return; }
  if (frame.type === "badgesLoaded") { location.reload(); }
}

/* ------------------------------------------------------ pacing and scrolling */

let lastRelease = 0;

function pump(now) {
  requestAnimationFrame(pump);
  if (state.paused || !state.settings) return updateJump();

  // Overflow protection: during a raid the queue can outrun any readable pace.
  if (state.queue.length > 300) state.queue.splice(0, state.queue.length - 300);

  const perSecond = state.settings.messagesPerSecond;
  if (!perSecond) {
    while (state.queue.length) appendItem(state.queue.shift(), false);
    return updateJump();
  }

  const interval = 1000 / perSecond;
  if (now - lastRelease < interval) return updateJump();
  lastRelease = now;
  if (state.queue.length) appendItem(state.queue.shift(), false);
  updateJump();
}

function isAtBottom() {
  return el.chat.scrollHeight - el.chat.scrollTop - el.chat.clientHeight < 48;
}

function scrollToEnd() {
  state.stick = true;
  el.chat.scrollTop = el.chat.scrollHeight;
  // Text wrapping and badge images settle after this frame and would otherwise
  // leave the view a row or two short of the newest message.
  requestAnimationFrame(() => {
    if (state.stick) el.chat.scrollTop = el.chat.scrollHeight;
  });
}

// Emote and badge images arrive late and grow the content under the reader.
el.chat.addEventListener("load", () => { if (state.stick) scrollToEnd(); }, true);

function updateJump() {
  const waiting = state.queue.length + state.missed;
  const show = waiting > 0 && (state.paused || !state.stick);
  el.jump.hidden = !show;
  if (show) el.jumpCount.textContent = String(waiting);
}

el.chat.addEventListener("scroll", () => {
  state.stick = isAtBottom();
  if (state.stick) state.missed = 0;
  updateJump();
});
el.jump.addEventListener("click", () => {
  setPaused(false);
  while (state.queue.length) appendItem(state.queue.shift(), false);
  state.missed = 0;
  scrollToEnd();
  updateJump();
});

/* A pause is for reading one line properly, not for leaving the chat behind. A dock left paused
   looks exactly like a dock that has gone quiet – same still column, no sign of which of the two it
   is – so the pause lets go by itself rather than waiting to be noticed. */
const AUTO_RESUME_MS = 2 * 60 * 1000;
let resumeTimer = 0;

function setPaused(paused) {
  clearTimeout(resumeTimer);
  state.paused = paused;
  el.pause.setAttribute("aria-pressed", String(paused));
  el.pause.textContent = paused ? "▶" : "⏸";
  el.pause.title = paused ? "Fortsätt" : "Pausa chatten";
  if (paused) {
    resumeTimer = setTimeout(() => {
      setPaused(false);
      // Said out loud: the column starting to move on its own is otherwise a small mystery.
      toast("Pausen släpptes automatiskt efter två minuter.", "info");
    }, AUTO_RESUME_MS);
  }
  updateJump();
}

el.pause.addEventListener("click", () => setPaused(!state.paused));

/* OBS gives no easy way to reload a dock, and a browser source that has been running for hours is
   the one place where "start over" is the shortest fix. Reloading replays the history from the app,
   so nothing is lost by pressing it. */
el.refresh.addEventListener("click", () => location.reload());

/* --------------------------------------------------------------- rendering */

/* The chat column holds two kinds of card. Tagging each line as it arrives keeps the pacer, the
   history replay and the re-render from having to guess which of the two they are holding. */
const msg = (data) => ({ kind: "message", data });
const evt = (data) => ({ kind: "event", data });

/* Nobody in chat types "https://". A link is written the way it is read – "linktr.ee/perralinks" –
   and a pattern that only knows schemes leaves most of them lying there as dead text. So a bare
   host counts too, but only when its last part is a suffix we recognise: that is what keeps
   "t.ex", "3.5" and "kl.20" out of the link list. */
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

/* Collapsing is about how much room a link takes, not about whether it is one: an address stays
   clickable either way, and only the label changes. */
function appendLink(target, raw) {
  const collapse = state.settings.collapseLinks;
  const anchor = document.createElement("a");
  anchor.className = collapse ? "link-chip" : "link";
  anchor.textContent = collapse ? "🔗 länk" : raw;
  anchor.href = /^https?:\/\//i.test(raw) ? raw : `https://${raw}`;
  anchor.target = "_blank";
  anchor.rel = "noreferrer noopener";
  anchor.title = raw;
  target.appendChild(anchor);
}

function appendText(target, text, calm) {
  const source = calm ? text.toLowerCase() : text;

  let cursor = 0;
  for (const link of linksIn(source)) {
    if (link.start > cursor) target.appendChild(document.createTextNode(source.slice(cursor, link.start)));
    // Calming a shout must not reach into an address: a path can be case-sensitive.
    appendLink(target, text.substr(link.start, link.length));
    cursor = link.start + link.length;
  }
  if (cursor < source.length) target.appendChild(document.createTextNode(source.slice(cursor)));
}

function renderBody(message) {
  const body = document.createElement("span");
  body.className = "msg-text";
  if (message.isAction) body.dataset.action = "true";

  // Lower-casing preserves length, so emote spans stay valid after calming a shout.
  const calm = state.settings.calmShouting && isShouting(message.text);
  const emotes = state.settings.showEmotes ? message.emotes : [];
  /* Which span the Gigantify an Emote power-up blew up. The desktop app decides it, so the dock and
     the overlay enlarge the same emote; showing it big is a reading setting of its own, because a
     three-line-tall image in a narrow column is exactly the kind of thing this dock exists to tame. */
  const giant = state.settings.giantEmotes ? message.giantEmote : undefined;

  let cursor = 0;
  for (let i = 0; i < emotes.length; i++) {
    const emote = emotes[i];
    if (emote.start < cursor || emote.start + emote.length > message.text.length) continue;
    if (emote.start > cursor) appendText(body, message.text.slice(cursor, emote.start), calm);
    const image = document.createElement("img");
    image.className = "emote";
    image.loading = "lazy";
    image.alt = message.text.substr(emote.start, emote.length);
    image.title = image.alt;
    // The 3.0 variant is the only one with the pixels for it; 2.0 scaled up is a blurry mess.
    const size = i === giant ? "3.0" : "2.0";
    if (i === giant) image.dataset.giant = "true";
    image.src = `https://static-cdn.jtvnw.net/emoticons/v2/${encodeURIComponent(emote.id)}/default/dark/${size}`;
    body.appendChild(image);
    cursor = emote.start + emote.length;
  }
  if (cursor < message.text.length) appendText(body, message.text.slice(cursor), calm);
  return body;
}

function tag(text, kind) {
  const span = document.createElement("span");
  span.className = "tag";
  span.dataset.kind = kind;
  span.textContent = text;
  return span;
}

function isMention(message) {
  const name = state.mentionName.toLowerCase();
  if (!name) return false;
  // An answer to something you said is aimed at you even though the parser cut the "@you" away.
  if (message.reply && message.reply.login === name) return true;
  return message.text.toLowerCase().includes(`@${name}`);
}

/* One line that says this is an answer rather than a fresh thought. It is context for the sentence
   below it, so it stays on a single row and gives way with an ellipsis instead of growing: a reply
   to a wall of text must not be taller than the reply itself. */
function buildReply(reply) {
  const line = document.createElement("button");
  line.className = "msg-reply";
  line.type = "button";
  line.title = `${reply.displayName}: ${reply.text}`;
  line.setAttribute("aria-label", `Svar på ${reply.displayName}: ${reply.text}`);

  const mark = document.createElement("span");
  mark.className = "msg-reply-mark";
  mark.setAttribute("aria-hidden", "true");
  mark.textContent = "↩";

  const name = document.createElement("span");
  name.className = "msg-reply-name";
  name.textContent = reply.displayName;

  const quote = document.createElement("span");
  quote.className = "msg-reply-text";
  quote.textContent = reply.text;

  line.append(mark, name, quote);
  line.addEventListener("click", () => revealMessage(reply.messageId));
  return line;
}

/* The answered message is usually a few rows up, and finding it by eye means leaving the newest
   line. Tapping the reply line goes there and marks it, so the way back is the jump button. */
function revealMessage(messageId) {
  const target = [...el.chat.children].find((node) => node.dataset.id === messageId);
  if (!target) { toast("Det meddelandet har redan rullat förbi.", "info"); return; }

  state.stick = false;
  target.scrollIntoView({ block: "center", behavior: "smooth" });
  target.dataset.flash = "true";
  setTimeout(() => { delete target.dataset.flash; }, 1600);
  updateJump();
}

function build(message) {
  const node = document.createElement("article");
  node.className = "msg";
  node.dataset.id = message.id;
  node.dataset.userId = message.userId || "";
  node.dataset.login = message.login || "";
  if (isMention(message)) node.dataset.mention = "true";
  if (state.settings.dimCommands && message.text.trimStart().startsWith("!")) node.dataset.dim = "true";

  if (message.reply) node.appendChild(buildReply(message.reply));

  const head = document.createElement("div");
  head.className = "msg-head";

  if (state.settings.showTimestamps) {
    const time = document.createElement("span");
    time.className = "msg-time";
    time.textContent = new Date(message.sentAt).toLocaleTimeString("sv-SE", { hour: "2-digit", minute: "2-digit" });
    head.appendChild(time);
  }

  if (state.settings.showBadges) {
    for (const badge of message.badges) {
      if (badge.imageUrl) {
        const image = document.createElement("img");
        image.className = "badge";
        image.src = badge.imageUrl;
        image.alt = badge.title || badge.setId;
        image.title = image.alt;
        head.appendChild(image);
      } else if (["broadcaster", "moderator", "vip", "subscriber"].includes(badge.setId)) {
        head.appendChild(tag({ broadcaster: "live", moderator: "mod", vip: "vip", subscriber: "sub" }[badge.setId], badge.setId));
      }
    }
  }

  const name = document.createElement("button");
  name.className = "msg-name";
  name.type = "button";
  name.textContent = message.displayName;
  if (state.settings.useTwitchNameColors && message.color) name.style.color = message.color;
  name.addEventListener("click", () => openUserSheet(message));
  head.appendChild(name);

  if (state.speech) head.appendChild(speakButton(message));

  if (message.isFirstMessage) head.appendChild(tag("ny", "new"));
  if (node.dataset.mention === "true") head.appendChild(tag("till dig", "mention"));
  // A cheer is a normal message that came with bits, so it gets a marker rather than its own card.
  if (message.bits) head.appendChild(tag(`${message.bits} bits`, "bits"));
  // Same for a redemption that asked the viewer to type something: the words are the message, and
  // the reward is a marker on it – which is how Twitch's own chat shows it too.
  if (message.rewardLabel) head.appendChild(tag(`🔮 ${message.rewardLabel}`, "reward"));
  // Power-ups. A message effect is an animation we do not reproduce, so it is always a marker; a
  // gigantified emote speaks for itself when it is shown big, and only needs saying when it is not.
  if (message.messageEffect) head.appendChild(tag("⚡ effekt", "powerup"));
  if (message.giantEmote != null && !state.settings.giantEmotes) head.appendChild(tag("⚡ förstorad", "powerup"));

  node.appendChild(head);
  node.appendChild(renderBody(message));

  if (state.removed.has(message.id)) markRemoved(node, state.removed.get(message.id));
  return node;
}

/* Icons carry the type at a glance, but never alone: the headline says the same thing in words,
   because an icon is one more thing to decode. */
const EVENT_ICONS = {
  subscription: "★", subGift: "🎁", communityGift: "🎁", subUpgrade: "★",
  raid: "🚚", unraid: "↩", announcement: "📣", bitsBadge: "💎",
  watchStreak: "🔥", newChatter: "👋", other: "✨",
  reward: "🔮", shoutoutSent: "📢", shoutoutReceived: "📢",
  celebration: "🎉",
};

function buildEvent(chatEvent) {
  const node = document.createElement("article");
  node.className = "evt";
  node.dataset.id = chatEvent.id;
  node.dataset.kind = chatEvent.kind;
  if (chatEvent.announcementColor) node.dataset.color = chatEvent.announcementColor.toLowerCase();

  const head = document.createElement("div");
  head.className = "evt-head";

  if (state.settings.showTimestamps) {
    const time = document.createElement("span");
    time.className = "msg-time";
    time.textContent = new Date(chatEvent.at).toLocaleTimeString("sv-SE", { hour: "2-digit", minute: "2-digit" });
    head.appendChild(time);
  }

  const icon = document.createElement("span");
  icon.className = "evt-icon";
  icon.setAttribute("aria-hidden", "true");
  icon.textContent = EVENT_ICONS[chatEvent.kind] || EVENT_ICONS.other;
  head.appendChild(icon);

  const headline = document.createElement("span");
  headline.className = "evt-headline";
  headline.textContent = chatEvent.headline;
  head.appendChild(headline);

  node.appendChild(head);
  // Subs and announcements often carry the chatter's own words, which are the part worth reading.
  if (chatEvent.message) node.appendChild(renderBody({ text: chatEvent.message, emotes: chatEvent.emotes, isAction: false }));
  return node;
}

function buildItem(item) {
  const node = item.kind === "event" ? buildEvent(item.data) : build(item.data);
  // Kept so the node can be rebuilt when the desktop app changes a reading setting.
  node.item = item;
  return node;
}

function markRemoved(node, note) {
  if (node.dataset.removed === "true") return;
  node.dataset.removed = "true";
  const label = document.createElement("div");
  label.className = "removed-note";
  label.textContent = note;
  node.appendChild(label);
}

function rerender() {
  // Switching a kind of event off takes the cards that are up down with it – leaving them would say
  // the switch only applies to a chat that has not happened yet. Switching it back on cannot put
  // them back, so the promise is the honest half of that: off now, on from here.
  const items = [...el.chat.children].map((node) => node.item)
    .filter((item) => item && (item.kind !== "event" || showsEvent(item.data)));
  el.chat.replaceChildren(...items.map(buildItem));
  scrollToEnd();
}

function appendItem(item, isHistory) {
  const follow = isHistory || state.stick;
  const node = buildItem(item);
  el.chat.appendChild(node);

  // Event cards live in the same column, so they count towards the limit like any other line.
  while (el.chat.childElementCount > state.settings.maxMessages) el.chat.firstElementChild.remove();

  if (!isHistory && node.dataset.mention === "true" && state.settings.pinMentions) pinMessage(item.data, "mention");
  if (follow) scrollToEnd(); else if (!isHistory) state.missed++;
}

/* ------------------------------------------------------------ pinned strip */

/* Two kinds of pin share the shelf, and they are not the same promise. A mention put itself there
   because it named you, so it takes itself away again after a while – nobody asked for it. A manual
   pin is a decision, "I want to come back to this", and letting a timer overrule that would be the
   dock quietly deciding the reader was done. So it stays until it is taken down by hand.

   Held as a list rather than as nodes, because everything that touches a pinned line – a power-up
   arriving late, a reading setting changing in the app – has to reach the strip as well as the
   column, and a rebuild from state is the only version of that which cannot drift apart. */
const PIN_GROUPS = [
  { kind: "manual", label: "Fastnålat" },
  { kind: "mention", label: "Till dig" },
];

function findPin(messageId) {
  return state.pins.find((pin) => pin.message.id === messageId);
}

function pinMessage(message, kind) {
  const existing = findPin(message.id);
  if (existing) {
    // Pinning a mention by hand promotes it: the timer would otherwise pull it away under the
    // reader who just said they wanted to keep it.
    if (kind === "manual" && existing.kind === "mention") {
      clearTimeout(existing.timer);
      existing.kind = "manual";
      existing.timer = 0;
      renderPins();
    }
    return;
  }

  const pin = { message, kind, timer: 0 };
  if (kind === "mention") {
    pin.timer = setTimeout(() => unpinMessage(message.id), state.settings.pinnedMentionSeconds * 1000);
  }
  state.pins.push(pin);
  renderPins();
}

function unpinMessage(messageId) {
  const index = state.pins.findIndex((pin) => pin.message.id === messageId);
  if (index < 0) return;
  clearTimeout(state.pins[index].timer);
  state.pins.splice(index, 1);
  renderPins();
}

function clearPins() {
  for (const pin of state.pins) clearTimeout(pin.timer);
  state.pins.length = 0;
  renderPins();
}

function renderPins() {
  el.pinned.replaceChildren();
  el.pinned.hidden = state.pins.length === 0;
  if (el.pinned.hidden) return;

  // Manual pins first: they were put there on purpose and are the ones being come back to.
  for (const group of PIN_GROUPS) {
    const pins = state.pins.filter((pin) => pin.kind === group.kind);
    if (!pins.length) continue;

    const section = document.createElement("div");
    section.className = "pin-group";
    section.dataset.kind = group.kind;

    const label = document.createElement("div");
    label.className = "pin-label";
    label.textContent = group.label;
    section.appendChild(label);

    for (const pin of pins) section.appendChild(buildPin(pin));
    el.pinned.appendChild(section);
  }
}

function buildPin(pin) {
  const item = document.createElement("div");
  item.className = "pin-item";
  item.appendChild(build(pin.message));

  const drop = document.createElement("button");
  drop.className = "pin-drop";
  drop.type = "button";
  drop.textContent = "✕";
  const label = `Ta bort nålen från ${pin.message.displayName}`;
  drop.title = label;
  drop.setAttribute("aria-label", label);
  drop.addEventListener("click", () => unpinMessage(pin.message.id));
  item.appendChild(drop);
  return item;
}

/* ------------------------------------------------------------- hype train */

/* A hype train is the one thing here that is a state rather than a line: it runs for minutes and
   changes while it does. So it gets a strip that stays put, and every frame is the whole current
   picture – rendering is a straight replace, never a merge. */

/* How long a finished train stays up so the level it reached can be read. Matches
   HypeTrainState.EndedLinger in the app, which is what decides whether a reconnecting dock is
   handed the train at all. */
const HYPE_LINGER_MS = 12000;
let hypeTimer = 0;

function applyHypeTrain(train) {
  clearTimeout(hypeTimer);
  // Held on to because the strip is a state, not a line: turning hype trains back on mid-train has
  // to be able to put it up again, and nothing else would arrive until the next progress frame.
  state.hypeTrain = train;
  if (!train || !showsEvent({ group: "hypeTrain" })) { el.hype.hidden = true; return; }

  el.hype.hidden = false;
  el.hype.dataset.phase = train.phase;
  el.hypeHeadline.textContent = train.headline;
  el.hypeDetail.textContent = train.detail || "";
  // "Toppbidrag", not "störst": Twitch ranks these per contribution method, so the first one is not
  // necessarily the biggest single contribution.
  el.hypeTop.textContent = train.top.length ? `Toppbidrag: ${train.top.join(" · ")}` : "";

  // The bar is about the level being climbed right now, so a train that is over has none.
  const share = train.goal > 0 ? Math.min(1, train.progress / train.goal) : 0;
  el.hypeBar.hidden = train.goal <= 0;
  el.hypeFill.style.width = `${Math.round(share * 100)}%`;
  el.hypeBar.setAttribute("aria-valuenow", String(Math.round(share * 100)));
  el.hypeBar.setAttribute("aria-label", train.detail || train.headline);

  /* Two ways the strip leaves on its own. A finished train has been read after a few seconds; and a
     running one whose deadline passes is a train we have lost contact with, because Twitch would
     have sent a level-up or an end otherwise. Without the second one, a dropped connection mid-train
     would leave a frozen bar sitting there for the rest of the stream. */
  const linger = train.phase === "ended"
    ? HYPE_LINGER_MS
    : Math.max(0, (train.expiresAt || 0) - Date.now());
  if (train.phase === "ended" || train.expiresAt) {
    // Forgotten as well as hidden: a train the strip has already retired must not come back up
    // because some unrelated reading setting was changed half an hour later.
    hypeTimer = setTimeout(() => { el.hype.hidden = true; state.hypeTrain = null; }, linger);
  }
}

/* A line we have already been given, changed: a Gigantify power-up that reached the desktop app
   after the message it belongs to. It may still be waiting in the pacer or already be a card on
   screen, so both are checked – and a line that has scrolled past its limit is simply gone, which
   is the right answer for a marker nobody can see any more. */
function applyMessageUpdate(payload) {
  // Still in the pacer, so it has not been built yet: swapping the payload is the whole job.
  const queued = state.queue.find((item) => item.kind === "message" && item.data.id === payload.id);
  if (queued) queued.data = payload;

  const node = [...el.chat.children].find((child) => child.dataset.id === payload.id);
  if (node) {
    node.item = msg(payload);
    el.chat.replaceChild(buildItem(node.item), node);
    if (state.stick) scrollToEnd();
  }

  // The pinned strip keeps a copy of its own. A marker that landed on only one of the two would
  // make a single message say different things depending on where in the dock it is read.
  const pinned = findPin(payload.id);
  if (pinned) { pinned.message = payload; renderPins(); }
}

function applyModeration(payload) {
  const nodes = [...document.querySelectorAll(".msg")].filter((node) => {
    if (payload.kind === "chatCleared") return true;
    if (payload.kind === "messageDeleted") return node.dataset.id === payload.messageId;
    return (payload.userId && node.dataset.userId === payload.userId)
      || (payload.login && node.dataset.login === payload.login);
  });

  const note = payload.kind === "userPurged" && payload.durationSeconds
    ? `Timeout ${formatDuration(payload.durationSeconds)}`
    : "Borttaget";

  for (const node of nodes) {
    state.removed.set(node.dataset.id, note);
    markRemoved(node, note);
  }
}

function formatDuration(seconds) {
  if (seconds < 60) return `${seconds} s`;
  if (seconds < 3600) return `${Math.round(seconds / 60)} min`;
  if (seconds < 86400) return `${Math.round(seconds / 3600)} tim`;
  return `${Math.round(seconds / 86400)} dygn`;
}

function setStatus(text, level) {
  el.statusText.textContent = text;
  el.statusDot.dataset.state = level;
}

/* ------------------------------------------------------- name pronunciation */

/* Twitch names are written to be looked at, not said: decorative x-es, doubled letters and
   shouted abbreviations. The app turns the name into speakable text and reads it out loud,
   so a name never has to be decoded before it can be used. */
function speakButton(message) {
  const button = document.createElement("button");
  button.className = "msg-say";
  button.type = "button";
  button.textContent = "🔊";
  const label = `Hör hur ${message.displayName} uttalas`;
  button.title = label;
  button.setAttribute("aria-label", label);
  button.addEventListener("click", () => speakName(message, button));
  return button;
}

function speakName(message, button) {
  if (button.dataset.busy === "true") return;
  button.dataset.busy = "true";
  api("/api/speech/name", {
    method: "POST",
    body: JSON.stringify({ login: message.login, displayName: message.displayName }),
  })
    .then((result) => { if (result && result.warning) toast(result.warning, "error"); })
    .catch((error) => toast(error.message, "error"))
    .finally(() => { button.dataset.busy = "false"; });
}

/* ------------------------------------------------------------ user actions */

const SHEETS = ["userSheet", "raidPanel"];

function closeSheets() {
  for (const id of SHEETS) $(id).hidden = true;
}

function openSheet(id) {
  closeSheets();
  $(id).hidden = false;
}

function openUserSheet(message) {
  state.target = message;
  el.sheetName.textContent = message.displayName;
  el.sheetQuote.textContent = message.text;
  el.sheetConfirm.hidden = true;
  el.sheetPin.textContent = pinLabel(message);
  el.sheetPinChat.textContent = state.sharedPin === message.id
    ? "📌 Ta bort tittarnas nål"
    : "📌 Nåla fast för tittarna";
  el.sheetActions.hidden = !state.auth.loggedIn;
  el.sheetLocked.hidden = state.auth.loggedIn;
  openSheet("userSheet");
}

/* Nailing a line to this dock's strip is a reading aid, not a moderation action: it changes nothing
   on Twitch and nobody but this reader sees it. So it sits outside the row a logout takes away, and
   works in any channel – which is the whole reason stage five was built from this end rather than
   from polling Twitch for what the streamer pinned. */
/* Three states, not two. A mention is already on the strip but on a clock, so the useful thing to
   offer is to stop the clock – and offering "remove" instead was what made the promotion the strip
   is built around unreachable from the one place anybody would look for it. Getting rid of a pin is
   the ✕ on the card, in both cases; this button never needs to be a second way to do that. */
function pinLabel(message) {
  const pin = findPin(message.id);
  if (!pin) return "📌 Nåla fast här";
  return pin.kind === "mention" ? "📌 Behåll nålen" : "📌 Ta bort nålen härifrån";
}

el.sheetPin.addEventListener("click", () => {
  const target = state.target;
  if (!target) return;
  el.userSheet.hidden = true;
  const pin = findPin(target.id);
  if (pin && pin.kind === "manual") unpinMessage(target.id);
  else pinMessage(target, "manual");
});

/* The other half: the same line in front of everyone watching. Twitch keeps one mod-pinned message
   per channel and pushes nothing back when it changes, so the only pin this dock can honestly speak
   for is the one it made itself.

   That claim is written down rather than kept in memory, because an OBS dock gets reloaded and a
   forgotten claim is a pin nobody can take down again without going to Twitch by hand. It can go
   stale – Twitch drops the pin at the end of a stream, and another moderator can replace it – and
   the cost of that is a button offering to remove a pin that is already gone, which answers with a
   readable error and then stops claiming it. Knowing for certain would mean asking Twitch, and
   that needs a scope this app does not hold. */
const SHARED_PIN_KEY = "toh.sharedPin";

function rememberSharedPin(messageId) {
  state.sharedPin = messageId;
  try {
    localStorage.setItem(SHARED_PIN_KEY, JSON.stringify({ channel: state.channel, messageId }));
  } catch (error) { /* storage can be off; the claim then simply lasts as long as the page does */ }
}

function forgetSharedPin() {
  state.sharedPin = "";
  try { localStorage.removeItem(SHARED_PIN_KEY); } catch (error) { /* nothing to undo */ }
}

function recallSharedPin(channel) {
  try {
    const saved = JSON.parse(localStorage.getItem(SHARED_PIN_KEY) || "null");
    return saved && saved.channel === channel ? saved.messageId : "";
  } catch (error) { return ""; }
}

function pinForViewers(target) {
  api("/api/chat/pin", { method: "POST", body: JSON.stringify({ messageId: target.id }) })
    .then(() => {
      rememberSharedPin(target.id);
      toast(`Meddelandet från ${target.displayName} är fastnålat i chatten.`, "info",
        { label: "Ta ner", action: () => unpinForViewers(target) });
    })
    .catch((error) => toast(error.message, "error"));
}

function unpinForViewers(target) {
  api("/api/chat/unpin", { method: "POST", body: JSON.stringify({ messageId: target.id }) })
    .then(() => toast("Nålen är borttagen för tittarna.", "info"))
    .catch((error) => toast(error.message, "error"))
    // Either answer ends the claim. A refusal means Twitch does not agree this dock owns the
    // channel's pin, and a button that keeps offering to take down a pin that is not there is a
    // dead end the reader can only get out of by guessing.
    .finally(() => { if (state.sharedPin === target.id) forgetSharedPin(); });
}

el.sheetActions.addEventListener("click", (event) => {
  const button = event.target.closest("button[data-act]");
  if (!button || !state.target) return;
  const target = state.target;

  if (button.dataset.act === "pinChat") {
    el.userSheet.hidden = true;
    if (state.sharedPin === target.id) { unpinForViewers(target); return; }
    pinForViewers(target);
    // Kept in reach here too: the line the whole channel is looking at is the one worth having
    // where it can be read, and nothing comes back from Twitch that would put it there for us.
    pinMessage(target, "manual");
    return;
  }
  if (button.dataset.act === "profile") {
    window.open(`https://twitch.tv/${target.login}`, "_blank", "noreferrer");
    el.userSheet.hidden = true;
    return;
  }
  if (button.dataset.act === "delete") {
    el.userSheet.hidden = true;
    run(api("/api/mod/delete", { method: "POST", body: JSON.stringify({ messageId: target.id }) }),
      "Meddelandet togs bort.");
    return;
  }
  if (button.dataset.act === "timeout") {
    const seconds = Number(button.dataset.seconds);
    el.userSheet.hidden = true;
    run(api("/api/mod/timeout", { method: "POST", body: JSON.stringify({ userId: target.userId, seconds }) }),
      `${target.displayName} fick timeout ${formatDuration(seconds)}.`,
      { label: "Ångra", action: () => undoBan(target) });
    return;
  }
  if (button.dataset.act === "ban") {
    // Bans are the one action that is painful to undo by hand, so they get a confirm step.
    el.sheetConfirmText.textContent = `Banna ${target.displayName} permanent?`;
    el.sheetConfirm.hidden = false;
  }
});

$("sheetConfirmNo").addEventListener("click", () => { el.sheetConfirm.hidden = true; });
$("sheetConfirmYes").addEventListener("click", () => {
  const target = state.target;
  el.userSheet.hidden = true;
  el.sheetConfirm.hidden = true;
  run(api("/api/mod/ban", { method: "POST", body: JSON.stringify({ userId: target.userId }) }),
    `${target.displayName} är bannad.`,
    { label: "Ångra", action: () => undoBan(target) });
});

function undoBan(target) {
  run(api("/api/mod/unban", { method: "POST", body: JSON.stringify({ userId: target.userId }) }),
    `${target.displayName} är släppt igen.`);
}

/* -------------------------------------------------------------------- raid */

el.raidBtn.addEventListener("click", async () => {
  openSheet("raidPanel");
  el.raidList.replaceChildren(message("Hämtar kanaler …"));
  try {
    state.raidCandidates = await api("/api/raid/candidates");
    renderRaidList();
  } catch (error) {
    el.raidList.replaceChildren(message(error.message));
  }
});

el.raidSearch.addEventListener("input", renderRaidList);

function message(text) {
  const node = document.createElement("p");
  node.className = "empty";
  node.textContent = text;
  return node;
}

function renderRaidList() {
  const term = el.raidSearch.value.trim().toLowerCase();
  const matches = state.raidCandidates.filter((candidate) =>
    !term || candidate.displayName.toLowerCase().includes(term) || candidate.login.includes(term));

  if (!matches.length) {
    el.raidList.replaceChildren(message(term ? "Ingen träff." : "Ingen av kanalerna du följer är live just nu."));
    return;
  }

  el.raidList.replaceChildren(...matches.map((candidate) => {
    const button = document.createElement("button");
    button.className = "raid-item";
    button.type = "button";

    if (candidate.thumbnailUrl) {
      const image = document.createElement("img");
      image.src = candidate.thumbnailUrl;
      image.alt = "";
      button.appendChild(image);
    }

    const meta = document.createElement("div");
    meta.className = "raid-meta";
    const name = document.createElement("div");
    name.className = "raid-name";
    name.textContent = candidate.displayName;
    const sub = document.createElement("div");
    sub.className = "raid-sub";
    sub.textContent = `${candidate.viewerCount} tittare · ${candidate.gameName || "okänt spel"}`;
    meta.append(name, sub);
    button.appendChild(meta);

    button.addEventListener("click", () => {
      el.raidPanel.hidden = true;
      run(api("/api/raid/start", { method: "POST", body: JSON.stringify({ userId: candidate.userId }) }),
        `Raid mot ${candidate.displayName} startad.`,
        { label: "Avbryt", action: () => run(api("/api/raid/cancel", { method: "POST" }), "Raiden avbröts.") });
    });
    return button;
  }));
}

$("raidCancel").addEventListener("click", () => {
  el.raidPanel.hidden = true;
  run(api("/api/raid/cancel", { method: "POST" }), "Raiden avbröts.");
});

/* ---------------------------------------------------------------- composer */

el.composer.addEventListener("submit", (event) => {
  event.preventDefault();
  const text = el.composerInput.value.trim();
  if (!text) return;
  el.composerInput.value = "";
  api("/api/chat/send", { method: "POST", body: JSON.stringify({ text }) })
    .catch((error) => {
      el.composerInput.value = text;
      toast(error.message, "error");
    });
});

/* ------------------------------------------------------------------- toast */

function toast(text, kind, action) {
  clearTimeout(state.toastTimer);
  el.toastText.textContent = text;
  el.toast.dataset.kind = kind || "info";
  el.toast.hidden = false;

  if (action) {
    el.toastAction.hidden = false;
    el.toastAction.textContent = action.label;
    el.toastAction.onclick = () => { el.toast.hidden = true; action.action(); };
  } else {
    el.toastAction.hidden = true;
    el.toastAction.onclick = null;
  }
  state.toastTimer = setTimeout(() => { el.toast.hidden = true; }, action ? 8000 : 4000);
}

function run(promise, successText, action) {
  promise.then(() => toast(successText, "info", action)).catch((error) => toast(error.message, "error"));
}

/* --------------------------------------------------- appearance and auth */

/* Reading settings are owned by the desktop app: the dock is a reading surface, not a
   settings screen, and space here is scarce. The server pushes the current values. */
function applySettings(settings) {
  const isChange = state.settings !== null;
  state.settings = settings;
  const root = document.body.style;
  root.setProperty("--font", `"${settings.fontFamily}", Verdana, sans-serif`);
  root.setProperty("--size", `${settings.fontSize}px`);
  root.setProperty("--lh", String(settings.lineHeight));
  root.setProperty("--ls", `${settings.letterSpacing}em`);
  root.setProperty("--ws", `${settings.wordSpacing}em`);
  root.setProperty("--gap", `${settings.messageGap}px`);
  document.body.dataset.theme = settings.theme;
  document.body.dataset.zebra = String(settings.zebraRows);
  document.body.dataset.nameline = String(settings.nameOnOwnLine);

  while (el.chat.childElementCount > settings.maxMessages) el.chat.firstElementChild.remove();

  // Badges, timestamps, link chips and shouting are baked into the markup, so changing them
  // from the app has to rebuild what is already on screen – not just swap CSS variables.
  // The pinned strip holds cards of its own and would otherwise be the one place in the dock
  // still showing the old setting.
  if (isChange) { rerender(); renderPins(); applyHypeTrain(state.hypeTrain); }
}

function applyAuth(auth) {
  state.auth = auth;
  el.raidBtn.hidden = !auth.canRaid;
  el.composer.hidden = !auth.canSend;
}

/* The speaker button is baked into each message, so turning pronunciation on or off in the app
   has to rebuild what is already on screen. */
function applySpeech(enabled) {
  if (state.speech === enabled) return;
  state.speech = enabled;
  if (state.settings) rerender();
}

/* ------------------------------------------------------------------ chrome */

document.addEventListener("click", (event) => {
  const closer = event.target.closest("[data-close]");
  if (closer) { $(closer.dataset.close).hidden = true; return; }
  // Tapping the dimmed area behind a sheet closes it.
  if (event.target.classList.contains("sheet")) event.target.hidden = true;
});

document.addEventListener("keydown", (event) => {
  if (event.key === "Escape") closeSheets();
});

if (!KEY) {
  setStatus("Nyckel saknas i adressen – kopiera dock-URL:en från appen igen.", "error");
} else {
  connect();
  requestAnimationFrame(pump);
}
