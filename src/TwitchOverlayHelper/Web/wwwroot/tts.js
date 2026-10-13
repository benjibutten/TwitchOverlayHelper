"use strict";

/* The readings: the sound and, when the streamer has turned it on, the card that says whose words
   they are. A browser source's audio goes into the stream by itself, while anything the app plays
   on the desktop is heard by the streamer alone.

   The card is drawn here rather than on the chat overlay because it starts and stops with the
   clip.

   Every clip is acknowledged when it ends. The app holds the next reading until then, and on the
   channel points route the answer is the evidence the redemption was delivered, so a clip that
   never played has to say so. The card has no part in that: a drawing that failed must never turn
   into a refund. */

const KEY = new URLSearchParams(location.search).get("key") || "";
const state = document.getElementById("state");
const stage = document.getElementById("stage");
const card = document.getElementById("card");
const label = document.getElementById("label");
const wave = document.getElementById("wave");
// Not "name": that is a property of the window itself.
const who = document.getElementById("name");
const cost = document.getElementById("cost");
const message = document.getElementById("message");

/* One element reused for every clip, so two readings can never overlap. */
const player = new Audio();
player.preload = "auto";

/* The same defaults as TtsWidgetSettings on the app side. Off, so the source stays 1×1 and silent
   until the streamer turns the card on. */
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
    // A clip left mid-play when the socket dropped is stopped. The app is not told, since the report
    // would go over the closed socket; it sees this page leave and settles the reading itself.
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

/* The card */

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

  // Switched off while a card was on screen: taken down at once rather than with the clip.
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

/* Hides the card after the given pause. The pause covers the gap between two readings in a
   queue, so the card does not blink out and back in. */
function conceal(delay) {
  clearTimeout(hideHandle);
  hideHandle = 0;
  if (!delay) { card.dataset.visible = "false"; return; }
  hideHandle = setTimeout(() => { card.dataset.visible = "false"; hideHandle = 0; }, delay);
}

/* A card with nothing playing, for the preview button in the settings. Nothing is acknowledged,
   and it is only shown while the page is idle. */
function preview(info) {
  if (current) return;
  show(info);
  conceal(Math.max(500, info.milliseconds || 4000));
}

/* The sound */

function play(clip) {
  /* A clip arriving while another is playing replaces it, as there is one audio element. The
     replaced clip is reported as failed so the app stops waiting for it and the queue does not
     stall; on the channel points route that refunds the viewer. */
  if (current && current !== clip.id) {
    player.pause();
    report(current, false, "replaced");
  }

  current = clip.id;
  state.dataset.playing = "true";
  // Name and message only travel with the clip when there is a card to draw them on.
  if (clip.viewer !== undefined && clip.viewer !== null) show(clip);
  player.src = clip.url;
  player.volume = Math.min(1, Math.max(0, clip.volume));
  player.play().catch((error) => report(clip.id, false, error && error.name));
}

function stop() {
  if (!current) return;
  player.pause();
  // Reported as played rather than failed: stopping is the streamer's decision, not a fault to
  // refund.
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
/* Anything the browser could not play. Reported, so the queue does not wait for a clip that is
   never coming. */
player.addEventListener("error", () => report(current, false, "error"));

connect();
