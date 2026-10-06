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
   whatever the pose. All face right in the strip; whoever stands on the right is drawn mirrored.

   The page is four scripts sharing one scope, loaded in this order: this one (the fighters, their
   blows and the arena), fight-crowd.js (the side panels of viewers taking part), fight-select.js
   (the character select) and fight-match.js (how a match runs, the HUD, the app and the loop). */

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
  commands: { enabled: false, cheer: "!heja", heal: "!hela", showHint: false, cooldown: 20, pick1: "!p1", pick2: "!p2" },
  match: { characterSelect: false, selectSeconds: 30, winsToWin: 0, showSupporters: false },
  roster: [],
  arena: null
};

/* Images are asked for once per address, however many places draw them – the select screen alone
   can want every fighter on the roster at once. */
const imageCache = new Map();
function cachedImage(src) {
  if (!src) return null;
  let img = imageCache.get(src);
  if (!img) {
    img = loadImage(src);
    imageCache.set(src, img);
  }
  return img;
}
const imgReady = img => !!img && img.complete && img.naturalWidth > 0;

function loadImage(src) {
  const img = new Image();
  img.src = src;
  return img;
}

const rand = (a, b) => a + Math.random() * (b - a);
/* A viewer's chat colour, or white – it is drawn on the broadcast, so nothing else gets through. */
const safeColor = c => /^#[0-9a-f]{6}$/i.test(c || "") ? c : "#ffffff";
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
  // The side panels listing the viewers take a strip of each edge; the fighters keep clear of it.
  const edge = supportersShown() ? CROWD_PANEL_EDGE : 150;
  return {
    x, y, w, h,
    ground: y + a.floor * h,
    left: Math.max(edge, x + a.left * w),
    right: Math.min(W - edge, x + a.right * w)
  };
}
// Laid out by fight-match.js once every script has loaded: the panels it reserves room for live in fight-crowd.js.
let stage = null;

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

  /* A new character in this corner, in one of its outfits. The record of wins stays with the corner. */
  dress(def, outfitCode) {
    if (!def) return;
    const outfits = def.outfits || [];
    const outfit = outfits.find(o => o.code === (outfitCode ?? def.outfit)) || outfits[0] || { code: 1, sprite: def.sprite };
    if (this.spriteUrl !== outfit.sprite) {
      this.sprite = cachedImage(outfit.sprite);
      this.spriteUrl = outfit.sprite;
    }
    this.id = def.id;
    this.outfit = outfit.code;
    this.name = (params.get("p" + this.player) || def.name || "").toUpperCase();
    this.scale = def.scale || 1;
    // The special's own poses are drawn in the ordinary outfit; in another outfit they would show the
    // wrong clothes for a moment, so that outfit swings with its kick instead.
    this.special = def.special || null;
    this.specialSprite = this.special && this.special.sprite && outfit.code === 1 ? cachedImage(this.special.sprite) : null;
    this.prop = this.special && this.special.prop ? cachedImage(this.special.prop) : null;
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
    this.burn = 0; // flames left on a fighter hit by something burning
    this.hasHit = false;
    this.lastStandUsed = false; // the special without a meter, once a round
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
    if (this.burn > 0) {
      this.burn = Math.max(0, this.burn - dt);
      if (Math.random() < dt * 40) flame(this.x + rand(-70, 70) * this.scale, stage.ground - this.y - rand(60, 420) * this.size, this.burnColor);
    }
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
      case "special": this.specialMove(); break;
      case "hit": if (this.t > 0.38) this.set("idle"); break;
      default: break;
    }
  }

  think(dt, fighting) {
    if (!fighting || this.foe.down) return;
    this.wait -= dt;
    if (this.wait > 0) return;

    // The special: what a full meter buys – or, once a round, a last stand when the round is slipping.
    const lastStand = this.special && !this.lastStandUsed && this.hp <= 30 && this.foe.hp > this.hp + 10 && Math.random() < 0.4;
    if (this.special && (this.charged || lastStand)) {
      if (this.distance > this.specialReach) { this.set("walk"); return; }
      if (this.charged) this.meter = 0; else this.lastStandUsed = true;
      this.hasHit = false;
      this.hits = 0;
      this.lunged = false;
      this.set("special");
      announceSpecial(this, !lastStand || this.charged);
      return;
    }

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
    const stopAt = this.special && this.charged ? Math.min(this.reach, this.specialReach) : this.reach;
    if (this.distance <= stopAt - 20) {
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

  /* How close the special has to be. A throw goes the length of the ring; a swing lunges in. */
  get specialReach() {
    const style = this.special ? this.special.style : "swing";
    return style === "throw" ? 4000 : this.reach + 260;
  }

  /* Wind-up and how long the whole move lasts, per style. */
  get specialTimes() {
    switch (this.special && this.special.style) {
      case "throw": return { windup: 0.5, total: 1.05 };
      case "saw": return { windup: 0.45, total: 1.3 };
      default: return { windup: 0.55, total: 1.05 };
    }
  }

  specialMove() {
    const { windup, total } = this.specialTimes;
    const style = this.special.style;
    const close = () => this.distance <= this.reach + 160 && !this.foe.down;
    // Swings and saws lunge in at the end of the wind-up, so the blow lands from where they stood.
    if (style !== "throw" && !this.lunged && this.t >= windup - 0.12) {
      this.lunged = true;
      this.vx = this.facing * Math.min(1400, Math.max(300, (this.distance - this.reach + 120) * 5));
    }
    if (style === "throw") {
      if (this.t >= windup && !this.hasHit) {
        this.hasHit = true;
        throwProp(this);
      }
    } else if (style === "saw") {
      // A burst of quick cuts while the blade is in, the last one the big one.
      while (this.hits < 5 && this.t >= windup + this.hits * 0.13) {
        this.hits++;
        if (close()) specialHit(this, this.foe, this.hits === 5 ? 11 : 5, this.hits === 5);
      }
    } else if (this.t >= windup && !this.hasHit) {
      this.hasHit = true;
      if (close()) specialHit(this, this.foe, rand(26, 32), true);
    }
    if (this.t >= total) {
      this.wait = rand(0.4, 0.9);
      this.set("idle");
    }
  }

  /* Which picture and which cell to draw: the special has poses of its own when there are any. */
  pose() {
    if (this.state === "special") {
      const strike = this.t >= this.specialTimes.windup;
      // The special's strip has three cells of its own size – wider and taller, for the weapon.
      if (imgReady(this.specialSprite)) {
        const sp = this.specialSprite;
        return { img: sp, frame: strike ? 2 : 1, cw: sp.naturalWidth / 3, ch: sp.naturalHeight };
      }
      const style = this.special.style;
      return { img: this.sprite, frame: strike ? (style === "throw" ? FRAME.punch : FRAME.kick) : FRAME.walk };
    }
    return { img: this.sprite, frame: this.frame() };
  }

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
    const { img, frame: f, cw = CELL_W, ch = CELL_H } = this.pose();
    const base = ch - (CELL_H - BASELINE);
    if (layer.width !== cw || layer.height !== ch) {
      layer.width = cw;
      layer.height = ch;
    }
    if (!imgReady(img)) return;
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

    layerCtx.globalCompositeOperation = "copy";
    layerCtx.drawImage(img, f * cw, 0, cw, ch, 0, 0, cw, ch);
    if (this.flash > 0) {
      layerCtx.globalCompositeOperation = "source-atop";
      layerCtx.fillStyle = `rgba(255,255,255,${this.flash * 0.8})`;
      layerCtx.fillRect(0, 0, cw, ch);
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
    ctx.drawImage(layer, -cw / 2, -base);
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
  // A fighter with a special of their own spends a full meter on that instead, never on this kick.
  const isSuper = attacker.charged && !attacker.special;
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

/* ------------------------------------------------------------------ specials */

/* The special's name, big over the fighter, and the screen tinted in its colour for a moment. */
function announceSpecial(f, viaMeter) {
  const sp = f.special;
  // At chest height rather than over the head, where the cheers and "laddad" already float.
  popText(`${sp.name.toUpperCase()}!`, f.x, Math.max(440, stage.ground - 330 * f.size), sp.color, 86, 1.3);
  if (!viaMeter) popText("SISTA UTVÄGEN", f.x, Math.max(510, stage.ground - 250 * f.size), "#ffffff", 40, 1.3);
  effects.push({ kind: "flash", color: sp.color, t: 0, life: 0.35 });
  shake(8);
}

/* A special's blow. The final one of a move is the big one: the burst with the special's name, the
   hit-stop, the screen flash and a fighter sent flying. The small ones are a saw's cuts. */
function specialHit(attacker, target, damage, final) {
  const sp = attacker.special;
  damage *= 1 + Math.max(0, target.hp - attacker.hp) / 150;
  target.hp = Math.max(0, target.hp - Math.round(damage));
  target.flash = 1;

  const dir = target.x > attacker.x ? 1 : -1;
  const hitX = target.x - dir * 40;
  const hitY = stage.ground - 380 * target.size;
  sparks(hitX, hitY, dir, final ? 46 : 12, [sp.color, "#ffffff", "#ffd43b"]);
  if (final) {
    effects.push({
      kind: "burst", x: hitX, y: hitY, t: 0, life: 1.1, spin: rand(-0.2, 0.2),
      text: `${sp.name.toUpperCase()}!`, size: 1.6, colors: ["#ffffff", sp.color],
      points: Array.from({ length: 16 }, (_, i) => (i % 2 ? rand(0.5, 0.65) : rand(0.92, 1.15)))
    });
    effects.push({ kind: "flash", color: sp.color, t: 0, life: 0.3 });
    hitStop = 0.28;
    shake(42);
  } else {
    popText(pick(["BRRR!", "VRRR!", "ZZZT!", "RRRAH!"]), hitX + rand(-40, 40), hitY - rand(40, 120), sp.color, 46, 0.5);
    hitStop = 0.04;
    shake(12);
  }

  if (target.hp <= 0) {
    target.set("down");
    target.vx = dir * 620;
    target.vy = 1000;
    target.y = 1;
    knockOut(attacker);
  } else {
    target.set("hit");
    target.vx = dir * (final ? 820 : 140);
  }
}

/* A thrown special: the prop leaves the hand, turns over in an arc and comes down on the other
   fighter wherever they have got to – then bursts into flames in the special's colour. */
function throwProp(f) {
  const target = f.foe;
  const x0 = f.x + f.facing * 110 * f.size;
  const y0 = stage.ground - 470 * f.size;
  effects.push({
    kind: "projectile", owner: f, target, img: f.prop, color: f.special.color,
    x0, y0, x: x0, y: y0, t: 0, life: 0.75, spin: f.facing * rand(9, 13), angle: 0,
    onEnd() {
      const sp = f.special;
      for (let i = 0; i < 40; i++) flame(target.x + rand(-110, 110), stage.ground - rand(20, 320) * target.size, sp.color);
      sparks(target.x, stage.ground - 300 * target.size, 0, 30, [sp.color, "#ffffff", "#ffb340"]);
      if (!target.down && inRounds()) {
        target.burn = 1.8;
        target.burnColor = sp.color;
        specialHit(f, target, rand(24, 30), true);
      }
    }
  });
}

/* One lick of fire: a soft blob that rises, swells and fades, added rather than painted over. */
function flame(x, y, color) {
  effects.push({ kind: "flame", x, y, vx: rand(-40, 40), vy: rand(-260, -120), r: rand(14, 32), t: 0, life: rand(0.35, 0.7), color: color || "#ff8a1a" });
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
    } else if (e.kind === "projectile") {
      // Homing on purpose: a super never misses, so the arc ends wherever the target now stands.
      const k = Math.min(1, e.t / e.life);
      const tx = e.target.x, ty = stage.ground - 330 * e.target.size;
      e.x = e.x0 + (tx - e.x0) * k;
      e.y = e.y0 + (ty - e.y0) * k - 130 * 4 * k * (1 - k);
      e.angle += e.spin * dt;
      if (Math.random() < dt * 30) flame(e.x, e.y, "#ff9a2a");
    } else if (e.kind === "flame") {
      e.x += e.vx * dt;
      e.y += e.vy * dt;
    }
    if (e.t >= e.life) {
      effects.splice(i, 1);
      if (e.onEnd) e.onEnd();
    }
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
    } else if (e.kind === "flash") {
      ctx.globalAlpha = 0.32 * (1 - k);
      ctx.fillStyle = e.color;
      ctx.fillRect(-60, -60, W + 120, H + 120);
    } else if (e.kind === "flame") {
      ctx.globalCompositeOperation = "lighter";
      ctx.globalAlpha = (1 - k) * 0.8;
      const r = e.r * (0.6 + k * 0.9);
      const g = ctx.createRadialGradient(e.x, e.y, 0, e.x, e.y, r);
      g.addColorStop(0, "#ffffff");
      g.addColorStop(0.35, e.color);
      g.addColorStop(1, "rgba(0,0,0,0)");
      ctx.fillStyle = g;
      ctx.beginPath();
      ctx.arc(e.x, e.y, r, 0, Math.PI * 2);
      ctx.fill();
    } else if (e.kind === "projectile") {
      ctx.translate(e.x, e.y);
      ctx.rotate(e.angle);
      if (imgReady(e.img)) {
        const s = 120 / Math.max(e.img.naturalWidth, e.img.naturalHeight);
        ctx.drawImage(e.img, -e.img.naturalWidth * s / 2, -e.img.naturalHeight * s / 2, e.img.naturalWidth * s, e.img.naturalHeight * s);
      } else {
        ctx.fillStyle = e.color;
        ctx.beginPath();
        ctx.arc(0, 0, 22, 0, Math.PI * 2);
        ctx.fill();
      }
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
  const color = safeColor(a.color);
  // The panel counts it whenever it arrives – the app has started the viewer's cooldown either way –
  // but a fighter can only be cheered on while there is a fight to cheer.
  crowd.note(a.player, a.kind, who, color);
  if (!inRounds()) return;
  // Just over the head, but never up among the bars, where a name would be lost.
  // Three rows taken in turn, so a busy chat stacks its names instead of writing them on each other.
  f.popRow = ((f.popRow || 0) + 1) % 3;
  const headY = Math.max(350, stage.ground - 500 * f.size + f.popRow * 70 - 70);

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
    popText(f.special ? `${f.special.name.toUpperCase()} LADDAD!` : "SUPER LADDAD!", f.x, headY - 90, f.special ? f.special.color : "#ffd43b", 64, 1.8);
    shake(10);
  }
}

