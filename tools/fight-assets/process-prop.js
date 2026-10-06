// Cuts a prop – something a fighter throws, like Zelda's Molotov – out of an image from the model:
// keyed, trimmed to the prop itself, scaled to at most 256 px, and saved with transparency.
//
//   node process-prop.js in.png prop.webp [--magenta]
//
// Also writes prop.preview.png (on a checkerboard). The page spins and scales the prop itself, so
// only its look matters here, not its angle.
const sharp = require('sharp');
const { key, keyColorFrom } = require('./chroma');

const [, , input, output, ...flags] = process.argv;
if (!input || !output) {
  console.error('usage: node process-prop.js in.png prop.webp [--magenta]');
  process.exit(1);
}
const MAX = 256;

(async () => {
  const { data, info } = await sharp(input).ensureAlpha().raw().toBuffer({ resolveWithObject: true });
  const { width: w, height: h } = info;
  key(data, w, h, keyColorFrom(flags));

  // The prop's own box: every pixel that is more than faintly there.
  let x0 = w, y0 = h, x1 = -1, y1 = -1;
  for (let y = 0; y < h; y++) for (let x = 0; x < w; x++)
    if (data[(y * w + x) * 4 + 3] > 40) { x0 = Math.min(x0, x); y0 = Math.min(y0, y); x1 = Math.max(x1, x); y1 = Math.max(y1, y); }
  if (x1 < 0) throw new Error('nothing left after keying – wrong background colour?');

  const pad = 4;
  const left = Math.max(0, x0 - pad), top = Math.max(0, y0 - pad);
  const cw = Math.min(w, x1 + pad + 1) - left, ch = Math.min(h, y1 + pad + 1) - top;
  const scale = Math.min(1, MAX / Math.max(cw, ch));
  const keyed = await sharp(data, { raw: { width: w, height: h, channels: 4 } })
    .extract({ left, top, width: cw, height: ch }).png().toBuffer();
  await sharp(keyed).resize(Math.round(cw * scale), Math.round(ch * scale)).webp({ quality: 90, alphaQuality: 100 }).toFile(output);

  const meta = await sharp(output).metadata();
  const board = Buffer.alloc(meta.width * meta.height * 4);
  for (let y = 0; y < meta.height; y++) for (let x = 0; x < meta.width; x++) {
    const v = ((x >> 4) + (y >> 4)) % 2 ? 200 : 140;
    board.set([v, v, v + 10, 255], (y * meta.width + x) * 4);
  }
  await sharp(board, { raw: { width: meta.width, height: meta.height, channels: 4 } })
    .composite([{ input: await sharp(output).png().toBuffer() }]).png()
    .toFile(output.replace(/\.webp$/, '.preview.png'));
  console.log(JSON.stringify({ size: [meta.width, meta.height] }));
})().catch(e => { console.error(e.message || e); process.exit(1); });
