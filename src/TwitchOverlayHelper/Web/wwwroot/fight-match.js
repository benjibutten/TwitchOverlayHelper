"use strict";

/* How a match runs, the HUD around it, the line to the app, and the loop that draws it all.

   A match is a character select (when the app has it switched on), a short "versus" card, and then
   rounds until one fighter has won as many as the app asks for – at which point the winner is
   crowned and it is back to the select. With no limit the rounds simply go on, as they always did.

     select → versus → intro → fight → ko → win → fade → intro …
                                             ↘ champion → select (or straight into a new match) */

stage = stageLayout();

const p1 = new Fighter(1, -1);
const p2 = new Fighter(2, 1);
p1.foe = p2;
p2.foe = p1;

let round = 1;
let phase = "waiting";
let phaseT = 0;
let winner = null;
let hasSetup = false;
let lastDefaults = { 1: null, 2: null }; // the app's own fighters as last told, to notice a change
let socket = null;

const VERSUS_SECONDS = 3.2;
const CHAMPION_SECONDS = 6;

function setPhase(next) {
  phase = next;
  phaseT = 0;
}

/* The phases a fighter is in the ring for – the only time a cheer can do anything. */
function inRounds() {
  return phase === "intro" || phase === "fight" || phase === "ko" || phase === "win" || phase === "fade";
}

function knockOut(attacker) {
  winner = attacker;
  attacker.wins++;
  slowMo = 0.3;
  shake(34);
  setPhase("ko");
}

function matchOver() {
  const target = setup.match.winsToWin;
  return target > 0 && winner && winner.wins >= target;
}

/* ------------------------------------------------------------------ the flow */

function rosterEntry(id) {
  return setup.roster.find(f => f.id === id) || null;
}

/* The character select, seeded with who is in each corner now – a corner nobody votes for keeps it. */
function startSelect() {
  if (!setup.match.characterSelect || !setup.roster.length) { startMatch(); return; }
  const now = side => side.id ? { id: side.id, outfit: side.outfit || 1 } : null;
  select.begin(setup.roster, setup.match.selectSeconds, {
    1: now(p1) || (setup.p1 && { id: setup.p1.id, outfit: setup.p1.outfit }),
    2: now(p2) || (setup.p2 && { id: setup.p2.id, outfit: setup.p2.outfit })
  });
  setPhase("select");
}

function finishSelect() {
  const result = select.finish();
  if (result[1]) p1.dress(result[1].fighter, result[1].outfit);
  if (result[2]) p2.dress(result[2].fighter, result[2].outfit);
  setPhase("versus");
}

/* A fresh match: no wins, full health, empty meters, round one. */
function startMatch() {
  round = 1;
  winner = null;
  for (const f of [p1, p2]) {
    f.wins = 0;
    f.meter = 0;
    f.reset();
  }
  crowd.newMatch();
  reportLineup();
  setPhase("intro");
}

function updateFlow(dt) {
  phaseT += dt;
  switch (phase) {
    case "select":
      if (!setup.match.characterSelect) finishSelect(); // switched off in the app mid-vote
      else if (select.update(dt)) finishSelect();
      break;
    case "versus":
      if (phaseT > VERSUS_SECONDS) startMatch();
      break;
    case "intro":
      if (phaseT > 2.0) setPhase("fight");
      break;
    case "ko":
      slowMo = Math.min(1, slowMo + dt * 0.9);
      if (phaseT > 1.6) {
        winner.set("win");
        setPhase(matchOver() ? "champion" : "win");
      }
      break;
    case "win":
      if (phaseT > 3.8) setPhase("fade");
      break;
    case "fade":
      if (phaseT > 0.6) {
        round++;
        p1.reset();
        p2.reset();
        winner = null;
        setPhase("intro");
      }
      break;
    case "champion":
      if (phaseT > CHAMPION_SECONDS) startSelect();
      else if (Math.random() < dt * 14) confetti();
      break;
    default: break;
  }
}

function confetti() {
  const x = rand(200, W - 200);
  sparks(x, rand(120, 420), 0, 10, ["#ffd43b", "#ff3b5c", "#2f7bff", "#6dff9c", "#ffffff"]);
}

/* The fighters fade out at the end of a round and back in at the start of the next, rather than the
   whole screen going black: over a transparent arena a black frame would cover the stream. */
function fighterAlpha() {
  if (phase === "fade") return 1 - Math.min(1, phaseT / 0.6);
  if (phase === "intro") return Math.min(1, phaseT / 0.4);
  return 1;
}

