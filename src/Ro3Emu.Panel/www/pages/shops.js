// Shops (/shops, /shops/<id>): every shop of the game data with its goods, prices and currencies.
'use strict';

async function shopsPage() {
  const { shops } = await api('GET', '/api/shops');
  const query = queryOf();
  const text = (query.q || '').toLowerCase();
  const shown = shops.filter(s => !text || s.name.toLowerCase().includes(text));
  const rows = shown.length
    ? table([
      { label: 'Shop', grow: true, value: s => link('/shops/' + s.id, { class: 'row-link' }, s.name) },
      { label: 'Goods', num: true, value: s => num(s.goods) },
      { label: 'Mostly paid in', opt: true, value: s => s.currency || '-' },
    ], shown, { onrow: s => navigate('/shops/' + s.id) })
    : emptyState('search', 'No shop matches', null);
  return [pageHead({ crumbs: dbCrumbs({ label: 'Shops' }), title: 'Shops', subtitle: 'What every shop sells, as the game data lists it.',
    actions: densityToggle() }),
  h('section', { class: 'card' }, h('div', { class: 'card-head' }, filterForm('/shops', query, [], 'Shop name')), rows,
    h('div', { class: 'card-foot' }, h('span', null, mono(num(shown.length)), shown.length === 1 ? ' shop' : ' shops')))];
}

async function shopPage(id) {
  const shop = await api('GET', '/api/shops/' + encodeURIComponent(id));
  const s = shop.shop;
  const goods = shop.goods.length
    ? table([
      { label: 'Item', grow: true, value: g => itemName(g.item.itemId, g.item.item, g.item.quality, false, g.item.icon) },
      { label: 'Per buy', num: true, opt: true, value: g => num(g.item.count) },
      { label: 'Price', num: true, value: g => g.price > 0 ? h('span', { class: 'named plain' }, h('span', { class: 'num strong' }, num(g.price)),
        g.currencyId ? link('/items/' + g.currencyId, { title: g.currency }, thumb(g.currencyIcon, g.currency, '')) : null) : 'Free' },
      { label: 'Paid in', opt: true, value: g => g.currencyId ? link('/items/' + g.currencyId, { class: 'text-link' }, g.currency) : '-' },
    ], shop.goods, { onrow: g => navigate('/items/' + g.item.itemId) })
    : emptyState('box', 'It sells nothing', null);
  return [pageHead({ crumbs: dbCrumbs({ href: '/shops', label: 'Shops' }, { label: s.name }), title: s.name,
    meta: [['ID', mono(String(s.id))], ['Goods', num(s.goods)]], actions: densityToggle() }),
  card('Goods', null, goods)];
}

routes.push([/^\/shops$/, shopsPage, 'database'], [/^\/shops\/(\d+)$/, shopPage, 'database']);
