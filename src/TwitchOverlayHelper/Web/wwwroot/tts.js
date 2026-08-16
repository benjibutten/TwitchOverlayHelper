"use strict";

/* The readings: the sound, and – when the streamer has turned it on – the card that says whose words
   they are. This is a browser source added to the scene so that OBS has somewhere to mix them: a
   browser source's audio goes into the stream by itself, while anything the app plays on the desktop
   is heard by the streamer alone unless they capture their whole desktop, which almost nobody wants
   to do.

   The card is drawn here rather than on the chat overlay for the same reason the sound is: it is the
   same event, it starts and stops with the clip, and a second source would have to be told separately
   – which is a second thing to place in OBS and a second thing that can fall out of step.

   Every clip is acknowledged when it ends. That answer is what lets the app hold the next reading
   until this one has finished, and on the channel points route it is the evidence the redemption was
   actually delivered – so a clip that never played has to say so rather than stay quiet. The card
   deliberately has no part in that: whether a reading was delivered is a question about the sound,
   and a drawing that failed must never turn into somebody's points coming back. */

const KEY = new URLSearchParams(location.search).get("key") || "";
const state = document.getElementById("state");
const stage = document.getElementById("stage");
const card = document.getElementById("card");
const label = document.getElementById("label");
const wave = document.getElementById("wave");
// Not "name": that one is a property of the window itself, and a page that shadows it is a page
// that behaves differently depending on which browser is drawing it.
const who = document.getElementById("name");
const cost = document.getElementById("cost");
const message = document.getElementById("message");

/* One element reused for every clip. A fresh <audio> per reading would leave the previous one alive
   long enough to overlap the next, which is the one thing the queue exists to prevent. */
const player = new Audio();
player.preload = "auto";

/* The same defaults as TtsWidgetSettings on the app side. Off, so a source that has always been 1×1
   and silent stays that way until somebody says otherwise. */
let widget = {
  enabled: false, position: "bottom-center", offsetX: 64, offsetY: 64, width: 720,
  fontSize: 26, fontFamily: "Verdana", accentColor: "#A970FF", backgroundOpacity: 0.72,
  cornerRadius: 16, label: "LÄSER UPP", showName: true, showText: true, showCost: false,
  showWave: true, textOutline: true, animation: "slide", lingerMilliseconds: 900,
};

let socket = null;
let reconnectDelay = 1000;
let current = "";
/* The pending hide. Held so a reading that starts while the previous card is still lingering keeps
   the card up instead of letting the old timer take it down mid-sentence. */
let hideHandle = 0;

function connect() {
  socket = new WebSocket(`ws://${location.host}/ws?key=${encodeURIComponent(KEY)}&view=tts`);

  socket.onopen = () => { reconnectDelay = 1000; state.dataset.live = "true"; };
  socket.onmessage = (event) => handle(JSON.parse(event.data));
  socket.onclose = () => {
    state.dataset.live = "false";
    // A clip left mid-play when the socket dropped is stopped: a voice reading on into a reconnect
    // belongs to a decision made a while ago. The app is not told – the report would go over the
    // socket that just closed – and does not need to be: it sees this page leave and settles
    // whatever it was waiting on itself.
    stop();
    setTimeout(connect, reconnectDelay);
    reconnectDelay = Math.min(15000, reconnectDelay * 1.7);
  };
  socket.onerror = () => socket.close();
}

function handle(frame) {
  if (frame.type === "hello") { applyWidget(frame.widget); return; }
  if (frame.type === "ttsWidget") { applyWidget(frame.payload); return; }
  if (frame.type === "ttsPlay") { play(frame.payload); return; }
  if (frame.type === "ttsPreview") { preview(frame.payload); return; }
  if (frame.type === "ttsStop") { stop(); }
}

/* ------------------------------------------------------------------ the card */