/* ------------------------------------------------------------------ HUD */

/* A slanted bar, drawn in the left corner's frame; the right corner is the same bar mirrored. */
function slantedBar(w, h, slant, draw) {
  const shape = () => {
    ctx.beginPath();
    ctx.moveTo(0, 0);
    ctx.lineTo(w, 0);
    ctx.lineTo(w - slant, h);
    ctx.lineTo(-slant, h);
    ctx.closePath();
  };
  shape();
  ctx.fillStyle = "#1b1b20";
  ctx.fill();
  ctx.save();
  shape();
  ctx.clip();
  draw();
  ctx.restore();
  shape();
  ctx.lineWidth = 6;
  ctx.strokeStyle = "#111";
  ctx.lineJoin = "round";
  ctx.stroke();
}

function healthBar(fighter, x, mirrored, time) {
  const w = 720, h = 46, y = 60, slant = 22;
  ctx.save();
  ctx.translate(x, y);
  if (mirrored) ctx.scale(-1, 1);
  slantedBar(w, h, slant, () => {
    // Bars drain toward the middle of the screen, like in every fighting game.
    const trail = w * fighter.shownHp / 100, now = w * fighter.hp / 100;
    ctx.fillStyle = "#d8352a";
    ctx.fillRect(w - trail - slant, 0, trail + slant, h);
    const g = ctx.createLinearGradient(0, 0, 0, h);
    g.addColorStop(0, "#ffe27a");
    g.addColorStop(0.5, "#ffb21f");
    g.addColorStop(1, "#e07a00");
    ctx.fillStyle = fighter.hp > 30 ? g : "#ff5a3d";
    ctx.fillRect(w - now - slant, 0, now + slant, h);
    ctx.fillStyle = "rgba(255,255,255,0.28)";
    ctx.fillRect(-slant, 4, w + slant, 8);
  });

  // The super meter under the bar, filling from the outside in. Blinks when it is full.
  const mw = 430, mh = 16, my = h + 8;
  ctx.save();
  ctx.translate(w - mw - 14, my);
  slantedBar(mw, mh, 8, () => {
    const fill = mw * fighter.meter / 100;
    const hot = fighter.charged && Math.sin(time * 12) > 0;
    ctx.fillStyle = hot ? "#fff27a" : fighter.charged ? "#ffb21f" : "#38c6ff";
    ctx.fillRect(mw - fill - 8, 0, fill + 8, mh);
  });
  ctx.restore();
  ctx.restore();

  // The corner's tag at the outer end of the bar: what the chat types, whoever is standing there.
  const tagX = mirrored ? x + 52 : x - 52;
  ctx.beginPath();
  ctx.arc(tagX, y + 30, 40, 0, Math.PI * 2);
  ctx.fillStyle = fighter.player === 1 ? "#2f7bff" : "#ff3b5c";
  ctx.fill();
  ctx.lineWidth = 6;
  ctx.strokeStyle = "#111";
  ctx.stroke();
  inkText(`P${fighter.player}`, tagX, y + 32, 40, "#fff", "#111", 6);

  // Name under the bars, then the rounds won: one pip per round needed when a match has a length,
  // one star per round otherwise – a number once the stars would crowd the bar.
  const align = mirrored ? "right" : "left";
  const nameX = mirrored ? x - 20 : x + 20;
  const nameY = y + h + mh + 46;
  inkText(fighter.name, nameX, nameY, 44, "#fff", "#111", 8, align);
  ctx.font = `44px ${FONT}`;
  const offset = ctx.measureText(fighter.name).width + 26;
  const target = setup.match.winsToWin;
  if (target > 0) {
    for (let i = 0; i < target; i++) {
      const px = mirrored ? nameX - offset - i * 34 - 12 : nameX + offset + i * 34 + 12;
      ctx.beginPath();
      ctx.arc(px, nameY + 2, 12, 0, Math.PI * 2);
      ctx.fillStyle = i < fighter.wins ? "#ffd43b" : "rgba(20,20,26,0.75)";
      ctx.fill();
      ctx.lineWidth = 4;
      ctx.strokeStyle = "#111";
      ctx.stroke();
    }
  } else if (fighter.wins) {
    const stars = fighter.wins > 5 ? `★ × ${fighter.wins}` : "★".repeat(fighter.wins);
    inkText(stars, mirrored ? nameX - offset + 8 : nameX + offset - 8, nameY + 2, 34, "#ffd43b", "#111", 7, align);
  }

  // What to type for this corner, right under its name.
  const c = setup.commands;
  if (c.enabled && c.showHint)
    inkText(`${c.cheer} ${fighter.player}   ${c.heal} ${fighter.player}`, nameX, nameY + 44, 28, "rgba(255,255,255,0.9)", "#111", 6, align);
}

