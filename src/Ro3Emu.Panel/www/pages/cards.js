// The card database (/cards): every card of the card library with its top star's effects, and a page per card with
// every star's effects, what each level adds and costs, and the monsters that drop it (/api/cards).
'use strict';

async function cardsPage() {
  const query = queryOf();
  const list = await api('GET', '/api/cards');
  const text = (query.q || '').toLowerCase();
  const shown = list.cards.filter(c => (!text || c.name.toLowerCase().includes(text) || c.summary.toLowerCase().includes(text) || String(c.id) === text)
    && (!query.part || String(c.part) === query.part) && (!query.quality || String(c.quality) === query.quality));
  const filters = filterForm('/cards', query, [
    optionSelect('part', 'Slot', list.parts, query.part, 'Every slot'),
    optionSelect('quality', 'Quality', list.qualities, query.quality, 'Every quality'),
  ], 'Name, effect or ID');
  const rows = shown.length
    ? table([
      { label: 'Card', value: c => itemName(c.id, c.name, c.quality, true, c.icon) },
      { label: 'Slot', value: c => c.partName },
      { label: 'Quality', opt: true, value: c => chip('q' + c.quality, c.qualityName) },
      { label: 'At the top star', grow: true, wrap: true, value: c => h('span', { class: 'muted' }, c.summary) },
    ], shown, { onrow: c => navigate('/cards/' + c.id) })
    : emptyState('search', 'No card matches', 'Try part of the name or of an effect ("CRIT", "Stun").');
  return [pageHead({ crumbs: dbCrumbs({ label: 'Cards' }), title: 'Cards',
    subtitle: 'Every card in the card library, with the effects of its highest star.', actions: densityToggle() }),
  h('section', { class: 'card' }, h('div', { class: 'card-head' }, filters), rows,
    h('div', { class: 'card-foot' }, h('span', null, mono(num(shown.length)), shown.length === 1 ? ' card' : ' cards')))];
}

const starLabel = tier => '★'.repeat(tier + 1);

/** One star: its level cap, power and effects; a Flash copy's extra effects below. */
function tierCard(t) {
  return h('section', { class: 'tier' },
    h('div', { class: 'tier-head' }, h('span', { class: 'stars', 'aria-label': `Star ${t.tier + 1}` }, starLabel(t.tier)),
      h('span', { class: 'muted' }, t.levelCap ? `Level up to ${t.levelCap}` : 'No level cap')),
    h('ul', { class: 'effects' }, t.effects.map(e => h('li', null, runs(e)))),
    t.flashEffects.length ? [h('span', { class: 'label' }, 'Flash copy adds'), h('ul', { class: 'effects flash' }, t.flashEffects.map(e => h('li', null, runs(e))))] : null,
    h('div', { class: 'tier-foot' }, h('span', null, 'Power ', mono(num(t.power))),
      t.copies ? h('span', null, 'Next star: ', mono(num(t.copies)), t.copies === 1 ? ' copy' : ' copies') : h('span', null, 'Top star')));
}

async function cardPage(id) {
  const detail = await api('GET', '/api/cards/' + encodeURIComponent(id));
  const c = detail.card;
  const levels = detail.levels.length
    ? table([
      { label: 'Level', num: true, value: l => num(l.level) },
      { label: 'Adds', grow: true, wrap: true, value: l => l.bonus.length ? l.bonus.map(s => h('span', { class: 'stat' }, s.name + ' ', h('b', null, s.value))) : h('span', { class: 'muted' }, '-') },
      { label: 'Next level costs', value: l => amounts(l.cost) || h('span', { class: 'muted' }, 'Top level') },
    ], detail.levels, { short: detail.levels.length <= 16 })
    : emptyState('card', 'This card does not level up', null);
  const dropped = detail.droppedBy.length
    ? table([
      { label: 'Monster', grow: true, value: d => monsterName(d.monsterId, d.monster, d.rank, d.icon) },
      { label: 'Level', num: true, value: d => num(d.level) },
      { label: 'How', opt: true, value: d => d.kind ? chip(d.kind === 'Rare winner' ? 'event' : 'info', 'MVP: ' + d.kind) : h('span', { class: 'muted' }, 'Drop') },
      { label: 'Chance', num: true, value: d => percent(d.chance) },
    ], detail.droppedBy, { short: detail.droppedBy.length <= 12, onrow: d => navigate('/monsters/' + d.monsterId) })
    : emptyState('paw', 'No monster drops it', 'It comes from card packs, quests or events.');
  const picture = c.picture ? h('img', { class: 'picture card-art', src: c.picture, alt: c.name, loading: 'lazy' }) : null;
  if (picture) picture.addEventListener('error', () => picture.closest('.card')?.remove(), { once: true });
  return [pageHead({ crumbs: dbCrumbs({ href: '/cards', label: 'Cards' }, { label: c.name }), title: c.name,
    art: thumb(c.icon, c.name, 'q' + c.quality, 'xl'), after: [chip('q' + c.quality, c.qualityName), chip('plain', c.partName)],
    meta: [['ID', mono(String(c.id))], ['Slot', c.partName], ['Stars', String(detail.tiers.length)]],
    actions: link('/items/' + c.id, { class: 'btn secondary sm' }, icon('box'), 'Item page') }),
  h('div', { class: 'grid' },
    h('div', { class: 'span-8 stack' },
      card('Stars', null, body(h('div', { class: 'tiers' }, detail.tiers.map(tierCard))),
        'A star\'s effects, plus what the card\'s level adds below. Stars cost copies of the same card.'),
      card('Levels', null, levels, detail.levels.length ? 'What each level adds on top of the star, for every card of this slot and quality.' : null),
      card('Dropped by', null, dropped)),
    h('div', { class: 'span-4 stack' },
      picture && card(null, null, body(picture)),
      card('Card', null, body(facts([
        ['Quality', h('span', { class: 'strong q' + c.quality }, c.qualityName)],
        ['Slot', c.partName],
        ['Card library', c.library ? 'Yes' : 'No'],
        ['Breaks down into', amounts(detail.decompose) || '-'],
        ['Revert costs', detail.returnCost ? amount(detail.returnCost, { always: true }) : '-']])),
        detail.rules.length ? h('ul', { class: 'card-rules' }, detail.rules.map(r => h('li', null, r))) : null)))];
}

routes.push([/^\/cards$/, cardsPage, 'database'], [/^\/cards\/(\d+)$/, cardPage, 'database']);
