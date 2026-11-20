// Derive palettes from hue triples; push lightness until the contrast targets hold.
function hsl2hex(h, s, l) {
  s /= 100; l /= 100;
  const k = (n) => (n + h / 30) % 12, a = s * Math.min(l, 1 - l);
  const f = (n) => l - a * Math.max(-1, Math.min(k(n) - 3, Math.min(9 - k(n), 1)));
  return '#' + [f(0), f(8), f(4)].map((x) => Math.round(x * 255).toString(16).padStart(2, '0')).join('');
}
function lum(hex) {
  const [r, g, b] = [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16) / 255).map((c) => (c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4));
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}
const contrast = (a, b) => { const [hi, lo] = [lum(a), lum(b)].sort((x, y) => y - x); return (hi + 0.05) / (lo + 0.05); };
// Move lightness (darker or lighter) until fg meets min contrast on every bg given.
function fit(h, s, l, dir, min, ...bgs) {
  for (let i = 0; i < 80; i++) {
    const hex = hsl2hex(h, s, l);
    if (bgs.every((bg) => contrast(hex, bg) >= min)) return hex;
    l += dir === 'darker' ? -1 : 1;
  }
  return hsl2hex(h, s, l);
}
function scheme([A, B, C]) {
  const light = {};
  light.bg = hsl2hex(A, 14, 94); light.surface = hsl2hex(A, 18, 98); light.rule = hsl2hex(A, 12, 82); light.empty = hsl2hex(A, 12, 87);
  light.ink = hsl2hex(A, 28, 11);
  light.muted = fit(A, 10, 42, 'darker', 4.5, light.bg, light.surface);
  light.accent = fit(A, 62, 38, 'darker', 4.5, '#ffffff'); light.accentInk = '#ffffff';
  light.second = fit(B, 60, 42, 'darker', 4.5, light.bg, light.surface);
  light.third = fit(C, 72, 44, 'darker', 4.5, light.bg, light.surface);
  const dark = {};
  dark.bg = hsl2hex(A, 14, 9); dark.surface = hsl2hex(A, 12, 14); dark.rule = hsl2hex(A, 10, 24); dark.empty = hsl2hex(A, 10, 20);
  dark.ink = hsl2hex(A, 14, 92);
  dark.muted = fit(A, 8, 58, 'lighter', 4.5, dark.bg, dark.surface);
  dark.accentInk = hsl2hex(A, 40, 8);
  dark.accent = fit(A, 48, 52, 'lighter', 4.5, dark.accentInk);
  dark.second = fit(B, 62, 58, 'lighter', 4.5, dark.bg, dark.surface);
  dark.third = fit(C, 78, 58, 'lighter', 4.5, dark.bg, dark.surface);
  return { light, dark };
}
const PALETTES = [
  { id: 'current', name: 'Windows Default', kind: 'Current', hues: [145, 215, 10],
    concept: "What the app looks like now: Windows' own gray surfaces with your Windows accent color (green on your PC). Nothing in the app picks a color; Windows does. The loop tools use a fixed blue, green and red.",
    light: { bg: '#f3f3f3', surface: '#fbfbfb', ink: '#1a1a1a', muted: '#5f5f5f', rule: '#e0e0e0', accent: '#00b058', accentInk: '#ffffff', second: '#2f80ed', third: '#e0553d', empty: '#e6e6e6' },
    dark:  { bg: '#202020', surface: '#2b2b2b', ink: '#f3f3f3', muted: '#a0a0a0', rule: '#3a3a3a', accent: '#00b058', accentInk: '#ffffff', second: '#5b9bf5', third: '#ef7b66', empty: '#333333' } },
  { id: 'moss', name: 'Moss', kind: 'Analogous', hues: [135, 90, 165],
    concept: 'Green with its warm neighbors: yellow-green for playback and a spring green for the attention color. Everything stays in the forest family. Calm and the closest to what the app is today.' },
  { id: 'lagoon', name: 'Lagoon', kind: 'Analogous', hues: [150, 180, 210],
    concept: 'Green leaning cool: teal for playback, a clear blue for "unsaved" and other flags. Reads as water and moss. The most modern-feeling of the analogous options.' },
  { id: 'ember', name: 'Violet & Ember', kind: 'Triadic', hues: [145, 275, 25], recommended: true,
    concept: 'Green, violet and orange, 120 degrees apart. Green is selection and the main buttons, violet is everything about playback (the timeline, the position dot), orange flags what needs attention (unsaved changes, the loop start). Each job gets its own color.' },
  { id: 'spring', name: 'Spring', kind: 'Triadic', hues: [95, 215, 335],
    concept: 'Fresh lime-green, a bright blue and a magenta. The most colorful and the most playful, and the hardest to keep calm across a long song list.' },
];
for (const p of PALETTES) if (!p.light) Object.assign(p, scheme(p.hues));
const CHECKS = (c) => [
  ['Text', contrast(c.ink, c.bg), 4.5],
  ['Muted text', contrast(c.muted, c.surface), 4.5],
  ['Button text', contrast(c.accentInk, c.accent), 4.5],
  ['Accent on window', contrast(c.accent, c.bg), 3],
  ['Playback color', contrast(c.second, c.surface), 4.5],
  ['Attention color', contrast(c.third, c.bg), 4.5],
];
if (process.argv[2] === 'json') { console.log(JSON.stringify(PALETTES)); }
else for (const p of PALETTES) for (const m of ['light', 'dark']) {
  const c = p[m];
  console.log(p.name.padEnd(16), m.padEnd(6), Object.entries(c).map(([k, v]) => `${k}:${v}`).join(' '));
  console.log('   ', CHECKS(c).map(([l, r, min]) => `${l} ${r.toFixed(1)}${r < min ? ' FAIL' : ''}`).join(' | '));
}
