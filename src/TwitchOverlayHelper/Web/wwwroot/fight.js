"use strict";

/* The wait screen: the streamer's two characters fighting each other, round after round, while the
   stream waits for something – a character lying unconscious, a streamer gone to fetch coffee. It is
   meant to be watched rather than played, so the fighters run on a small brain of their own and the
   rounds stay close: either of them can win, and nobody wins every time.

   The app decides who stands in which corner, where they fight and what the banner says, and tells
   the page over the socket – so a new fighter or arena reaches OBS without touching the source. The
   chat can take sides: a viewer cheers a corner on, which fills that player's super meter, or patches
   it up for a little health. Corners, not characters: player 1 is player 1 whoever is in the ring.

   Every fighter strip holds the same eight frames in the same order, each cell the same size with the
   feet on one baseline and the torso on the cell's centre line – so placing a fighter is one point,
   whatever the pose. All face right in the strip; whoever stands on the right is drawn mirrored. */

const params = new URLSearchParams(location.search);
const KEY = params.get("key") || "";
const canvas = document.getElementById("stage");
const ctx = canvas.getContext("2d");

const W = 1920, H = 1080;

const CELL_W = 640, CELL_H = 560, BASELINE = 550;
const SPRITE_SCALE = 1.08;
const FRAME = { idle: 0, idle2: 1, walk: 2, punch: 3, kick: 4, hit: 5, down: 6, win: 7 };
const FONT = `Impact, "Arial Black", sans-serif`;

const SOUNDS = ["POW!", "BAM!", "WHAM!", "SMACK!", "KAPOW!", "THWACK!", "BOOM!"];
const CHEER_METER = 25; // four cheers make a super
const HEAL_HP = 10;

/* What the app last said. Own words in ?msg= and ?sub= win over the app's banner, for a scene that
   should say something of its own. */
const setup = {
  headline: "",
  subline: "",
  commands: { enabled: false, cheer: "!heja", heal: "!hela", showHint: false },
  arena: null
};

function loadImage(src) {
  const img = new Image();
  img.src = src;
  return img;
}

const rand = (a, b) => a + Math.random() * (b - a);
const pick = list => list[Math.floor(Math.random() * list.length)];
const clamp = (v, a, b) => Math.max(a, Math.min(b, v));

/* A fighter is drawn on a layer of its own first, so the white blink of a blow lands on the sprite
   alone and not on the arena behind it. */
const layer = document.createElement("canvas");
layer.width = CELL_W;
layer.height = CELL_H;
const layerCtx = layer.getContext("2d");

/* ------------------------------------------------------------------ arena */

let arenaImage = null;

/* Where the arena picture lands on the stage, and from that where the floor and the walls are. The
   picture covers the stage with a little to spare, so a shaking camera never shows an edge; with no
   picture the numbers are taken against the stage itself. */
function stageLayout() {
  const a = setup.arena || { floor: 0.815, left: 0.09, right: 0.91 };
  let x = 0, y = 0, w = W, h = H;
  if (arenaImage && arenaImage.complete && arenaImage.naturalWidth) {
    const s = Math.max(W / arenaImage.naturalWidth, H / arenaImage.naturalHeight) * 1.04;
    w = arenaImage.naturalWidth * s;
    h = arenaImage.naturalHeight * s;
    x = (W - w) / 2;
    y = (H - h) / 2;
  }
  return {
    x, y, w, h,
    ground: y + a.floor * h,
    left: Math.max(150, x + a.left * w),
    right: Math.min(W - 150, x + a.right * w)
  };
}
let stage = stageLayout();

function drawArena() {
  if (arenaImage && arenaImage.complete && arenaImage.naturalWidth)
    ctx.drawImage(arenaImage, stage.x, stage.y, stage.w, stage.h);
}

/* ------------------------------------------------------------------ fighters */

class Fighter {
  constructor(player, side) {
    this.player = player; // 1 or 2 – what the chat calls this corner
    this.side = side;     // -1 starts on the left, 1 on the right
    this.name = "";
    this.sprite = null;
    this.scale = 1;
    this.wins = 0;
    this.meter = 0;       // the super meter, filled by the chat
    this.lastFacing = -side;
    this.reset();
  }

