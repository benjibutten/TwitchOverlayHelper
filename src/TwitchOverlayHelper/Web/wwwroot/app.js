"use strict";

const KEY = new URLSearchParams(location.search).get("key") || "";
const $ = (id) => document.getElementById(id);

const el = {
  chat: $("chat"), pinned: $("pinned"), statusDot: $("statusDot"), statusText: $("statusText"),
  jump: $("jumpBtn"), jumpCount: $("jumpCount"), pause: $("pauseBtn"), raidBtn: $("raidBtn"),
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
    applySettings(frame.settings);
    applyAuth(frame.auth);
    setStatus(frame.status.text, frame.status.state);
    el.chat.replaceChildren();
    // History is already read; it should appear at once rather than trickle through the pacer.
    frame.history.forEach((message) => append(message, true));
    scrollToEnd();
    return;
  }
  if (frame.type === "message") { state.queue.push(frame.payload); return; }
  if (frame.type === "clear") { el.chat.replaceChildren(); state.queue.length = 0; state.missed = 0; return; }
  if (frame.type === "moderation") { applyModeration(frame.payload); return; }
  if (frame.type === "status") { setStatus(frame.payload.text, frame.payload.state); return; }
  if (frame.type === "settings") { applySettings(frame.payload); return; }
  if (frame.type === "auth") { applyAuth(frame.payload); return; }
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
    while (state.queue.length) append(state.queue.shift(), false);
    return updateJump();
  }

  const interval = 1000 / perSecond;
  if (now - lastRelease < interval) return updateJump();
  lastRelease = now;
  if (state.queue.length) append(state.queue.shift(), false);
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
  state.paused = false;
  el.pause.setAttribute("aria-pressed", "false");
  while (state.queue.length) append(state.queue.shift(), false);
  state.missed = 0;
  scrollToEnd();
  updateJump();
});
el.pause.addEventListener("click", () => {
  state.paused = !state.paused;
  el.pause.setAttribute("aria-pressed", String(state.paused));
  el.pause.textContent = state.paused ? "▶" : "⏸";
  el.pause.title = state.paused ? "Fortsätt" : "Pausa chatten";
  updateJump();
});

/* --------------------------------------------------------------- rendering */

const URL_PATTERN = /https?:\/\/[^\s]+|www\.[^\s]+/gi;

function isShouting(text) {
  const letters = text.replace(/[^A-Za-zÅÄÖåäö]/g, "");
  if (letters.length < 8) return false;
  const upper = letters.replace(/[^A-ZÅÄÖ]/g, "").length;
  return upper / letters.length > 0.7;
}

function appendText(target, text, calm) {
  const source = calm ? text.toLowerCase() : text;
  if (!state.settings.collapseLinks) { target.appendChild(document.createTextNode(source)); return; }

  let cursor = 0;
  for (const match of source.matchAll(URL_PATTERN)) {
    if (match.index > cursor) target.appendChild(document.createTextNode(source.slice(cursor, match.index)));
    const chip = document.createElement("a");
    chip.className = "link-chip";
    chip.textContent = "🔗 länk";
    chip.href = match[0].startsWith("http") ? match[0] : `https://${match[0]}`;
    chip.target = "_blank";
    chip.rel = "noreferrer noopener";
    chip.title = match[0];
    target.appendChild(chip);
    cursor = match.index + match[0].length;
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

  let cursor = 0;
  for (const emote of emotes) {
    if (emote.start < cursor || emote.start + emote.length > message.text.length) continue;
    if (emote.start > cursor) appendText(body, message.text.slice(cursor, emote.start), calm);
    const image = document.createElement("img");
    image.className = "emote";
    image.loading = "lazy";
    image.alt = message.text.substr(emote.start, emote.length);
    image.title = image.alt;
    image.src = `https://static-cdn.jtvnw.net/emoticons/v2/${encodeURIComponent(emote.id)}/default/dark/2.0`;
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
  const name = state.mentionName;
  return name.length > 0 && message.text.toLowerCase().includes(`@${name.toLowerCase()}`);
}

function build(message) {
  const node = document.createElement("article");
  node.className = "msg";
  node.dataset.id = message.id;
  node.dataset.userId = message.userId || "";
  node.dataset.login = message.login || "";
  if (isMention(message)) node.dataset.mention = "true";
  if (state.settings.dimCommands && message.text.trimStart().startsWith("!")) node.dataset.dim = "true";

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

  if (message.isFirstMessage) head.appendChild(tag("ny", "new"));
  if (node.dataset.mention === "true") head.appendChild(tag("till dig", "mention"));

  node.appendChild(head);
  node.appendChild(renderBody(message));

  // Kept so the node can be rebuilt when the desktop app changes a reading setting.
  node.chatMessage = message;
  if (state.removed.has(message.id)) markRemoved(node, state.removed.get(message.id));
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
  const messages = [...el.chat.children].map((node) => node.chatMessage).filter(Boolean);
  el.chat.replaceChildren(...messages.map(build));
  scrollToEnd();
}

function append(message, isHistory) {
  const follow = isHistory || state.stick;
  const node = build(message);
  el.chat.appendChild(node);

  while (el.chat.childElementCount > state.settings.maxMessages) el.chat.firstElementChild.remove();

  if (!isHistory && node.dataset.mention === "true" && state.settings.pinMentions) pin(message);
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
