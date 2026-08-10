"use strict";

/* The chat as a browser source on the stream itself. It shares the socket, the message shapes and
   the renderer with the dock the streamer reads, and almost nothing else – because the two are read
   by different people.

   What is deliberately not here:
     · nicknames – private notes about viewers, which the server does not even send to this page
     · every moderation control, the composer and the emote picker – tools, not chat
     · the connection status, the pause button, the jump counter, the "earlier sitting" marker –
       information about the app rather than about the conversation
     · the pinned strip – the streamer's own way of not losing a question
     · the speaker button – it plays sound on the streamer's machine
     · deleted messages – gone means gone here, not struck through

   What it does show: who said what, their badges, their emotes, and the events worth thanking
   somebody for out loud. */

const KEY = new URLSearchParams(location.search).get("key") || "";
const chat = document.getElementById("chat");

/* The same defaults as StreamSettings on the app side, so the first paint is right even in the
   moment before the socket has said anything. */
let settings = {
  fontSize: 26, fontFamily: "Verdana", lineHeight: 1.35, messageGap: 8,
  maxMessages: 12, fadeAfterSeconds: 0, newestOnTop: false, animate: true,
  messageBackgroundOpacity: 0.35, textOutline: true, nameOnOwnLine: false,
  showBadges: true, useTwitchNameColors: true, showEmotes: true, giantEmotes: true,
  showTimestamps: false, showReplies: true, collapseLinks: true, calmShouting: false,
  hideCommands: true, ignoredAccounts: "", events: {},
};

/* The ignore list is a line the user typed, so it is read the way people write one: names separated
   by commas, spaces or newlines, with or without the @. */
const loginsIn = (text) => new Set(
  (text || "").split(/[\s,;]+/).map((name) => name.replace(/^@/, "").toLowerCase()).filter(Boolean));

let ignored = new Set();

/* How far back a replay may reach when the page opens or the server redraws the timeline. A scene
   switch in OBS reloads this page, and chat from before the switch coming back as though it were
   new is the kind of thing viewers notice. With a fade time set, that time is the honest answer –
   anything older would have faded already; without one, a few minutes. */
const REPLAY_FALLBACK_MS = 5 * 60 * 1000;
const replayWindow = () => (settings.fadeAfterSeconds > 0 ? settings.fadeAfterSeconds * 1000 : REPLAY_FALLBACK_MS);

/* ------------------------------------------------------------------ transport */

let socket = null;
let reconnectDelay = 1000;

function connect() {
  // Saying which page this is keeps the private half of the fan-out – the nicknames above all – from
  // ever being sent to a socket that lives on the broadcast.
  socket = new WebSocket(`ws://${location.host}/ws?view=stream&key=${encodeURIComponent(KEY)}`);
  socket.onopen = () => { reconnectDelay = 1000; };
  socket.onmessage = (event) => handle(JSON.parse(event.data));
  socket.onclose = () => {
    setTimeout(connect, reconnectDelay);
    reconnectDelay = Math.min(15000, reconnectDelay * 1.7);
  };
  socket.onerror = () => socket.close();
}

function handle(frame) {
  switch (frame.type) {
    case "hello":
      applySettings(frame.stream);
      if (frame.samples) drawSamples(frame.history); else drawHistory(frame.history);
      return;
    case "samples":
      drawSamples(frame.items);
      return;
    case "streamSettings":
      applySettings(frame.payload);
      return;
    case "message":
      queue({ kind: "message", data: frame.payload });
      return;
    case "event":
      queue({ kind: "event", data: frame.payload });
      return;
    case "messageUpdate":
      applyMessageUpdate(frame.payload);
      return;
    case "moderation":
      applyModeration(frame.payload);
      return;
    case "clear":
      pending.length = 0;
      chat.replaceChildren();
      return;
    case "history":
      drawHistory(frame.payload);
      return;
    case "badgesLoaded":
      /* A badge's image address is resolved on the app's side as each message is serialised, so the
         lines already here cannot grow badges by being rebuilt – they would be rebuilt from the same
         payload, which says there was no image. Only asking again helps, which is what the dock does
         by reloading. Here a reload is a blink on the broadcast, so it happens only when there is
         nothing to blink. That is the ordinary case: the catalogue lands seconds after the app
         connects, and every line after it carries its badges either way. */
      if (chat.children.length === 0 && pending.length === 0) location.reload();
      return;
    default:
      // Pet spawns, hype train, the dock's own frames: someone else's business.
  }
}