function drawHud(time) {
  healthBar(p1, 120, false, time);
  healthBar(p2, W - 120, true, time);

  // Round badge between the bars, and how long the match is under it.
  ctx.beginPath();
  ctx.arc(W / 2, 82, 56, 0, Math.PI * 2);
  ctx.fillStyle = "#1b1b20";
  ctx.fill();
  ctx.lineWidth = 7;
  ctx.strokeStyle = "#111";
  ctx.stroke();
  inkText(String(round), W / 2, 84, 64, "#ffd43b", "#111", 9);
  inkText("RUNDA", W / 2, 162, 28, "#fff", "#111", 6);
  if (setup.match.winsToWin > 0) inkText(`FÖRST TILL ${setup.match.winsToWin}`, W / 2, 194, 22, "rgba(255,255,255,0.8)", "#111", 5);
}

function drawAnnouncer() {
  let text = null, size = 190, color = "#ffd43b", k = 0, sub = null;
  if (phase === "intro") {
    if (phaseT < 1.2) {
      const final = setup.match.winsToWin > 0 && p1.wins === setup.match.winsToWin - 1 && p2.wins === setup.match.winsToWin - 1;
      text = final ? "AVGÖRANDE RUNDA" : `RUNDA ${round}`;
      if (final) size = 150;
      k = phaseT / 1.2;
    } else { text = "FIGHT!"; size = 230; color = "#ff5a2a"; k = (phaseT - 1.2) / 0.8; }
  } else if (phase === "ko") {
    text = "K.O.!"; size = 300; color = "#ff3b2a"; k = Math.min(phaseT / 1.6, 0.6);
  } else if (phase === "win" && winner) {
    text = `${winner.name} VINNER!`; size = 140; k = Math.min(phaseT / 3.8, 0.6);
  } else if (phase === "champion" && winner) {
    text = `${winner.name} VINNER MATCHEN!`; size = 120; k = Math.min(phaseT / CHAMPION_SECONDS, 0.6);
    sub = `${p1.wins} – ${p2.wins}`;
  }
  if (!text) return;
  const pop = k < 0.12 ? 0.4 + (k / 0.12) * 0.75 : 1.15 - Math.min(0.15, (k - 0.12) * 0.6);
  ctx.save();
  ctx.globalAlpha = k > 0.85 ? (1 - k) / 0.15 : 1;
  ctx.translate(W / 2, 400);
  ctx.rotate(-0.04);
  ctx.scale(pop, pop);
  ctx.font = `${size}px ${FONT}`;
  const fit = Math.min(size, size * (W - 160) / Math.max(1, ctx.measureText(text).width));
  inkText(text, 8, 10, fit, "rgba(0,0,0,0.5)", "rgba(0,0,0,0.5)", fit * 0.2);
  inkText(text, 0, 0, fit, color, "#111", fit * 0.13);
  if (sub) inkText(sub, 0, fit * 0.95, 90, "#fff", "#111", 12);
  ctx.restore();
}

/* Between the select and the first round: the two picks side by side, like every fighting game. */
function drawVersus(time) {
  const k = Math.min(1, phaseT / 0.45);
  const out = phaseT > VERSUS_SECONDS - 0.4 ? (VERSUS_SECONDS - phaseT) / 0.4 : 1;
  ctx.save();
  ctx.globalAlpha = Math.max(0, out);
  ctx.fillStyle = "rgba(8,8,14,0.78)";
  ctx.fillRect(0, 0, W, H);

  const slide = (1 - k) * 700;
  for (const f of [p1, p2]) {
    const left = f === p1;
    const cx = left ? 520 - slide : W - 520 + slide;
    const img = f.sprite;
    if (imgReady(img)) {
      ctx.save();
      ctx.translate(cx, 860);
      const s = 1.15 * f.scale;
      ctx.scale(left ? s : -s, s);
      ctx.drawImage(img, FRAME.idle * CELL_W, 0, CELL_W, CELL_H, -CELL_W / 2, -BASELINE, CELL_W, CELL_H);
      ctx.restore();
    }
    ctx.font = `86px ${FONT}`;
    const fit = Math.min(86, 86 * 700 / Math.max(1, ctx.measureText(f.name).width));
    inkText(f.name, cx, 930, fit, left ? "#2f7bff" : "#ff3b5c", "#111", 12);
  }
  const vs = 1 + Math.max(0, Math.sin(time * 8)) * 0.06;
  ctx.translate(W / 2, 470);
  ctx.scale(k * vs, k * vs);
  inkText("VS", 10, 12, 300, "rgba(0,0,0,0.5)", "rgba(0,0,0,0.5)", 50);
  inkText("VS", 0, 0, 300, "#ffd43b", "#111", 34);
  ctx.restore();
}

