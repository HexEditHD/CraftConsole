// Minimal SVG chart primitives: sparkline (single-series trend) and a radial
// gauge. Marks follow the house dataviz rules: 2px lines, one hue per chart,
// values rendered as text in text tokens (never in the series color alone),
// threshold status colors always accompanied by the visible number.
const SVG_NS = 'http://www.w3.org/2000/svg';

function svgEl(tag, attrs = {}) {
  const el = document.createElementNS(SVG_NS, tag);
  for (const [k, v] of Object.entries(attrs)) el.setAttribute(k, v);
  return el;
}

// SVG presentation attributes (stroke, fill, stop-color) don't reliably resolve
// var() the way a style declaration does, so callers that don't pass an explicit
// color get the accent resolved to a real value here.
function accentColor() {
  return getComputedStyle(document.documentElement).getPropertyValue('--color-accent').trim() || '#9184d9';
}

/**
 * Single-series sparkline with soft area fill.
 * @param values numeric array (oldest → newest)
 */
export function sparkline(values, { width = 180, height = 36, max = null, color = accentColor() } = {}) {
  const svg = svgEl('svg', {
    viewBox: `0 0 ${width} ${height}`,
    width: '100%',
    height,
    preserveAspectRatio: 'none',
    role: 'img',
  });

  if (values.length < 2) {
    const line = svgEl('line', {
      x1: 0, y1: height - 1.5, x2: width, y2: height - 1.5,
      stroke: 'rgba(255,255,255,.08)', 'stroke-width': 2,
    });
    svg.append(line);
    return svg;
  }

  const top = max ?? Math.max(...values, 1e-9);
  const pad = 3;
  const stepX = width / (values.length - 1);
  const y = v => pad + (1 - Math.min(v / top, 1)) * (height - pad * 2);

  const points = values.map((v, i) => `${(i * stepX).toFixed(2)},${y(v).toFixed(2)}`);
  const linePath = `M${points.join(' L')}`;
  const areaPath = `${linePath} L${width},${height} L0,${height} Z`;

  const gradId = `sg-${Math.random().toString(36).slice(2, 8)}`;
  const grad = svgEl('linearGradient', { id: gradId, x1: 0, y1: 0, x2: 0, y2: 1 });
  const s1 = svgEl('stop', { offset: '0%', 'stop-color': color, 'stop-opacity': '.22' });
  const s2 = svgEl('stop', { offset: '100%', 'stop-color': color, 'stop-opacity': '0' });
  grad.append(s1, s2);
  const defs = svgEl('defs');
  defs.append(grad);

  const area = svgEl('path', { d: areaPath, fill: `url(#${gradId})` });
  const line = svgEl('path', {
    d: linePath, fill: 'none', stroke: color,
    'stroke-width': 2, 'stroke-linejoin': 'round', 'stroke-linecap': 'round',
  });

  const [lastX, lastY] = points[points.length - 1].split(',');
  const dot = svgEl('circle', { cx: lastX, cy: lastY, r: 2.5, fill: color });

  const title = svgEl('title');
  title.textContent = `latest ${values[values.length - 1].toFixed(1)} · peak ${Math.max(...values).toFixed(1)}`;

  svg.append(defs, area, line, dot, title);
  return svg;
}

function cssVar(name) {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
}

/**
 * Percent → status color. Pressure reads as tonal value on the neutral ramp,
 * not hue — a resolved value works both as an inline style and as an SVG
 * presentation attribute, unlike a bare var() string.
 */
export function thresholdColor(percent) {
  if (percent >= 88) return cssVar('--color-neutral-100');
  if (percent >= 68) return cssVar('--color-neutral-300');
  return accentColor();
}

/**
 * Radial gauge (270° arc). The numeric value is drawn as real text in text
 * tokens; the arc color carries the threshold state.
 */
export function gauge(percent, { size = 112, label = '' } = {}) {
  const p = Math.max(0, Math.min(100, percent));
  const r = size / 2 - 8;
  const c = size / 2;
  const startAngle = 135, sweep = 270;
  const arcLen = (2 * Math.PI * r) * (sweep / 360);

  const polar = deg => {
    const rad = (deg - 90) * Math.PI / 180;
    return [c + r * Math.cos(rad), c + r * Math.sin(rad)];
  };
  const [x1, y1] = polar(startAngle);
  const [x2, y2] = polar(startAngle + sweep);
  const d = `M${x1.toFixed(2)},${y1.toFixed(2)} A${r},${r} 0 1 1 ${x2.toFixed(2)},${y2.toFixed(2)}`;

  const svg = svgEl('svg', { viewBox: `0 0 ${size} ${size}`, width: size, height: size, role: 'img' });

  const track = svgEl('path', {
    d, fill: 'none', stroke: cssVar('--color-neutral-900'), 'stroke-width': 6, 'stroke-linecap': 'round',
  });

  const value = svgEl('path', {
    d, fill: 'none', stroke: thresholdColor(p), 'stroke-width': 6, 'stroke-linecap': 'round',
    'stroke-dasharray': `${(arcLen * p / 100).toFixed(2)} ${arcLen.toFixed(2)}`,
    style: 'transition: stroke-dasharray var(--t2), stroke var(--t2)',
  });

  const num = svgEl('text', {
    x: c, y: c + 1, 'text-anchor': 'middle', 'dominant-baseline': 'middle',
    fill: cssVar('--color-text'), 'font-size': '21', 'font-weight': '500',
    'font-family': 'inherit',
  });
  num.textContent = `${Math.round(p)}%`;

  const sub = svgEl('text', {
    x: c, y: c + 18, 'text-anchor': 'middle',
    fill: cssVar('--muted-2'), 'font-size': '9', 'letter-spacing': '.1em',
  });
  sub.textContent = label.toUpperCase();

  svg.append(track, value, num, sub);
  return svg;
}
