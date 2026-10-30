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
      h('span', { class: 'sub' }, levels(m), m.monsters ? ` · ${m.monsters} ${m.monsters === 1 ? 'monster' : 'monsters'}` : '',
        m.players ? ` · ${m.players} online` : '')))))
    : emptyState('search', 'No map matches', 'Try part of the name.');
  return [pageHead({ crumbs: [{ href: '/', label: 'Home' }, { label: 'Maps' }], title: 'Maps',
    subtitle: 'Every map in the game, with the monsters that live there and who is on it now.' }),
  h('section', { class: 'card' }, h('div', { class: 'card-head' }, filters), body(tiles),
    h('div', { class: 'card-foot' }, h('span', null, mono(num(shown.length)), shown.length === 1 ? ' map' : ' maps')))];
}

async function mapPage(id) {
  const detail = await api('GET', '/api/maps/' + encodeURIComponent(id));
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
  return [pageHead({ crumbs: [{ href: '/', label: 'Home' }, { href: '/maps', label: 'Maps' }, { label: map.name }], title: map.name,
    meta: [['ID', mono(String(map.id))], ['Levels', map.monsters ? `${map.minLevel}-${map.maxLevel}` : '-'], ['Online', num(map.players)]] }),
  h('div', { class: 'grid' },
    h('div', { class: 'span-7 stack' },
      card('Monsters', null, monsters),
      card('Players here', null, players, 'Refreshed every 5 seconds at most.')),
    h('div', { class: 'span-5 stack' }, picture && card(null, null, body(picture))))];
}

routes.push([/^\/maps$/, mapsPage, 'maps'], [/^\/maps\/(\d+)$/, mapPage, 'maps']);
titles.maps = 'Maps';
