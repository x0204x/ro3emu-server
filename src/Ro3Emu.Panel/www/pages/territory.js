// The territory war and siege map (/territory): the zones of the war with the guilds that hold them, the stage in force
// and the next battles, and the siege cities. The client keeps its territory map's art in a UI prefab no table names, so
// the map is a schematic of the zones' own tree (GVGWarZoneConfig): the capital in the middle, its regions around it,
// each region's strongholds and fields around the region.
'use strict';

const SVGNS = 'http://www.w3.org/2000/svg';
function svg(tag, attrs, ...children) {
  const node = document.createElementNS(SVGNS, tag);
  for (const [key, value] of Object.entries(attrs || {})) if (value != null) node.setAttribute(key, String(value));
  for (const child of children.flat(Infinity)) if (child != null) node.append(child instanceof Node ? child : document.createTextNode(String(child)));
  return node;
}

/** The time left until a moment, in words: "2d 4h", "35m". */
const until = iso => duration(Math.max(0, (new Date(iso).getTime() - Date.now()) / 1000));

/** Positions of the schematic: the capital in the middle, regions on a ring, their zones fanned outwards. */
function territoryLayout(zones) {
  const at = new Map(), size = 640, c = size / 2;
  const capitals = zones.filter(z => z.parent === 0);
  capitals.forEach((z, i) => at.set(z.id, { x: c + i * 60, y: c, r: 30 }));
  const regions = zones.filter(z => z.kind === 'Region');
  regions.forEach((z, i) => {
    const a = -Math.PI / 2 + i * 2 * Math.PI / Math.max(regions.length, 1);
    at.set(z.id, { x: c + Math.cos(a) * 170, y: c + Math.sin(a) * 170, r: 22, a });
    const kids = zones.filter(k => k.parent === z.id).sort((p, q) => (p.kind === 'Stronghold' ? 0 : 1) - (q.kind === 'Stronghold' ? 0 : 1) || p.id - q.id);
    const span = Math.PI * 1.1;
    kids.forEach((k, j) => {
      const b = a - span / 2 + (kids.length > 1 ? j * span / (kids.length - 1) : span / 2);
      const rr = k.kind === 'Stronghold' ? 92 : 112;
      at.set(k.id, { x: at.get(z.id).x + Math.cos(b) * rr, y: at.get(z.id).y + Math.sin(b) * rr, r: k.kind === 'Stronghold' ? 13 : 8 });
    });
  });
  return { at, size };
}

function schematic(zones) {
  const { at, size } = territoryLayout(zones);
  const edges = zones.filter(z => z.parent && at.has(z.id) && at.has(z.parent)).map(z => {
    const [p, q] = [at.get(z.parent), at.get(z.id)];
    return svg('line', { class: 'edge', x1: p.x, y1: p.y, x2: q.x, y2: q.y });
  });
  const nodes = zones.filter(z => at.has(z.id)).map(z => {
    const p = at.get(z.id);
    const circle = svg('circle', { class: 'node' + (z.owner ? '' : ' neutral'), cx: p.x, cy: p.y, r: p.r, fill: z.owner ? guildColor(z.owner.id) : null });
    const label = z.kind === 'Field' ? null
      : z.kind === 'Stronghold' ? svg('text', { class: 'label small', x: p.x, y: p.y + 3.5 }, z.name.replace(/^\D+/, ''))
        : svg('text', { class: 'label', x: p.x, y: p.y + p.r + 17 }, z.name);
    const halo = z.battle ? svg('circle', { class: 'halo', cx: p.x, cy: p.y, r: p.r + 4 }) : null;
    const tip = svg('title', null, `${z.name} (${z.kind})` + (z.owner ? ` · held by ${z.owner.name}` : ' · neutral') + (z.battle ? ' · battle now' : ''));
    return svg('a', { href: z.mapId ? '/maps/' + z.mapId : null, 'data-link': z.mapId ? '' : null, 'aria-label': tip.textContent }, tip, halo, circle, label);
  });
  // Regions and the capital on top, so their labels stay readable over the fans.
  nodes.sort((p, q) => (p.querySelector('circle.node').getAttribute('r') - q.querySelector('circle.node').getAttribute('r')));
  // The drawing's own bounds, with room for the labels under the nodes.
  const points = [...at.values()];
  const x0 = points.length ? Math.min(...points.map(p => p.x - p.r)) - 48 : 0, x1 = points.length ? Math.max(...points.map(p => p.x + p.r)) + 48 : size;
  const y0 = points.length ? Math.min(...points.map(p => p.y - p.r)) - 16 : 0, y1 = points.length ? Math.max(...points.map(p => p.y + p.r)) + 28 : size;
  return svg('svg', { class: 'territory-map', viewBox: `${x0} ${y0} ${x1 - x0} ${y1 - y0}`, role: 'img', 'aria-label': 'Territory map: the capital, its regions and their strongholds and fields' },
    edges, nodes);
}