function applyWidget(next) {
  if (!next) return;
  widget = Object.assign({}, widget, next);

  const style = document.documentElement.style;
  style.setProperty("--font", `${widget.fontFamily}, sans-serif`);
  style.setProperty("--size", `${widget.fontSize}px`);
  style.setProperty("--accent", widget.accentColor);
  style.setProperty("--plate", widget.backgroundOpacity);
  style.setProperty("--radius", `${widget.cornerRadius}px`);
  style.setProperty("--width", `${widget.width}px`);
  style.setProperty("--offset-x", `${widget.offsetX}px`);
  style.setProperty("--offset-y", `${widget.offsetY}px`);

  document.body.dataset.widget = String(widget.enabled);
  document.body.dataset.animation = widget.animation;
  document.body.dataset.outline = String(widget.textOutline);
  document.body.dataset.plate = String(widget.backgroundOpacity > 0.02);
  stage.dataset.position = widget.position;

  // Switched off while something was on screen. Taken down at once rather than left to the clip that
  // is playing: the streamer just said they did not want it in the picture.
  if (!widget.enabled) conceal();
}

/* Fills the card in and brings it up. Everything goes in as text, never as markup: this is a
   stranger's message on its way onto a broadcast. */
function show(info) {
  if (!widget.enabled) return;
  clearTimeout(hideHandle);
  hideHandle = 0;

  label.textContent = widget.label;
  who.textContent = widget.showName ? (info.viewer || "") : "";
  message.textContent = widget.showText ? (info.text || "") : "";
  cost.textContent = widget.showCost && info.cost > 0
    ? `${info.cost} ${info.source === "powerUp" ? "bits" : "poäng"}`
    : "";
  wave.hidden = !widget.showWave;
  card.dataset.visible = "true";
}

/* Lets the card go, after the pause the settings ask for. The pause is what covers the gap between
   two readings in a queue: without it the card would blink out and straight back in. */
function conceal(delay) {
  clearTimeout(hideHandle);
  hideHandle = 0;
  if (!delay) { card.dataset.visible = "false"; return; }
  hideHandle = setTimeout(() => { card.dataset.visible = "false"; hideHandle = 0; }, delay);
}

/* A card with nothing playing, for the preview button in the settings. It answers to nobody: no
   acknowledgement is sent and no money hangs on it, so it is only ever shown when the page is idle –
   a real reading owns the card for as long as it lasts. */
function preview(info) {
  if (current) return;
  show(info);
  conceal(Math.max(500, info.milliseconds || 4000));
}

/* ------------------------------------------------------------------ the sound */

function play(clip) {
  /* A clip arriving while another is playing means the app and this page disagree about what is
     going on. The previous one is dropped rather than layered – there is one audio element – but it
     has to be reported on the way out, or the app sits waiting on an acknowledgement for a clip
     that no longer exists and the whole queue behind it stalls until the timeout. Reported as
     failed, because it did not finish: on the channel points route that pays the viewer back rather
     than charging them for a reading that was cut off by something else. */
  if (current && current !== clip.id) {
    player.pause();
    report(current, false, "replaced");
  }

  current = clip.id;
  state.dataset.playing = "true";
  // Name and message only travel with the clip when there is a card to draw them on, so their
  // absence is the app saying there is nothing to show rather than something having gone missing.
  if (clip.viewer !== undefined && clip.viewer !== null) show(clip);
  player.src = clip.url;
  player.volume = Math.min(1, Math.max(0, clip.volume));
  player.play().catch((error) => report(clip.id, false, error && error.name));
}

function stop() {
  if (!current) return;
  player.pause();
  // Reported as played rather than failed: the viewers heard what there was to hear, and stopping
  // is the streamer's decision, not a fault the viewer should be paid back for.
  report(current, true, "stopped");
}

function report(id, played, note) {
  if (id !== current) return;
  current = "";
  state.dataset.playing = "false";
  conceal(widget.lingerMilliseconds);
  if (socket && socket.readyState === WebSocket.OPEN) {
    socket.send(JSON.stringify({ type: played ? "ttsPlayed" : "ttsFailed", id, note: note || "" }));
  }
}

player.addEventListener("ended", () => report(current, true, "ended"));
/* Anything the browser could not play: a fetch that failed, a codec it refused, an address the app
   has already forgotten. Silence would leave the queue waiting for a clip that is never coming. */
player.addEventListener("error", () => report(current, false, "error"));

connect();
