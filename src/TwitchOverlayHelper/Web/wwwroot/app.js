"use strict";

const KEY = new URLSearchParams(location.search).get("key") || "";
const $ = (id) => document.getElementById(id);

const el = {
  chat: $("chat"), pinned: $("pinned"), statusDot: $("statusDot"), statusText: $("statusText"),
  jump: $("jumpBtn"), jumpCount: $("jumpCount"), pause: $("pauseBtn"), refresh: $("refreshBtn"),
  raidBtn: $("raidBtn"),
  composer: $("composer"), composerInput: $("composerInput"), composerSend: $("composerSend"),
  mentionList: $("mentionList"),
  emoteBtn: $("emoteBtn"), emotePanel: $("emotePanel"), emoteSearch: $("emoteSearch"),
  emoteBody: $("emoteBody"), emoteNote: $("emoteNote"),
  toast: $("toast"), toastText: $("toastText"), toastAction: $("toastAction"),
  userSheet: $("userSheet"), sheetName: $("sheetName"), sheetQuote: $("sheetQuote"),
  sheetActions: $("sheetActions"), sheetLocked: $("sheetLocked"),
  sheetPin: $("sheetPin"), sheetPinChat: $("sheetPinChat"), sheetDelete: $("sheetDelete"),
  sheetConfirm: $("sheetConfirm"), sheetConfirmText: $("sheetConfirmText"),
  raidPanel: $("raidPanel"), raidList: $("raidList"), raidSearch: $("raidSearch"),
  nickBtn: $("nickBtn"), sheetNick: $("sheetNick"),
  nickSheet: $("nickSheet"), nickTitle: $("nickTitle"), nickFor: $("nickFor"), nickForm: $("nickForm"),
  nickInput: $("nickInput"), nickCount: $("nickCount"), nickRemove: $("nickRemove"),
  nickPanel: $("nickPanel"), nickList: $("nickList"), nickSearch: $("nickSearch"),
  hype: $("hype"), hypeHeadline: $("hypeHeadline"), hypeDetail: $("hypeDetail"),
  hypeBar: $("hypeBar"), hypeFill: $("hypeFill"), hypeTop: $("hypeTop"),
  ttsBar: $("ttsBar"), ttsWho: $("ttsWho"), ttsCost: $("ttsCost"), ttsMore: $("ttsMore"),
  ttsText: $("ttsText"), ttsApprove: $("ttsApprove"), ttsReject: $("ttsReject"),
  ttsStop: $("ttsStop"), ttsLeft: $("ttsLeft"),
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
  nickTarget: null,    // chatter the nickname sheet is naming
  raidCandidates: [],
  toastTimer: 0,
  removed: new Map(),  // message id -> note, so deletions survive a re-render
  stick: true,         // follow the newest message until the reader scrolls up
  pins: [],            // what the strip at the top is holding, mentions and manual pins alike
  sharedPin: "",       // the message this dock last pinned for the viewers, so it can take it down
  channel: "",         // which chat that claim belongs to
  hypeTrain: null,     // last state Twitch sent, kept so the strip survives a settings change
  tts: [],             // paid readings waiting on the streamer, oldest first
  chatters: new Map(), // login -> who they are, newest first: what the @-list is picked from
  seenEmotes: [],      // emote names we have used ourselves, newest first, for the picker's top row
  emotes: null,        // what Twitch says may be sent here; asked for once per account and channel
  emoteIndex: null,    // the same list keyed by name, for turning written words back into pictures
  emoteOwner: null,    // the account and room that answer belongs to, so a change can invalidate it
};

/* Whether this kind of event card is switched on in the app. An app that sends no group list means
   everything shows. */
function showsEvent(chatEvent) {
  const groups = state.settings && state.settings.events;
  return !groups || groups[chatEvent.group] !== false;
}

/* Transport */

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
    // Before the history is drawn, so the names are on the lines the moment they appear.
    applyNicknameBook(frame.nicknames);
    applySettings(frame.settings);
    applyAuth(frame.auth);
    setStatus(frame.status.text, frame.status.state);

    // After a reconnect the history already contains whatever was queued here; leftovers would show
    // twice.
    el.chat.replaceChildren();
    clearPins();
    // A pin belongs to the chat it was made in, so the claim is only recalled for the same channel.
    state.channel = frame.channel || "";
    state.sharedPin = recallSharedPin(state.channel);
    // Before the history is replayed, because replaying it is what fills these back up.
    forgetChannel();
    state.queue.length = 0;
    state.missed = 0;
    updateJump();
    // A train that is still running when OBS restarts should be there when the dock comes back.
    applyHypeTrain(frame.hypeTrain || null);
    // So should a reading somebody paid for and nobody has answered yet.
    applyTts(frame.tts || []);

    drawHistory(frame.history);
    return;
  }
  /* The app rebuilt the whole timeline. Drawn at once: through the pacer it would trickle back at
     the reader's speed. Pins stay, because the channel has not changed. */
  if (frame.type === "history") {
    el.chat.replaceChildren();
    state.queue.length = 0;
    state.missed = 0;
    drawHistory(frame.payload);
    return;
  }
  /* The made-up lines shown while nothing is connected, drawn at once rather than paced. */
  if (frame.type === "samples") {
    el.chat.replaceChildren();
    state.queue.length = 0;
    state.missed = 0;
    drawHistory(frame.items);
    return;
  }
  if (frame.type === "message") { state.queue.push(msg(frame.payload)); return; }
  // Dropped here rather than at render, so a hidden card never counts toward the jump button.
  if (frame.type === "event") { if (showsEvent(frame.payload)) state.queue.push(evt(frame.payload)); return; }
  if (frame.type === "messageUpdate") { applyMessageUpdate(frame.payload); return; }
  // No payload means there is no train. Kept apart from the clear frame, which also fires when the
  // first real line replaces the samples.
  if (frame.type === "hypeTrain") { applyHypeTrain(frame.payload || null); return; }
  // The whole queue every time, not a delta, so the bar never shows a reading already answered.
  if (frame.type === "tts") { applyTts(frame.payload || []); return; }
  // A clear (a channel switch, or the first real line replacing the samples) empties the column, and
  // every pin is a copy of a line that was in it.
  if (frame.type === "clear") {
    el.chat.replaceChildren();
    clearPins();
    forgetSharedPin();
    forgetChannel();
    state.queue.length = 0;
    state.missed = 0;
    return;
  }
  if (frame.type === "moderation") { applyModeration(frame.payload); return; }
  if (frame.type === "status") { setStatus(frame.payload.text, frame.payload.state); return; }
  if (frame.type === "settings") { applySettings(frame.payload); return; }
  if (frame.type === "auth") { applyAuth(frame.payload); return; }
  if (frame.type === "speech") { applySpeech(frame.payload.enabled); return; }
  // Every dock gets it, including the one that made the change, so lines already on screen update.
  if (frame.type === "nickname") { rememberNickname(frame.payload); redrawNicknames(); return; }
  if (frame.type === "badgesLoaded") { location.reload(); }
}

