// The game database's hub (/database) and the element table (/elements), with the pieces the other database pages
// share (pages/cards.js, crafting.js, skills.js, pets.js, shops.js, events.js, mvps.js): readable game texts, item
// amounts, local times and live countdowns. Built on app.js and pages/database.js.
'use strict';

/** A game text as runs [{ text, mark }]: the parts the game colours (the numbers of a level) in bold. */
function runs(list, tag = 'span') {
  return h(tag, { class: 'runs' }, (list || []).map(r => r.mark ? h('b', { class: 'hl' }, r.text) : r.text));
}

/** An item and its count as a small chip with its icon, linking to the item. */
function amount(a, opts = {}) {
  const content = [thumb(a.icon, a.item, 'q' + a.quality), h('span', null, a.item),
    a.count > 1 || opts.always ? h('b', { class: 'x' }, '×' + num(a.count)) : null, opts.after];
  return link('/items/' + a.itemId, { class: 'amount q' + a.quality }, content);
}

const amounts = (list, opts) => list && list.length ? h('div', { class: 'amounts' }, list.map(a => amount(a, opts))) : null;

/** A date in the viewer's own time zone (English names, 24-hour clock): "Fri 18:30", or with the date when it is not this week. */
const timeFormat = new Intl.DateTimeFormat('en-GB', { hour: '2-digit', minute: '2-digit' });
const weekdayFormat = new Intl.DateTimeFormat('en-GB', { weekday: 'short' });
const dayFormat = new Intl.DateTimeFormat('en-GB', { weekday: 'long', day: 'numeric', month: 'short' });
const localTime = iso => timeFormat.format(new Date(iso));
function localWhen(iso) {
  const d = new Date(iso);
  return h('span', { class: 'mono', title: d.toString() }, weekdayFormat.format(d) + ' ' + timeFormat.format(d));
}

/** "2d 4h", "3h 05m", "4m 09s": the time left until an instant. */
function timeLeft(ms) {
  const s = Math.max(0, Math.floor(ms / 1000));
  const d = Math.floor(s / 86400), hr = Math.floor(s % 86400 / 3600), m = Math.floor(s % 3600 / 60), sec = s % 60;
  if (d > 0) return `${d}d ${hr}h`;
  if (hr > 0) return `${hr}h ${pad(m)}m`;
  return `${m}m ${pad(sec)}s`;
}

/**
 * A live countdown to an instant: "in 3h 05m", then the text of done() once it passes. One timer moves every
 * countdown on the page: the nodes with a target time (data-until), nothing else of the class.
 */
function countdown(iso, prefix = 'in ', done = 'now') {
  const node = h('span', { class: 'countdown mono', 'data-until': String(new Date(iso).getTime()), 'data-prefix': prefix, 'data-done': done });
  tick(node);
  return node;
}
function tick(node) {
  const rest = Number(node.dataset.until) - Date.now();
  node.textContent = rest > 0 ? node.dataset.prefix + timeLeft(rest) : node.dataset.done;
  node.classList.toggle('done', rest <= 0);
}
setInterval(() => { for (const node of document.querySelectorAll('.countdown[data-until]')) tick(node); }, 1000);

/** A percentage of damage: "+50%", "-25%", "±0". */
const signed = p => p > 0 ? `+${p}%` : p < 0 ? `−${-p}%` : '±0';

/** An element's damage change as a coloured cell: more damage reads warm, less reads cool. */
function elementCell(percent) {
  const level = percent >= 50 ? 'up2' : percent > 0 ? 'up1' : percent <= -50 ? 'down2' : percent < 0 ? 'down1' : 'even';
  return h('span', { class: 'el ' + level }, signed(percent));
}

// ---------------------------------------------------------------- the hub

