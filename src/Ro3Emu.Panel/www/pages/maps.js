// The maps: every named map of the game's data with its picture, the levels of the monsters that spawn there and who
// is on it now; a page per map with its monsters and players (/api/maps).
'use strict';

async function mapsPage() {
  const query = queryOf();
  const { maps } = await api('GET', '/api/maps');
  const text = (query.q || '').toLowerCase();
  const shown = text ? maps.filter(m => m.name.toLowerCase().includes(text) || String(m.id) === text) : maps;
  const filters = filterForm('/maps', query, [], 'Map name or ID');
  const levels = m => m.monsters ? (m.minLevel === m.maxLevel ? 'Lv ' + m.minLevel : `Lv ${m.minLevel}-${m.maxLevel}`) : 'No monsters';
  const tiles = shown.length
    ? h('ul', { class: 'map-tiles' }, shown.map(m => h('li', null, link('/maps/' + m.id, { class: 'map-tile' },
      h('span', { class: 'map-art' }, thumb(m.image, m.name, 'map', 'fill')),
      h('b', null, m.name),
      h('span', { class: 'sub' }, levels(m), m.monsters ? ` · ${plural(m.monsters, 'monster')}` : '',
        m.players ? ` · ${m.players} online` : '')))))
    : emptyState('search', 'No map matches', 'Try part of the name.');
  return [pageHead({ crumbs: dbCrumbs({ label: 'Maps' }), title: 'Maps',
    subtitle: 'Every map in the game, with the monsters that live there and who is on it now.' }),
  h('section', { class: 'card' }, h('div', { class: 'card-head' }, filters), body(tiles),
    h('div', { class: 'card-foot' }, h('span', null, mono(num(shown.length)), shown.length === 1 ? ' map' : ' maps')))];
}

async function mapPage(id) {
  const [detail, world] = await Promise.all([api('GET', '/api/maps/' + encodeURIComponent(id)),
    api('GET', '/api/maps/' + encodeURIComponent(id) + '/world').catch(() => null)]);
  const map = detail.map;
  const monsters = detail.monsters.length
    ? table([
      { label: 'Monster', grow: true, value: s => monsterName(s.monster.id, s.monster.name, s.monster.rank, s.monster.icon) },
      { label: 'Level', num: true, value: s => num(s.monster.level) },
      { label: 'Rank', opt: true, value: s => rankChip(s.monster.rank) || h('span', { class: 'muted' }, 'Normal') },
      { label: 'Alive at most', num: true, opt: true, value: s => num(s.count) },
      { label: 'Respawn', num: true, value: s => respawn(s.respawnSeconds) },
    ], detail.monsters, { onrow: s => navigate('/monsters/' + s.monster.id) })
    : emptyState('paw', 'No monsters here', 'A town, or a map whose monsters come with a quest or an event.');
  const players = detail.players.length
    ? table([
      { label: 'Name', grow: true, value: p => h('span', { class: 'strong' }, p.name) },
      classCol, levelCol,
    ], detail.players, { short: true })
    : emptyState('users', 'Nobody is here right now', null);
  const picture = map.image ? h('img', { class: 'picture map', src: map.image, alt: map.name }) : null;
  if (picture) picture.addEventListener('error', () => picture.closest('.card')?.remove(), { once: true });
  return [pageHead({ crumbs: dbCrumbs({ href: '/maps', label: 'Maps' }, { label: map.name }), title: map.name,
    after: world ? chip('plain', world.type) : null,
    meta: [['ID', mono(String(map.id))], ['Levels', map.monsters ? `${map.minLevel}-${map.maxLevel}` : '-'],
      world && world.minLevel ? ['Recommended', `Lv ${world.minLevel}-${world.maxLevel}`] : null, ['Online', num(map.players)]].filter(Boolean) }),
  h('div', { class: 'grid' },
    h('div', { class: 'span-7 stack' },
      card('Monsters', null, monsters),
      world && npcCard(world),
      card('Players here', null, players, 'Refreshed every 5 seconds at most.')),
    h('div', { class: 'span-5 stack' }, picture && card(null, null, body(picture)), world && linksCard(world)))];
}

routes.push([/^\/maps$/, mapsPage, 'maps'], [/^\/maps\/(\d+)$/, mapPage, 'maps']);
titles.maps = 'Maps';

/** The NPCs placed on the map, with what each offers (its title, warps, windows and shop). */
function npcCard(world) {
  return card('NPCs', world.npcs.length ? h('span', { class: 'muted' }, plural(world.npcs.length, 'NPC')) : null,
    world.npcs.length
      ? table([
        { label: 'NPC', value: n => h('span', { class: 'named' }, thumb(n.icon, n.name, ''), h('span', null, n.name),
          n.count > 1 ? h('span', { class: 'chip plain variants-chip' }, '×' + n.count) : null) },
        { label: 'What it offers', grow: true, wrap: true, value: n => n.functions.length
          ? h('span', { class: 'fn' }, n.functions.map(f => n.shopId && f.startsWith('Shop: ') ? link('/shops/' + n.shopId, { class: 'chip info' }, f) : chip('plain', f)))
          : h('span', { class: 'muted' }, '-') },
      ], world.npcs, { short: world.npcs.length <= 14 })
      : emptyState('users', 'No NPCs here', null));
}

/** Where the map leads: its portals and world-map neighbours, the dungeon it belongs to, its floors. */
function linksCard(world) {
  const tile = l => h('li', null, link('/maps/' + l.mapId, { class: 'map-tile' }, h('span', { class: 'map-art' }, thumb(l.image, l.map, 'map', 'fill')),
    h('b', null, l.map), l.via.length ? h('span', { class: 'sub' }, l.via.join(' · ')) : null));
  const groups = [
    world.parent ? ['Part of', [world.parent]] : null,
    world.floors.length ? ['Floors', world.floors] : null,
    ['Connected maps', world.links],
  ].filter(Boolean);
  return card('Where it leads', world.kafra ? chip('info', 'Kafra destination') : null, body(groups.map(([label, list]) => [
    h('span', { class: 'label' }, label),
    list.length ? h('ul', { class: 'map-tiles narrow' }, list.map(tile)) : h('p', { class: 'muted' }, 'None in the game data')])));
}