/* Draws lines the app has already read at once, skipping the pacer and any event kind that is
   switched off. */
function drawHistory(items) {
  items
    .filter((item) => item.type !== "event" || showsEvent(item.event))
    .forEach((item) => appendItem(item.type === "event" ? evt(item.event) : msg(item.message), true));
  syncSessionSplit();
  updateJump();
  scrollToEnd();
}

/* Pacing and scrolling */

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

/* A paused dock looks exactly like a quiet one, so the pause lets go by itself. */
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
      // Announced, so the column starting to move on its own is not a mystery.
      toast("Pausen släpptes automatiskt efter två minuter.", "info");
    }, AUTO_RESUME_MS);
  }
  updateJump();
}

el.pause.addEventListener("click", () => setPaused(!state.paused));

/* OBS gives no easy way to reload a dock. Reloading replays the history from the app, so nothing
   is lost. */
el.refresh.addEventListener("click", () => location.reload());

/* Rendering */

/* The chat column holds two kinds of card; each line is tagged as it arrives. */
const msg = (data) => { remember(data); return { kind: "message", data }; };
const evt = (data) => { remember(data); return { kind: "event", data }; };

/* Learns chatters and emotes for the composer. Called from the two factories rather than the
   renderer, so a line that is paced, hidden or scrolled off still counts. */
function remember(data) {
  rememberChatter(data);
  rememberEmotes(data);
}

/* The inside of a message is drawn by chat-render.js, shared with the stream overlay so both show
   a message the same way. */

function isMention(message) {
  const name = state.mentionName.toLowerCase();
  if (!name) return false;
  // An answer to something you said is aimed at you even though the parser cut the "@you" away.
  if (message.reply && message.reply.login === name) return true;
  return message.text.toLowerCase().includes(`@${name}`);
}

/* The line saying this message is an answer. Stays on a single row and truncates with an ellipsis. */
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

  // The reply line is where you try to remember who that was, so the nickname shows here too.
  const nickname = nicknameOf(null, reply.login);
  if (nickname) {
    const nickNode = document.createElement("span");
    nickNode.className = "msg-reply-nick";
    nickNode.textContent = nickname;
    name.appendChild(nickNode);
  }

  const quote = document.createElement("span");
  quote.className = "msg-reply-text";
  quote.textContent = reply.text;

  line.append(mark, name, quote);
  line.addEventListener("click", () => revealMessage(reply.messageId));
  return line;
}

/* Scrolls to the answered message and flashes it; the jump button is the way back. */
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
    time.textContent = clockOf(message.sentAt);
    head.appendChild(time);
  }

  if (state.settings.showBadges) appendBadges(head, message.badges);

  const name = document.createElement("button");
  name.className = "msg-name";
  name.type = "button";
  name.textContent = message.displayName;
  if (state.settings.useTwitchNameColors && message.color) name.style.color = message.color;
  name.addEventListener("click", () => openUserSheet(message));
  head.appendChild(name);

  const nickname = nicknameOf(message.userId, message.login);
  if (nickname) head.appendChild(nickChip(message, nickname));

  if (state.speech) head.appendChild(speakButton(message));

  if (message.isFirstMessage) head.appendChild(tag("ny", "new"));
  if (node.dataset.mention === "true") head.appendChild(tag("till dig", "mention"));
  // A cheer is a normal message that came with bits, so it gets a marker rather than its own card.
  if (message.bits) head.appendChild(tag(`${message.bits} bits`, "bits"));
  // Same for a redemption with typed text: the reward is a marker on the message, as in Twitch's
  // own chat.
  if (message.rewardLabel) head.appendChild(tag(`🔮 ${message.rewardLabel}`, "reward"));
  // A message effect is an animation we do not reproduce, so it is always a marker. A gigantified
  // emote only needs one when it is not shown big.
  if (message.messageEffect) head.appendChild(tag("⚡ effekt", "powerup"));
  if (message.giantEmote != null && !state.settings.giantEmotes) head.appendChild(tag("⚡ förstorad", "powerup"));

  node.appendChild(head);
  node.appendChild(renderBody(message, state.settings));

  if (state.removed.has(message.id)) markRemoved(node, state.removed.get(message.id));
  return node;
}

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
    time.textContent = clockOf(chatEvent.at);
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

  // The headline is worded in the app and names the chatter, so the nickname chip goes after it.
  const nickname = nicknameOf(chatEvent.userId, chatEvent.login);
  if (nickname) head.appendChild(nickChip(chatEvent, nickname));

  node.appendChild(head);
  // Subs and announcements often carry the chatter's own words, which are the part worth reading.
  if (chatEvent.message) node.appendChild(renderBody({ text: chatEvent.message, emotes: chatEvent.emotes, isAction: false }, state.settings));
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

function rerender(preserveReadingPosition = false) {
  // Cards of a kind that is switched off are dropped; switching it back on does not restore them.
  const wasFollowing = state.stick && !state.paused;
  const previousScrollTop = el.chat.scrollTop;
  const items = [...el.chat.children].map((node) => node.item)
    .filter((item) => item && (item.kind !== "event" || showsEvent(item.data)));
  el.chat.replaceChildren(...items.map(buildItem));
  // The session marker is not an item, so a rebuild drops it; it is placed again from the lines.
  syncSessionSplit();
  if (!preserveReadingPosition || wasFollowing) scrollToEnd();
  else {
    state.stick = false;
    el.chat.scrollTop = previousScrollTop;
  }
}

function appendItem(item, isHistory) {
  const follow = isHistory || state.stick;
  const node = buildItem(item);
  el.chat.appendChild(node);

  const lines = trimToLimit();

  // Replayed lines get their marker once at the end of the run; a live line may itself start a new
  // sitting. The trimmed lines are passed on so the column is not walked twice.
  if (!isHistory) syncSessionSplit(lines);

  if (!isHistory && node.dataset.mention === "true" && state.settings.pinMentions) pinMessage(item.data, "mention");
  if (follow) scrollToEnd(); else if (!isHistory) state.missed++;
}

/* Cuts the column back to the reading limit and returns the lines that survived. Counts lines,
   event cards included, but not the session marker; a marker left with nothing above it is removed
   by syncSessionSplit. */
function trimToLimit() {
  const lines = chatLines();
  const excess = lines.length - state.settings.maxMessages;
  if (excess <= 0) return lines;
  for (let i = 0; i < excess; i++) lines[i].remove();
  return lines.slice(excess);
}

/* The cards in the column, without the session marker. Walked fresh each time: the column is
   changed from many places and a kept copy would go stale. */
function chatLines() {
  const lines = [];
  for (const node of el.chat.children) if (node.item) lines.push(node);
  return lines;
}

/* Earlier sittings */

