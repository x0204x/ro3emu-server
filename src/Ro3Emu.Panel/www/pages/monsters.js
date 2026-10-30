// The monster database: every monster of the game's data, searchable, and a page per monster with its stats (the
// server's own HP formula), drops and spawn maps (/api/monsters). Rows the game repeats per map, quest or scene with
// the same numbers are one entry; when they drop differently, each drop list says where its rows spawn.
'use strict';

const rankOptions = [{ id: 'normal', name: 'Normal' }, { id: 'mini', name: 'Mini boss' }, { id: 'boss', name: 'Boss' }, { id: 'mvp', name: 'MVP' }];

async function monstersPage() {
  const query = queryOf();
  const page = Number(query.page) || 1;
  const result = await api('GET', '/api/monsters' + listHref('', { ...query, page }));
  const filters = filterForm('/monsters', query, [
    optionSelect('element', 'Element', result.elements, query.element, 'Every element'),
    optionSelect('race', 'Race', result.races, query.race, 'Every race'),
    optionSelect('size', 'Size', result.sizes, query.size, 'Every size'),
    optionSelect('rank', 'Rank', rankOptions, query.rank, 'Every rank'),
    levelRange(query),
  ], 'Name or ID');
  const rows = result.monsters.length
    ? table([
      { label: 'Monster', grow: true, value: m => [monsterName(m.id, m.name, m.rank, m.icon), variantsChip(m.variants)] },
      { label: 'ID', num: true, opt: true, value: m => mono(String(m.id)) },
      { label: 'Level', num: true, value: m => range(m.level, m.maxLevel) },
      { label: 'HP', num: true, value: m => range(m.hp, m.maxHp) },
      { label: 'Element', opt: true, value: m => m.elementName || '-' },
      { label: 'Race', opt: true, value: m => m.race || '-' },
      { label: 'Size', opt: true, value: m => m.size || '-' },
      { label: 'Rank', opt: true, value: m => rankChip(m.rank) || h('span', { class: 'muted' }, 'Normal') },
    ], result.monsters, { onrow: m => navigate('/monsters/' + m.id) })
    : emptyState('search', 'No monster matches', 'Try part of the name, or fewer filters.');
  return [pageHead({ crumbs: [{ href: '/', label: 'Home' }, { label: 'Monsters' }], title: 'Monsters',
    subtitle: 'Every monster in the game, with the HP and drops the server uses.', actions: densityToggle() }),
  h('section', { class: 'card' }, h('div', { class: 'card-head' }, filters), rows,
    h('div', { class: 'card-foot' }, listPager(result, p => listHref('/monsters', { ...query, page: p }), 'monster')))];
}

function respawn(seconds) {
  if (!seconds) return '-';
  if (seconds < 120) return `${seconds} s`;
  if (seconds < 7200) return `${Math.round(seconds / 60)} min`;
  return `${(seconds / 3600).toFixed(1).replace(/\.0$/, '')} h`;
}

/** A kill's loot as a table, or the reason there is none. When every row comes from the same variants (shared), the
 * card says so once instead of on each row. */
function lootTable(loot, m, rewards, shared) {
  return loot.length
    ? table([
      { label: 'Item', grow: true, value: d => [itemName(d.itemId, d.item, d.quality, false, d.icon),
        d.maps.length ? h('span', { class: 'sub' }, 'Only on ' + d.maps.join(', ')) : null,
        d.from && !shared ? h('span', { class: 'sub' }, 'Only from ' + d.from.join(', ')) : null] },
      { label: 'Count', num: true, value: d => countRange(d.min, d.max) },
      { label: 'Chance', num: true, value: d => percent(d.chance) },
    ], loot, { short: loot.length <= 12 })
    : emptyState('box', rewards.length ? 'No loot on the corpse' : 'It drops nothing', rewards.length ? 'Its kill pays out the MVP rewards above.'
      : m.rank !== 'normal' ? 'A boss of a dungeon or story scene: its rewards come with the clear, not the kill.' : null);
}

/** The drops of every monster of this name, merged; "Only from" names the variants when not all of them drop it. */
function dropsCard(detail, m, rewards) {
  const loot = detail.drops.filter(d => !d.kind);
  const key = d => (d.from || []).join(',');
  const shared = loot.length > 0 && loot.every(d => key(d) === key(loot[0])) ? loot[0].from : null;
  const note = 'Chance per kill that at least one drops; a player\'s level and class limits are not counted.'
    + (shared ? ` Of this name only ${shared.join(', ')} drop${shared.length === 1 ? 's' : ''} them.`
      : detail.variants.length > 1 ? ' Every monster of this name, merged: the best chance of them.' : '');
  return card('Drops', null, lootTable(loot, m, rewards, !!shared), loot.length ? note : null);
}

