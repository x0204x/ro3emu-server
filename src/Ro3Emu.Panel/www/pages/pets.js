// Pets (/pets, /pets/<id>): growth, every star's step with its power, bonuses and skills; mounts (/mounts): the speed
// each one adds, its other stats and the items that lend it.
'use strict';

const campNames = { 1: 'Camp I', 2: 'Camp II', 3: 'Camp III', 4: 'Camp IV' };

async function petsPage() {
  const query = queryOf();
  const { pets } = await api('GET', '/api/pets');
  const text = (query.q || '').toLowerCase();
  const qualities = [...new Map(pets.map(p => [p.quality, { id: p.quality, name: p.qualityName }])).values()].sort((a, b) => b.id - a.id);
  const shown = pets.filter(p => (!text || p.name.toLowerCase().includes(text)) && (!query.quality || String(p.quality) === query.quality));
  const filters = filterForm('/pets', query, [optionSelect('quality', 'Quality', qualities, query.quality, 'Every quality')], 'Pet name');
  const tiles = shown.length
    ? h('ul', { class: 'map-tiles pet-tiles' }, shown.map(p => h('li', null, link('/pets/' + p.id, { class: 'map-tile' },
      h('span', { class: 'map-art pet-art' }, thumb(p.picture || p.icon, p.name, 'q' + p.quality, 'fill')),
      h('b', { class: 'q' + p.quality }, p.name), h('span', { class: 'sub' }, p.qualityName, ' · ', campNames[p.camp] || '')))))
    : emptyState('search', 'No pet matches', null);
  return [pageHead({ crumbs: dbCrumbs({ label: 'Pets' }), title: 'Pets',
    subtitle: 'Every pet: how it grows, what each star gives you and its skills.' }),
  h('section', { class: 'card' }, h('div', { class: 'card-head' }, filters), body(tiles),
    h('div', { class: 'card-foot' }, h('span', null, mono(num(shown.length)), shown.length === 1 ? ' pet' : ' pets')))];
}

const stats = list => list.length ? list.map(s => h('span', { class: 'stat' }, s.name + ' ', h('b', null, s.value))) : h('span', { class: 'muted' }, '-');