/* Chat survives a restart, so a dock can open onto an earlier sitting's lines. Neither Twitch nor
   the app marks where a stream ended, so six hours of quiet counts as the boundary. The marker sits
   in the column at that point and carries the hide button, so the reader sees what it would hide. */
const SESSION_GAP_MS = 6 * 60 * 60 * 1000;

const itemTime = (item) => (item.kind === "event" ? item.data.at : item.data.sentAt);

/* The newest gap only, so "hide the earlier chat" has one meaning after several restarts. */
function sessionBoundary(lines = chatLines()) {
  for (let i = lines.length - 1; i > 0; i--) {
    const quiet = itemTime(lines[i].item) - itemTime(lines[i - 1].item);
    if (quiet >= SESSION_GAP_MS) return { node: lines[i], older: i, quiet };
  }
  return null;
}

/* Places, updates or removes the session marker from where the lines are. */
function syncSessionSplit(lines) {
  const existing = el.chat.querySelector(".session-split");
  const boundary = sessionBoundary(lines ?? chatLines());
  if (!boundary) {
    if (existing) existing.remove();
    return;
  }

  const split = existing || buildSessionSplit();
  split.dataset.cutoff = String(itemTime(boundary.node.item));
  split.querySelector(".session-label").textContent = `Nytt pass · ${quietText(boundary.quiet)} tyst`;
  split.querySelector(".session-hide").textContent =
    `Dölj ${boundary.older} ${boundary.older === 1 ? "rad" : "rader"} ovanför`;
  if (split.nextElementSibling !== boundary.node) el.chat.insertBefore(split, boundary.node);
}

function buildSessionSplit() {
  const split = document.createElement("div");
  split.className = "session-split";

  const label = document.createElement("span");
  label.className = "session-label";
  split.appendChild(label);

  const hide = document.createElement("button");
  hide.className = "session-hide";
  hide.type = "button";
  hide.addEventListener("click", () => hideEarlierSession(split));
  split.appendChild(hide);
  return split;
}

/* Asks the app to trim, because the same lines are on the viewers' overlay and in the saved
   history. The app answers every dock with the trimmed timeline and its own count, usually higher
   than what was on screen. */
function hideEarlierSession(split) {
  const cutoff = Number(split.dataset.cutoff);
  if (!cutoff) return;
  const hide = split.querySelector(".session-hide");
  hide.disabled = true;
  api("/api/chat/trim", { method: "POST", body: JSON.stringify({ before: cutoff }) })
    .then((result) => toast(`Dolde ${result.removed} ${result.removed === 1 ? "rad" : "rader"} från tidigare pass.`, "info"))
    // Re-enabled rather than left dead: nothing was hidden, and the marker is still standing there.
    .catch((error) => { hide.disabled = false; toast(error.message, "error"); });
}

function quietText(ms) {
  const hours = Math.round(ms / 3600000);
  if (hours < 24) return `${hours} timmar`;
  const days = Math.round(hours / 24);
  return days === 1 ? "ett dygn" : `${days} dygn`;
}

/* Pinned strip */

/* A mention pins itself and expires on a timer; a manual pin stays until taken down by hand.

   Held as a list rather than as nodes, so a late update to a pinned line reaches the strip through
   a rebuild from state. */
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
    // Pinning a mention by hand promotes it, so the timer does not pull it away.
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

/* Hype train */

/* A hype train is a state rather than a line, so it gets a strip that stays put. Every frame is
   the whole current picture: rendering replaces, never merges. */

/* How long a finished train stays up. Matches HypeTrainState.EndedLinger in the app, which decides
   whether a reconnecting dock is handed the train at all. */
const HYPE_LINGER_MS = 12000;
let hypeTimer = 0;

function applyHypeTrain(train) {
  clearTimeout(hypeTimer);
  // Kept so the strip can come back when hype trains are switched on mid-train.
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

  /* A finished train leaves after a short linger. A running one leaves when its deadline passes:
     Twitch would have sent a level-up or an end otherwise, so contact was lost. */
  const linger = train.phase === "ended"
    ? HYPE_LINGER_MS
    : Math.max(0, (train.expiresAt || 0) - Date.now());
  if (train.phase === "ended" || train.expiresAt) {
    // Forgotten as well as hidden, so a later settings change does not put a retired train back up.
    hypeTimer = setTimeout(() => { el.hype.hidden = true; state.hypeTrain = null; }, linger);
  }
}

/* Paid readings */

/* Readings paid for with bits or channel points wait in the app until the streamer answers. The
   bar shows the oldest one, with a count of the rest.

   Each frame carries the whole queue and replaces what is shown: the app's queue is the only
   version of what is still unanswered. */

let ttsTimer = 0;

function applyTts(list) {
  state.tts = Array.isArray(list) ? list : [];
  renderTts();
}

function renderTts() {
  clearTimeout(ttsTimer);
  // The one being read aloud outranks the ones waiting: the stop button belongs to it.
  const current = state.tts.find((entry) => entry.state === "speaking")
    || state.tts.find((entry) => entry.state === "pending")
    || state.tts.find((entry) => entry.state === "queued");
  if (!current) { el.ttsBar.hidden = true; return; }

  const waiting = state.tts.filter((entry) => entry.id !== current.id).length;
  el.ttsBar.hidden = false;
  el.ttsBar.dataset.state = current.state;
  el.ttsBar.dataset.id = current.id;
  el.ttsWho.textContent = current.viewer;
  el.ttsCost.textContent = current.cost > 0
    ? (current.source === "powerUp" ? `${current.cost} bits` : `${current.cost} poäng`)
    : "";
  el.ttsMore.hidden = waiting === 0;
  el.ttsMore.textContent = `+${waiting}`;
  el.ttsMore.title = waiting === 1 ? "En uppläsning till väntar" : `${waiting} uppläsningar till väntar`;
  el.ttsText.textContent = current.text;

  const pending = current.state === "pending";
  el.ttsApprove.hidden = !pending;
  el.ttsApprove.disabled = false;
  el.ttsReject.hidden = !pending;
  el.ttsReject.disabled = false;
  /* Not refundable has two causes: bits, which nothing can hand back, and a channel points reward
     the app did not create, which is refundable only from Twitch's own queue. The tooltip says
     which. */
  el.ttsReject.textContent = current.refundable ? "↩ Refundera" : "🚫 Neka";
  el.ttsReject.title = current.refundable
    ? "Nekar uppläsningen och ger tillbaka poängen"
    : current.source === "powerUp"
      ? "Läser inte upp meddelandet. Appen kan inte betala tillbaka bits – Twitch har inget API för det."
      : "Läser inte upp meddelandet. Belöningen är inte skapad av appen, så poängen måste lämnas tillbaka i Twitchs egen kö.";
  el.ttsStop.hidden = current.state !== "speaking";
  el.ttsStop.disabled = false;

  showTtsCountdown(current);
}

/* Shows how long is left before the app lets an unanswered reading go. Ticks only while something
   is pending. */
