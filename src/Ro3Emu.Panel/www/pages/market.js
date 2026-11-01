// The market (/market, /market/<id>): what is on offer on the exchange and in the stalls, what sold and for how much,
// the auction lots and the guild materials exchange, as the server keeps them (/api/market). Sellers on the exchange
// stay anonymous, as in the game; a stall shows its owner.
'use strict';

const price = (value, currency) => [h('span', { class: 'num strong' }, num(value)), currency && currency !== 'Zeny' ? h('span', { class: 'sub' }, currency) : null];

/**
 * A price chart: the average as a line over the low-high band, the quantity as faint bars under it; inline SVG, one
 * series, the axis labels at its ends.
 */
function priceChart(points, label, W = 640) {
  if (points.length === 0) return emptyState('coins', 'No sales yet', 'The chart fills as the item sells.');
  const H = Math.round(W * 0.34), L = 56, R = 12, T = 12, B = 28;
  const lo = Math.min(...points.map(p => p.low)), hi = Math.max(...points.map(p => p.high));
  const pad = Math.max(1, Math.round((hi - lo) * 0.1));
  const y0 = Math.max(0, lo - pad), y1 = hi + pad;
  const t0 = new Date(points[0].time).getTime(), t1 = new Date(points[points.length - 1].time).getTime();
  const x = t => points.length === 1 ? (L + W - R) / 2 : L + (new Date(t).getTime() - t0) / Math.max(1, t1 - t0) * (W - L - R);
  const y = v => T + (1 - (v - y0) / Math.max(1, y1 - y0)) * (H - T - B);
  const most = Math.max(1, ...points.map(p => p.quantity));
  const band = points.length > 1
    ? svg('path', { class: 'band', d: 'M' + points.map(p => `${x(p.time)},${y(p.high)}`).join('L') + 'L' + points.slice().reverse().map(p => `${x(p.time)},${y(p.low)}`).join('L') + 'Z' })
    : null;
  const bars = points.filter(p => p.quantity > 0).map(p => svg('rect', { class: 'bar', x: x(p.time) - 2, width: 4,
    y: H - B - (p.quantity / most) * 28, height: (p.quantity / most) * 28 }));
  const grid = [y0, (y0 + y1) / 2, y1].map(v => [svg('line', { class: 'grid-line', x1: L, x2: W - R, y1: y(v), y2: y(v) }),
    svg('text', { class: 'axis', x: L - 8, y: y(v) + 4, 'text-anchor': 'end' }, num(Math.round(v)))]);
  const fmt = t => { const d = new Date(t); return `${d.getFullYear()}-${pad2(d.getMonth() + 1)}-${pad2(d.getDate())}`; };
  const axis = [svg('text', { class: 'axis', x: L, y: H - 8 }, fmt(points[0].time)),
    points.length > 1 ? svg('text', { class: 'axis', x: W - R, y: H - 8, 'text-anchor': 'end' }, fmt(points[points.length - 1].time)) : null];
  const line = svg('path', { class: 'line', d: 'M' + points.map(p => `${x(p.time)},${y(p.average)}`).join('L') });
  const dots = points.map(p => svg('circle', { class: 'dot', cx: x(p.time), cy: y(p.average), r: 3.5 },
    svg('title', null, `${fmt(p.time)}: ${num(p.average)} average` + (p.high > p.low ? ` (${num(p.low)}-${num(p.high)})` : '') + (p.quantity ? `, ${num(p.quantity)} sold` : ''))));
  return svg('svg', { class: 'chart', viewBox: `0 0 ${W} ${H}`, role: 'img', 'aria-label': label }, grid, bars, band, line, dots, axis);
}
const pad2 = n => String(n).padStart(2, '0');

async function marketPage() {
  const query = queryOf();
  const page = Number(query.page) || 1;
  const m = await api('GET', '/api/market' + listHref('', { q: query.q || '', page }));
  const href = p => listHref('/market', { ...query, page: p });
  const offers = m.offers.length
    ? table([
      { label: 'Item', grow: true, value: r => link('/market/' + r.item.id, { class: 'named q' + r.item.quality }, thumb(r.item.icon, r.item.name, 'q' + r.item.quality), h('span', null, r.item.name)) },
      { label: 'Offers', num: true, opt: true, value: r => num(r.offers) },
      { label: 'Quantity', num: true, opt: true, value: r => num(r.quantity) },
      { label: 'Lowest', num: true, value: r => price(r.lowest, r.currency) },
      { label: 'Last sold', num: true, opt: true, value: r => r.lastPrice ? [num(r.lastPrice), h('span', { class: 'sub' }, ago(r.lastSold))] : h('span', { class: 'muted' }, '-') },
    ], m.offers, { onrow: r => navigate('/market/' + r.item.id) })
    : emptyState('coins', query.q ? 'Nothing matching is on offer' : 'Nothing is on offer', query.q ? 'Try part of the name.' : 'Items show up here as players list them on the exchange or open a stall.');
  const traded = m.mostTraded.length
    ? table([
      { label: 'Item', grow: true, value: t => link('/market/' + t.item.id, { class: 'named q' + t.item.quality }, thumb(t.item.icon, t.item.name, 'q' + t.item.quality), h('span', null, t.item.name)) },
      { label: 'Sold', num: true, value: t => num(t.quantity) },
      { label: 'Average', num: true, opt: true, value: t => num(t.average) },
    ], m.mostTraded, { short: true })
    : emptyState('trophy', 'Nothing sold in 30 days', null);
  const materials = m.guildMaterials.map(p => ({ ...p, quantity: 0 }));
  return [pageHead({ crumbs: [{ href: '/', label: 'Home' }, { label: 'Market' }], title: 'Market',
    subtitle: 'What is on offer on the exchange and in the stalls, and what it sold for. Refreshed every 30 seconds at most.', actions: densityToggle() }),
  h('section', { class: 'kpis', 'aria-label': 'Market figures' },
    kpi('On offer', 'box', num(m.total), m.total === 1 ? 'item' : 'items', 'different items for sale'),
    kpi('Listings', 'tag', num(m.listings), null, 'on the exchange'),
    kpi('Stalls', 'users', num(m.stalls), null, 'open in the streets'),
    kpi('Sales', 'coins', num(m.sales), null, 'on the exchange in 7 days')),
  h('div', { class: 'grid' },
    h('div', { class: 'span-8' }, h('section', { class: 'card' }, h('div', { class: 'card-head' }, filterForm('/market', query, [], 'Item name or ID')), offers,
      h('div', { class: 'card-foot' }, listPager(m, href, 'item')))),
    h('div', { class: 'span-4 stack' },
      card('Most traded', h('span', { class: 'muted' }, '30 days'), traded),
      card('Guild materials', h('span', { class: 'muted' }, 'hourly average'),
        materials.length ? body(priceChart(materials, 'Guild materials price, hourly average', 360)) : emptyState('coins', 'No trade yet', 'The guild materials exchange records its price each hour it trades.'))))];
}