function territoryHolders(zones, castles) {
  const by = new Map();
  for (const z of zones.filter(z => z.owner)) {
    const row = by.get(z.owner.id) || { guild: z.owner, zones: 0, castles: 0 };
    row.zones++;
    by.set(z.owner.id, row);
  }
  for (const c of castles.filter(c => c.defender)) if (by.has(c.defender.id)) by.get(c.defender.id).castles++;
  const rows = [...by.values()].sort((a, b) => b.zones - a.zones || a.guild.name.localeCompare(b.guild.name));
  if (!rows.length) return emptyState('map', 'All land is neutral', 'A guild that wins a territory battle holds its zone.');
  return body(h('ul', { class: 'legend' }, rows.map(r => {
    const sw = h('i', { class: 'swatch' });
    sw.style.background = guildColor(r.guild.id);
    return h('li', null, sw, guildName(r.guild), h('span', { class: 'num' }, plural(r.zones, 'zone')));
  })));
}

function castleTable(castles, siege) {
  return castles.length
    ? table([
      { label: 'City', grow: true, value: c => [h('b', null, c.name), h('span', { class: 'sub' }, c.kind)] },
      { label: 'Defender', value: c => [c.defender ? guildName(c.defender) : h('span', { class: 'muted' }, 'None'),
        c.attacker ? h('span', { class: 'sub' }, 'attacked by ' + c.attacker.name) : null] },
      { label: 'State', value: c => c.state === 'Fighting' ? chip('banned', c.stage ? 'Siege · ' + c.stage : 'Siege') : c.state === 'Over' ? chip('', 'Over') : chip('plain', siege ? 'In ' + until(siege.start) : 'Not open') },
    ], castles, { short: true })
    : emptyState('shield', 'No siege city', null);
}

function zoneTable(zones) {
  const shown = zones.filter(z => z.kind !== 'Field' || z.owner || z.battle);
  return table([
    { label: 'Zone', grow: true, value: z => mapName(z.mapId, z.name, z.image) },
    { label: 'Kind', opt: true, value: z => chip(z.kind === 'Capital' ? 'event' : z.kind === 'Region' ? 'info' : 'plain', z.kind) },
    { label: 'Held by', value: z => z.owner ? guildName(z.owner) : h('span', { class: 'muted' }, 'Neutral') },
    { label: 'Battle', opt: true, value: z => z.battle
      ? [chip('banned', z.battle.players ? `${z.battle.players} fighting` : 'Battle'), h('span', { class: 'sub' }, z.battle.scores.slice(0, 3).map(s => `${s.guild.name} ${num(s.score)}`).join(' · ') || 'no score yet')]
      : h('span', { class: 'muted' }, '-') },
  ], shown);
}

async function territoryPage() {
  const t = await api('GET', '/api/territory');
  const fighting = t.zones.filter(z => z.battle).length;
  const coming = t.battles.filter(b => new Date(b.end) > new Date(t.now));
  const siege = t.siege;
  const kpis = h('section', { class: 'kpis', 'aria-label': 'War state' },
    kpi('Stage now', 'clock', t.stage.kind, null, t.stage.id ? t.stage.name + ' · ends in ' + until(t.stage.end) : 'no war block scheduled'),
    kpi('Next', 'arrow', t.next ? t.next.kind : '-', null, t.next ? t.next.name + ' · in ' + until(t.next.start) : 'nothing scheduled'),
    kpi('Battles now', 'pulse', num(fighting), fighting === 1 ? 'zone' : 'zones', fighting ? 'scores below' : 'no battle running', { warn: fighting > 0 }),
    kpi('Siege', 'shield', siege ? (siege.running ? 'Running' : until(siege.start)) : '-', null,
      siege ? (siege.running ? 'ends in ' + until(siege.end) : 'until the next siege') : 'no siege scheduled', { warn: !!siege?.running }));
  const legend = h('div', { class: 'map-legend' },
    h('span', null, h('i', { class: 'swatch' }), 'Neutral'), h('span', null, h('i', { class: 'ring' }), 'Battle now'),
    h('span', null, 'Large: capital and regions · medium: strongholds · small: fields'));
  const schedule = coming.length
    ? table([
      { label: 'Battle', grow: true, value: b => h('b', null, b.name) },
      { label: 'Starts', value: b => stamp(b.start) },
      { label: 'Ends', opt: true, value: b => stamp(b.end) },
    ], coming, { short: true })
    : emptyState('clock', 'No battle scheduled', 'The war\'s calendar comes from the game data.');
  return [pageHead({ crumbs: [{ href: '/', label: 'Home' }, { href: '/guilds', label: 'Guilds' }, { label: 'Territory map' }], title: 'Territory map',
    subtitle: 'Who holds the land, the war\'s stage and the sieges, as the server runs them. Refreshed every 5 seconds at most.',
    actions: [guildTabs('territory'), h('button', { type: 'button', class: 'btn secondary sm', onclick: route }, 'Refresh')] }),
  kpis,
  h('div', { class: 'grid' },
    h('div', { class: 'span-8' }, card('The land', null, body(schematic(t.zones)), legend)),
    h('div', { class: 'span-4 stack' },
      card('Holders', null, territoryHolders(t.zones, t.castles)),
      card('Siege cities', null, castleTable(t.castles, siege)),
      card('Coming battles', null, schedule))),
  h('div', { class: 'grid' }, h('div', { class: 'span-12' }, card('Zones', null, zoneTable(t.zones), 'Fields are listed while a guild holds one or fights for it.')))];
}

routes.push([/^\/territory$/, territoryPage, 'guilds']);