/* ------------------------------------------------------------------ the queue

   A raid or a hype train can push hundreds of lines through in a second, and appending each one the
   moment it lands means hundreds of layouts in that second. They are drained a few per frame
   instead, which is both smoother and – on a machine that is also encoding video – considerably
   cheaper. Anything still waiting that could never reach the screen is dropped rather than drawn:
   the column holds a dozen lines, so a backlog three times that deep is already history. */

const pending = [];
const PER_FRAME = 4;
let drainHandle = 0;

function queue(item) {
  if (!shows(item)) return;
  pending.push(item);
  const backlog = Math.max(settings.maxMessages * 3, 12);
  if (pending.length > backlog) pending.splice(0, pending.length - backlog);
  if (!drainHandle) drainHandle = requestAnimationFrame(drain);
}

function drain() {
  drainHandle = 0;
  for (let i = 0; i < PER_FRAME && pending.length > 0; i++) append(buildItem(pending.shift()));
  if (pending.length > 0) drainHandle = requestAnimationFrame(drain);
}

function append(node) {
  chat.appendChild(node);
  // Oldest first in the markup either way round, so the line that has to go is always the first one.
  while (chat.children.length > settings.maxMessages) chat.firstElementChild.remove();
  startSweeping();
}

/* ------------------------------------------------------------------ what is shown */

const timeOf = (item) => (item.kind === "event" ? item.data.at : item.data.sentAt);

/* Both halves of "not on the stream": the accounts the streamer never wants quoted, and the command
   traffic aimed at them. The dock dims commands rather than hiding them, because there the point is
   to see who asked for what; here they are noise with a bot answer coming right behind. */
function shows(item) {
  const login = (item.data.login || "").toLowerCase();
  if (login && ignored.has(login)) return false;
  if (item.kind === "event") return settings.events[item.data.group] !== false;
  if (settings.hideCommands && item.data.text.trimStart().startsWith("!")) return false;
  return true;
}

function buildItem(item) {
  const node = item.kind === "event" ? buildEvent(item.data) : buildMessage(item.data);
  // Kept on the node so a settings change can rebuild what is already on screen, and so the sweeper
  // can ask how old a line is without parsing anything back out of the markup.
  node.item = item;
  return node;
}

function buildMessage(message) {
  const node = document.createElement("article");
  node.className = "msg";
  node.dataset.id = message.id;
  node.dataset.userId = message.userId || "";
  node.dataset.login = message.login || "";

  if (settings.showReplies && message.reply) node.appendChild(buildReply(message.reply));

  const head = document.createElement("span");
  head.className = "msg-head";

  if (settings.showTimestamps) {
    const time = document.createElement("span");
    time.className = "msg-time";
    time.textContent = clockOf(message.sentAt);
    head.appendChild(time);
  }

  if (settings.showBadges) appendBadges(head, message.badges);

  const name = document.createElement("span");
  name.className = "msg-name";
  name.textContent = message.displayName;
  if (settings.useTwitchNameColors && message.color) name.style.color = message.color;
  head.appendChild(name);

  // A first-time chatter is the one marker that is for the viewers as much as for the streamer:
  // it is what makes a chat greet somebody.
  if (message.isFirstMessage) head.appendChild(tag("ny", "new"));
  if (message.bits) head.appendChild(tag(`${message.bits} bits`, "bits"));
  if (message.rewardLabel) head.appendChild(tag(`🔮 ${message.rewardLabel}`, "reward"));
  // A message effect is an animation we do not reproduce, so it is always a marker; a gigantified
  // emote speaks for itself when it is shown big, and only needs saying when it is not.
  if (message.messageEffect) head.appendChild(tag("⚡ effekt", "powerup"));
  if (message.giantEmote != null && !settings.giantEmotes) head.appendChild(tag("⚡ förstorad", "powerup"));

  node.appendChild(head);
  node.appendChild(renderBody(message, settings));
  return node;
}