/** The monsters of this name: one row each, with where it spawns. */
function variantsCard(detail, m) {
  if (detail.variants.length <= 1) return null;
  return card('Monsters of this name', h('span', { class: 'muted' }, num(detail.variants.length) + ' variants'), table([
    { label: 'ID', value: v => v.id === m.id || v.ids.includes(m.id) ? h('span', { class: 'mono strong' }, String(v.id)) : link('/monsters/' + v.id, { class: 'text-link mono' }, String(v.id)) },
    { label: 'Level', num: true, value: v => num(v.level) },
    { label: 'HP', num: true, value: v => num(v.hp) },
    { label: 'Size', opt: true, value: v => v.size || '-' },
    { label: 'Rank', opt: true, value: v => rankChip(v.rank) || h('span', { class: 'muted' }, 'Normal') },
    { label: 'Where it spawns', grow: true, wrap: true, value: v => v.maps.length ? v.maps.join(', ') : h('span', { class: 'muted' }, 'No field spawn') },
  ], detail.variants, { short: detail.variants.length <= 12, onrow: v => navigate('/monsters/' + v.id) }));
}

async function monsterPage(id) {
  const detail = await api('GET', '/api/monsters/' + encodeURIComponent(id));
  const m = detail.monster;
  const stat = name => detail.stats.find(s => s.name === name)?.value ?? '-';
  const rewards = detail.drops.filter(d => d.kind);
  // MvpService's kinds in the order it hands them out.
  const kinds = ['First strike', 'Final blow', 'Top 10 parties', 'Participation', 'Rare winner'];
  const mvpRewards = rewards.length
    ? table([
      { label: 'Reward', value: d => chip(d.kind === 'Rare winner' ? 'event' : 'info', d.kind) },
      { label: 'Item', grow: true, value: d => itemName(d.itemId, d.item, d.quality, false, d.icon) },
      { label: 'Count', num: true, value: d => countRange(d.min, d.max) },
      { label: 'Chance', num: true, value: d => percent(d.chance) },
    ], [...rewards].sort((a, b) => kinds.indexOf(a.kind) - kinds.indexOf(b.kind) || b.chance - a.chance), { short: rewards.length <= 16 })
    : null;
  const spawns = detail.spawns.length
    ? table([
      { label: 'Map', grow: true, value: s => mapName(s.mapId, s.map, s.image) },
      { label: 'Alive at most', num: true, value: s => num(s.count) },
      { label: 'Respawn', num: true, value: s => respawn(s.respawnSeconds) },
    ], detail.spawns, { short: true, onrow: s => navigate('/maps/' + s.mapId) })
    : emptyState('home', 'No field spawn', 'It appears in dungeons, events or story scenes.');
  const portrait = m.icon ? h('div', { class: 'portrait' }, thumb(m.icon, m.name, m.rank !== 'normal' ? m.rank : '', 'xxl')) : null;
  return [pageHead({ crumbs: [{ href: '/', label: 'Home' }, { href: '/monsters', label: 'Monsters' }, { label: m.name }],
    title: m.name, art: thumb(m.icon, m.name, m.rank !== 'normal' ? m.rank : '', 'xl'),
    after: [rankChip(m.rank), m.elementName && chip('plain', m.elementName)], subtitle: variantLine(detail.ids),
    meta: [['ID', mono(String(m.id))], ['Race', m.race || '-'], ['Size', m.size || '-']] }),
  h('section', { class: 'kpis', 'aria-label': 'Key figures' },
    kpi('Level', 'trophy', num(m.level), null, rankNames[m.rank]),
    kpi('HP', 'pulse', num(m.hp), null, 'as the server computes it'),
    kpi('Attack', 'sparkle', stat('PATK'), null, 'MATK ' + stat('MATK')),
    kpi('Defense', 'shield', stat('PDEF'), null, 'MDEF ' + stat('MDEF'))),
  h('div', { class: 'grid' },
    h('div', { class: 'span-8 stack' },
      mvpRewards && card('MVP rewards', null, mvpRewards,
        'Per kill: first strike and final blow go to one player each, the top 10 parties and every participant get theirs, one rare winner is drawn. Each kind has a daily limit per player.'),
      variantsCard(detail, m),
      dropsCard(detail, m, rewards),
      card('Where it spawns', null, spawns)),
    h('div', { class: 'span-4 stack' },
      card('Monster', null, body(portrait, facts([['Element', m.elementName || '-'], ['Race', m.race || '-'], ['Size', m.size || '-'],
        ['Rank', rankNames[m.rank]]]))),
      card('Stats', null, body(facts([...detail.stats.map(s => [s.name, h('span', { class: 'num' }, s.value)]),
        ['Move speed', h('span', { class: 'num' }, num(detail.speed))]])))))];
}

routes.push([/^\/monsters$/, monstersPage, 'monsters'], [/^\/monsters\/(\d+)$/, monsterPage, 'monsters']);
titles.monsters = 'Monsters';
