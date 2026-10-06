"use strict";

/* The character select between matches. Every fighter on the roster gets a tile; the chat votes with
   "!p1 my2" – a corner, a fighter, and the digit for an outfit – and when the time is up each corner
   goes to whatever got the most votes there. One vote per viewer per corner: voting again moves it.
   A corner nobody voted for keeps the fighter it had.

   The roster is whatever the app has, so the grid is laid out from the count rather than for a fixed
   number: tiles grow to fill the space when there are few and shrink when there are many, and past
   the point where they would be too small to read, the grid turns into pages that flip by themselves. */

const SELECT_GRID = { x: 520, y: 210, w: 880, h: 620 };
const SELECT_MIN_TILE = 136; // narrower than this and a name is no longer readable on stream
const SELECT_PAGE_SECONDS = 6;

/* The part of a fighter's idle frame a tile shows: head and shoulders down to the hips. */
const PORTRAIT = { x: CELL_W / 2 - 165, y: 40, w: 330, h: 380 };

const select = {
  active: false,
  roster: [],
  votes: { 1: new Map(), 2: new Map() }, // voter key → { key, fighter, outfit, viewer, color, at }
  fallback: { 1: null, 2: null },        // { id, outfit } a corner keeps when nobody votes for it
  duration: 30,
  t: 0,
  feed: [],                              // the latest votes, shown under each corner's preview
  bump: new Map(),                       // tile id → glow when a vote just landed on it

  begin(roster, seconds, fallback) {
    this.active = true;
    this.roster = roster.slice();
    this.votes = { 1: new Map(), 2: new Map() };
    this.fallback = fallback;
    this.duration = seconds;
    this.t = 0;
    this.feed = [];
    this.bump.clear();
  },

  /* The roster changed while the vote is open – a fighter added in the app, say. Votes for fighters
     that no longer exist go with them. */
  setRoster(roster) {
    this.roster = roster.slice();
    for (const side of [1, 2])
      for (const [voter, v] of this.votes[side])
        if (!this.find(v.fighter)) this.votes[side].delete(voter);
  },

  find(id) { return this.roster.find(f => f.id === id) || null; },

  vote(p) {
    if (!this.active || (p.player !== 1 && p.player !== 2)) return;
    const fighter = this.find(p.fighter);
    if (!fighter) return;
    const outfit = (fighter.outfits || []).some(o => o.code === p.outfit) ? p.outfit : 1;
    const viewer = String(p.viewer || "").slice(0, 25);
    const color = safeColor(p.color);
    this.votes[p.player].set(p.voter || viewer, { key: `${fighter.id}:${outfit}`, fighter: fighter.id, outfit, viewer, color, at: this.t });
    this.feed.unshift({ player: p.player, viewer, color, fighter: fighter.name, outfit, at: performance.now() / 1000 });
    this.feed.length = Math.min(this.feed.length, 12);
    this.bump.set(fighter.id, 1);
  },

  /* Votes per fighter-and-outfit for one corner, most first; a tie goes to whoever got there first. */
  tally(player) {
    const counts = new Map();
    for (const v of this.votes[player].values()) {
      const c = counts.get(v.key) || { fighter: v.fighter, outfit: v.outfit, count: 0, first: v.at };
      c.count++;
      c.first = Math.min(c.first, v.at);
      counts.set(v.key, c);
    }
    return [...counts.values()].sort((a, b) => b.count - a.count || a.first - b.first);
  },

  /* Who would get the corner if the vote ended now. */
  leader(player) {
    const top = this.tally(player)[0];
    if (top && this.find(top.fighter)) return { fighter: this.find(top.fighter), outfit: top.outfit, votes: top.count };
    const fb = this.fallback[player];
    const fighter = (fb && this.find(fb.id)) || this.roster[(player - 1) % Math.max(1, this.roster.length)] || null;
    return fighter ? { fighter, outfit: fb && fb.id === fighter.id ? fb.outfit : 1, votes: 0 } : null;
  },

  votesFor(player, fighterId) {
    let n = 0;
    for (const v of this.votes[player].values()) if (v.fighter === fighterId) n++;
    return n;
  },

  get left() { return Math.max(0, this.duration - this.t); },

  update(dt) {
    if (!this.active) return false;
    this.t += dt;
    for (const [id, k] of this.bump) {
      const next = k - dt * 1.5;
      if (next <= 0) this.bump.delete(id); else this.bump.set(id, next);
    }
    return this.t >= this.duration;
  },

  finish() {
    this.active = false;
    return { 1: this.leader(1), 2: this.leader(2) };
  },

  /* ---------------------------------------------------------------- drawing */

  layout() {
    const n = Math.max(1, this.roster.length);
    const g = SELECT_GRID, gap = 14, aspect = 0.82; // tile width / height
    let best = null;
    for (let cols = 1; cols <= n; cols++) {
      const rows = Math.ceil(n / cols);
      const tw = Math.min((g.w - gap * (cols - 1)) / cols, ((g.h - gap * (rows - 1)) / rows) * aspect);
      if (!best || tw > best.tw) best = { cols, rows, tw };
    }
    if (best.tw >= SELECT_MIN_TILE) return { ...best, th: best.tw / aspect, perPage: n, pages: 1, gap };
    // Too many to fit at a readable size: as many per page as fit at the smallest size.
    const cols = Math.max(1, Math.floor((g.w + gap) / (SELECT_MIN_TILE + gap)));
    const th = SELECT_MIN_TILE / aspect;
    const rows = Math.max(1, Math.floor((g.h + gap) / (th + gap)));
    const tw = Math.min((g.w - gap * (cols - 1)) / cols, ((g.h - gap * (rows - 1)) / rows) * aspect);
    return { cols, rows, tw, th: tw / aspect, perPage: cols * rows, pages: Math.ceil(n / (cols * rows)), gap };
  },

  draw(time) {
    // The arena stays behind a dark veil, so this reads as a screen of its own.
    ctx.fillStyle = "rgba(8,8,14,0.72)";
    ctx.fillRect(0, 0, W, H);

    const pop = Math.min(1, this.t / 0.5);
    ctx.save();
    ctx.translate(W / 2, 86);
    ctx.scale(0.6 + pop * 0.4, 0.6 + pop * 0.4);
    inkText("VÄLJ KÄMPAR", 6, 8, 104, "rgba(0,0,0,0.5)", "rgba(0,0,0,0.5)", 18);
    inkText("VÄLJ KÄMPAR", 0, 0, 104, "#ffd43b", "#111", 14);
    ctx.restore();

    const c = setup.commands;
    const example = this.roster.find(f => (f.outfits || []).length > 1) || this.roster[0];
    const exampleText = example ? `t.ex. ${c.pick1} ${example.id}${(example.outfits || []).length > 1 ? "2" : ""}` : "";
    inkText(`Rösta: ${c.pick1} namn  eller  ${c.pick2} namn – en siffra efter namnet väljer klädsel, ${exampleText}`,
      W / 2, 166, 30, "#fff", "#111", 6);

    this.drawTimer(time);
    this.drawGrid(time);
    this.drawCorner(1, time);
    this.drawCorner(2, time);
  },

  drawTimer(time) {
    const left = this.left;
    const x = W / 2 + 470, y = 86, r = 52;
    const urgent = left <= 5;
    ctx.beginPath();
    ctx.arc(x, y, r, 0, Math.PI * 2);
    ctx.fillStyle = "#1b1b20";
    ctx.fill();
    ctx.lineWidth = 7;
    ctx.strokeStyle = "#111";
    ctx.stroke();
    ctx.beginPath();
    ctx.arc(x, y, r - 9, -Math.PI / 2, -Math.PI / 2 + Math.PI * 2 * (left / this.duration));
    ctx.lineWidth = 10;
    ctx.strokeStyle = urgent ? "#ff3b2a" : "#ffd43b";
    ctx.stroke();
    const beat = urgent ? 1 + Math.max(0, Math.sin(time * Math.PI * 2)) * 0.15 : 1;
    inkText(String(Math.ceil(left)), x, y + 3, 50 * beat, urgent ? "#ff5a3d" : "#fff", "#111", 8);
  },

  drawGrid(time) {
    const L = this.layout();
    const page = L.pages > 1 ? Math.floor(this.t / SELECT_PAGE_SECONDS) % L.pages : 0;
    const items = this.roster.slice(page * L.perPage, (page + 1) * L.perPage);
    const cols = Math.min(L.cols, Math.max(1, items.length));
    const rows = Math.ceil(items.length / cols);
    const gridW = cols * L.tw + (cols - 1) * L.gap;
    const gridH = rows * L.th + (rows - 1) * L.gap;
    const x0 = SELECT_GRID.x + (SELECT_GRID.w - gridW) / 2;
    const y0 = SELECT_GRID.y + (SELECT_GRID.h - gridH) / 2;
    const lead1 = this.leader(1), lead2 = this.leader(2);

    items.forEach((f, i) => {
      const x = x0 + (i % cols) * (L.tw + L.gap);
      const y = y0 + Math.floor(i / cols) * (L.th + L.gap);
      this.drawTile(f, x, y, L.tw, L.th, lead1 && lead1.fighter.id === f.id, lead2 && lead2.fighter.id === f.id, time);
    });

    if (L.pages > 1) {
      // Which page this is, and that more are coming.
      const k = (this.t % SELECT_PAGE_SECONDS) / SELECT_PAGE_SECONDS;
      const y = SELECT_GRID.y + SELECT_GRID.h + 22;
      inkText(`Sida ${page + 1} av ${L.pages}`, W / 2, y, 26, "#fff", "#111", 5);
      roundRect(W / 2 - 120, y + 20, 240, 8, 4);
      ctx.fillStyle = "rgba(255,255,255,0.2)";
      ctx.fill();
      roundRect(W / 2 - 120, y + 20, 240 * k, 8, 4);
      ctx.fillStyle = "#ffd43b";
      ctx.fill();
    }
  },

  drawTile(f, x, y, w, h, lead1, lead2, time) {
    const bump = this.bump.get(f.id) || 0;
    ctx.save();
    if (bump) {
      ctx.translate(x + w / 2, y + h / 2);
      ctx.scale(1 + bump * 0.06, 1 + bump * 0.06);
      ctx.translate(-(x + w / 2), -(y + h / 2));
    }

    roundRect(x, y, w, h, 12);
    const g = ctx.createLinearGradient(0, y, 0, y + h);
    g.addColorStop(0, "#2a2a36");
    g.addColorStop(1, "#14141c");
    ctx.fillStyle = g;
    ctx.fill();

    // The portrait, clipped to the tile above the name strip.
    const nameH = Math.max(34, h * 0.2);
    const img = cachedImage((f.outfits && f.outfits[0] && f.outfits[0].sprite) || f.sprite);
    if (imgReady(img)) {
      ctx.save();
      roundRect(x + 4, y + 4, w - 8, h - nameH - 4, 9);
      ctx.clip();
      const ph = h - nameH - 4, pw = ph * PORTRAIT.w / PORTRAIT.h;
      ctx.drawImage(img, PORTRAIT.x, PORTRAIT.y, PORTRAIT.w, PORTRAIT.h, x + (w - pw) / 2, y + 4, pw, ph);
      ctx.restore();
    }

    // Name and what to type for it; more than one outfit shows their codes.
    const fs = Math.max(14, Math.min(30, w * 0.17));
    ctx.font = `${fs}px ${FONT}`;
    const name = f.name.toUpperCase();
    const size = Math.min(fs, fs * (w - 12) / Math.max(1, ctx.measureText(name).width));
    inkText(name, x + w / 2, y + h - nameH * 0.62, size, "#fff", "#111", Math.max(3, size * 0.16));
    // Under the name: on a roomy tile the id as well (what to type when the name is awkward), on a
    // small one only the outfits – the name alone can always be typed, "!p1 dj jenni" included.
    const outfits = (f.outfits || []).length > 1 ? `klädsel ${(f.outfits || []).map(o => o.code).join("/")}` : "";
    const codes = w >= 170 ? [f.id, outfits].filter(Boolean).join(" – ") : outfits;
    if (codes) {
      const small = Math.max(14, fs * 0.55);
      ctx.font = `${small}px ${FONT}`;
      inkText(codes, x + w / 2, y + h - nameH * 0.18, Math.min(small, small * (w - 12) / Math.max(1, ctx.measureText(codes).width)),
        outfits && !codes.startsWith(f.id) ? "#ffd43b" : "rgba(255,255,255,0.75)", "#111", 3);
    }

    // Frame: blue for player 1's leader, red for player 2's, both when they want the same fighter.
    roundRect(x, y, w, h, 12);
    ctx.lineWidth = lead1 || lead2 ? 7 : 4;
    if (lead1 && lead2) {
      const both = ctx.createLinearGradient(x, 0, x + w, 0);
      both.addColorStop(0.45, "#2f7bff");
      both.addColorStop(0.55, "#ff3b5c");
      ctx.strokeStyle = both;
    } else {
      ctx.strokeStyle = lead1 ? "#2f7bff" : lead2 ? "#ff3b5c" : "#0b0b10";
    }
    ctx.stroke();

    // Vote counts in the top corners, in the corner's colour.
    const v1 = this.votesFor(1, f.id), v2 = this.votesFor(2, f.id);
    const badge = Math.max(16, Math.min(28, w * 0.16));
    if (v1) this.voteBadge(x + badge * 0.9, y + badge * 0.9, badge, v1, "#2f7bff");
    if (v2) this.voteBadge(x + w - badge * 0.9, y + badge * 0.9, badge, v2, "#ff3b5c");
    ctx.restore();
  },

  voteBadge(x, y, r, n, color) {
    ctx.beginPath();
    ctx.arc(x, y, r * 0.8, 0, Math.PI * 2);
    ctx.fillStyle = color;
    ctx.fill();
    ctx.lineWidth = 4;
    ctx.strokeStyle = "#111";
    ctx.stroke();
    inkText(String(n), x, y + 1, r, "#fff", "#111", 4);
  },

  /* A corner's current leader, full figure, beside the grid – blue on the left, red on the right. */
  drawCorner(player, time) {
    const lead = this.leader(player);
    const left = player === 1;
    const cx = left ? 260 : W - 260;
    const accent = left ? "#2f7bff" : "#ff3b5c";

    inkText(`P${player}`, cx, 250, 64, accent, "#111", 10);
    if (!lead) return;
    const f = lead.fighter;
    const outfit = (f.outfits || []).find(o => o.code === lead.outfit) || (f.outfits || [])[0];
    const img = cachedImage(outfit ? outfit.sprite : f.sprite);

    // A soft spotlight on the floor, then the fighter breathing in its stance.
    const ground = 760;
    const glow = ctx.createRadialGradient(cx, ground, 10, cx, ground, 230);
    glow.addColorStop(0, left ? "rgba(47,123,255,0.45)" : "rgba(255,59,92,0.45)");
    glow.addColorStop(1, "rgba(0,0,0,0)");
    ctx.fillStyle = glow;
    ctx.fillRect(cx - 240, ground - 240, 480, 300);
    if (imgReady(img)) {
      const frame = Math.floor(time / 0.42) % 2 ? FRAME.idle2 : FRAME.idle;
      const s = 0.86 * (f.scale || 1);
      ctx.save();
      ctx.translate(cx, ground);
      ctx.scale(left ? s : -s, s);
      ctx.drawImage(img, frame * CELL_W, 0, CELL_W, CELL_H, -CELL_W / 2, -BASELINE, CELL_W, CELL_H);
      ctx.restore();
    }

    ctx.font = `54px ${FONT}`;
    const name = f.name.toUpperCase();
    inkText(name, cx, ground + 46, Math.min(54, 54 * 420 / Math.max(1, ctx.measureText(name).width)), "#fff", "#111", 9);
    const outfitLabel = (f.outfits || []).length > 1 && outfit ? `${outfit.name} (${outfit.code})` : "";
    const votes = lead.votes ? `${lead.votes} ${lead.votes === 1 ? "röst" : "röster"}` : "inga röster ännu";
    inkText([outfitLabel, votes].filter(Boolean).join(" · "), cx, ground + 90, 26, lead.votes ? accent : "rgba(255,255,255,0.7)", "#111", 5);

    // The latest votes for this corner, newest on top, fading out.
    const now = performance.now() / 1000;
    this.feed.filter(e => e.player === player).slice(0, 2).forEach((e, i) => {
      const age = now - e.at;
      if (age > 8) return;
      ctx.save();
      ctx.globalAlpha = age > 6 ? (8 - age) / 2 : 1;
      inkText(`${e.viewer} → ${e.fighter.toUpperCase()}${e.outfit > 1 ? " " + e.outfit : ""}`, cx, 300 + i * 34, 24, e.color, "#111", 5);
      ctx.restore();
    });
  }
};
