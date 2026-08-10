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
  chatters: new Map(), // login -> who they are, newest first: what the @-list is picked from
  seenEmotes: [],      // emote names we have used ourselves, newest first, for the picker's top row
  emotes: null,        // what Twitch says may be sent here; asked for once per account and channel
  emoteIndex: null,    // the same list keyed by name, for turning written words back into pictures
  emoteOwner: null,    // the account and room that answer belongs to, so a change can invalidate it
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
    // Before the history is drawn: the names have to be on the lines the moment they appear, not
    // one repaint later.
    applyNicknameBook(frame.nicknames);
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
    // Before the history is replayed, because replaying it is what fills these back up: a hello is
    // the one frame that says which room the dock is in.
    forgetChannel();
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
  // Every dock hears it, including the one that made the change: a nickname belongs to the chatter
  // rather than to a message, so it has to reach the lines that are already on screen.
  if (frame.type === "nickname") { rememberNickname(frame.payload); redrawNicknames(); return; }
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
const msg = (data) => { remember(data); return { kind: "message", data }; };
const evt = (data) => { remember(data); return { kind: "event", data }; };

/* Everything the composer needs to offer is learned from the chat going past: who is here to be
   named, and which emotes we have used ourselves. Wired into the two factories rather than into the
   renderer, so a line that is paced, hidden or scrolled off still counts – the list of people in
   chat should not depend on which reading settings are switched on. */
function remember(data) {
  rememberChatter(data);
  rememberEmotes(data);
}

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

/* "default" rather than "static" so an animated emote animates where the browser can play it, which
   is the same choice the picker makes – the two must never show different pictures of one emote. */
const emoteUrl = (id, size) => `https://static-cdn.jtvnw.net/emoticons/v2/${encodeURIComponent(id)}/default/dark/${size}`;