  /* A new character in this corner. The record of wins stays with the corner. */
  dress(def) {
    if (!def) return;
    if (this.id !== def.id || this.spriteUrl !== def.sprite) {
      this.sprite = loadImage(def.sprite);
      this.spriteUrl = def.sprite;
    }
    this.id = def.id;
    this.name = (params.get("p" + this.player) || def.name || "").toUpperCase();
    this.scale = def.scale || 1;
  }

  get size() { return SPRITE_SCALE * this.scale; }

  reset() {
    const mid = (stage.left + stage.right) / 2;
    const half = Math.min(380, (stage.right - stage.left) / 2 * 0.7);
    this.x = mid + this.side * half;
    this.y = 0; // height above the floor, for knock-downs that bounce
    this.vx = 0;
    this.vy = 0;
    this.hp = 100;
    this.shownHp = 100; // the red trail behind the bar
    this.state = "idle";
    this.t = 0;
    this.wait = rand(0.2, 0.7);
    this.flash = 0;
    this.glow = 0; // the green shimmer of a heal
    this.hasHit = false;
    // A temperament per round, so two rounds in a row never play out the same.
    this.aggression = rand(0.45, 0.95);
    this.reach = rand(300, 330) * Math.min(1.3, Math.max(0.8, this.scale));
  }

  get facing() { return this.foe.x > this.x ? 1 : -1; }
  get distance() { return Math.abs(this.foe.x - this.x); }
  get down() { return this.state === "down"; }
  get charged() { return this.meter >= 100; }

  set(state) {
    this.state = state;
    this.t = 0;
  }

  update(dt, fighting) {
    this.t += dt;
    this.flash = Math.max(0, this.flash - dt * 4);
    this.glow = Math.max(0, this.glow - dt * 1.2);
    // The trail waits a moment and then drains slowly, so a blow can be read on the bar.
    this.shownHp += (this.hp - this.shownHp) * Math.min(1, dt * (this.shownHp > this.hp + 1 ? 2.2 : 10));

    // Momentum from blows and knock-downs, with friction on the floor.
    this.x += this.vx * dt;
    if (this.y > 0 || this.vy !== 0) {
      this.vy -= 2600 * dt;
      this.y += this.vy * dt;
      if (this.y <= 0) {
        this.y = 0;
        this.vy = Math.abs(this.vy) > 320 ? -this.vy * 0.35 : 0;
        if (this.vy) shake(12);
      }
    }
    if (this.y === 0) this.vx *= Math.pow(0.0025, dt);
    // Lying down a fighter is twice as wide, and should still be lying inside the arena.
    const inset = this.down ? Math.min(160, (stage.right - stage.left) / 4) : 0;
    this.x = clamp(this.x, stage.left + inset, stage.right - inset);

    switch (this.state) {
      case "idle": this.think(dt, fighting); break;
      case "walk": this.walk(dt, fighting); break;
      case "back": this.backstep(); break;
      case "attack": this.attack(); break;
      case "hit": if (this.t > 0.38) this.set("idle"); break;
      default: break;
    }
  }

  think(dt, fighting) {
    if (!fighting || this.foe.down) return;
    this.wait -= dt;
    if (this.wait > 0) return;

    if (this.distance > this.reach) {
      this.set("walk");
    } else if (!this.charged && Math.random() < 0.18) {
      this.set("back");
    } else {
      // A charged fighter goes for the kick: the super is the biggest thing they own.
      this.move = this.charged || Math.random() >= 0.58 ? "kick" : "punch";
      this.hasHit = false;
      this.set("attack");
    }
  }

  walk(dt, fighting) {
    if (!fighting) { this.set("idle"); return; }
    this.x += this.facing * 300 * dt;
    if (this.distance <= this.reach - 20) {
      this.wait = rand(0.05, 0.45) / this.aggression;
      this.set("idle");
    }
  }

  backstep() {
    this.vx = -this.facing * 260;
    if (this.t > 0.32) {
      this.wait = rand(0.3, 0.8);
      this.set("idle");
    }
  }

  get windup() { return this.move === "kick" ? (this.charged ? 0.32 : 0.2) : 0.13; }