/* One quiet row saying this is an answer. A span rather than the dock's button: there is nothing to
   jump to on a stream, and nothing on this page is ever pressed. */
function buildReply(reply) {
  const line = document.createElement("span");
  line.className = "msg-reply";

  const mark = document.createElement("span");
  mark.textContent = "↩ ";

  const name = document.createElement("span");
  name.className = "msg-reply-name";
  name.textContent = reply.displayName;

  const quote = document.createElement("span");
  quote.textContent = reply.text;

  line.append(mark, name, quote);
  return line;
}

function buildEvent(chatEvent) {
  const node = document.createElement("article");
  node.className = "evt";
  node.dataset.id = chatEvent.id;
  node.dataset.kind = chatEvent.kind;
  if (chatEvent.announcementColor) node.dataset.color = chatEvent.announcementColor.toLowerCase();

  const head = document.createElement("span");
  head.className = "evt-head";

  const icon = document.createElement("span");
  icon.className = "evt-icon";
  icon.textContent = EVENT_ICONS[chatEvent.kind] || EVENT_ICONS.other;

  const headline = document.createElement("span");
  headline.className = "evt-headline";
  headline.textContent = chatEvent.headline;

  head.append(icon, headline);
  node.appendChild(head);
  // Subs and announcements often carry the chatter's own words, which are the part worth reading.
  if (chatEvent.message) {
    node.appendChild(renderBody({ text: chatEvent.message, emotes: chatEvent.emotes, isAction: false }, settings));
  }
  return node;
}

/* Rebuilds what is on screen. Anything a changed setting has since hidden – a newly ignored account,
   an event kind switched off – goes with it, because a switch that only applied to chat that has not
   happened yet would be half a switch. */
function rerender() {
  const items = [...chat.children].map((node) => node.item).filter((item) => item && shows(item));
  chat.replaceChildren(...items.map(buildItem));
}

const itemOf = (item) => (item.type === "event" ? { kind: "event", data: item.event } : { kind: "message", data: item.message });

/* A replay: the page opening, or the server redrawing the timeline after the streamer put an earlier
   sitting away. Never animated – these lines are not arriving, they are already here. */
function drawHistory(items) {
  if (!items) return;
  const oldest = Date.now() - replayWindow();
  const kept = items
    .map(itemOf)
    .filter((item) => shows(item) && timeOf(item) >= oldest)
    .slice(-settings.maxMessages);

  pending.length = 0;
  chat.replaceChildren(...kept.map(buildItem));
  startSweeping();
}

/* The made-up lines the app shows while nothing is connected, so the overlay can be aimed at
   something when it is dragged into place in OBS.

   They are not chat and neither rule for chat applies to them. The replay window is skipped because
   they all carry the moment the app started: half an hour into a session they are older than any
   window we would honestly allow, and reloading the source – which is most of what placing one
   consists of – would leave a blank page that only restarting the whole app could fill. The fade is
   skipped for the same reason from the other end: a preview that has quietly swept itself away is a
   preview of nothing. Real chat takes them down, and so does connecting to a channel. */
function drawSamples(items) {
  if (!items) return;
  const kept = items.map(itemOf).filter(shows).slice(-settings.maxMessages);
  for (const item of kept) item.sample = true;

  pending.length = 0;
  chat.replaceChildren(...kept.map(buildItem));
}

/* ------------------------------------------------------------------ leaving

   Two ways off the screen. Old age is swept once a second from a single timer – a timer per message
   would mean a dozen of them running at all times, each holding a node alive. Moderation is the
   other way, and it is immediate and unanimated: a line the streamer just deleted must not spend
   half a second sliding out in front of the viewers. */