function renderBody(message) {
  const body = document.createElement("span");
  body.className = "msg-text";
  if (message.isAction) body.dataset.action = "true";

  // Lower-casing preserves length, so emote spans stay valid after calming a shout.
  const calm = state.settings.calmShouting && isShouting(message.text);
  /* Including our own lines, which Twitch sends no emote spans for – it decides which words were
     emotes on the way to the viewers and tells everyone except the sender. The app fills those in
     before either view sees the message, so the overlay over the game and the dock agree, and this
     stays one branch rather than two. */
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
    image.src = emoteUrl(emote.id, size);
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

  // The answered message is often from someone whose Twitch name says nothing, and the reply line
  // is exactly where you are trying to remember who that was.
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
      } else if (["broadcaster", "moderator", "lead_moderator", "vip", "subscriber"].includes(badge.setId)) {
        head.appendChild(tag({ broadcaster: "live", moderator: "mod", lead_moderator: "mod", vip: "vip", subscriber: "sub" }[badge.setId], badge.setId));
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

  const nickname = nicknameOf(message.userId, message.login);
  if (nickname) head.appendChild(nickChip(message, nickname));

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

  // "NyTittare prenumererar" says nothing about who that is; the name you gave them does. The
  // headline is worded in the app and names the chatter, so the chip goes after it rather than
  // inside it.
  const nickname = nicknameOf(chatEvent.userId, chatEvent.login);
  if (nickname) head.appendChild(nickChip(chatEvent, nickname));

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

function rerender(preserveReadingPosition = false) {
  // Switching a kind of event off takes the cards that are up down with it – leaving them would say
  // the switch only applies to a chat that has not happened yet. Switching it back on cannot put
  // them back, so the promise is the honest half of that: off now, on from here.
  const wasFollowing = state.stick && !state.paused;
  const previousScrollTop = el.chat.scrollTop;
  const items = [...el.chat.children].map((node) => node.item)
    .filter((item) => item && (item.kind !== "event" || showsEvent(item.data)));
  el.chat.replaceChildren(...items.map(buildItem));
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

/* ---------------------------------------------------------------- nicknames */

/* A Twitch name is chosen to look a certain way, not to be recognised: xXx-padding, deliberate
   misspellings, three regulars whose names all start the same. So a chatter can be given a name of
   your own, shown next to theirs and nowhere else – it never leaves this machine and no viewer ever
   sees it.

   Held as a book of its own rather than as a field on each message: the name belongs to the person,
   so giving one has to reach every line they have already written, including the ones replayed from
   the history. The app owns the book and saves it; this is a copy for drawing. */
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

/* Names are baked into the markup next to the one Twitch gave, so a change has to rebuild what is
   already on screen – the pinned strip included, or one line would keep saying the old name. */
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

/* Takes anything carrying a login and a name: a chat message, or a row in the list of every
   nickname – where the Twitch name is all that was written down. */
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
  // The sheet exists only to type in, so it opens with the caret already there and the old name
  // selected: replacing it is then one keystroke rather than a hunt for the end of the field.
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

/* Blank text is how a nickname is taken back – the same call, so there is one thing that can fail
   and one place it is answered. The undo in the notice is the reason the previous name is read
   before the call rather than after it. */
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
      // The app tells every dock, this one included. Applying the answer here as well means the
      // name still lands if the socket happens to be reconnecting at this exact moment.
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

/* Every nickname in one list. Without it, a name given months ago can only be found again by
   waiting for its owner to say something. */
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

/* ------------------------------------------------------------ user actions */

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
  // The name you gave them leads here too, for the same reason it does in the column: the sheet is
  // about a person, and the Twitch name is the half that was hard to place.
  el.sheetName.textContent = nickname ? `${nickname} · ${message.displayName}` : message.displayName;
  el.sheetQuote.textContent = message.text;
  el.sheetConfirm.hidden = true;
  el.sheetNick.textContent = nickname ? "🏷 Ändra smeknamn" : "🏷 Sätt smeknamn";
  el.sheetPin.textContent = pinLabel(message);
  el.sheetPinChat.textContent = state.sharedPin === message.id
    ? "📌 Ta bort tittarnas nål"
    : "📌 Nåla fast för tittarna";

  /* A line of our own that Twitch never gave an id to carries a local one instead, so it can be a
     message like any other here. The two buttons that would hand that id to Twitch are the ones it
     cannot answer, and a button that can only fail is worse than no button. Everything else on the
     sheet – the local pin, the nickname, the profile – works on a name rather than on a message. */
  const addressable = message.localEcho !== true;
  el.sheetPinChat.hidden = !addressable;
  el.sheetDelete.hidden = !addressable;

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

/* Twitch's own limit on one chat line. Enforced here as well as on the way out, so a message is
   stopped while it is being written rather than quietly cut in half after it was sent. */
const MAX_MESSAGE = 480;

/* The field holds two kinds of node and nothing else: text, and an <img> standing in for an emote
   whose name is written on it. That is the whole contract – everything below reads or writes the
   field through these two functions, so "what is in the box" and "what gets sent" cannot drift. */
function composerText() {
  return textOfNodes(el.composerInput.childNodes);
}

/* What a run of those nodes says, as the chat would read it. Split out from composerText because
   what a paste is about to replace has to be measured the same way the whole field is. */
function textOfNodes(nodes) {
  let text = "";
  for (const node of nodes) {
    if (node.nodeType === Node.TEXT_NODE) text += node.nodeValue;
    else if (node.nodeName === "IMG") text += node.dataset.emote || "";
    // A browser leaves a <br> behind when the last character is deleted, and pasted markup can
    // arrive as anything at all; both are worth exactly their text.
    else text += node.textContent;
  }
  return text;
}

/* How many characters may still be added: what is left of the limit, plus whatever the caret has
   selected – that text is on its way out and its room comes back with it. */
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

/* Text back into the field, with any emote name in it drawn as the emote again. Used when a refused
   message is handed back: it went in as pictures and should not come back as words. */
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

/* An empty editor is not empty markup – there is usually a stray <br> in it – so the placeholder is
   driven from what the field actually says rather than from CSS's idea of emptiness. */
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

// The field is no longer an <input>, so nothing submits the form on its own.
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
  // Cut to what is left, and said out loud. Typing stops at the limit and the picker refuses an
  // emote that will not fit, so a paste going straight past it is the one way over – and the send
  // path cuts at the same limit without a word, which loses the end of the line after it was sent.
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

/* ------------------------------------------------------------ caret */

/* The caret, as the two things everything here needs: which text node it sits in and how far along.
   Null when it is not in the field at all, or is resting against an emote rather than inside words –
   neither an @-name nor a search term can begin there. */
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

/* Inserts at the caret, or at the very end when the field has never been focused – which is where a
   reader who has only clicked the picker would expect their first emote to land. */
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

/* ------------------------------------------------------- who is in chat */

/* Deep enough to hold a quiet evening's regulars, short enough that a raid cannot turn the list into
   a thousand strangers. The map is keyed by login and rewritten on every line, so its insertion
   order is recency – which is the order the suggestions want, and costs nothing to keep. */
const CHATTER_LIMIT = 400;

function rememberChatter(data) {
  const login = (data.login || "").toLowerCase();
  if (!login) return;
  state.chatters.delete(login);
  state.chatters.set(login, { login, displayName: data.displayName || login, userId: data.userId || "" });
  if (state.chatters.size > CHATTER_LIMIT) state.chatters.delete(state.chatters.keys().next().value);
}

/* The names of the emotes we have used ourselves, so the picker can open on what this account keeps
   reaching for rather than on an alphabetical wall. Only the name is kept: which of them may be sent
   is Twitch's answer, and the picker only shows the ones that appear in both lists. */
const SEEN_EMOTE_LIMIT = 40;

function rememberEmotes(data) {
  const emotes = data.emotes;
  const text = data.text || data.message || "";
  if (!emotes || !emotes.length || !text) return;
  // Our own lines only. What the room is using is not the same as what we may send: a subscriber
  // emote scrolling past belongs to whoever wrote it, and putting it in the top row of our own
  // picker is an invitation to send something that reaches the chat as loose words. An emote we
  // have already used is one Twitch has already accepted from this account.
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

/* A channel switch takes the people and the emotes with it: the names in the old room are not who
   is here now, and offering them would be the one way this list could put the wrong @ in a message. */
function forgetChannel() {
  state.chatters.clear();
  state.seenEmotes.length = 0;
  // The @-list is gone with the people in it, so it cannot stay up. The emote picker can: it refills
  // itself in place, and closing it here would have shut it under the reader every time the sample
  // lines were replaced by the first real message – which is a clear frame like any other.
  closeMentions();
  forgetEmotes();
}

/* Which fetch is the current one. An emote list is asked for once and then held, so a switch of
   channel or account has to invalidate both what is held and whatever is still on its way – a reply
   landing afterwards would quietly install the previous room's emotes over the new room's. */
let emoteRequest = 0;

function forgetEmotes() {
  state.emotes = null;
  state.emoteIndex = null;
  emoteRequest++;
  // Open right now: fill it again rather than leave the reader looking at nothing.
  if (!el.emotePanel.hidden) loadEmotes();
}

/* Emote names in a line, for drawing what is being typed into the composer. Whole words only, the
   way Twitch matches them: "Kappa" inside "Kappagrejen" is five more letters and nothing else.
   Messages in the column need none of this – they arrive with their spans already worked out. */
function emoteSpansIn(text) {
  if (!state.emoteIndex || !text) return [];
  const spans = [];
  for (const word of text.matchAll(/\S+/g)) {
    const emote = state.emoteIndex.get(word[0]);
    if (emote) spans.push({ id: emote.id, start: word.index, length: word[0].length });
  }
  return spans;
}

/* --------------------------------------------------------- @-suggestions */

/* An @ that starts a word. Deliberately not \w: display names carry å, ä and ö, and a reader who
   types the name they see should meet the same list as one who types the login. */
const MENTION_PATTERN = /(?:^|\s)@([^\s@]{0,25})$/u;
const MENTION_LIMIT = 8;

/* Where the half-typed name is: the text node holding it and the offsets of its "@" and its end.
   A node rather than a position in the whole field, because a name cannot run across an emote – so
   the one run of text the caret is in is the whole of what has to be read and replaced. */
const mention = { open: false, matches: [], index: 0, node: null, at: 0, end: 0 };

function mentionQuery() {
  const caret = caretInText();
  if (!caret) return null;
  const found = MENTION_PATTERN.exec(caret.node.nodeValue.slice(0, caret.offset));
  if (!found) return null;
  return { term: found[1], node: caret.node, at: caret.offset - found[1].length - 1, end: caret.offset };
}

/* Three tiers rather than one: a name that starts with what was typed is nearly always the one
   meant, and a nickname match is worth having but should never outrank the Twitch name someone is
   halfway through typing. Sorting is stable, so recency decides inside each tier. */
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

  // The two panels hang in the same place above the field, so one opening puts the other away.
  // Typing an @ is the more specific thing to be doing, so it wins.
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

    // The name you gave them is the half you recognise, so it is in the list too – the whole reason
    // nicknames exist is that three regulars can start with the same four letters.
    const nickname = nicknameOf(chatter.userId, chatter.login);
    if (nickname) {
      const chip = document.createElement("span");
      chip.className = "suggest-nick";
      chip.textContent = nickname;
      row.appendChild(chip);
    }

    // Only when it says something the display name does not: for most accounts the two are the
    // same word in different case, and repeating it would be noise on every row.
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

/* Twitch notices a mention by the login, and for an account whose display name is written in another
   script the two are different words – so the login is what goes in unless the display name is the
   same name with capitals, which is the case for nearly everyone. */
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

/* -------------------------------------------------------- emote picker */

/* Reading order, closest first. Which of the three groups an emote belongs to is settled in the app
   before it gets here – a name the channel owns and the global set also uses is the channel's – so
   this list only has to decide where each group is drawn. "Recent" is the exception and deliberately
   repeats: it is a shortcut to the ones we keep using, not a fourth place to look. */
const EMOTE_GROUPS = [
  { key: "recent", label: "Nyligen använda" },
  { key: "channel", label: "Kanalens emotes" },
  { key: "yours", label: "Dina emotes" },
  { key: "global", label: "Globala" },
];

/* Someone subscribed to a hundred channels has more emotes than anyone can look through, and every
   one of them is an image request. The cap is a reading limit, not a fetch limit: the whole list is
   still searchable, it is only the wall of pictures that stops. */
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
  // Only ever asked for once per channel: the answer cannot change while a chat is open, and the
  // picker should feel like a panel rather than like a page load every time it is opened.
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
      // The channel or the account changed while this was in flight: it is an answer about a room
      // the dock has already left.
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

  // Held against the list as well as collected from our own lines: an emote used before a channel
  // switch or a sub running out is one this account may no longer send, and it would go into the
  // box as a picture and reach the chat as plain words.
  const recent = state.seenEmotes.map((name) => state.emoteIndex.get(name)).filter(Boolean);

  const sections = term
    // A search is a search: sections would only put distance between a name and the match for it.
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

  /* A section that is missing has to say so where it would have been, not stay quiet: the picker
     otherwise looks like a channel with no emotes, which is a different and unfixable thing. */
  const missing = state.emotes.missingScope
    ? "Kanalens och dina egna emotes visas inte: appen får inte veta vilka du får skicka. "
      + "Välj Logga in igen i appen, så kommer de hit."
    : !state.emotes.channelChecked
      ? "Kanalens emotes visas inte: din egen emote-lista är för lång för att kunna kontrolleras mot den."
      : "";
  el.emoteNote.hidden = missing.length === 0;
  el.emoteNote.textContent = missing;
}

/* Where an emote comes from, for the tooltip. Worth saying because a search shows one flat list with
   no headings above it, and "which of my four hundred emotes is this" is a fair question. */
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

/* Goes in as the picture it is, not as its name. The two are the same message – what is sent is
   read back off the image – but a name in the box tells the reader nothing about whether they
   picked the right one, which is the whole reason for having a picker.

   Twitch only reads an emote that stands alone, so the space after it is part of inserting one, and
   the panel stays open on purpose: picking two or three in a row is the normal case. */
function insertEmote(emote) {
  // The name is what counts against the limit; the picture is only how it is shown here.
  if (composerText().length + emote.name.length + 1 > MAX_MESSAGE) {
    toast("Meddelandet får inte plats med fler emotes.", "error");
    return;
  }

  el.composerInput.focus();
  // "hej" + Kappa must not send "hejKappa": Twitch would read that as one word and neither half
  // would mean anything.
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
  // A logout takes the field away mid-sentence; the panels hanging above it would otherwise be
  // left floating over the chat with nothing under them.
  if (el.composer.hidden) closeComposerPanels();

  /* An emote list belongs to one account in one channel, and this frame is the only thing that says
     when either changes: a login, a logout, a channel switch, and – the one that is easy to miss –
     the room id arriving after the picker has already been opened and filled from the global list
     alone. Comparing the pair is what makes all four the same case. */
  const owner = `${auth.login}@${auth.room || ""}`;
  if (owner !== state.emoteOwner) {
    state.emoteOwner = owner;
    forgetEmotes();
    /* Fetched before anyone asks for it, once the room is known. The list is not only the picker's:
       a line of our own arrives with no emote spans, and this is what turns its words back into
       pictures – which has to be ready by the time the message is written, not by the time somebody
       thinks to open the picker. Waiting for the room means one call instead of two. */
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

/* ------------------------------------------------------------------ chrome */

document.addEventListener("click", (event) => {
  // The emote panel has no backdrop of its own – it is a small thing above a field, not a dialog –
  // so anything pressed outside the composer is what closes it.
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