  attack() {
    const active = this.move === "kick" ? 0.32 : 0.24;
    if (this.t >= this.windup && !this.hasHit) {
      this.hasHit = true;
      const reach = this.reach + (this.move === "kick" ? 60 : 40);
      if (this.distance <= reach && !this.foe.down) {
        // A super never misses – the chat paid for it.
        if (!this.charged && (this.foe.state === "back" || Math.random() < 0.12)) {
          popText("MISS", this.foe.x, stage.ground - 520, "#d6dbe4");
        } else {
          strike(this, this.foe, this.move);
        }
      }
    }
    if (this.t >= this.windup + active + 0.12) {
      this.wait = rand(0.25, 0.9) / this.aggression;
      this.set("idle");
    }
  }

  frame() {
    switch (this.state) {
      case "walk": return Math.floor(this.t / 0.16) % 2 ? FRAME.walk : FRAME.idle;
      case "back": return FRAME.walk;
      case "attack": return this.t < this.windup ? FRAME.walk : FRAME[this.move];
      case "hit": return FRAME.hit;
      case "down": return this.y > 30 || this.t < 0.2 ? FRAME.hit : FRAME.down;
      case "win": return FRAME.win;
      default: return Math.floor(this.t / 0.42) % 2 ? FRAME.idle2 : FRAME.idle;
    }
  }

  draw(alpha, time) {
    const img = this.sprite;
    if (!img || !img.complete || !img.naturalWidth) return;
    // Lying down or celebrating, a fighter keeps the way they faced when it happened.
    const facing = this.down || this.state === "win" ? this.lastFacing : this.facing;
    this.lastFacing = facing;

    ctx.save();
    ctx.globalAlpha = alpha;

    // Floor shadow, shrinking as the fighter leaves the floor.
    const lift = clamp(1 - this.y / 400, 0.4, 1);
    ctx.fillStyle = "rgba(0,0,0,0.45)";
    ctx.beginPath();
    ctx.ellipse(this.x, stage.ground + 4, (this.down && this.y === 0 ? 240 : 140) * lift * this.scale, 22 * lift, 0, 0, Math.PI * 2);
    ctx.fill();

    const f = this.frame();
    layerCtx.globalCompositeOperation = "copy";
    layerCtx.drawImage(img, f * CELL_W, 0, CELL_W, CELL_H, 0, 0, CELL_W, CELL_H);
    if (this.flash > 0) {
      layerCtx.globalCompositeOperation = "source-atop";
      layerCtx.fillStyle = `rgba(255,255,255,${this.flash * 0.8})`;
      layerCtx.fillRect(0, 0, CELL_W, CELL_H);
    }

    const bob = this.state === "walk" ? -Math.abs(Math.sin(this.t * 19)) * 8 : 0;
    ctx.translate(this.x, stage.ground - this.y + bob);
    ctx.scale(facing * this.size, this.size);
    // A full meter burns around the fighter, a heal shimmers – both follow the outline of the pose.
    if (this.charged) {
      ctx.shadowColor = `rgba(255,196,40,${0.75 + Math.sin(time * 9) * 0.25})`;
      ctx.shadowBlur = 38 + Math.sin(time * 9) * 12;
    } else if (this.glow > 0) {
      ctx.shadowColor = `rgba(90,255,150,${this.glow})`;
      ctx.shadowBlur = 40;
    }
    ctx.drawImage(layer, -CELL_W / 2, -BASELINE);
    ctx.restore();
  }
}

/* ------------------------------------------------------------------ effects */

const effects = [];
let shakeAmount = 0;
let hitStop = 0;
let slowMo = 1;

function shake(amount) { shakeAmount = Math.max(shakeAmount, amount); }