let sweepHandle = 0;

function startSweeping() {
  if (sweepHandle || settings.fadeAfterSeconds <= 0 || chat.children.length === 0) return;
  sweepHandle = setInterval(sweep, 1000);
}

function stopSweeping() {
  if (!sweepHandle) return;
  clearInterval(sweepHandle);
  sweepHandle = 0;
}

function sweep() {
  const cutoff = Date.now() - settings.fadeAfterSeconds * 1000;
  // A snapshot: with animations off, retiring a line removes it there and then, and a live
  // collection would hand us the next node twice and skip one.
  for (const node of [...chat.children]) {
    if (node.dataset.leaving === "true" || !node.item || node.item.sample) continue;
    if (timeOf(node.item) <= cutoff) retire(node);
  }
  // Nothing left that can ever age – an empty column, or one holding only the preview – so there is
  // nothing for the timer to come back for. Every arriving line starts it again.
  if (![...chat.children].some((node) => node.item && !node.item.sample)) stopSweeping();
}

function retire(node) {
  if (!settings.animate) { node.remove(); return; }
  node.dataset.leaving = "true";
  // Whichever comes first. The animation is the nice ending; the timer is what guarantees the node
  // actually goes when the page is in a background tab and no animation ever finishes.
  const drop = () => node.remove();
  node.addEventListener("animationend", drop, { once: true });
  setTimeout(drop, 1000);
}

/* The one thing that lands here is a Gigantify power-up arriving after the line it enlarged. The
   queue is searched first: during a burst the line it belongs to may not be drawn yet, and an
   update applied only to the page would be undone a frame later by the version still waiting. */
function applyMessageUpdate(message) {
  for (const item of pending) {
    if (item.kind === "message" && item.data.id === message.id) { item.data = message; return; }
  }
  for (const node of chat.children) {
    if (node.dataset.id !== message.id) continue;
    node.replaceWith(buildItem({ kind: "message", data: message }));
    return;
  }
}

function applyModeration(payload) {
  const hit = (item) => item.kind === "message" && affected(item.data, payload);
  for (const node of [...chat.children]) if (node.item && hit(node.item)) node.remove();
  // What has not been drawn yet must go too, or a deleted line would appear a frame after it died.
  for (let i = pending.length - 1; i >= 0; i--) if (hit(pending[i])) pending.splice(i, 1);
}

/* Moderation reaches messages only. A sub or a raid is not something a timeout takes back. */
function affected(message, payload) {
  if (payload.kind === "chatCleared") return true;
  if (payload.kind === "messageDeleted") return message.id === payload.messageId;
  return (payload.userId && message.userId === payload.userId)
    || (payload.login && (message.login || "").toLowerCase() === payload.login.toLowerCase());
}

/* ------------------------------------------------------------------ appearance */

function applySettings(next) {
  if (!next) return;
  const first = settings.__loaded !== true;
  settings = { ...next, __loaded: true };
  ignored = loginsIn(next.ignoredAccounts);

  const root = document.documentElement.style;
  root.setProperty("--font", `"${settings.fontFamily}", Verdana, sans-serif`);
  root.setProperty("--size", `${settings.fontSize}px`);
  root.setProperty("--lh", String(settings.lineHeight));
  root.setProperty("--gap", `${settings.messageGap}px`);
  root.setProperty("--plate", String(settings.messageBackgroundOpacity));

  const body = document.body.dataset;
  body.outline = String(settings.textOutline);
  body.animate = String(settings.animate);
  body.nameline = String(settings.nameOnOwnLine);
  body.top = String(settings.newestOnTop);

  // Badges, timestamps, link chips and the reply line are baked into the markup, so changing one of
  // them from the app has to rebuild what is on screen rather than only swap a variable.
  if (!first) {
    rerender();
    while (chat.children.length > settings.maxMessages) chat.firstElementChild.remove();
  }
  if (settings.fadeAfterSeconds > 0) startSweeping(); else stopSweeping();
}

applySettings(settings);
connect();