function drawBanner(time) {
  // Why the stream is waiting – the one thing a viewer who just arrived needs to read.
  const banner = (params.get("msg") ?? setup.headline ?? "").toUpperCase();
  const subline = params.get("sub") ?? setup.subline ?? "";
  if (!banner && !subline) return;

  const g = ctx.createLinearGradient(0, H - 210, 0, H);
  g.addColorStop(0, "rgba(8,8,12,0)");
  g.addColorStop(1, "rgba(8,8,12,0.85)");
  ctx.fillStyle = g;
  ctx.fillRect(0, H - 210, W, 210);

  if (banner) {
    ctx.save();
    ctx.translate(W / 2, H - 112);
    ctx.rotate(-0.015 + Math.sin(time * 2.2) * 0.01);
    ctx.font = `78px ${FONT}`;
    const tw = Math.min(ctx.measureText(banner).width + 110, W - 160);
    // A slanted caption box behind the words, like a comic panel.
    ctx.beginPath();
    ctx.moveTo(-tw / 2 + 16, -50);
    ctx.lineTo(tw / 2 + 16, -50);
    ctx.lineTo(tw / 2 - 16, 46);
    ctx.lineTo(-tw / 2 - 16, 46);
    ctx.closePath();
    ctx.fillStyle = "#ffd43b";
    ctx.fill();
    ctx.lineWidth = 8;
    ctx.strokeStyle = "#111";
    ctx.lineJoin = "round";
    ctx.stroke();
    ctx.textAlign = "center";
    ctx.textBaseline = "middle";
    ctx.fillStyle = "#111";
    ctx.fillText(banner, 0, 0, tw - 70);
    ctx.restore();
  }

  if (subline) inkText(subline, W / 2, H - 34, 34, "#fff", "#111", 7);
}

function drawNotice(text) {
  ctx.fillStyle = "rgba(8,8,12,0.8)";
  ctx.fillRect(0, H / 2 - 70, W, 140);
  inkText(text, W / 2, H / 2, 40, "#fff", "#111", 7);
}

/* ------------------------------------------------------------------ the app */

function sameFighter(a, b) {
  return !!a && !!b && a.id === b.id && a.outfit === b.outfit && a.sprite === b.sprite;
}

function applySetup(s) {
  if (!s) return;
  setup.headline = s.headline || "";
  setup.subline = s.subline || "";
  setup.commands = s.commands || setup.commands;
  setup.match = s.match || setup.match;
  setup.roster = s.roster || [];
  setup.p1 = s.p1 || null;
  setup.p2 = s.p2 || null;

  const arena = s.arena || null;
  if (!setup.arena || !arena || setup.arena.id !== arena.id || setup.arena.image !== arena.image) {
    arenaImage = arena && arena.image ? cachedImage(arena.image) : null;
    // The floor moves with the new picture once it has loaded. The round goes on – a K.O. on screen
    // is not undone by a change of scenery – and the walls simply close in on whoever is outside.
    const img = arenaImage;
    if (img && !imgReady(img)) img.addEventListener("load", () => { if (img === arenaImage) stage = stageLayout(); });
  }
  setup.arena = arena;
  stage = stageLayout();
  if (select.active) select.setRoster(setup.roster);

  // The streamer picking a fighter in the app puts them in the ring straight away – that is the
  // streamer overriding, and a corner left alone in the select falls back to it too.
  const changed1 = !sameFighter(lastDefaults[1], s.p1), changed2 = !sameFighter(lastDefaults[2], s.p2);
  lastDefaults = { 1: s.p1 || null, 2: s.p2 || null };

  if (!hasSetup) {
    hasSetup = true;
    p1.dress(s.p1);
    p2.dress(s.p2);
    p1.reset();
    p2.reset();
    startSelect();
    return;
  }
  if (select.active) {
    if (changed1 && s.p1) select.fallback[1] = { id: s.p1.id, outfit: s.p1.outfit };
    if (changed2 && s.p2) select.fallback[2] = { id: s.p2.id, outfit: s.p2.outfit };
  } else {
    if (changed1) p1.dress(s.p1);
    if (changed2) p2.dress(s.p2);
    if (changed1 || changed2) reportLineup();
  }
  // The roster can also have lost the fighter in a corner; it gets the app's choice instead.
  if (p1.id && !rosterEntry(p1.id) && s.p1) p1.dress(s.p1);
  if (p2.id && !rosterEntry(p2.id) && s.p2) p2.dress(s.p2);
}