function strike(attacker, target, move) {
  const isSuper = attacker.charged;
  const critical = !isSuper && Math.random() < 0.12;
  let damage = move === "kick" ? rand(11, 16) : rand(7, 11);
  if (critical) damage *= 1.7;
  if (isSuper) {
    damage = rand(26, 32);
    attacker.meter = 0;
  }
  // Whoever is behind hits a little harder, so a round can still turn – a comeback is the best part.
  damage *= 1 + Math.max(0, target.hp - attacker.hp) / 150;
  target.hp = Math.max(0, target.hp - Math.round(damage));
  target.flash = 1;

  const dir = attacker.facing;
  const hitX = target.x - dir * 50;
  const hitY = stage.ground - (move === "kick" ? 400 : 420) * target.size;
  effects.push({
    kind: "burst", x: hitX, y: hitY, t: 0, life: isSuper ? 1.1 : 0.75, spin: rand(-0.25, 0.25),
    text: isSuper ? "SUPER!" : critical ? "CRITICAL!" : pick(SOUNDS), size: isSuper ? 1.6 : critical ? 1.3 : 1,
    colors: isSuper ? ["#fff27a", "#ff2f6d"] : ["#ffd43b", "#ff6b1a"],
    points: Array.from({ length: 14 }, (_, i) => (i % 2 ? rand(0.5, 0.65) : rand(0.92, 1.12)))
  });
  sparks(hitX, hitY, dir, isSuper ? 44 : critical ? 26 : 14);
  hitStop = isSuper ? 0.3 : critical ? 0.16 : 0.08;
  shake(isSuper ? 40 : critical ? 26 : move === "kick" ? 16 : 10);

  if (target.hp <= 0) {
    target.set("down");
    target.vx = dir * 520;
    target.vy = 900;
    target.y = 1;
    knockOut(attacker);
  } else {
    target.set("hit");
    target.vx = dir * (move === "kick" ? 520 : 380) * (critical || isSuper ? 1.5 : 1);
  }
}

function popText(text, x, y, color, size = 54, life = 0.8) {
  effects.push({ kind: "text", x, y, text, color, size, t: 0, life });
}

function sparks(x, y, dir, count, colors = ["#fff4c2", "#ffb340", "#ff7a1a"]) {
  for (let i = 0; i < count; i++) {
    const a = dir === 0 ? rand(-Math.PI, 0) : rand(-1.1, 1.1) + (dir > 0 ? 0 : Math.PI);
    const v = rand(500, 1200);
    effects.push({ kind: "spark", x, y, vx: Math.cos(a) * v, vy: Math.sin(a) * v - 200, t: 0, life: rand(0.25, 0.5), color: pick(colors) });
  }
}

function updateEffects(dt) {
  for (let i = effects.length - 1; i >= 0; i--) {
    const e = effects[i];
    e.t += dt;
    if (e.kind === "spark") {
      e.x += e.vx * dt;
      e.y += e.vy * dt;
      e.vy += 1800 * dt;
    } else if (e.kind === "text") {
      e.y -= 60 * dt;
    }
    if (e.t >= e.life) effects.splice(i, 1);
  }
}

function inkText(text, x, y, size, fill, stroke = "#111", lineWidth = size * 0.16, align = "center") {
  ctx.font = `${size}px ${FONT}`;
  ctx.textAlign = align;
  ctx.textBaseline = "middle";
  ctx.lineJoin = "round";
  ctx.lineWidth = lineWidth;
  ctx.strokeStyle = stroke;
  ctx.strokeText(text, x, y);
  ctx.fillStyle = fill;
  ctx.fillText(text, x, y);
}

function star(points, radius, turn) {
  ctx.beginPath();
  points.forEach((r, i) => {
    const a = (i / points.length) * Math.PI * 2 + turn;
    const px = Math.cos(a) * 150 * r * radius, py = Math.sin(a) * 105 * r * radius;
    if (i) ctx.lineTo(px, py); else ctx.moveTo(px, py);
  });
  ctx.closePath();
}

function drawEffects() {
  for (const e of effects) {
    const k = e.t / e.life;
    ctx.save();
    if (e.kind === "spark") {
      ctx.globalAlpha = 1 - k;
      ctx.strokeStyle = e.color;
      ctx.lineWidth = 5;
      ctx.lineCap = "round";
      ctx.beginPath();
      ctx.moveTo(e.x, e.y);
      ctx.lineTo(e.x - e.vx * 0.03, e.y - e.vy * 0.03);
      ctx.stroke();
    } else if (e.kind === "burst") {
      // Comic-book burst: a jagged star with an ink outline, a hotter star inside, the sound on top.
      const pop = k < 0.15 ? k / 0.15 * 1.25 : 1.25 - Math.min(0.25, (k - 0.15) * 1.2);
      ctx.globalAlpha = k > 0.7 ? 1 - (k - 0.7) / 0.3 : 1;
      ctx.translate(e.x, e.y);
      ctx.rotate(e.spin);
      ctx.scale(pop * e.size, pop * e.size);
      star(e.points, 1, 0);
      ctx.fillStyle = e.colors[0];
      ctx.lineWidth = 9;
      ctx.lineJoin = "round";
      ctx.strokeStyle = "#111";
      ctx.fill();
      ctx.stroke();
      star(e.points, 0.62, 0.2);
      ctx.fillStyle = e.colors[1];
      ctx.fill();
      inkText(e.text, 0, 4, e.text.length > 7 ? 52 : 70, "#fff");
    } else {
      ctx.globalAlpha = k > 0.6 ? 1 - (k - 0.6) / 0.4 : 1;
      inkText(e.text, e.x, e.y, e.size, e.color, "#111", e.size * 0.16);
    }
    ctx.restore();
  }
}

