// Turns a chroma-green sprite sheet from the image model into a clean, evenly spaced strip:
// keyed alpha, each pose found as its own connected figure (so a raised fist poking into the row
// above never leaks into another frame), every pose scaled alike, standing on one baseline and
// anchored on the torso.
// usage: node process-fighter.js sheet.png sprite.webp [frames]
// Writes sprite.webp (the strip the app reads) and sprite.preview.png (the strip over grey, to judge by eye).
const sharp = require('sharp');

const [, , input, output, framesArg] = process.argv;
const FRAMES = Number(framesArg || 8);
const CW = 640, CH = 560, BASE = CH - 10, STAND_H = 470;

function key(data, w, h) {
  const a = new Uint8Array(w * h);
  for (let i = 0; i < w * h; i++) {
    const r = data[i * 4], g = data[i * 4 + 1], b = data[i * 4 + 2];
    const spill = g - Math.max(r, b);
    let alpha = spill <= 30 ? 255 : spill >= 90 ? 0 : Math.round(255 * (90 - spill) / 60);
    if (g < 90) alpha = 255;
    a[i] = alpha;
    if (spill > 0) data[i * 4 + 1] = Math.max(r, b); // despill the green fringe
    data[i * 4 + 3] = alpha;
  }
  return a;
}

function components(alpha, w, h) {
  const label = new Int32Array(w * h).fill(-1);
  const comps = [];
  const stack = [];
  for (let start = 0; start < w * h; start++) {
    if (alpha[start] < 40 || label[start] >= 0) continue;
    const id = comps.length;
    const c = { id, area: 0, x0: 1e9, y0: 1e9, x1: -1, y1: -1, sx: 0, sy: 0 };
    label[start] = id;
    stack.push(start);
    while (stack.length) {
      const p = stack.pop();
      const x = p % w, y = (p - x) / w;
      c.area++; c.sx += x; c.sy += y;
      if (x < c.x0) c.x0 = x; if (x > c.x1) c.x1 = x; if (y < c.y0) c.y0 = y; if (y > c.y1) c.y1 = y;
      for (let dy = -1; dy <= 1; dy++) for (let dx = -1; dx <= 1; dx++) {
        const nx = x + dx, ny = y + dy;
        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
        const q = ny * w + nx;
        if (alpha[q] >= 40 && label[q] < 0) { label[q] = id; stack.push(q); }
      }
    }
    c.cx = c.sx / c.area; c.cy = c.sy / c.area;
    comps.push(c);
  }
  return { label, comps };
}

// Horizontal anchor: centroid of the torso band, so a punch or kick does not drag the body sideways.
function torsoX(owned, w, b) {
  let sum = 0, n = 0;
  const ya = b.y0 + Math.round((b.y1 - b.y0) * 0.25), yb = b.y0 + Math.round((b.y1 - b.y0) * 0.55);
  for (let y = ya; y <= yb; y++) for (let x = b.x0; x <= b.x1; x++)
    if (owned(y * w + x)) { sum += x; n++; }
  return n ? sum / n : (b.x0 + b.x1) / 2;
}