function showTtsCountdown(entry) {
  if (entry.state === "speaking") { el.ttsLeft.textContent = "Läses upp …"; return; }
  if (entry.state === "queued") { el.ttsLeft.textContent = "Godkänd – väntar på tur"; return; }
  if (!entry.deadlineAt) { el.ttsLeft.textContent = ""; return; }

  const left = Math.max(0, entry.deadlineAt - Date.now());
  const minutes = Math.floor(left / 60000);
  const seconds = Math.floor((left % 60000) / 1000);
  el.ttsLeft.textContent = minutes > 0 ? `${minutes} min kvar` : `${seconds} s kvar`;
  // Slower once there are minutes left: the number would not change anyway.
  ttsTimer = setTimeout(() => showTtsCountdown(entry), minutes > 0 ? 15000 : 1000);
}

/* Sends the decision to the app. Nothing is taken off screen here: the app sends the queue back
   once it has acted. */
function decideTts(path) {
  const id = el.ttsBar.dataset.id;
  if (!id) return;
  el.ttsApprove.disabled = true;
  el.ttsReject.disabled = true;
  api(path, { method: "POST", body: JSON.stringify({ id }) })
    .catch((error) => { toast(error.message, "error"); renderTts(); });
}

el.ttsApprove.addEventListener("click", () => decideTts("/api/tts/approve"));
el.ttsReject.addEventListener("click", () => decideTts("/api/tts/reject"));
el.ttsStop.addEventListener("click", () => {
  el.ttsStop.disabled = true;
  api("/api/tts/stop", { method: "POST", body: "{}" })
    .catch((error) => { toast(error.message, "error"); renderTts(); });
});

/* A message already delivered has changed: a Gigantify power-up reached the app after its
   message. The line may be waiting in the pacer or already on screen, so both are checked. */
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

  // The pinned strip keeps its own copy of the message.
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
    rememberRemoved(node.dataset.id, note);
    markRemoved(node, note);
  }
}

/* A re-render rebuilds every card from its message, and the strike-through is not part of the
   message, so removal notes are kept here. Capped because only lines still in the column can be
   rebuilt; the oldest ids go first. */
const REMOVED_LIMIT = 500;