const sections = [
  ['/items', 'box', 'Items', 'Every item with its stats, drops, shops and recipes.'],
  ['/monsters', 'paw', 'Monsters', 'HP, stats, drops, EXP per kill and element weaknesses.'],
  ['/maps', 'map', 'Maps', 'Monsters, NPCs, connected maps and recommended levels.'],
  ['/cards', 'card', 'Cards', 'Every star\'s effects, level-up bonuses and who drops them.'],
  ['/crafting', 'hammer', 'Crafting', 'Cooking formulas and equipment crafting, with chances.'],
  ['/classes', 'sparkle', 'Classes & skills', 'Each class\'s skill trees and every level of every skill.'],
  ['/pets', 'paw', 'Pets', 'Growth, stars, skills and what each pet gives its owner.'],
  ['/mounts', 'pulse', 'Mounts', 'Speed bonuses, stats and the items that unlock them.'],
  ['/shops', 'coins', 'Shops', 'What every shop sells and for how much.'],
  ['/elements', 'shield', 'Elements & EXP', 'The element table and how a kill\'s EXP is scaled.'],
  ['/events', 'calendar', 'Event calendar', 'Today and the week ahead, in your own time.'],
  ['/mvps', 'crown', 'MVP timers', 'Each field MVP\'s last kill and next respawn, live.'],
];

async function databasePage() {
  return [pageHead({ crumbs: [{ href: '/', label: 'Home' }, { label: 'Database' }], title: 'Game database',
    subtitle: 'Everything here comes from the server\'s own game data and rules, and the live pages from the realm itself.' }),
  h('ul', { class: 'hub' }, sections.map(([href, iconName, title, text]) => h('li', null,
    link(href, { class: 'card hub-tile' }, h('span', { class: 'hub-icon' }, icon(iconName)), h('b', null, title), h('span', { class: 'sub' }, text)))))];
}

// ---------------------------------------------------------------- elements and EXP rules

async function elementsPage() {
  const rules = await api('GET', '/api/elements');
  const attackers = rules.elements.length ? rules.elements[0].attacks : [];
  const matrix = rules.elements.length
    ? h('div', { class: 'table-wrap short' }, h('table', { class: 'data el-matrix' },
      h('thead', null, h('tr', null, h('th', { scope: 'col' }, 'Defender ↓ / Attack →'), attackers.map(a => h('th', { scope: 'col', class: 'num' }, a.name)))),
      h('tbody', null, rules.elements.map(row => h('tr', null, h('th', { scope: 'row' }, row.name),
        row.attacks.map(a => h('td', { class: 'num' }, elementCell(a.percent))))))))
    : emptyState('shield', 'No element table', null);
  const gaps = table([
    { label: 'Player level − monster level', grow: true, value: g => g.fromGap === g.toGap ? signedGap(g.fromGap)
      : `${signedGap(g.fromGap)} to ${signedGap(g.toGap)}` },
    { label: 'EXP paid', num: true, value: g => g.percent + '%' },
  ], rules.levelGap, { short: true });
  const party = table([
    { label: 'Party members on the map', grow: true, value: p => num(p.members) },
    { label: 'EXP', num: true, value: p => p.percent + '%' },
  ], rules.party, { short: true });
  return [pageHead({ crumbs: dbCrumbs({ label: 'Elements & EXP' }), title: 'Elements & EXP',
    subtitle: 'The damage a hit of each element does to each element, and how the server scales the EXP of a kill.' }),
  card('Element table', null, matrix, 'Each row is the element of the one hit, each column the attack\'s. The server applies it to every hit (at least 10% of the damage).'),
  h('div', { class: 'grid' },
    h('div', { class: 'span-6' }, card('EXP by level gap', null, gaps, 'Base and Job EXP of a kill, by the killer\'s level against the monster\'s.')),
    h('div', { class: 'span-6' }, card('Party bonus', null, party, 'Members of the killer\'s party on the same map.')))];
}

const signedGap = g => g > 0 ? '+' + g : String(g);

routes.push([/^\/database$/, databasePage, 'database'], [/^\/elements$/, elementsPage, 'database']);
titles.database = 'Database';

/** A key figure whose value is a live node (a countdown): no tooltip of the node's text. */
function kpiLive(...args) {
  const node = kpi(...args);
  node.querySelector('.value b')?.removeAttribute('title');
  return node;
}