(async () => {
  const { data, info } = await sharp(input).ensureAlpha().raw().toBuffer({ resolveWithObject: true });
  const { width: w, height: h } = info;
  const alpha = key(data, w, h);
  const { label, comps } = components(alpha, w, h);

  const sorted = [...comps].sort((a, b) => b.area - a.area);
  const bodies = sorted.slice(0, FRAMES);
  if (bodies.length < FRAMES || bodies[FRAMES - 1].area < sorted[0].area * 0.1)
    throw new Error(`expected ${FRAMES} figures, sizes: ${sorted.slice(0, FRAMES + 2).map(c => c.area).join(',')}`);

  // Reading order: rows by vertical centre, then left to right.
  const rowSplit = (Math.min(...bodies.map(b => b.cy)) + Math.max(...bodies.map(b => b.cy))) / 2;
  bodies.sort((a, b) => ((a.cy > rowSplit) - (b.cy > rowSplit)) || a.cx - b.cx);

  // Stray specks (a loose strand of hair, a shoelace) join the nearest figure that encloses them.
  const owner = new Int32Array(comps.length).fill(-1);
  bodies.forEach((b, i) => { owner[b.id] = i; b.frame = i; b.members = [b]; });
  for (const c of comps) {
    if (owner[c.id] >= 0 || c.area < 12) continue;
    let best = null, bestD = 40;
    for (const b of bodies) {
      const dx = Math.max(b.x0 - c.cx, 0, c.cx - b.x1), dy = Math.max(b.y0 - c.cy, 0, c.cy - b.y1);
      const d = Math.hypot(dx, dy);
      if (d < bestD) { bestD = d; best = b; }
    }
    if (best) { owner[c.id] = best.frame; best.members.push(c); }
  }

  const standing = bodies[0];
  const scale = STAND_H / (standing.y1 - standing.y0 + 1);
  const layers = [];
  const meta = [];
  for (const body of bodies) {
    const b = { x0: Math.min(...body.members.map(m => m.x0)), y0: Math.min(...body.members.map(m => m.y0)),
                x1: Math.max(...body.members.map(m => m.x1)), y1: Math.max(...body.members.map(m => m.y1)) };
    const bw = b.x1 - b.x0 + 1, bh = b.y1 - b.y0 + 1;
    const owned = p => label[p] >= 0 && owner[label[p]] === body.frame;
    const crop = Buffer.alloc(bw * bh * 4);
    for (let y = 0; y < bh; y++) for (let x = 0; x < bw; x++) {
      const p = (b.y0 + y) * w + (b.x0 + x);
      if (!owned(p)) continue;
      data.copy(crop, (y * bw + x) * 4, p * 4, p * 4 + 4);
    }
    const sw = Math.max(1, Math.round(bw * scale)), sh = Math.max(1, Math.round(bh * scale));
    let img = await sharp(crop, { raw: { width: bw, height: bh, channels: 4 } }).resize(sw, sh, { kernel: 'lanczos3' }).png().toBuffer();
    const ax = (torsoX(owned, w, b) - b.x0) * scale;
    const left = Math.round(CW / 2 - ax), top = BASE - sh;
    // Clip anything that would leave the cell instead of failing the whole sheet.
    const cl = Math.max(0, -left), ct = Math.max(0, -top);
    const cr = Math.max(0, left + sw - CW), cb = Math.max(0, top + sh - CH);
    if (cl || ct || cr || cb) img = await sharp(img).extract({ left: cl, top: ct, width: sw - cl - cr, height: sh - ct - cb }).png().toBuffer();
    layers.push({ input: img, left: body.frame * CW + Math.max(0, left), top: Math.max(0, top) });
    meta.push({ frame: body.frame, size: [bw, bh], parts: body.members.length, clipped: !!(cl || ct || cr || cb) });
  }

  await sharp({ create: { width: CW * FRAMES, height: CH, channels: 4, background: { r: 0, g: 0, b: 0, alpha: 0 } } })
    .composite(layers).webp({ quality: 88, alphaQuality: 100 }).toFile(output);
  // A preview over flat grey so the keying can be judged by eye.
  const preview = await sharp({ create: { width: CW * FRAMES, height: CH, channels: 4, background: { r: 90, g: 90, b: 100, alpha: 1 } } })
    .composite([{ input: await sharp(output).png().toBuffer() }]).png().toBuffer();
  await sharp(preview).resize(Math.round(CW * FRAMES / 3)).png().toFile(output.replace(/\.webp$/, '.preview.png'));
  console.log(JSON.stringify({ frames: FRAMES, cell: [CW, CH], baseline: BASE, scale: +scale.toFixed(3), meta }));
})().catch(e => { console.error(e); process.exit(1); });