/* ------------------------------------------------------------------ the chat */

/* A viewer took a side. The effect lands on the fighter so the stream sees it happen, and the
   viewer's name rides along – being seen on screen is half the point of typing it. */
function assist(a) {
  const f = a.player === 2 ? p2 : a.player === 1 ? p1 : null;
  if (!f) return;
  const who = String(a.viewer || "").slice(0, 25);
  const color = /^#[0-9a-f]{6}$/i.test(a.color || "") ? a.color : "#ffffff";
  // Just over the head, but never up among the bars, where a name would be lost.
  // Three rows taken in turn, so a busy chat stacks its names instead of writing them on each other.
  f.popRow = ((f.popRow || 0) + 1) % 3;
  const headY = Math.max(300, stage.ground - 500 * f.size + f.popRow * 70 - 70);

  if (a.kind === "heal") {
    // A fighter on the floor is past patching up.
    if (f.down || f.hp <= 0) return;
    f.hp = Math.min(100, f.hp + HEAL_HP);
    f.shownHp = Math.max(f.shownHp, f.hp);
    f.glow = 1;
    popText(`+${HEAL_HP} HP`, f.x, headY, "#6dff9c", 60, 1.2);
    popText(`${who} plåstrar om!`, f.x, headY - 66, color, 36, 1.6);
    sparks(f.x, stage.ground - 260 * f.size, 0, 14, ["#c6ffd8", "#6dff9c", "#ffffff"]);
    return;
  }

  const wasCharged = f.charged;
  f.meter = Math.min(100, f.meter + CHEER_METER);
  popText(`${who} hejar!`, f.x, headY - 20, color, 40, 1.6);
  if (f.charged && !wasCharged) {
    popText("SUPER LADDAD!", f.x, headY - 90, "#ffd43b", 64, 1.8);
    shake(10);
  }
}

/* ------------------------------------------------------------------ rounds */

const p1 = new Fighter(1, -1);
const p2 = new Fighter(2, 1);
p1.foe = p2;
p2.foe = p1;

let round = 1;
let phase = "intro"; // intro → fight → ko → win → fade → intro
let phaseT = 0;
let winner = null;

function setPhase(next) {
  phase = next;
  phaseT = 0;
}

function knockOut(attacker) {
  winner = attacker;
  attacker.wins++;
  slowMo = 0.3;
  shake(34);
  setPhase("ko");
}