async function petPage(id) {
  const pet = await api('GET', '/api/pets/' + encodeURIComponent(id));
  const p = pet.pet;
  const statNames = pet.growth.length ? pet.growth[0].stats.map(s => s.name) : [];
  const growth = pet.growth.length
    ? table([
      { label: 'Level', num: true, value: g => num(g.level) },
      ...statNames.map((name, i) => ({ label: name, num: true, opt: i > 2, value: g => g.stats[i]?.value ?? '-' })),
      { label: 'Fight power', num: true, value: g => num(g.fightPower) },
      { label: 'Assist', num: true, opt: true, value: g => num(g.assistPower) },
      { label: 'EXP to next', num: true, opt: true, value: g => g.exp ? num(g.exp) : '-' },
    ], pet.growth, { short: true })
    : emptyState('paw', 'No growth data', null);
  const starRows = pet.stars.filter(s => s.stage === 0);
  const steps = table([
    { label: 'Step', value: s => h('span', { class: 'stars' }, '★'.repeat(s.star), s.stage ? h('span', { class: 'muted' }, ' +' + s.stage) : null) },
    { label: 'Stats', num: true, value: s => '×' + s.multiplier.toFixed(2).replace(/\.?0+$/, '') },
    { label: 'Fight power', num: true, opt: true, value: s => num(s.fightPower) },
    { label: 'Costs', grow: true, value: s => s.cost ? amount(s.cost, { always: true }) : h('span', { class: 'muted' }, '-') },
  ], pet.stars, { short: pet.stars.length <= 24 });
  const bonuses = table([
    { label: 'Star', value: s => h('span', { class: 'stars' }, '★'.repeat(s.star)) },
    { label: 'While it fights', grow: true, wrap: true, value: s => stats(s.fight) },
    { label: 'Collection', wrap: true, value: s => stats(s.collection) },
  ], starRows, { short: true });
  const skillBox = h('div');
  const showStar = star => {
    const step = starRows.find(s => s.star === star) || starRows[0];
    skillBox.replaceChildren(step && step.skills.length ? h('ul', { class: 'skill-list' }, step.skills.map(k => h('li', null,
      thumb(k.icon, k.name, '', 'lg'), h('div', null, h('b', { class: 'skill-name' }, k.name, ' ', chip('plain', k.role)), h('p', { class: 'desc' }, runs(k.description))))))
      : emptyState('sparkle', 'No skills at this star', null));
    for (const b of seg.querySelectorAll('button')) b.setAttribute('aria-pressed', String(Number(b.dataset.star) === star));
  };
  const seg = h('div', { class: 'seg', role: 'group', 'aria-label': 'Star' }, starRows.map(s =>
    h('button', { type: 'button', 'data-star': String(s.star), onclick: () => showStar(s.star) }, '★' + s.star)));
  if (starRows.length) showStar(starRows[0].star);
  const picture = p.picture ? h('img', { class: 'picture', src: p.picture, alt: p.name, loading: 'lazy' }) : null;
  if (picture) picture.addEventListener('error', () => picture.closest('.card')?.remove(), { once: true });
  return [pageHead({ crumbs: dbCrumbs({ href: '/pets', label: 'Pets' }, { label: p.name }), title: p.name,
    art: thumb(p.icon, p.name, 'q' + p.quality, 'xl'), after: [chip('q' + p.quality, p.qualityName), chip('plain', campNames[p.camp] || 'Camp')],
    meta: [['ID', mono(String(p.id))], ['Stars', String(starRows.length)],
      ['In the wild', pet.wild ? monsterName(pet.wild.id, pet.wild.name, pet.wild.rank, pet.wild.icon) : '-']] }),
  h('div', { class: 'grid' },
    h('div', { class: 'span-8 stack' },
      card('Skills', starRows.length > 1 ? seg : null, body(skillBox), 'The skills of the star picked above; the numbers grow with each star.'),
      card('Growth', null, growth, 'Its own stats at its first star; each star multiplies them.'),
      card('What it gives you', null, bonuses, 'Fight: while it is in a fight slot. Collection: for good, once collected.'),
      card('Stars', null, steps, 'Each step costs fragments of this pet.')),
    h('div', { class: 'span-4 stack' },
      picture && card(null, null, body(picture)),
      card('Pet', null, body(facts([['Quality', h('span', { class: 'strong q' + p.quality }, p.qualityName)], ['Camp', campNames[p.camp] || '-'],
        ['Fight power at Lv 1', pet.growth.length ? h('span', { class: 'num' }, num(pet.growth[0].fightPower)) : '-'],
        ['A duplicate gives', pet.fragment ? amount(pet.fragment, { always: true }) : '-']])))))];
}

async function mountsPage() {
  const { mounts } = await api('GET', '/api/mounts');
  const fastest = Math.max(1, ...mounts.map(m => m.speedPercent));
  const rows = mounts.length
    ? table([
      { label: 'Mount', value: m => h('span', { class: 'named q' + m.quality }, thumb(m.icon, m.name, 'q' + m.quality, 'lg'), h('span', null, m.name)) },
      { label: 'Quality', opt: true, value: m => chip('q' + m.quality, m.qualityName) },
      { label: 'Speed', num: true, value: m => h('span', { class: 'strong' }, '+' + num(m.speedPercent) + '%') },
      { label: '', opt: true, value: m => { const bar = h('i'); bar.style.width = Math.round(m.speedPercent / fastest * 100) + '%'; return h('span', { class: 'share' }, bar); } },
      { label: 'Other stats', opt: true, value: m => stats(m.stats) },
      { label: 'Unlocked by', grow: true, wrap: true, value: m => m.items.length ? h('div', { class: 'amounts' }, m.items.map(i =>
        amount({ itemId: i.itemId, item: i.item, quality: i.quality, icon: i.icon, count: 1 },
          { after: i.durationSeconds ? h('span', { class: 'chip plain' }, duration(i.durationSeconds)) : null })))
        : h('span', { class: 'muted' }, 'A quest or event') },
      { label: 'Fashion', num: true, opt: true, value: m => m.fashionScore ? num(m.fashionScore) : '-' },
    ], mounts, { short: true })
    : emptyState('star', 'No mounts', null);
  return [pageHead({ crumbs: dbCrumbs({ label: 'Mounts' }), title: 'Mounts',
    subtitle: 'Every mount and the move speed it adds to your own (the server adds it to the base speed).', actions: densityToggle() }),
  card(null, null, rows, 'A time-limited item lends the mount for the time shown; the others unlock it for good.')];
}

routes.push([/^\/pets$/, petsPage, 'database'], [/^\/pets\/(\d+)$/, petPage, 'database'], [/^\/mounts$/, mountsPage, 'database']);
