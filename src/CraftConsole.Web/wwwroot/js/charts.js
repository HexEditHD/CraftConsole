// CYANOTYPE chart primitives — plotted, not illustrated.
// The gauge is a fine arc inside a dashed reference ring, with the value
// set in mono. Values are always real text as well, so colour never
// carries the reading alone.
const NS = 'http://www.w3.org/2000/svg';

function el(tag, attrs = {}) {
  const n = document.createElementNS(NS, tag);
  for (const [k, v] of Object.entries(attrs)) n.setAttribute(k, v);
  return n;
}

function cssVar(name) {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
}

/** Percent → status colour, resolved to a real value so it works as an
 *  SVG presentation attribute (which does not reliably accept var()).
 *  Near-monochrome by intent: plain ink for normal load, and the muted
 *  status hues only once a threshold is actually crossed. */
export function thresholdColor(percent) {
  if (percent >= 88) return cssVar('--bad')  || '#ff6f61';
  if (percent >= 68) return cssVar('--warn') || '#e8b24a';
  return cssVar('--n-700') || '#bed3e5';
}

/** Fine arc gauge inside a dashed reference ring. */
export function gauge(percent, { size = 104, label = '' } = {}) {
  const p = Math.max(0, Math.min(100, percent));
  const c = size / 2;
  const r = c - 8;
  const start = 135, sweep = 270;
  const arcLen = (2 * Math.PI * r) * (sweep / 360);

  const svg = el('svg', { viewBox: `0 0 ${size} ${size}`, width: size, height: size, role: 'img' });

  const polar = (deg, rr) => {
    const rad = (deg - 90) * Math.PI / 180;
    return [c + rr * Math.cos(rad), c + rr * Math.sin(rad)];
  };
  const [x1, y1] = polar(start, r);
  const [x2, y2] = polar(start + sweep, r);
  const d = `M${x1.toFixed(2)},${y1.toFixed(2)} A${r},${r} 0 1 1 ${x2.toFixed(2)},${y2.toFixed(2)}`;

  // Dashed reference ring — the drawn envelope the value is measured into.
  svg.append(el('path', {
    d, fill: 'none', stroke: cssVar('--grid') || '#16395c',
    'stroke-width': 1, 'stroke-dasharray': '2 3',
  }));
  // Solid measured value.
  svg.append(el('path', {
    d, fill: 'none', stroke: thresholdColor(p), 'stroke-width': 2.5,
    'stroke-dasharray': `${(arcLen * p / 100).toFixed(2)} ${arcLen.toFixed(2)}`,
    style: 'transition: stroke-dasharray .2s, stroke .2s',
  }));

  const num = el('text', {
    x: c, y: c, 'text-anchor': 'middle', 'dominant-baseline': 'middle',
    fill: cssVar('--ink') || '#ecf4fb',
    'font-size': '21', 'font-weight': '600',
    'font-family': "'JBMono', ui-monospace, monospace",
  });
  num.textContent = `${Math.round(p)}%`;

  const sub = el('text', {
    x: c, y: c + 18, 'text-anchor': 'middle',
    fill: cssVar('--ink-low') || '#7396b4',
    'font-size': '8.5', 'letter-spacing': '1.4',
    'font-family': "'JBMono', ui-monospace, monospace",
  });
  sub.textContent = label.toUpperCase();

  svg.append(num, sub);
  return svg;
}