/* Tells the app who is in the ring, so a cheer by a fighter's name finds the right corner. */
function reportLineup() {
  if (socket && socket.readyState === WebSocket.OPEN && p1.id && p2.id)
    socket.send(JSON.stringify({ type: "fightLineup", id: p1.id, p2: p2.id }));
}

let reconnectDelay = 1000;

function connect() {
  if (!KEY) return;
  socket = new WebSocket(`ws://${location.host}/ws?key=${encodeURIComponent(KEY)}&view=fight`);
  socket.onopen = () => { reconnectDelay = 1000; };
  socket.onmessage = event => {
    let frame;
    try { frame = JSON.parse(event.data); } catch { return; }
    if (frame.type === "hello" || frame.type === "fightSetup") {
      applySetup(frame.payload);
      if (frame.type === "hello") reportLineup();
    } else if (frame.type === "fightAssist") assist(frame.payload || {});
    else if (frame.type === "fightPick") select.vote(frame.payload || {});
  };
  socket.onclose = () => {
    // The fight goes on with what it has; the app coming back simply picks up from there.
    setTimeout(connect, reconnectDelay);
    reconnectDelay = Math.min(15000, reconnectDelay * 1.7);
  };
}
connect();

/* ------------------------------------------------------------------ loop */

let last = performance.now();
function tick(now) {
  let dt = Math.min(0.05, (now - last) / 1000);
  last = now;
  const time = now / 1000;
  const realDt = dt;

  // Hit-stop: a few frames of stillness when a blow lands, so it feels like it connected.
  if (hitStop > 0) {
    hitStop -= dt;
    dt = 0;
  }
  const sim = dt * slowMo;
  const fightersReady = hasSetup && p1.sprite && p2.sprite;
  if (hasSetup) updateFlow(dt);
  const inRing = fightersReady && (inRounds() || phase === "champion");
  if (inRing) {
    p1.update(sim, phase === "fight");
    p2.update(sim, phase === "fight");
    // Fighters never pass through each other.
    const gap = p2.x - p1.x;
    const minGap = p1.down || p2.down ? 150 : 260;
    if (Math.abs(gap) < minGap) {
      const push = (minGap - Math.abs(gap)) / 2 * Math.sign(gap || 1);
      p1.x -= push;
      p2.x += push;
    }
  }
  updateEffects(sim);
  shakeAmount *= Math.pow(0.002, Math.max(dt, 0.016));

  // Cleared rather than painted: what the arena leaves transparent shows the stream.
  ctx.clearRect(0, 0, W, H);
  ctx.save();
  ctx.translate(rand(-1, 1) * shakeAmount, rand(-1, 1) * shakeAmount);
  drawArena();
  if (inRing) {
    // Whoever attacks is drawn in front, so the blow lands on top of the one taking it.
    const order = p1.state === "attack" ? [p2, p1] : [p1, p2];
    const alpha = fighterAlpha();
    for (const f of order) f.draw(alpha, time);
  }
  drawEffects();
  ctx.restore();

  if (phase === "select") select.draw(time);
  else if (phase === "versus") drawVersus(time);
  else if (inRing) {
    crowd.draw(time, realDt);
    drawHud(time);
    drawAnnouncer();
  }
  drawBanner(time);
  if (!KEY) drawNotice("Adressen saknar nyckel – kopiera den på nytt under fliken Fajt i appen.");
  else if (!hasSetup) drawNotice("Väntar på appen …");
  else if (!setup.roster.length && !p1.sprite) drawNotice("Inga karaktärer – lägg en mapp under fight\\fighters och klicka Ladda om.");

  requestAnimationFrame(tick);
}
requestAnimationFrame(tick);
