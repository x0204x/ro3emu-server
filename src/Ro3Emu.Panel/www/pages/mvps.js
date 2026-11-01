// The MVP timers (/mvps): every field MVP with its last kill and next respawn from the server's live state, counting
// down in the viewer's own time; the page asks again every 30 seconds while it is open.
'use strict';

function mvpState(m) {
  if (m.state === 'alive') return chip('online', 'Alive');
  if (m.state === 'respawning' && m.nextSpawn) return h('span', { class: 'chip event' }, countdown(m.nextSpawn, 'respawns in ', 'due now'));
  return chip('info', 'Spawning');
}

let mvpRefresh = 0;

async function mvpsPage() {
  const { mvps } = await api('GET', '/api/mvps');
  clearTimeout(mvpRefresh);
  mvpRefresh = setTimeout(() => { if (location.pathname === '/mvps') route(); }, 30000);
  const alive = mvps.filter(m => m.state === 'alive').length;
  const next = mvps.filter(m => m.nextSpawn).sort((a, b) => new Date(a.nextSpawn) - new Date(b.nextSpawn))[0];
  const rows = mvps.length
    ? table([
      { label: 'MVP', grow: true, value: m => monsterName(m.monster.id, m.monster.name, 'mvp', m.monster.icon) },
      { label: 'Level', num: true, value: m => num(m.level) },
      { label: 'Map', opt: true, value: m => mapName(m.mapId, m.map, m.mapImage) },
      { label: 'Status', value: mvpState },
      { label: 'Respawn at', opt: true, value: m => m.nextSpawn ? localWhen(m.nextSpawn) : h('span', { class: 'muted' }, '-') },
      { label: 'Last kill', opt: true, value: m => m.lastDeath ? h('span', { title: new Date(m.lastDeath).toString() }, ago(m.lastDeath)) : h('span', { class: 'muted' }, 'Not yet') },
      { label: 'Every', num: true, opt: true, value: m => respawn(m.respawnSeconds) },
    ], mvps, { onrow: m => navigate('/monsters/' + m.monster.id) })
    : emptyState('crown', 'No field MVPs', 'The game data lists none.');
  return [pageHead({ crumbs: dbCrumbs({ label: 'MVP timers' }), title: 'MVP timers',
    subtitle: 'Live from the realm: each field MVP comes back its respawn time after its last kill.',
    actions: [densityToggle(), h('button', { type: 'button', class: 'btn secondary sm', onclick: route }, 'Refresh')] }),
  h('section', { class: 'kpis', 'aria-label': 'MVPs' },
    kpi('Alive', 'crown', num(alive), null, `of ${num(mvps.length)} field MVPs`),
    kpi('Waiting', 'clock', num(mvps.filter(m => m.state === 'respawning').length), null, 'killed, coming back'),
    kpiLive('Next respawn', 'pulse', next ? countdown(next.nextSpawn, '', 'now') : '-', null, next ? next.monster.name + ' · ' + next.map : 'none waiting'),
    kpi('Respawn', 'map', mvps.length ? respawn(Math.min(...mvps.map(m => m.respawnSeconds))) : '-', null, 'after a kill')),
  card(null, null, rows, 'Times in your own time zone. Refreshed every 30 seconds while this page is open.')];
}

routes.push([/^\/mvps$/, mvpsPage, 'database']);
