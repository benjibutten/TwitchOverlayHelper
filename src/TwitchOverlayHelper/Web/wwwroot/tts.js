"use strict";

/* The readings, as sound. This page draws nothing – it is a browser source added to the scene so
   that OBS has somewhere to mix them: a browser source's audio goes into the stream by itself,
   while anything the app plays on the desktop is heard by the streamer alone unless they capture
   their whole desktop, which almost nobody wants to do.

   Every clip is acknowledged when it ends. That answer is what lets the app hold the next reading
   until this one has finished, and on the channel points route it is the evidence the redemption
   was actually delivered – so a clip that never played has to say so rather than stay quiet. */

const KEY = new URLSearchParams(location.search).get("key") || "";
const state = document.getElementById("state");

/* One element reused for every clip. A fresh <audio> per reading would leave the previous one alive
   long enough to overlap the next, which is the one thing the queue exists to prevent. */
const player = new Audio();
player.preload = "auto";

let socket = null;
let reconnectDelay = 1000;
let current = "";

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
  if (frame.type === "ttsPlay") { play(frame.payload); return; }
  if (frame.type === "ttsStop") { stop(); }
}

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
  if (socket && socket.readyState === WebSocket.OPEN) {
    socket.send(JSON.stringify({ type: played ? "ttsPlayed" : "ttsFailed", id, note: note || "" }));
  }
}

player.addEventListener("ended", () => report(current, true, "ended"));
/* Anything the browser could not play: a fetch that failed, a codec it refused, an address the app
   has already forgotten. Silence would leave the queue waiting for a clip that is never coming. */
player.addEventListener("error", () => report(current, false, "error"));

connect();
