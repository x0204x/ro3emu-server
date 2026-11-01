// Crafting (/crafting): the life jobs' cooking formulas and equipment crafting, each with its materials, what it makes
// and how likely; the item pages show the recipes an item comes from and goes into (recipeCards).
'use strict';

/** One recipe as a card: name, requirement, materials, products with their chances and the other numbers. */
function recipeCard(r, highlight) {
  const products = r.products.length
    ? h('ul', { class: 'outputs' }, r.products.map(p => h('li', { class: p.itemId === highlight ? 'here' : null },
      itemName(p.itemId, p.item, p.quality, false, p.icon),
      p.max > 1 ? h('span', { class: 'muted' }, ' ×' + countRange(p.min, p.max)) : null,
      h('b', { class: 'num' }, percent(p.chance)))))
    : h('p', { class: 'muted' }, 'Its products are not in the game data.');
  return h('article', { class: 'card recipe' },
    h('div', { class: 'recipe-head' }, thumb(r.icon, r.name, '', 'lg'),
      h('div', null, h('b', null, r.name), h('span', { class: 'sub' }, r.requirement)),
      chip(r.kind === 'cooking' ? 'event' : 'info', r.kind === 'cooking' ? 'Cooking' : 'Equipment')),
    r.description.length ? h('p', { class: 'desc' }, runs(r.description)) : null,
    h('span', { class: 'label' }, 'Materials'), amounts(r.materials, { always: true }) || h('p', { class: 'muted' }, 'None'),
    h('span', { class: 'label' }, 'Makes'), products,
    r.facts.length ? h('dl', { class: 'recipe-facts' }, r.facts.map(f => h('div', null, h('dt', null, f.name), h('dd', null, f.value)))) : null);
}

const recipeGrid = (list, highlight) => h('div', { class: 'recipes' }, list.map(r => recipeCard(r, highlight)));

async function craftingPage() {
  const query = queryOf();
  const book = await api('GET', '/api/crafting');
  const kind = query.kind === 'equipment' ? 'equipment' : 'cooking';
  const text = (query.q || '').toLowerCase();
  const matches = r => !text || r.name.toLowerCase().includes(text)
    || r.materials.some(m => m.item.toLowerCase().includes(text)) || r.products.some(p => p.item.toLowerCase().includes(text));
  const shown = book.recipes.filter(r => r.kind === kind && matches(r) && (!query.tier || r.requirement === `Tier ${query.tier} blueprint`));
  const tiers = [...new Set(book.recipes.filter(r => r.kind === 'equipment').map(r => r.requirement.match(/\d+/)?.[0]).filter(Boolean))]
    .map(t => ({ id: t, name: 'Tier ' + t }));
  const tabs = h('div', { class: 'seg', role: 'tablist', 'aria-label': 'Kind' },
    [['cooking', 'Cooking'], ['equipment', 'Equipment crafting']].map(([id, label]) =>
      link(listHref('/crafting', { kind: id, q: query.q }), { role: 'tab', 'aria-selected': String(id === kind) }, label,
        h('span', { class: 'muted' }, ' ' + book.recipes.filter(r => r.kind === id).length))));
  const filters = filterForm('/crafting', query, [h('input', { type: 'hidden', name: 'kind', value: kind }),
    kind === 'equipment' ? optionSelect('tier', 'Tier', tiers, query.tier, 'Every tier') : null], 'Recipe, material or product');
  return [pageHead({ crumbs: dbCrumbs({ label: 'Crafting' }), title: 'Crafting',
    subtitle: kind === 'cooking' ? 'The Chef\'s formulas: what they take, what they make and how often, as the server rolls them.'
      : 'Equipment crafting: a blueprint and materials give one quality of the piece, by the weights of its tier.' }),
  h('section', { class: 'card' }, h('div', { class: 'card-head' }, h('div', { class: 'toolbar' }, tabs, filters))),
  shown.length ? recipeGrid(shown) : card(null, null, emptyState('search', 'No recipe matches', 'Try a material or a product.')),
  h('div', { class: 'card-foot plain' }, h('span', null, mono(num(shown.length)), shown.length === 1 ? ' recipe' : ' recipes'),
    kind === 'equipment' ? h('span', null, 'Chances per craft; the guarantees count every craft of the account.') : h('span', null, 'Chances per craft, before a life skill\'s bonuses.'))];
}

/** The item page's crafting cards: what makes this item, and what it is a material of. */
function recipeCards(crafting, itemId) {
  return [crafting.from.length ? card('Crafted from', null, body(recipeGrid(crafting.from, Number(itemId)))) : null,
    crafting.into.length ? card('Used in', h('span', { class: 'muted' }, plural(crafting.into.length, 'recipe')),
      body(recipeGrid(crafting.into.slice(0, 24), Number(itemId))), crafting.into.length > 24 ? 'The first 24 shown; the crafting page has them all.' : null) : null];
}

routes.push([/^\/crafting$/, craftingPage, 'database']);