function updateRound(dt) {
  phaseT += dt;
  switch (phase) {
    case "intro":
      if (phaseT > 2.0) setPhase("fight");
      break;
    case "ko":
      slowMo = Math.min(1, slowMo + dt * 0.9);
      if (phaseT > 1.6) {
        winner.set("win");
        setPhase("win");
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
    default: break;
  }
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

  // Name under the bars, one star per round won – a number once the stars would crowd the bar.
  const align = mirrored ? "right" : "left";
  const nameX = mirrored ? x - 20 : x + 20;
  const nameY = y + h + mh + 46;
  inkText(fighter.name, nameX, nameY, 44, "#fff", "#111", 8, align);
  if (fighter.wins) {
    const stars = fighter.wins > 5 ? `★ × ${fighter.wins}` : "★".repeat(fighter.wins);
    ctx.font = `44px ${FONT}`;
    const offset = ctx.measureText(fighter.name).width + 18;
    inkText(stars, mirrored ? nameX - offset : nameX + offset, nameY + 2, 34, "#ffd43b", "#111", 7, align);
  }

  // What to type for this corner, right under its name.
  const c = setup.commands;
  if (c.enabled && c.showHint)
    inkText(`${c.cheer} ${fighter.player}   ${c.heal} ${fighter.player}`, nameX, nameY + 44, 28, "rgba(255,255,255,0.9)", "#111", 6, align);
}

function drawHud(time) {
  healthBar(p1, 120, false, time);
  healthBar(p2, W - 120, true, time);

  // Round badge between the bars.
  ctx.beginPath();
  ctx.arc(W / 2, 82, 56, 0, Math.PI * 2);
  ctx.fillStyle = "#1b1b20";
  ctx.fill();
  ctx.lineWidth = 7;
  ctx.strokeStyle = "#111";
  ctx.stroke();
  inkText(String(round), W / 2, 84, 64, "#ffd43b", "#111", 9);
  inkText("RUNDA", W / 2, 162, 28, "#fff", "#111", 6);
}

function drawAnnouncer() {
  let text = null, size = 190, color = "#ffd43b", k = 0;
  if (phase === "intro") {
    if (phaseT < 1.2) { text = `RUNDA ${round}`; k = phaseT / 1.2; }
    else { text = "FIGHT!"; size = 230; color = "#ff5a2a"; k = (phaseT - 1.2) / 0.8; }
  } else if (phase === "ko") {
    text = "K.O.!"; size = 300; color = "#ff3b2a"; k = Math.min(phaseT / 1.6, 0.6);
  } else if (phase === "win" && winner) {
    text = `${winner.name} VINNER!`; size = 140; k = Math.min(phaseT / 3.8, 0.6);
  }
  if (!text) return;
  const pop = k < 0.12 ? 0.4 + (k / 0.12) * 0.75 : 1.15 - Math.min(0.15, (k - 0.12) * 0.6);
  ctx.save();
  ctx.globalAlpha = k > 0.85 ? (1 - k) / 0.15 : 1;
  ctx.translate(W / 2, 400);
  ctx.rotate(-0.04);
  ctx.scale(pop, pop);
  inkText(text, 8, 10, size, "rgba(0,0,0,0.5)", "rgba(0,0,0,0.5)", size * 0.2);
  inkText(text, 0, 0, size, color, "#111", size * 0.13);
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

function applySetup(s) {
  if (!s) return;
  setup.headline = s.headline || "";
  setup.subline = s.subline || "";
  setup.commands = s.commands || setup.commands;

  const arena = s.arena || null;
  if (!setup.arena || !arena || setup.arena.id !== arena.id || setup.arena.image !== arena.image) {
    arenaImage = arena && arena.image ? loadImage(arena.image) : null;
    // The floor moves with the new picture once it has loaded. The round goes on – a K.O. on screen
    // is not undone by a change of scenery – and the walls simply close in on whoever is outside.
    const img = arenaImage;
    if (img) img.onload = () => { if (img === arenaImage) stage = stageLayout(); };
  }
  setup.arena = arena;
  stage = stageLayout();

  p1.dress(s.p1);
  p2.dress(s.p2);
  if (!hasSetup) { p1.reset(); p2.reset(); }
  hasSetup = true;
}

let hasSetup = false;
let reconnectDelay = 1000;

function connect() {
  if (!KEY) return;
  const socket = new WebSocket(`ws://${location.host}/ws?key=${encodeURIComponent(KEY)}&view=fight`);
  socket.onopen = () => { reconnectDelay = 1000; };
  socket.onmessage = event => {
    let frame;
    try { frame = JSON.parse(event.data); } catch { return; }
    if (frame.type === "hello" || frame.type === "fightSetup") applySetup(frame.payload);
    else if (frame.type === "fightAssist") assist(frame.payload || {});
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

  // Hit-stop: a few frames of stillness when a blow lands, so it feels like it connected.
  if (hitStop > 0) {
    hitStop -= dt;
    dt = 0;
  }
  const sim = dt * slowMo;
  const ready = hasSetup && p1.sprite && p2.sprite;
  if (ready) {
    updateRound(dt);
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
  if (ready) {
    // Whoever attacks is drawn in front, so the blow lands on top of the one taking it.
    const order = p1.state === "attack" ? [p2, p1] : [p1, p2];
    const alpha = fighterAlpha();
    for (const f of order) f.draw(alpha, time);
  }
  drawEffects();
  ctx.restore();

  if (ready) {
    drawHud(time);
    drawAnnouncer();
  }
  drawBanner(time);
  if (!KEY) drawNotice("Adressen saknar nyckel – kopiera den på nytt under fliken Fajt i appen.");
  else if (!hasSetup) drawNotice("Väntar på appen …");

  requestAnimationFrame(tick);
}
requestAnimationFrame(tick);
