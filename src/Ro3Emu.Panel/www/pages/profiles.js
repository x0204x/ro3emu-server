// Public character profiles (/profiles/<name>) and guild pages (/guilds, /guilds/<id>): what other players may see,
// read from the server (/api/profiles, /api/guilds). No bags, purse, position or mail ever reach these pages.
'use strict';

/** A guild's badge: the design over the frame, or the name's initial on a tint without pictures. */
function emblem(guild, size) {
  const e = guild.emblem || {};
  const box = h('span', { class: 'emblem' + (size ? ' ' + size : ''), 'aria-hidden': 'true' });
  const layers = [e.frame, e.design].filter(Boolean);
  if (!layers.length) { box.append(glyph(guild.name, '', '')); return box; }
  let failed = 0;
  for (const src of layers) {
    const img = h('img', { src, alt: '', loading: 'lazy', decoding: 'async' });
    img.addEventListener('error', () => { img.remove(); if (++failed === layers.length) box.append(glyph(guild.name, '', '')); }, { once: true });
    box.append(img);
  }
  return box;
}

/** A guild's name with its badge, linking to its page. */
const guildName = (guild, size) => guild
  ? link('/guilds/' + guild.id, { class: 'named' }, emblem(guild, size), h('span', null, guild.name))
  : h('span', { class: 'muted' }, '-');

/** A character's name linking to its public profile. */
const profileLink = (name, attrs) => link('/profiles/' + encodeURIComponent(name), { class: 'row-link', ...attrs }, name);

/** A guild's colour on the territory map: one hue per guild id, spread by the golden angle. */
const guildHue = id => Math.round((Number(id) * 137.508) % 360);
const guildColor = id => `hsl(${guildHue(id)} 62% 58%)`;

const guildTabs = current => h('div', { class: 'seg', role: 'tablist', 'aria-label': 'Guilds and territory' },
  link('/guilds', { role: 'tab', 'aria-selected': String(current === 'guilds') }, icon('flag'), 'Guilds'),
  link('/territory', { role: 'tab', 'aria-selected': String(current === 'territory') }, icon('map'), 'Territory map'));

function rankTable(ranks) {
  return ranks.length
    ? table([
      { label: 'Board', grow: true, value: r => link('/rankings?board=' + encodeURIComponent(r.board), { class: 'text-link strong' }, r.title) },
      { label: '#', num: true, value: r => h('span', { class: 'rank' + (r.rank <= 3 ? ' r' + r.rank : '') }, r.rank) },
      { label: 'Score', num: true, opt: true, value: r => [num(r.score), h('span', { class: 'sub' }, r.scoreName)] },
    ], ranks, { short: true })
    : emptyState('trophy', 'Not on a board yet', 'The top 100 of each board are listed.');
}

function achievementCard(a) {
  if (!a) return card('Achievements', null, emptyState('sparkle', 'No achievements yet', null));
  const level = h('div', { class: 'named plain' }, thumb(a.icon, a.levelName || 'A', 'q5', 'lg'),
    h('div', null, h('b', null, a.levelName || 'Level ' + a.level), h('span', { class: 'sub' }, `${plural(a.points, 'point')} · ${num(a.reached)} of ${num(a.total)} reached`)));
  return card('Achievements', null, [
    body(level, a.types.length ? h('ul', { class: 'badges' }, a.types.map(t => h('li', null, thumb(t.icon, t.name, 'q3'),
      h('div', null, h('b', null, t.name), h('span', { class: 'sub' }, t.levelName || 'Level ' + t.level))))) : null),
    a.recent.length ? table([
      { label: 'Latest', grow: true, wrap: true, value: r => [h('b', null, r.name), r.description && h('span', { class: 'sub' }, r.description)] },
      { label: 'When', opt: true, value: r => r.reached ? day(r.reached) : '' },
    ], a.recent, { short: true }) : null]);
}

