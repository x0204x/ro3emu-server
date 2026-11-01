// Classes and skills: every class with a skill tree (/classes), a class's trees laid out as the game's skill window
// does it, 7 columns (/classes/<id>), and a skill's every level with its numbers and prerequisites (/skills/<id>).
'use strict';

const rankLabels = { 1: 'Novice', 2: 'First job', 3: 'Second job', 4: 'Transcendent', 5: 'Third job' };

function classTile(c) {
  return link('/classes/' + c.id, { class: 'map-tile class-tile' },
    h('span', { class: 'class-art' }, thumb(c.icon, c.name, 'cls', 'xl')),
    h('b', null, c.name), h('span', { class: 'sub' }, rankLabels[c.rank] || '', c.skills ? ` · ${c.skills} skills` : ''));
}

async function classesPage() {
  const { classes } = await api('GET', '/api/classes');
  const ranks = [...new Set(classes.map(c => c.rank))].sort();
  return [pageHead({ crumbs: dbCrumbs({ label: 'Classes & skills' }), title: 'Classes & skills',
    subtitle: 'Every class with its skill tree. A class plays with its own tree and those of the classes before it.' }),
  h('div', { class: 'stack' }, ranks.map(rank => card(rankLabels[rank] || 'Rank ' + rank, null,
    body(h('ul', { class: 'map-tiles' }, classes.filter(c => c.rank === rank).map(c => h('li', null, classTile(c))))))))];
}

/** A tree as the skill window lays it out: 7 columns, a skill per cell, its icon, name and highest level. */
function skillGrid(tree) {
  const rows = Math.max(tree.rows, 1);
  const grid = h('div', { class: 'skill-grid', role: 'list' });
  for (let row = 1; row <= rows; row++)
    for (let col = 1; col <= 7; col++) {
      const cell = tree.cells.find(c => c.row === row && c.col === col);
      grid.append(cell
        ? link('/skills/' + cell.skill.id, { class: 'skill-cell' + (cell.skill.passive ? ' passive' : ''), role: 'listitem', title: cell.skill.name },
          thumb(cell.skill.icon, cell.skill.name, '', 'lg'), h('span', { class: 'n' }, cell.skill.name),
          h('span', { class: 'lv mono' }, 'Lv ' + cell.skill.maxLevel))
        : h('span', { class: 'skill-cell empty', 'aria-hidden': 'true' }));
    }
  return grid;
}

async function classPage(id) {
  const detail = await api('GET', '/api/classes/' + encodeURIComponent(id));
  const c = detail.class;
  const line = h('nav', { class: 'line', 'aria-label': 'Class line' }, detail.line.map((l, i) => [
    i > 0 && h('span', { class: 'sep', 'aria-hidden': 'true' }, '→'),
    l.id === c.id ? h('span', { class: 'named strong' }, thumb(l.icon, l.name, 'cls'), h('span', null, l.name))
      : link('/classes/' + l.id, { class: 'named' }, thumb(l.icon, l.name, 'cls'), h('span', null, l.name))]));
  const art = detail.bigIcon ? h('img', { class: 'picture class-big', src: detail.bigIcon, alt: c.name }) : null;
  if (art) art.addEventListener('error', () => art.remove(), { once: true });
  return [pageHead({ crumbs: dbCrumbs({ href: '/classes', label: 'Classes & skills' }, { label: c.name }), title: c.name,
    art: thumb(c.icon, c.name, 'cls', 'xl'), after: chip('plain', rankLabels[c.rank] || 'Class'),
    meta: [['ID', mono(String(c.id))], ['Own skills', num(c.skills)], ['Trees', num(detail.trees.length)]] }),
  h('div', { class: 'grid' },
    h('div', { class: 'span-8 stack' },
      detail.trees.length
        ? detail.trees.map(t => card(`${t.class.name} skills`,
          t.class.id === c.id ? h('span', { class: 'muted' }, rankLabels[t.class.rank] || '')
            : link('/classes/' + t.class.id, { class: 'more' }, 'Earlier class', icon('arrow')), body(skillGrid(t))))
        : card(null, null, emptyState('sparkle', 'This class has no skill tree', 'The game data gives it no skills.'))),
    h('div', { class: 'span-4 stack' },
      card('Class line', null, body(line, art)),
      detail.description.length ? card('About', null, body(h('p', { class: 'desc' }, runs(detail.description)))) : null,
      detail.next.length ? card('Advances to', null, body(h('ul', { class: 'map-tiles narrow' }, detail.next.map(n => h('li', null, classTile(n)))))) : null))];
}

