// Emits the two Lagoon theme dictionaries (WPF XAML) from the Lagoon scheme in gen.js, plus accent shades.
const fs = require('fs');
const { execSync } = require('child_process');
const lagoon = JSON.parse(execSync('node ' + JSON.stringify(require('path').join(__dirname, 'gen.js')) + ' json').toString()).find((p) => p.id === 'lagoon');
function hex2hsl(hex) {
  let [r, g, b] = [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16) / 255);
  const max = Math.max(r, g, b), min = Math.min(r, g, b); let h = 0, s = 0; const l = (max + min) / 2;
  if (max !== min) { const d = max - min; s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
    h = max === r ? (g - b) / d + (g < b ? 6 : 0) : max === g ? (b - r) / d + 2 : (r - g) / d + 4; h *= 60; }
  return [h, s * 100, l * 100];
}
function hsl2hex(h, s, l) { s /= 100; l /= 100; const k = (n) => (n + h / 30) % 12, a = s * Math.min(l, 1 - l);
  const f = (n) => l - a * Math.max(-1, Math.min(k(n) - 3, Math.min(9 - k(n), 1)));
  return '#' + [f(0), f(8), f(4)].map((x) => Math.round(x * 255).toString(16).padStart(2, '0')).join('').toUpperCase(); }
const shade = (hex, dl) => { const [h, s, l] = hex2hsl(hex); return hsl2hex(h, s, Math.max(0, Math.min(100, l + dl))); };
const up = (s) => s.toUpperCase();
const lum = (h) => { const [r, g, b] = [1, 3, 5].map((i) => parseInt(h.slice(i, i + 2), 16) / 255).map((x) => (x <= 0.03928 ? x / 12.92 : ((x + 0.055) / 1.055) ** 2.4)); return 0.2126 * r + 0.7152 * g + 0.0722 * b; };
const cr = (a, b) => { const [hi, lo] = [lum(a), lum(b)].sort((x, y) => y - x); return (hi + 0.05) / (lo + 0.05); };
// text drawn on a colored flag: white or near-black, whichever reads better
const on = (bg) => (cr('#FFFFFF', bg) >= cr('#0D1311', bg) ? '#FFFFFF' : '#0D1311');
// Second color, chosen by the user on the color options page (cobalt), fitted to 4.5:1 on the Lagoon backgrounds.
const SECOND = { light: '#1B54DA', dark: '#5E89ED' };
function xaml(mode, c) {
  const a = up(c.accent);
  const two = SECOND[mode];
  // Fluent picks different shades per theme (Dark1 in light, Light2 in dark); give both a sensible value.
  const shades = mode === 'light'
    ? { Dark3: shade(a, -14), Dark2: shade(a, -7), Dark1: a, Light1: shade(a, 8), Light2: shade(a, 16), Light3: shade(a, 24) }
    : { Dark3: shade(a, -30), Dark2: shade(a, -22), Dark1: shade(a, -14), Light1: shade(a, -6), Light2: a, Light3: shade(a, 8) };
  const colors = [
    ['AccentColor', a], ...Object.entries(shades).map(([k, v]) => ['AccentColor' + k, up(v)]),
    ['SystemAccentColor', a], ...Object.entries(shades).map(([k, v]) => ['SystemAccentColor' + k, up(v)]),
    ['ApplicationBackgroundColor', up(c.bg)],
    // The second color (cobalt): listening and noticing. Hover/pressed shades are for the filled Play buttons.
    ['LagoonSecondColor', two], ['LagoonSecondHoverColor', shade(two, 6)], ['LagoonSecondPressedColor', shade(two, -6)], ['LagoonOnSecondColor', on(two)],
  ];
  const brushes = [
    ['LagoonAccentBrush', a], ['LagoonOnAccentBrush', up(c.accentInk)], ['LagoonSecondBrush', two], ['LagoonOnSecondBrush', on(two)],
    ['LagoonEndBrush', mode === 'light' ? '#C4472F' : '#EF7B66'],
    ['LagoonEmptyBrush', up(c.empty)], ['LagoonMutedBrush', up(c.muted)], ['LagoonRuleBrush', up(c.rule)],
    ['LagoonSurfaceBrush', up(c.surface)], ['LagoonInkBrush', up(c.ink)], ['LagoonOnEndBrush', on(mode === 'light' ? '#C4472F' : '#EF7B66')], ['ApplicationBackgroundBrush', up(c.bg)], ['LagoonWindowBrush', up(c.bg)],
  ];
  return `<!-- Lagoon colors, ${mode} (generated from the palette page; see PLAN.md). ThemeService copies these into Application.Resources,
     so they override the Fluent theme's accent colors. Brushes use literal colors on purpose: they are copied, not referenced. -->
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
${colors.map(([k, v]) => `    <Color x:Key="${k}">${v}</Color>`).join('\n')}
${brushes.map(([k, v]) => `    <SolidColorBrush x:Key="${k}" Color="${v}" />`).join('\n')}
</ResourceDictionary>
`;
}

const dir = require('path').join(__dirname, '..', '..', 'src', 'App', 'Themes');
fs.mkdirSync(dir, { recursive: true });
fs.writeFileSync(dir + '/Lagoon.Light.xaml', xaml('light', lagoon.light));
fs.writeFileSync(dir + '/Lagoon.Dark.xaml', xaml('dark', lagoon.dark));
console.log(fs.readFileSync(dir + '/Lagoon.Light.xaml', 'utf8'));
