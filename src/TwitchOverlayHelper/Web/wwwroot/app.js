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
  sheetConfirm: $("sheetConfirm"), sheetConfirmText: $("sheetConfirmText"),
  raidPanel: $("raidPanel"), raidList: $("raidList"), raidSearch: $("raidSearch"),
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
};

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
    el.pinned.replaceChildren();
    el.pinned.hidden = true;
    state.queue.length = 0;
    state.missed = 0;
    updateJump();

    // History is already read; it should appear at once rather than trickle through the pacer.
    frame.history.forEach((item) => appendItem(item.type === "event" ? evt(item.event) : msg(item.message), true));
    scrollToEnd();
    return;
  }
  if (frame.type === "message") { state.queue.push(msg(frame.payload)); return; }
  if (frame.type === "event") { state.queue.push(evt(frame.payload)); return; }
  if (frame.type === "messageUpdate") { applyMessageUpdate(frame.payload); return; }
  if (frame.type === "clear") { el.chat.replaceChildren(); state.queue.length = 0; state.missed = 0; return; }
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
  const items = [...el.chat.children].map((node) => node.item).filter(Boolean);
  el.chat.replaceChildren(...items.map(buildItem));
  scrollToEnd();
}

function appendItem(item, isHistory) {
  const follow = isHistory || state.stick;
  const node = buildItem(item);
  el.chat.appendChild(node);

  // Event cards live in the same column, so they count towards the limit like any other line.
  while (el.chat.childElementCount > state.settings.maxMessages) el.chat.firstElementChild.remove();

  if (!isHistory && node.dataset.mention === "true" && state.settings.pinMentions) pin(item.data);
  if (follow) scrollToEnd(); else if (!isHistory) state.missed++;
}

function pin(message) {
  const node = build(message);
  const wrapper = document.createElement("div");
  if (el.pinned.hidden) {
    el.pinned.hidden = false;
    const label = document.createElement("div");
    label.className = "pin-label";
    label.textContent = "Till dig";
    el.pinned.appendChild(label);
  }
  wrapper.appendChild(node);
  el.pinned.appendChild(wrapper);

  setTimeout(() => {
    wrapper.remove();
    if (el.pinned.querySelectorAll(".msg").length === 0) {
      el.pinned.replaceChildren();
      el.pinned.hidden = true;
    }
  }, state.settings.pinnedMentionSeconds * 1000);
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
  const pinnedNode = el.pinned.querySelector(`.msg[data-id="${CSS.escape(payload.id)}"]`);
  if (pinnedNode) pinnedNode.replaceWith(build(payload));
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
  el.sheetActions.hidden = !state.auth.loggedIn;
  el.sheetLocked.hidden = state.auth.loggedIn;
  openSheet("userSheet");
}

el.sheetActions.addEventListener("click", (event) => {
  const button = event.target.closest("button[data-act]");
  if (!button || !state.target) return;
  const target = state.target;

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
  if (isChange) rerender();
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