async function profilePage(name) {
  const p = await api('GET', '/api/profiles/' + encodeURIComponent(name));
  const crumbs = [{ href: '/', label: 'Home' }, { label: 'Profiles' }, { label: p.name }];
  const head = pageHead({ crumbs, title: p.name, art: thumb(p.classIcon, p.class, 'cls', 'xl'),
    after: [p.online ? chip('online', 'Online') : chip('', 'Offline'), chip('plain', p.class)],
    meta: [['Guild', p.guild ? guildName(p.guild) : '-'], p.position && ['Position', p.position], p.title && ['Title', p.title.name]].filter(Boolean) });
  const kpis = h('section', { class: 'kpis', 'aria-label': 'Key figures' },
    kpi('Base level', 'trophy', num(p.level), null, p.class),
    kpi('Job level', 'sparkle', num(p.jobLevel), null, 'class skills'),
    kpi('Achievements', 'star', p.achievements ? num(p.achievements.points) : '-', p.achievements ? 'points' : null,
      p.achievements ? `${num(p.achievements.reached)} reached` : p.private ? 'private' : 'none yet'),
    kpi('Title', 'shield', p.title ? num(p.title.level) : '-', null, p.title ? p.title.name : p.private ? 'private' : 'no title rank yet'));
  if (p.private)
    return [head, kpis, card(null, null, h('div', { class: 'private-note' }, icon('key'),
      h('p', null, 'This player chose not to show their details to others in the game, so their equipment, achievements and ranks stay private here too.')))];
  return [head, kpis, h('div', { class: 'grid' },
    h('div', { class: 'span-8 stack' },
      card('Equipment', null, equipmentTable(p.equipment), 'Strengthen and refine levels belong to the slot, as in the game.'),
      achievementCard(p.achievements)),
    h('div', { class: 'span-4 stack' },
      card('Rankings', link('/rankings', { class: 'more' }, 'All boards', icon('arrow')), rankTable(p.ranks)),
      p.guild && card('Guild', link('/guilds/' + p.guild.id, { class: 'more' }, 'Guild page', icon('arrow')),
        body(facts([['Name', guildName(p.guild)], ['Level', h('span', { class: 'num' }, num(p.guild.level))], ['Position', p.position || '-']])))))];
}

async function guildsPage() {
  const query = queryOf();
  const { guilds } = await api('GET', '/api/guilds');
  const text = (query.q || '').toLowerCase();
  const shown = text ? guilds.filter(g => g.guild.name.toLowerCase().includes(text) || g.leader.toLowerCase().includes(text)) : guilds;
  const rows = shown.length
    ? table([
      { label: '#', value: g => h('span', { class: 'rank' + (guilds.indexOf(g) < 3 ? ' r' + (guilds.indexOf(g) + 1) : '') }, guilds.indexOf(g) + 1) },
      { label: 'Guild', grow: true, value: g => guildName(g.guild) },
      { label: 'Leader', opt: true, value: g => g.leader ? profileLink(g.leader, { class: 'text-link' }) : '-' },
      { label: 'Level', num: true, value: g => num(g.guild.level) },
      { label: 'Members', num: true, value: g => num(g.members) },
      { label: 'Prestige', num: true, opt: true, value: g => num(g.prestige) },
      { label: 'Territories', num: true, opt: true, value: g => g.territories ? num(g.territories) : h('span', { class: 'muted' }, '-') },
      { label: 'League', opt: true, value: g => g.league ? chip('info', g.league) : h('span', { class: 'muted' }, '-') },
    ], shown, { onrow: g => navigate('/guilds/' + g.guild.id) })
    : emptyState('users', text ? 'No guild matches' : 'No guilds yet', text ? 'Try part of the name.' : 'Guilds founded in the game show up here.');
  return [pageHead({ crumbs: [{ href: '/', label: 'Home' }, { label: 'Guilds' }], title: 'Guilds',
    subtitle: 'Every guild of the realm by prestige, with the land it holds.', actions: [guildTabs('guilds'), densityToggle()] }),
  h('section', { class: 'card' }, h('div', { class: 'card-head' }, filterForm('/guilds', query, [], 'Guild or leader')), rows,
    h('div', { class: 'card-foot' }, h('span', null, mono(num(shown.length)), shown.length === 1 ? ' guild' : ' guilds')))];
}