/** The numbers of one level: cooldown, cast time, cost, prerequisites. */
function levelFacts(l) {
  return facts([
    ['Cooldown', l.cooldownSeconds ? h('span', { class: 'num' }, l.cooldownSeconds + ' s') : 'None'],
    ['Cast time', l.castSeconds ? h('span', { class: 'num' }, l.castSeconds + ' s') : 'Instant'],
    ['Cost', l.cost || 'Free'],
    ['Needs', l.requires.length ? h('span', { class: 'reqs' }, l.requires.map(r => requirement(r))) : 'Nothing'],
  ]);
}

function requirement(r) {
  return link('/skills/' + r.skillId, { class: 'named' + (r.level < 0 ? ' excl' : '') }, thumb(r.icon, r.name, ''),
    h('span', null, r.name + (r.level > 0 ? ' Lv ' + r.level : r.level < 0 ? ' (not with)' : '')));
}

async function skillPage(id) {
  const detail = await api('GET', '/api/skills/' + encodeURIComponent(id));
  const s = detail.skill;
  const params = new URLSearchParams(location.search);
  let current = Math.min(Math.max(Number(params.get('level')) || detail.levels.length, 1), detail.levels.length || 1);
  const text = h('div', { class: 'level-text' });
  const numbers = h('div');
  const show = level => {
    const l = detail.levels.find(x => x.level === level) || detail.levels[0];
    if (!l) return;
    current = l.level;
    text.replaceChildren(h('p', { class: 'desc' }, l.description.length ? runs(l.description) : 'The game gives no description.'));
    numbers.replaceChildren(levelFacts(l));
    for (const b of seg.querySelectorAll('button')) b.setAttribute('aria-pressed', String(Number(b.dataset.level) === current));
  };
  const seg = h('div', { class: 'seg', role: 'group', 'aria-label': 'Level' }, detail.levels.map(l =>
    h('button', { type: 'button', 'data-level': String(l.level), onclick: () => show(l.level) }, 'Lv ' + l.level)));
  show(current);
  const all = detail.levels.length > 1
    ? table([
      { label: 'Level', num: true, value: l => num(l.level) },
      { label: 'Description', grow: true, wrap: true, value: l => runs(l.description) },
      { label: 'Cooldown', num: true, opt: true, value: l => l.cooldownSeconds ? l.cooldownSeconds + ' s' : '-' },
      { label: 'Cost', opt: true, value: l => l.cost || '-' },
    ], detail.levels, { short: true })
    : null;
  return [pageHead({ crumbs: dbCrumbs({ href: '/classes', label: 'Classes & skills' },
    detail.class ? { href: '/classes/' + detail.class.id, label: detail.class.name } : null, { label: s.name }).filter(Boolean),
  title: s.name, art: thumb(s.icon, s.name, '', 'xl'),
  after: [chip(s.passive ? 'plain' : 'info', s.passive ? 'Passive' : 'Active'), detail.damageType && chip('plain', detail.damageType),
    detail.element && chip('plain', detail.element)],
  meta: [['ID', mono(String(s.id))], ['Max level', num(s.maxLevel)], ['Class', detail.class ? link('/classes/' + detail.class.id, { class: 'text-link' }, detail.class.name) : '-']] }),
  h('div', { class: 'grid' },
    h('div', { class: 'span-8 stack' },
      card('Description', detail.levels.length > 1 ? seg : null, body(text)),
      all && card('Every level', null, all)),
    h('div', { class: 'span-4 stack' },
      card(detail.levels.length > 1 ? 'This level' : 'Numbers', null, body(numbers)),
      card('Skill', null, body(facts([
        ['Type', s.passive ? 'Passive' : 'Active'], ['Damage', detail.damageType || '-'], ['Element', detail.element || '-'],
        ['Range', detail.rangeMeters ? h('span', { class: 'num' }, detail.rangeMeters + ' m') : '-'], ['Max level', h('span', { class: 'num' }, num(s.maxLevel))]]))),
      detail.unlocks.length ? card('Needed for', null, body(h('div', { class: 'reqs' }, detail.unlocks.map(requirement)))) : null))];
}

routes.push([/^\/classes$/, classesPage, 'database'], [/^\/classes\/(\d+)$/, classPage, 'database'], [/^\/skills\/(\d+)$/, skillPage, 'database']);
