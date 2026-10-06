// The chroma key the processing scripts share. Green is the usual background; magenta is for art
// that is green itself – a green bottle on a green background would be keyed away with it.
//
// Alpha comes from how far a pixel leans toward the key colour, and the fringe that leans that way
// on an edge is pulled back to neutral so nothing glows green (or pink) over the stream.

function spillOf(r, g, b, color) {
  return color === 'magenta' ? Math.min(r, b) - g : g - Math.max(r, b);
}

/** Keys data (RGBA, in place) and answers the alpha channel as its own array. */
function key(data, w, h, color = 'green') {
  const a = new Uint8Array(w * h);
  for (let i = 0; i < w * h; i++) {
    const r = data[i * 4], g = data[i * 4 + 1], b = data[i * 4 + 2];
    const spill = spillOf(r, g, b, color);
    let alpha = spill <= 30 ? 255 : spill >= 90 ? 0 : Math.round(255 * (90 - spill) / 60);
    // Dark pixels are ink, whatever their hue.
    if (color === 'magenta' ? Math.min(r, b) < 90 : g < 90) alpha = 255;
    a[i] = alpha;
    if (spill > 0) {
      if (color === 'magenta') {
        const cap = Math.max(g, Math.min(r, b) - spill);
        data[i * 4] = Math.min(r, cap + (r - Math.min(r, b)));
        data[i * 4 + 2] = Math.min(b, cap + (b - Math.min(r, b)));
      } else {
        data[i * 4 + 1] = Math.max(r, b);
      }
    }
    data[i * 4 + 3] = alpha;
  }
  return a;
}

/** "--magenta" anywhere among the arguments picks the magenta key. */
function keyColorFrom(args) {
  return args.includes('--magenta') ? 'magenta' : 'green';
}

module.exports = { key, keyColorFrom };
