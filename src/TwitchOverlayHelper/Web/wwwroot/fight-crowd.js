"use strict";

/* The viewers taking part, one panel on each side of the ring: the left one for whoever cheers on
   player 1, the right one for player 2. Each viewer has a row with their name in their chat colour,
   how much they have done, and how long until each command works for them again – the same cooldown
   the app holds them to, counted down here from the moment the app let their command through.

   A viewer belongs to the corner they last helped. The list is newest first and scrolls on its own
   once it is longer than the panel, so a busy chat is still every name, not just the top ten. */

const CROWD_PANEL_W = 300;
const CROWD_PANEL_EDGE = CROWD_PANEL_W + 140; // how far from the edge the fighters keep
const CROWD_TOP = 300, CROWD_BOTTOM = 840;
const CROWD_ROW = 58;
const CROWD_FORGET = 5 * 60; // seconds of quiet before a viewer leaves the panel

/* The panels are only there when the chat can take part and the streamer wants them. */
function supportersShown() {
  return !!(setup.match.showSupporters && setup.commands.enabled);
}

const crowd = {
  viewers: new Map(), // lower-case name → entry

  note(player, kind, viewer, color) {
    if (!viewer) return;
    const key = viewer.toLowerCase();
    const now = performance.now() / 1000;
    let v = this.viewers.get(key);
    if (!v) {
      v = { viewer, color, cheers: 0, heals: 0, cheerAt: -1e9, healAt: -1e9, last: now, side: player };
      this.viewers.set(key, v);
    }
    v.viewer = viewer;
    v.color = color;
    v.side = player;
    v.last = now;
    v.flash = 1;
    if (kind === "heal") { v.heals++; v.healAt = now; }
    else { v.cheers++; v.cheerAt = now; }
  },

  /* A new match keeps whoever is still waiting out a cooldown or was busy a minute ago, and counts
     them from zero: the panel is about this match, the cooldown is about the viewer. */
  newMatch() {
    const now = performance.now() / 1000;
    const cooldown = setup.commands.cooldown || 0;
    for (const [key, v] of this.viewers) {
      const busy = now - Math.max(v.cheerAt, v.healAt) < Math.max(cooldown, 60);
      if (!busy) this.viewers.delete(key);
      else { v.cheers = 0; v.heals = 0; }
    }
  },

  side(player) {
    const now = performance.now() / 1000;
    const list = [];
    for (const [key, v] of this.viewers) {
      if (now - v.last > CROWD_FORGET) { this.viewers.delete(key); continue; }
      if (v.side === player) list.push(v);
    }
    return list.sort((a, b) => b.last - a.last);
  },

  /* How many are helping a corner right now, for what one viewer's help is worth there. The panel
     keeps a name for minutes after the last command; this counts only the ones still in it – two
     cooldowns, and never longer than the panel remembers – so a corner the chat has drifted away
     from is a small corner again. */
  helping(player) {
    const now = performance.now() / 1000;
    const recent = Math.min(CROWD_FORGET, Math.max(2 * (setup.commands.cooldown || 0), 60));
    let count = 0;
    for (const v of this.viewers.values())
      if (v.side === player && now - Math.max(v.cheerAt, v.healAt) < recent) count++;
    return count;
  },

  draw(time, dt) {
    if (!supportersShown()) return;
    for (const v of this.viewers.values()) v.flash = Math.max(0, (v.flash || 0) - dt * 2);
    this.panel(1, 16, time);
    this.panel(2, W - 16 - CROWD_PANEL_W, time);
  },

  panel(player, x, time) {
    const list = this.side(player);
    const fighter = player === 1 ? p1 : p2;
    const accent = player === 1 ? "#2f7bff" : "#ff3b5c";
    const w = CROWD_PANEL_W;

    // Header: who this side is cheering for.
    ctx.save();
    roundRect(x, CROWD_TOP - 54, w, 46, 10);
    ctx.fillStyle = accent;
    ctx.fill();
    ctx.lineWidth = 5;
    ctx.strokeStyle = "#111";
    ctx.stroke();
    const title = `HEJAR PÅ ${fighter.name || `P${player}`}`;
    ctx.font = `26px ${FONT}`;
    inkText(title, x + w / 2, CROWD_TOP - 30, Math.min(26, 26 * (w - 24) / Math.max(1, ctx.measureText(title).width)), "#fff", "#111", 5);

    const h = CROWD_BOTTOM - CROWD_TOP;
    roundRect(x, CROWD_TOP, w, h, 12);
    ctx.fillStyle = "rgba(10,10,16,0.62)";
    ctx.fill();
    ctx.lineWidth = 4;
    ctx.strokeStyle = "rgba(0,0,0,0.8)";
    ctx.stroke();

    if (!list.length) {
      const c = setup.commands;
      inkText("Ingen ännu", x + w / 2, CROWD_TOP + 44, 28, "rgba(255,255,255,0.75)", "#111", 5);
      inkText(`${c.cheer} ${player}`, x + w / 2, CROWD_TOP + 92, 30, "#ffd43b", "#111", 6);
      inkText(`${c.heal} ${player}`, x + w / 2, CROWD_TOP + 132, 30, "#6dff9c", "#111", 6);
      ctx.restore();
      return;
    }

    ctx.beginPath();
    ctx.rect(x + 4, CROWD_TOP + 4, w - 8, h - 8);
    ctx.clip();

    const total = list.length * CROWD_ROW;
    const visible = h - 12;
    if (total <= visible) {
      list.forEach((v, i) => this.row(v, x, CROWD_TOP + 8 + i * CROWD_ROW, w, time));
    } else {
      // Longer than the panel: the list runs past on a loop, with a gap so the start is visible.
      const loop = total + CROWD_ROW;
      const offset = (time * 28) % loop;
      for (let pass = 0; pass < 2; pass++)
        list.forEach((v, i) => {
          const y = CROWD_TOP + 8 + i * CROWD_ROW - offset + pass * loop;
          if (y > CROWD_TOP - CROWD_ROW && y < CROWD_BOTTOM) this.row(v, x, y, w, time);
        });
    }
    ctx.restore();
  },

  row(v, x, y, w, time) {
    const now = performance.now() / 1000;
    const cooldown = setup.commands.cooldown || 0;
    if (v.flash > 0) {
      roundRect(x + 8, y, w - 16, CROWD_ROW - 6, 8);
      ctx.fillStyle = `rgba(255,212,59,${v.flash * 0.35})`;
      ctx.fill();
    }

    // Name, shrunk to fit rather than cut – it is the one thing the viewer looks for.
    ctx.font = `26px ${FONT}`;
    const nameSize = Math.min(26, 26 * 170 / Math.max(1, ctx.measureText(v.viewer).width));
    inkText(v.viewer, x + 16, y + 17, nameSize, v.color, "#111", 5, "left");
    const tally = [v.cheers ? `★${v.cheers}` : "", v.heals ? `+${v.heals}` : ""].filter(Boolean).join(" ");
    inkText(tally, x + w - 14, y + 17, 20, "rgba(255,255,255,0.85)", "#111", 4, "right");

    // One bar per command: empty while it cools down, full and green once it works again.
    this.cooldownBar(x + 16, y + 36, (w - 44) / 2, "★", now - v.cheerAt, cooldown, "#ffd43b");
    this.cooldownBar(x + 28 + (w - 44) / 2, y + 36, (w - 44) / 2, "+", now - v.healAt, cooldown, "#6dff9c");
  },

  cooldownBar(x, y, w, icon, elapsed, cooldown, color) {
    const left = Math.max(0, cooldown - elapsed);
    const k = cooldown > 0 ? 1 - left / cooldown : 1;
    inkText(icon, x + 8, y + 6, 18, color, "#111", 4);
    const bx = x + 20, bw = w - 20;
    roundRect(bx, y, bw, 12, 6);
    ctx.fillStyle = "rgba(255,255,255,0.12)";
    ctx.fill();
    if (k > 0) {
      roundRect(bx, y, Math.max(12, bw * k), 12, 6);
      ctx.fillStyle = left > 0 ? "rgba(255,255,255,0.55)" : color;
      ctx.fill();
    }
    if (left > 0) inkText(`${Math.ceil(left)}s`, bx + bw / 2, y + 6, 15, "#fff", "#111", 4);
  }
};

function roundRect(x, y, w, h, r) {
  ctx.beginPath();
  ctx.moveTo(x + r, y);
  ctx.arcTo(x + w, y, x + w, y + h, r);
  ctx.arcTo(x + w, y + h, x, y + h, r);
  ctx.arcTo(x, y + h, x, y, r);
  ctx.arcTo(x, y, x + w, y, r);
  ctx.closePath();
}