async function guildPage(id) {
  const g = await api('GET', '/api/guilds/' + encodeURIComponent(id));
  const s = g.summary;
  const members = table([
    { label: 'Name', grow: true, value: m => profileLink(m.name) },
    classCol, levelCol,
    { label: 'Position', opt: true, value: m => m.position },
    { label: 'Merit', num: true, opt: true, value: m => m.merit ? num(m.merit) : h('span', { class: 'muted' }, '-') },
    { label: 'Status', value: m => m.online ? chip('online', 'Online') : chip('', 'Offline') },
  ], g.members, { onrow: m => navigate('/profiles/' + encodeURIComponent(m.name)) });
  const land = g.zones.length || g.castles.length
    ? [g.zones.length ? table([
      { label: 'Territory', grow: true, value: z => mapName(z.mapId, z.name, z.image) },
      { label: 'Kind', value: z => chip(z.kind === 'Capital' ? 'event' : z.kind === 'Region' ? 'info' : 'plain', z.kind) },
    ], g.zones, { short: true }) : null,
    g.castles.length ? body(facts(g.castles.map(c => [c, chip('active', 'Defends')]))) : null]
    : emptyState('map', 'No land held', 'Territory battles and sieges decide who holds the land.');
  return [pageHead({ crumbs: [{ href: '/', label: 'Home' }, { href: '/guilds', label: 'Guilds' }, { label: s.guild.name }], title: s.guild.name,
    art: emblem(s.guild, 'xl'), after: [chip('plain', 'Level ' + s.guild.level), s.league && chip('info', s.league)],
    meta: [['Leader', s.leader ? profileLink(s.leader, { class: 'text-link' }) : '-'], ['Founded', day(g.created)], ['ID', mono(String(s.guild.id))]],
    actions: [link('/territory', { class: 'btn secondary sm' }, icon('map'), 'Territory map'), densityToggle()] }),
  h('section', { class: 'kpis', 'aria-label': 'Key figures' },
    kpi('Members', 'users', num(s.members), null, `${num(g.members.filter(m => m.online).length)} online now`),
    kpi('Prestige', 'trophy', num(s.prestige), null, 'guild prestige'),
    kpi('Territories', 'map', num(s.territories), null, g.castles.length ? `defends ${plural(g.castles.length, 'city', 'cities')}` : 'no city defended'),
    kpi('Battle Merit', 'shield', num(g.merit), null, 'the members\' this season')),
  h('div', { class: 'grid' },
    h('div', { class: 'span-8 stack' }, card('Members', null, members)),
    h('div', { class: 'span-4 stack' },
      g.announcement && card('Declaration', null, body(h('p', { class: 'desc' }, g.announcement))),
      card('Land', link('/territory', { class: 'more' }, 'Map', icon('arrow')), land),
      card('Guild league', null, g.standing
        ? body(facts([['Tier', chip('info', g.standing.tier)], ['Group', g.standing.group ? num(g.standing.group) : '-'],
          ['Place', g.standing.place ? num(g.standing.place) : '-'], ['Strength', h('span', { class: 'num' }, num(g.standing.strength))]]))
        : emptyState('trophy', 'Not in the league yet', null)),
      card('Rankings', null, rankTable(g.ranks))))];
}

routes.push([/^\/profiles\/([^/]+)$/, profilePage, 'guilds'], [/^\/guilds$/, guildsPage, 'guilds'], [/^\/guilds\/(\d+)$/, guildPage, 'guilds']);
titles.guilds = 'Guilds';
