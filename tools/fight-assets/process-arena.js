// Turns an arena picture from the image model into the arena.webp the wait screen reads.
//
//   node process-arena.js in.png arena.webp            keys out chroma green (#00FF00) – the parts
//                                                      that were green show the stream through
//   node process-arena.js in.png arena.webp --opaque   a full backdrop, only converted and sized
//
// Also writes arena.preview.png (the arena over a checkerboard, so the keying can be judged by eye)
// and prints a guess at "floor" for arena.json: where, as a fraction of the height, the fighters'
// feet should go. Check the guess against the preview – it is a starting point, not an answer.
const sharp = require('sharp');

const [, , input, output, flag] = process.argv;
if (!input || !output) {
  console.error('usage: node process-arena.js in.png arena.webp [--opaque]');
  process.exit(1);
}
const opaque = flag === '--opaque';
const MAX_W = 1920;

// Same key as the fighters: alpha from how much greener than red and blue a pixel is, with the
// green fringe on the edges pulled back to grey so nothing glows green over the stream.
function key(data, w, h) {
  for (let i = 0; i < w * h; i++) {
    const r = data[i * 4], g = data[i * 4 + 1], b = data[i * 4 + 2];
    const spill = g - Math.max(r, b);
    let alpha = spill <= 30 ? 255 : spill >= 90 ? 0 : Math.round(255 * (90 - spill) / 60);
    if (g < 90) alpha = 255;
    if (spill > 0) data[i * 4 + 1] = Math.max(r, b);
    data[i * 4 + 3] = alpha;
  }
}

// Specks of leftover colour floating in the empty area – a few pixels the model dithered – are
// dropped, so the stream is not dotted with them.
function dropSpecks(data, w, h, minArea) {
  const seen = new Uint8Array(w * h);
  const stack = [];
  for (let start = 0; start < w * h; start++) {
    if (seen[start] || data[start * 4 + 3] < 40) continue;
    const members = [];
    seen[start] = 1;
    stack.push(start);
    while (stack.length) {
      const p = stack.pop();
      members.push(p);
      const x = p % w, y = (p - x) / w;
      for (let dy = -1; dy <= 1; dy++) for (let dx = -1; dx <= 1; dx++) {
        const nx = x + dx, ny = y + dy;
        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
        const q = ny * w + nx;
        if (!seen[q] && data[q * 4 + 3] >= 40) { seen[q] = 1; stack.push(q); }
      }
    }
    if (members.length < minArea) for (const p of members) data[p * 4 + 3] = 0;
  }
}

// The first solid row in the middle fifth of the picture, below the top third: the near edge of
// whatever the fighters stand on. Feet go a little below it, on the surface rather than its rim.
function guessFloor(data, w, h) {
  const x0 = Math.round(w * 0.4), x1 = Math.round(w * 0.6);
  for (let y = Math.round(h / 3); y < h; y++) {
    let solid = 0;
    for (let x = x0; x < x1; x++) if (data[(y * w + x) * 4 + 3] > 200) solid++;
    if (solid > (x1 - x0) * 0.9) return Math.min(0.95, y / h + 0.035);
  }
  return null;
}

(async () => {
  let img = sharp(input).ensureAlpha();
  const meta = await img.metadata();
  if (meta.width > MAX_W) img = img.resize(MAX_W);
  const { data, info } = await img.raw().toBuffer({ resolveWithObject: true });
  const { width: w, height: h } = info;

  if (!opaque) {
    key(data, w, h);
    dropSpecks(data, w, h, Math.round(w * h / 20000));
  }

  await sharp(data, { raw: { width: w, height: h, channels: 4 } })
    .webp({ quality: 86, alphaQuality: 100 }).toFile(output);

  // Checkerboard preview: transparent parts are obvious, and so is a green fringe.
  const cell = 24;
  const board = Buffer.alloc(w * h * 4);
  for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) {
    const v = ((x / cell | 0) + (y / cell | 0)) % 2 ? 200 : 140;
    board.set([v, v, v + 10, 255], (y * w + x) * 4);
  }
  // Two steps: sharp resizes before it composites, whatever order the calls are written in.
  const preview = await sharp(board, { raw: { width: w, height: h, channels: 4 } })
    .composite([{ input: await sharp(output).png().toBuffer() }]).png().toBuffer();
  await sharp(preview).resize(Math.round(w / 2)).png().toFile(output.replace(/\.webp$/, '.preview.png'));

  const floor = opaque ? null : guessFloor(data, w, h);
  console.log(JSON.stringify({ size: [w, h], transparent: !opaque, floorGuess: floor && +floor.toFixed(3) }));
})().catch(e => { console.error(e); process.exit(1); });