async function marketItemPage(id) {
  const m = await api('GET', '/api/market/' + encodeURIComponent(id));
  const item = m.item;
  const lowest = m.offers.length ? m.offers.reduce((a, b) => (a.currency === 'Zeny') !== (b.currency === 'Zeny') ? (a.currency === 'Zeny' ? a : b) : a.unitPrice <= b.unitPrice ? a : b) : null;
  const last = m.sales[0];
  const month = m.daily.filter(p => new Date(p.time) > Date.now() - 30 * 86400000);
  const monthQty = month.reduce((s, p) => s + p.quantity, 0);
  const monthAvg = monthQty ? Math.round(month.reduce((s, p) => s + p.average * p.quantity, 0) / monthQty) : 0;
  const offers = m.offers.length
    ? table([
      { label: 'Where', value: o => o.where === 'Stall' ? chip('event', 'Stall') : chip('info', 'Exchange') },
      { label: 'Seller', opt: true, value: o => o.where === 'Stall' ? [h('b', null, o.seller), h('span', { class: 'sub' }, o.map)] : h('span', { class: 'muted' }, 'Anonymous') },
      { label: 'Quantity', num: true, value: o => num(o.quantity) },
      { label: 'Unit price', num: true, value: o => price(o.unitPrice, o.currency) },
      { label: 'Until', opt: true, value: o => o.onShowUntil ? [chip('plain', 'On show'), h('span', { class: 'sub' }, 'for sale in ' + until(o.onShowUntil))] : o.expires ? stamp(o.expires) : '-' },
    ], m.offers, { short: m.offers.length <= 12 })
    : emptyState('tag', 'Nobody offers it now', null);
  const sales = m.sales.length
    ? table([
      { label: 'Sold', value: s => stamp(s.time) },
      { label: 'Quantity', num: true, value: s => num(s.quantity) },
      { label: 'Unit price', num: true, value: s => h('span', { class: 'num strong' }, num(s.unitPrice)) },
    ], m.sales.slice(0, 15), { short: true })
    : emptyState('coins', 'Never sold on the exchange', null);
  const auctions = m.auctions.length
    ? table([
      { label: 'Auction', grow: true, value: a => a.kind },
      { label: 'Price', num: true, value: a => price(a.price, a.currency) },
      { label: 'State', value: a => a.state === 'Sold' ? chip('active', 'Sold') : a.state === 'Bidding' ? chip('event', 'Bidding') : chip('', a.state) },
      { label: 'Ends', opt: true, value: a => stamp(a.end) },
    ], m.auctions, { short: true })
    : null;
  return [pageHead({ crumbs: [{ href: '/', label: 'Home' }, { href: '/market', label: 'Market' }, { label: item.name }], title: item.name,
    art: thumb(item.icon, item.name, 'q' + item.quality, 'xl'), after: chip('q' + item.quality, item.qualityName),
    meta: [['Type', item.subType || item.typeName], ['ID', mono(String(item.id))]],
    actions: link('/items/' + item.id, { class: 'btn secondary sm' }, icon('book'), 'In the item database') }),
  h('section', { class: 'kpis', 'aria-label': 'Prices' },
    kpi('Lowest offer', 'tag', lowest ? num(lowest.unitPrice) : '-', lowest && lowest.currency !== 'Zeny' ? lowest.currency : null, lowest ? `${num(m.offers.length)} on offer` : 'nobody offers it'),
    kpi('Last price', 'coins', last ? num(last.unitPrice) : '-', null, last ? ago(last.time) : 'never sold'),
    kpi('30-day average', 'pulse', monthAvg ? num(monthAvg) : '-', null, monthQty ? `${num(monthQty)} sold` : 'no sale in 30 days'),
    kpi('Sales', 'list', num(m.daily.reduce((s, p) => s + p.quantity, 0)), null, 'sold in 90 days')),
  h('div', { class: 'grid' },
    h('div', { class: 'span-8 stack' },
      card('Price history', h('span', { class: 'muted' }, 'daily, 90 days'), body(priceChart(m.daily, item.name + ' price history, daily average with the day\'s range'))),
      card('On offer', null, offers),
      auctions && card('Auctions', null, auctions)),
    h('div', { class: 'span-4 stack' }, card('Latest sales', null, sales)))];
}

routes.push([/^\/market$/, marketPage, 'market'], [/^\/market\/(\d+)$/, marketItemPage, 'market']);
titles.market = 'Market';