function rememberRemoved(id, note) {
  if (!id) return;
  // Deleted first so a repeat moves the id to the newest end rather than keeping its old place.
  state.removed.delete(id);
  state.removed.set(id, note);
  while (state.removed.size > REMOVED_LIMIT) state.removed.delete(state.removed.keys().next().value);
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

/* Name pronunciation */

/* Twitch names are written to be looked at, not said. The app turns the name into speakable text
   and reads it out loud. */
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

/* Nicknames */

/* A chatter can be given a name of your own, shown next to their Twitch name. It never leaves this
   machine and no viewer sees it.

   Held as a book rather than a field on each message, so a name reaches every line the person has
   already written. The app owns and saves the book; this is a copy for drawing. */
const nick = { entries: [], byId: new Map(), byLogin: new Map() };

function applyNicknameBook(list) {
  nick.entries = (list || []).filter((entry) => entry && entry.text);
  reindexNicknames();
}

/* One nickname given, changed or taken away. An entry without text is a removal. */
function rememberNickname(entry) {
  if (!entry) return;
  const index = nick.entries.findIndex((existing) => sameChatter(existing, entry));
  if (!entry.text) { if (index >= 0) nick.entries.splice(index, 1); }
  else if (index >= 0) nick.entries[index] = entry;
  else nick.entries.push(entry);
  reindexNicknames();
}

/* The numeric id decides when both sides have one: a login can be changed, and later be taken by
   somebody else entirely. A line that arrived without an id – the sample chat – has only the login. */
function sameChatter(a, b) {
  if (a.userId && b.userId) return a.userId === b.userId;
  return Boolean(a.login) && a.login.toLowerCase() === (b.login || "").toLowerCase();
}

function reindexNicknames() {
  nick.byId.clear();
  nick.byLogin.clear();
  for (const entry of nick.entries) {
    if (entry.userId) nick.byId.set(entry.userId, entry.text);
    if (entry.login) nick.byLogin.set(entry.login.toLowerCase(), entry.text);
  }
}

function nicknameOf(userId, login) {
  // A known id is authoritative. A login may later be reused by a different Twitch account.
  if (userId) return nick.byId.get(userId) || "";
  if (login && nick.byLogin.has(login.toLowerCase())) return nick.byLogin.get(login.toLowerCase());
  return "";
}

/* Names are baked into the markup, so a change rebuilds what is on screen, the pinned strip
   included. */
function redrawNicknames() {
  if (!state.settings) return;
  rerender(true);
  renderPins();
  if (!el.nickPanel.hidden) renderNickList();
}

function nickChip(message, nickname) {
  const chip = document.createElement("button");
  chip.className = "msg-nick";
  chip.type = "button";
  chip.textContent = nickname;
  const label = `Smeknamn för ${message.displayName} – klicka för att ändra`;
  chip.title = label;
  chip.setAttribute("aria-label", label);
  chip.addEventListener("click", () => openNickSheet(message));
  return chip;
}

/* Takes anything carrying a login and a name: a chat message, or a row in the nickname list. */
function openNickSheet(target) {
  state.nickTarget = { userId: target.userId || "", login: target.login || "", displayName: target.displayName || target.login || "" };
  const current = nicknameOf(state.nickTarget.userId, state.nickTarget.login);

  el.nickTitle.textContent = current ? "Ändra smeknamn" : "Sätt smeknamn";
  el.nickFor.textContent = state.nickTarget.login && state.nickTarget.login !== state.nickTarget.displayName.toLowerCase()
    ? `${state.nickTarget.displayName} (@${state.nickTarget.login})`
    : state.nickTarget.displayName;
  el.nickInput.value = current;
  el.nickRemove.hidden = !current;
  updateNickCount();
  openSheet("nickSheet");
  // Caret in the field and the current name selected, so replacing it is one keystroke.
  el.nickInput.focus();
  el.nickInput.select();
}

function updateNickCount() {
  const length = el.nickInput.value.trim().length;
  el.nickCount.textContent = String(length);
  el.nickCount.parentElement.dataset.full = String(length >= 24);
}

el.nickInput.addEventListener("input", updateNickCount);

el.nickForm.addEventListener("submit", (event) => {
  event.preventDefault();
  saveNickname(el.nickInput.value);
});

el.nickRemove.addEventListener("click", () => saveNickname(""));

el.sheetNick.addEventListener("click", () => {
  if (state.target) openNickSheet(state.target);
});

/* Saves a nickname; blank text removes it. The previous name is read before the call so the
   notice can offer an undo. */
function saveNickname(text) {
  const target = state.nickTarget;
  if (!target) return;
  const previous = nicknameOf(target.userId, target.login);
  el.nickSheet.hidden = true;

  api("/api/nickname", {
    method: "POST",
    body: JSON.stringify({ userId: target.userId, login: target.login, text }),
  })
    .then((saved) => {
      // The app tells every dock, this one included. Applied here as well so the name lands even if
      // the socket is reconnecting.
      rememberNickname(saved || { userId: target.userId, login: target.login, text: "" });
      redrawNicknames();

      const undo = previous === (saved && saved.text ? saved.text : "")
        ? undefined
        : { label: "Ångra", action: () => restoreNickname(target, previous) };
      toast(saved && saved.text
        ? `${target.displayName} visas nu som “${saved.text}”.`
        : `Smeknamnet för ${target.displayName} är borttaget.`, "info", undo);
    })
    .catch((error) => toast(error.message, "error"));
}

function restoreNickname(target, text) {
  api("/api/nickname", {
    method: "POST",
    body: JSON.stringify({ userId: target.userId, login: target.login, text }),
  })
    .then((saved) => {
      rememberNickname(saved || { userId: target.userId, login: target.login, text: "" });
      redrawNicknames();
    })
    .catch((error) => toast(error.message, "error"));
}

/* Every nickname in one list, so a name can be found without waiting for its owner to speak. */
el.nickBtn.addEventListener("click", () => {
  openSheet("nickPanel");
  renderNickList();
});

el.nickSearch.addEventListener("input", renderNickList);

function renderNickList() {
  const term = el.nickSearch.value.trim().toLowerCase();
  const matches = nick.entries
    .filter((entry) => !term || entry.text.toLowerCase().includes(term) || (entry.login || "").includes(term))
    .sort((a, b) => a.text.localeCompare(b.text, "sv"));

  if (!matches.length) {
    el.nickList.replaceChildren(message(nick.entries.length
      ? "Ingen träff."
      : "Inga smeknamn än. Klicka på ett namn i chatten för att sätta ett."));
    return;
  }

  el.nickList.replaceChildren(...matches.map((entry) => {
    const button = document.createElement("button");
    button.className = "nick-item";
    button.type = "button";

    const name = document.createElement("span");
    name.className = "nick-item-name";
    name.textContent = entry.text;

    const sub = document.createElement("span");
    sub.className = "nick-item-sub";
    sub.textContent = entry.login ? `@${entry.login}` : "okänt konto";

    button.append(name, sub);
    button.addEventListener("click", () => openNickSheet({ userId: entry.userId, login: entry.login, displayName: entry.login || entry.text }));
    return button;
  }));
}

/* User actions */

const SHEETS = ["userSheet", "raidPanel", "nickSheet", "nickPanel"];

function closeSheets() {
  for (const id of SHEETS) $(id).hidden = true;
}

function openSheet(id) {
  closeSheets();
  $(id).hidden = false;
}

function openUserSheet(message) {
  state.target = message;
  const nickname = nicknameOf(message.userId, message.login);
  // The nickname leads here too, as it does in the column.
  el.sheetName.textContent = nickname ? `${nickname} · ${message.displayName}` : message.displayName;
  el.sheetQuote.textContent = message.text;
  el.sheetConfirm.hidden = true;
  el.sheetNick.textContent = nickname ? "🏷 Ändra smeknamn" : "🏷 Sätt smeknamn";
  el.sheetPin.textContent = pinLabel(message);
  el.sheetPinChat.textContent = state.sharedPin === message.id
    ? "📌 Ta bort tittarnas nål"
    : "📌 Nåla fast för tittarna";

  /* A line of our own that Twitch never gave an id carries a local one, which Twitch cannot act on,
     so the two buttons that would send it are hidden. */
  const addressable = message.localEcho !== true;
  el.sheetPinChat.hidden = !addressable;
  el.sheetDelete.hidden = !addressable;

  el.sheetActions.hidden = !state.auth.loggedIn;
  el.sheetLocked.hidden = state.auth.loggedIn;
  openSheet("userSheet");
}

/* The local pin is a reading aid, not a moderation action: it changes nothing on Twitch, so it
   works logged out and in any channel.

   Three states: a mention is already on the strip but on a timer, so the button offers to keep
   it. Removing a pin is the ✕ on the card. */
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

/* Pins the line for everyone watching. Twitch keeps one mod-pinned message per channel and pushes
   nothing back when it changes, so the dock only tracks the pin it made itself.

   The claim is saved in localStorage so it survives a dock reload. It can go stale (Twitch drops
   the pin at stream end, another moderator can replace it); removing a pin that is gone answers
   with an error and ends the claim. Asking Twitch would need a scope this app does not hold. */
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
    // Either answer ends the claim: a refusal means Twitch does not agree this dock owns the pin.
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
    // Pinned locally as well: nothing comes back from Twitch that would put it on the strip.
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

/* Raid */

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

/* Composer */

/* Twitch's limit on one chat line, enforced while typing as well as on the way out. */
const MAX_MESSAGE = 480;

/* The field holds only text nodes and <img> emotes carrying their name. Everything reads the
   field through these functions, so what is shown and what is sent cannot drift. */
function composerText() {
  return textOfNodes(el.composerInput.childNodes);
}

/* What a run of composer nodes says, as the chat would read it. */
function textOfNodes(nodes) {
  let text = "";
  for (const node of nodes) {
    if (node.nodeType === Node.TEXT_NODE) text += node.nodeValue;
    else if (node.nodeName === "IMG") text += node.dataset.emote || "";
    // A stray <br> or pasted markup is worth exactly its text.
    else text += node.textContent;
  }
  return text;
}

/* How many characters may still be added: what is left of the limit plus the current selection,
   which typing would replace. */
function composerRoom() {
  const selection = window.getSelection();
  let selected = 0;
  if (selection && !selection.isCollapsed && selection.rangeCount > 0) {
    const range = selection.getRangeAt(0);
    if (el.composerInput.contains(range.commonAncestorContainer))
      selected = textOfNodes(range.cloneContents().childNodes).length;
  }
  return Math.max(0, MAX_MESSAGE - composerText().length + selected);
}

/* Puts text back into the field with emote names drawn as emotes, for a refused message. */
function setComposerText(text) {
  el.composerInput.replaceChildren(...composerNodes(text));
  updateComposerEmpty();
}

function composerNodes(text) {
  const nodes = [];
  let cursor = 0;
  for (const span of emoteSpansIn(text)) {
    if (span.start > cursor) nodes.push(document.createTextNode(text.slice(cursor, span.start)));
    nodes.push(emoteImage(text.substr(span.start, span.length), span.id));
    cursor = span.start + span.length;
  }
  if (cursor < text.length) nodes.push(document.createTextNode(text.slice(cursor)));
  return nodes;
}

function emoteImage(name, id) {
  const image = document.createElement("img");
  // The larger file scaled down by CSS: the field is 22px tall and the 1.0 variant is 28, which
  // lands somewhere blurry on a high-density screen.
  image.src = emoteUrl(id, "2.0");
  image.alt = name;
  image.title = name;
  // What the name was, kept on the node: it is the only way back from the picture to the text.
  image.dataset.emote = name;
  return image;
}

/* An empty editor usually holds a stray <br>, so the placeholder follows the field's text rather
   than CSS emptiness. */
function updateComposerEmpty() {
  el.composerInput.dataset.empty = String(composerText().length === 0);
}

el.composer.addEventListener("submit", (event) => {
  event.preventDefault();
  const text = composerText().trim();
  if (!text) return;
  el.composerInput.replaceChildren();
  updateComposerEmpty();
  closeComposerPanels();
  api("/api/chat/send", { method: "POST", body: JSON.stringify({ text }) })
    .catch((error) => {
      setComposerText(text);
      toast(error.message, "error");
    });
});

// The field is not an <input>, so nothing submits the form on its own.
el.composerInput.addEventListener("keydown", (event) => {
  if (event.key !== "Enter" || event.shiftKey || mention.open) return;
  event.preventDefault();
  el.composer.requestSubmit();
});

/* Editable markup accepts anything the clipboard holds – styled HTML, whole tables. Only the words
   are wanted, and a newline would be an IRC command separator rather than a line break. */
el.composerInput.addEventListener("paste", (event) => {
  event.preventDefault();
  const pasted = (event.clipboardData?.getData("text/plain") || "").replace(/\s+/g, " ");
  // Cut to what is left, with a notice: a paste is the one way past the limit, and the send path
  // cuts at the same limit silently.
  const text = pasted.slice(0, composerRoom());
  if (text.length < pasted.length) toast(`Bara de första ${MAX_MESSAGE} tecknen får plats.`, "error");
  if (text) insertAtCaret(document.createTextNode(text));
  updateComposerEmpty();
  updateMentions();
});

el.composerInput.addEventListener("beforeinput", (event) => {
  if (!event.inputType.startsWith("insert")) return;
  // Only when nothing is selected: replacing a selection cannot make the line longer.
  const selection = window.getSelection();
  if (selection && !selection.isCollapsed) return;
  if (composerText().length >= MAX_MESSAGE) event.preventDefault();
});

el.composerInput.addEventListener("input", updateComposerEmpty);

/* Caret */

/* The text node the caret sits in and its offset. Null when the caret is outside the field or
   resting against an emote. */
function caretInText() {
  const selection = window.getSelection();
  if (!selection || !selection.isCollapsed || selection.rangeCount === 0) return null;
  const node = selection.anchorNode;
  if (!node || node.nodeType !== Node.TEXT_NODE || !el.composerInput.contains(node)) return null;
  return { node, offset: selection.anchorOffset };
}

function placeCaret(node, offset) {
  const range = document.createRange();
  range.setStart(node, Math.min(offset, node.nodeValue.length));
  range.collapse(true);
  const selection = window.getSelection();
  selection.removeAllRanges();
  selection.addRange(range);
}

/* Inserts at the caret, or at the end when the field has never been focused. */
function insertAtCaret(node) {
  const selection = window.getSelection();
  let range;
  if (selection && selection.rangeCount > 0 && el.composerInput.contains(selection.anchorNode)) {
    range = selection.getRangeAt(0);
    range.deleteContents();
  } else {
    range = document.createRange();
    range.selectNodeContents(el.composerInput);
    range.collapse(false);
  }
  range.insertNode(node);
  range.setStartAfter(node);
  range.collapse(true);
  selection.removeAllRanges();
  selection.addRange(range);
}

/* Who is in chat */

/* Holds an evening's regulars without a raid filling the list with strangers. The map is rewritten
   on every line, so its insertion order is recency, which is the order the suggestions want. */
const CHATTER_LIMIT = 400;

function rememberChatter(data) {
  const login = (data.login || "").toLowerCase();
  if (!login) return;
  state.chatters.delete(login);
  state.chatters.set(login, { login, displayName: data.displayName || login, userId: data.userId || "" });
  if (state.chatters.size > CHATTER_LIMIT) state.chatters.delete(state.chatters.keys().next().value);
}

/* Names of the emotes we have used ourselves, for the picker's top row. Only names are kept; the
   picker shows those that are also in Twitch's list of what may be sent. */
const SEEN_EMOTE_LIMIT = 40;

function rememberEmotes(data) {
  const emotes = data.emotes;
  const text = data.text || data.message || "";
  if (!emotes || !emotes.length || !text) return;
  // Our own lines only: an emote someone else used may not be ours to send, while one we have used
  // is one Twitch has accepted from this account.
  const me = (state.auth.login || "").toLowerCase();
  if (!me || (data.login || "").toLowerCase() !== me) return;

  for (const emote of emotes) {
    const name = text.substr(emote.start, emote.length);
    if (!name) continue;
    const at = state.seenEmotes.indexOf(name);
    if (at >= 0) state.seenEmotes.splice(at, 1);
    state.seenEmotes.unshift(name);
  }
  if (state.seenEmotes.length > SEEN_EMOTE_LIMIT) state.seenEmotes.length = SEEN_EMOTE_LIMIT;
}

/* A channel switch takes the people and the emotes with it, so the old room's names are never
   offered. */
function forgetChannel() {
  state.chatters.clear();
  state.seenEmotes.length = 0;
  // The notes belong to lines every caller has just emptied from the column.
  state.removed.clear();
  // The @-list closes with the people in it. The emote picker stays open and refills in place: a
  // clear frame also arrives when the first real message replaces the samples.
  closeMentions();
  forgetEmotes();
}

/* Which fetch is the current one. A switch of channel or account invalidates both the held list
   and any reply still on its way. */
let emoteRequest = 0;

function forgetEmotes() {
  state.emotes = null;
  state.emoteIndex = null;
  emoteRequest++;
  // Open right now: fill it again rather than leave the reader looking at nothing.
  if (!el.emotePanel.hidden) loadEmotes();
}

/* Emote names in a line being typed into the composer. Whole words only, the way Twitch matches
   them. Messages in the column arrive with their spans already worked out. */
function emoteSpansIn(text) {
  if (!state.emoteIndex || !text) return [];
  const spans = [];
  for (const word of text.matchAll(/\S+/g)) {
    const emote = state.emoteIndex.get(word[0]);
    if (emote) spans.push({ id: emote.id, start: word.index, length: word[0].length });
  }
  return spans;
}

/* @-suggestions */

/* An @ that starts a word. Deliberately not \w: display names carry å, ä and ö, and a reader who
   types the name they see should meet the same list as one who types the login. */
const MENTION_PATTERN = /(?:^|\s)@([^\s@]{0,25})$/u;
const MENTION_LIMIT = 8;

/* The half-typed name: the text node holding it and the offsets of its "@" and its end. A name
   cannot run across an emote, so one text node is all that is read and replaced. */
const mention = { open: false, matches: [], index: 0, node: null, at: 0, end: 0 };

function mentionQuery() {
  const caret = caretInText();
  if (!caret) return null;
  const found = MENTION_PATTERN.exec(caret.node.nodeValue.slice(0, caret.offset));
  if (!found) return null;
  return { term: found[1], node: caret.node, at: caret.offset - found[1].length - 1, end: caret.offset };
}

/* Ranked in three tiers: name starts with the term, nickname starts with it, then any contains.
   The sort is stable, so recency decides inside each tier. */
function chattersMatching(term) {
  const needle = term.toLowerCase();
  const found = [];
  const newestFirst = [...state.chatters.values()].reverse();

  for (const chatter of newestFirst) {
    const nickname = nicknameOf(chatter.userId, chatter.login).toLowerCase();
    const display = chatter.displayName.toLowerCase();
    let rank;
    if (!needle) rank = 0;
    else if (chatter.login.startsWith(needle) || display.startsWith(needle)) rank = 0;
    else if (nickname && nickname.startsWith(needle)) rank = 1;
    else if (chatter.login.includes(needle) || display.includes(needle) || (nickname && nickname.includes(needle))) rank = 2;
    else continue;
    found.push({ chatter, rank });
  }

  found.sort((a, b) => a.rank - b.rank);
  return found.slice(0, MENTION_LIMIT).map((entry) => entry.chatter);
}

function updateMentions() {
  if (el.composer.hidden) return closeMentions();
  const query = mentionQuery();
  if (!query) return closeMentions();

  const matches = chattersMatching(query.term);
  if (!matches.length) return closeMentions();

  // The two panels hang in the same place above the field, so one opening closes the other.
  closeEmotePanel();
  mention.open = true;
  mention.matches = matches;
  mention.node = query.node;
  mention.at = query.at;
  mention.end = query.end;
  mention.index = 0;
  renderMentions();
}

function closeMentions() {
  if (!mention.open) return;
  mention.open = false;
  mention.matches = [];
  mention.node = null;
  el.mentionList.hidden = true;
  el.mentionList.replaceChildren();
}

function renderMentions() {
  el.mentionList.replaceChildren(...mention.matches.map((chatter, index) => {
    const row = document.createElement("button");
    row.className = "suggest-item";
    row.type = "button";
    row.setAttribute("role", "option");
    row.setAttribute("aria-selected", String(index === mention.index));

    const name = document.createElement("span");
    name.className = "suggest-name";
    name.textContent = chatter.displayName;
    row.appendChild(name);

    // The nickname is the half you recognise, so it is in the list too.
    const nickname = nicknameOf(chatter.userId, chatter.login);
    if (nickname) {
      const chip = document.createElement("span");
      chip.className = "suggest-nick";
      chip.textContent = nickname;
      row.appendChild(chip);
    }

    // Only when it differs from the display name by more than case.
    if (chatter.login !== chatter.displayName.toLowerCase()) {
      const login = document.createElement("span");
      login.className = "suggest-login";
      login.textContent = `@${chatter.login}`;
      row.appendChild(login);
    }

    // The caret has to stay in the field, so the press must never move focus to the button.
    row.addEventListener("mousedown", (event) => event.preventDefault());
    row.addEventListener("click", () => acceptMention(chatter));
    return row;
  }));

  el.mentionList.hidden = false;
  el.mentionList.children[mention.index]?.scrollIntoView({ block: "nearest" });
}

function moveMention(step) {
  mention.index = (mention.index + step + mention.matches.length) % mention.matches.length;
  renderMentions();
}

/* Twitch notices a mention by the login, so the login goes in unless the display name is the same
   name with capitals. */
function acceptMention(chatter) {
  const name = chatter.displayName.toLowerCase() === chatter.login ? chatter.displayName : chatter.login;
  const inserted = `@${name} `;
  const node = mention.node;
  const value = node.nodeValue;
  const at = mention.at;

  node.nodeValue = value.slice(0, at) + inserted + value.slice(mention.end);
  closeMentions();
  el.composerInput.focus();
  placeCaret(node, at + inserted.length);
  updateComposerEmpty();
}

el.composerInput.addEventListener("input", updateMentions);
el.composerInput.addEventListener("click", updateMentions);
// Moving the caret with the keyboard changes which word it sits in, and only these keys do that
// without also raising an input event.
el.composerInput.addEventListener("keyup", (event) => {
  if (["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) updateMentions();
});
el.composerInput.addEventListener("blur", closeMentions);

el.composerInput.addEventListener("keydown", (event) => {
  if (!mention.open) return;
  if (event.key === "ArrowDown") { event.preventDefault(); moveMention(1); }
  else if (event.key === "ArrowUp") { event.preventDefault(); moveMention(-1); }
  // Enter would otherwise send a half-typed name to the chat, and Tab would leave the field.
  else if (event.key === "Enter" || event.key === "Tab") { event.preventDefault(); acceptMention(mention.matches[mention.index]); }
  else if (event.key === "Escape") { event.preventDefault(); event.stopPropagation(); closeMentions(); }
});

/* Emote picker */

/* Reading order, closest first. The app decides which group an emote belongs to. "Recent" repeats
   emotes from the other groups on purpose: it is a shortcut. */
const EMOTE_GROUPS = [
  { key: "recent", label: "Nyligen använda" },
  { key: "channel", label: "Kanalens emotes" },
  { key: "yours", label: "Dina emotes" },
  { key: "global", label: "Globala" },
];

/* Caps the pictures drawn per section, each of which is an image request. The whole list stays
   searchable. */
const EMOTE_SECTION_LIMIT = 200;

function toggleEmotePanel() {
  if (el.emotePanel.hidden) openEmotePanel(); else closeEmotePanel();
}

function openEmotePanel() {
  closeMentions();
  el.emoteSearch.value = "";
  el.emotePanel.hidden = false;
  el.emoteBtn.setAttribute("aria-expanded", "true");
  renderEmotes();
  // Asked for once per channel: the answer cannot change while a chat is open.
  if (!state.emotes) loadEmotes();
}

function closeEmotePanel() {
  el.emotePanel.hidden = true;
  el.emoteBtn.setAttribute("aria-expanded", "false");
}

function closeComposerPanels() {
  closeMentions();
  closeEmotePanel();
}

function loadEmotes() {
  const request = ++emoteRequest;
  el.emoteBody.replaceChildren(emoteNote("Hämtar emotes …"));
  el.emoteNote.hidden = true;
  api("/api/emotes")
    .then((data) => {
      // The channel or the account changed while this was in flight.
      if (request !== emoteRequest) return;
      state.emotes = data;
      state.emoteIndex = new Map(data.emotes.map((emote) => [emote.name, emote]));
      if (!el.emotePanel.hidden) renderEmotes();
    })
    .catch((error) => {
      if (request !== emoteRequest) return;
      // Nothing is kept, so the next opening tries again rather than showing the failure forever.
      if (!el.emotePanel.hidden) el.emoteBody.replaceChildren(emoteNote(error.message));
    });
}

function emoteNote(text) {
  const note = document.createElement("p");
  note.className = "emote-empty";
  note.textContent = text;
  return note;
}

function renderEmotes() {
  if (!state.emotes) return;
  const term = el.emoteSearch.value.trim().toLowerCase();

  // Checked against the list: an emote used before a channel switch or a lapsed sub may no longer
  // be sendable and would reach the chat as plain words.
  const recent = state.seenEmotes.map((name) => state.emoteIndex.get(name)).filter(Boolean);

  const sections = term
    // A search shows one flat list without sections.
    ? [{ label: "", emotes: state.emotes.emotes.filter((emote) => emote.name.toLowerCase().includes(term)) }]
    : EMOTE_GROUPS.map((group) => ({
      label: group.label,
      emotes: group.key === "recent" ? recent : state.emotes.emotes.filter((emote) => emote.group === group.key),
    }));

  const drawn = [];
  for (const section of sections) {
    if (!section.emotes.length) continue;
    if (section.label) {
      const heading = document.createElement("div");
      heading.className = "emote-group";
      heading.textContent = section.emotes.length > EMOTE_SECTION_LIMIT
        ? `${section.label} (${EMOTE_SECTION_LIMIT} av ${section.emotes.length} – sök för att hitta fler)`
        : section.label;
      drawn.push(heading);
    }
    const grid = document.createElement("div");
    grid.className = "emote-grid";
    for (const emote of section.emotes.slice(0, EMOTE_SECTION_LIMIT)) grid.appendChild(emoteButton(emote));
    drawn.push(grid);
  }

  el.emoteBody.replaceChildren(...(drawn.length ? drawn : [emoteNote(term ? "Ingen emote heter så." : "Inga emotes att visa.")]));

  /* A missing section is explained, so the picker does not look like a channel with no emotes. */
  const missing = state.emotes.missingScope
    ? "Kanalens och dina egna emotes visas inte: appen får inte veta vilka du får skicka. "
      + "Välj Logga in igen i appen, så kommer de hit."
    : !state.emotes.channelChecked
      ? "Kanalens emotes visas inte: din egen emote-lista är för lång för att kunna kontrolleras mot den."
      : "";
  el.emoteNote.hidden = missing.length === 0;
  el.emoteNote.textContent = missing;
}

/* Where an emote comes from, for the tooltip: a search shows one flat list with no headings. */
const EMOTE_SOURCES = { channel: "kanalens", yours: "din", global: "global" };

function emoteButton(emote) {
  const button = document.createElement("button");
  button.className = "emote-pick";
  button.type = "button";
  button.title = `${emote.name} · ${EMOTE_SOURCES[emote.group] || emote.group}`;

  const image = document.createElement("img");
  image.loading = "lazy";
  image.src = emoteUrl(emote.id, "2.0");
  image.alt = emote.name;
  button.appendChild(image);

  button.addEventListener("mousedown", (event) => event.preventDefault());
  button.addEventListener("click", () => insertEmote(emote));
  return button;
}

/* Inserts the emote as its picture; what is sent is read back off the image. Twitch only reads an
   emote that stands alone, so a space follows it. The panel stays open for picking several. */
function insertEmote(emote) {
  // The name is what counts against the limit; the picture is only how it is shown here.
  if (composerText().length + emote.name.length + 1 > MAX_MESSAGE) {
    toast("Meddelandet får inte plats med fler emotes.", "error");
    return;
  }

  el.composerInput.focus();
  // "hej" + Kappa must not send "hejKappa", which Twitch would read as one word.
  if (needsLeadingSpace()) insertAtCaret(document.createTextNode(" "));
  // The trailing space is what keeps the next one from being typed onto the end of this name.
  const spacer = document.createTextNode(" ");
  insertAtCaret(emoteImage(emote.name, emote.id));
  insertAtCaret(spacer);
  placeCaret(spacer, 1);
  updateComposerEmpty();
}

function needsLeadingSpace() {
  const caret = caretInText();
  if (caret) return caret.offset > 0 && !/\s/.test(caret.node.nodeValue[caret.offset - 1]);
  // Not inside a run of text, so this is going on the end: what the field already says decides.
  const text = composerText();
  return text.length > 0 && !/\s$/.test(text);
}

el.emoteBtn.addEventListener("click", toggleEmotePanel);
el.emoteSearch.addEventListener("input", renderEmotes);
el.emoteSearch.addEventListener("keydown", (event) => {
  // The search box lives inside the composer's form, where Enter would send whatever the message
  // field happens to hold.
  if (event.key === "Enter") event.preventDefault();
  if (event.key === "Escape") { event.preventDefault(); event.stopPropagation(); closeEmotePanel(); el.composerInput.focus(); }
});

/* Toast */

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

/* Appearance and auth */

/* Reading settings are owned by the desktop app; the server pushes the current values. */
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

  trimToLimit();

  // Badges, timestamps, link chips and shouting are baked into the markup, so a change rebuilds
  // what is on screen, the pinned strip included.
  if (isChange) { rerender(); renderPins(); applyHypeTrain(state.hypeTrain); }
}

function applyAuth(auth) {
  state.auth = auth;
  el.raidBtn.hidden = !auth.canRaid;
  el.composer.hidden = !auth.canSend;
  // A logout takes the field away; the panels above it would be left floating over the chat.
  if (el.composer.hidden) closeComposerPanels();

  /* An emote list belongs to one account in one channel. This frame reports every change to
     either, including the room id arriving after the picker was filled from the global list alone. */
  const owner = `${auth.login}@${auth.room || ""}`;
  if (owner !== state.emoteOwner) {
    state.emoteOwner = owner;
    forgetEmotes();
    /* Fetched as soon as the room is known: a line of our own arrives with no emote spans, and this
       list is what turns its words back into pictures. */
    if (auth.canSend && auth.room) loadEmotes();
  }
}

/* The speaker button is baked into each message, so turning pronunciation on or off in the app
   has to rebuild what is already on screen. */
function applySpeech(enabled) {
  if (state.speech === enabled) return;
  state.speech = enabled;
  if (state.settings) rerender();
}

/* Chrome */

document.addEventListener("click", (event) => {
  // The emote panel has no backdrop, so a press outside the composer closes it.
  if (!el.emotePanel.hidden && !event.target.closest("#composer")) closeEmotePanel();

  const closer = event.target.closest("[data-close]");
  if (closer) { $(closer.dataset.close).hidden = true; return; }
  // Tapping the dimmed area behind a sheet closes it.
  if (event.target.classList.contains("sheet")) event.target.hidden = true;
});

document.addEventListener("keydown", (event) => {
  if (event.key === "Escape") { closeSheets(); closeComposerPanels(); }
});

if (!KEY) {
  setStatus("Nyckel saknas i adressen – kopiera dock-URL:en från appen igen.", "error");
} else {
  connect();
  requestAnimationFrame(pump);
}
