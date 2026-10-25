// Writes docs/assets/progress.svg, the "What works today" bars in the README. Edit the list, then:
//   node tools/render-progress.mjs
import { writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const areas = [
  ['World, combat and bosses', 100, 'done'],
  ['Story dungeons', 100, 'done'],
  ['Character progress', 100, 'done'],
  ['Guilds', 100, 'done'],
  ['Survivor Battle', 35, 'now'],
  ['War of Emperium', 25, 'next'],
  ['Tavern and smaller features', 15, 'next'],
  ['Remaining client tables', 10, 'next'],
  ['Tested with many players', 5, 'next'],
];
const look = {
  done: { color: '#3fb950', label: 'Done' },
  now: { color: '#d8b46a', label: 'In progress' },
  next: { color: '#8b949e', label: 'Next' },
};

const row = 40, top = 30, pad = 32, width = 900, nameW = 260, labelW = 110, barW = 900 - 64 - nameW - labelW - 40;
const height = top * 2 + areas.length * row - 12;
const font = `font-family="'Segoe UI', -apple-system, Helvetica, Arial, sans-serif"`;
const lines = areas.map(([name, pct, state], i) => {
  const y = top + i * row, { color, label } = look[state];
  const fill = Math.max(0, Math.min(100, pct)) / 100 * barW;
  return `  <text x="${pad}" y="${y + 18}" ${font} font-size="16" font-weight="600" fill="#e6edf3">${name}</text>
  <rect x="${pad + nameW}" y="${y + 6}" width="${barW}" height="12" rx="6" fill="#21262d"/>
  <rect x="${pad + nameW}" y="${y + 6}" width="${fill.toFixed(1)}" height="12" rx="6" fill="${color}"/>
  <text x="${pad + nameW + barW + 20}" y="${y + 18}" ${font} font-size="14" font-weight="700" fill="${color}">${label}</text>`;
});
const svg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${width} ${height}" width="${width}" height="${height}" role="img" aria-label="Progress by area: ${areas.map(([n, , s]) => `${n} ${look[s].label}`).join(', ')}">
  <rect x="0.5" y="0.5" width="${width - 1}" height="${height - 1}" rx="12" fill="#0d1117" stroke="#30363d"/>
${lines.join('\n')}
</svg>
`;
const out = fileURLToPath(new URL('../docs/assets/progress.svg', import.meta.url));
writeFileSync(out, svg);
console.log(`wrote ${out}`);
