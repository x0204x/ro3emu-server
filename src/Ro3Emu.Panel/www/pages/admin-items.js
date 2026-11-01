// The admin's item tracking (/admin/items, /admin/items/<id>): who holds an item and where, how the ledger saw it move,
// and a duplication check over the whole realm (/api/admin/items/...). Read only.
'use strict';

const placeChip = place => chip({ Bag: 'plain', Worn: 'active', Cart: 'plain', Mail: 'info', Exchange: 'event', Stall: 'event', Auction: 'event' }[place] || 'plain', place);

function flagList(flags) {
  return h('div', null, flags.map(f => h('div', { class: 'flag' },
    h('div', { class: 'row' }, chip(f.kind === 'Unrecorded' ? 'event' : 'banned', f.kind), f.instance ? mono('#' + f.instance) : null),
    h('p', null, f.detail))));
}

function itemSearch(q) {
  const form = h('form', { class: 'filters', role: 'search' },
    h('span', { class: 'search' }, icon('search'), h('input', { class: 'input', name: 'q', value: q, placeholder: 'Item name or ID', 'aria-label': 'Item name or ID', spellcheck: 'false', autocapitalize: 'off' })),
    h('button', { type: 'submit', class: 'btn primary sm' }, 'Find'));
  form.addEventListener('submit', e => {
    e.preventDefault();
    const text = String(new FormData(form).get('q')).trim();
    navigate(/^\d+$/.test(text) ? '/admin/items/' + text : '/admin/items?q=' + encodeURIComponent(text));
  });
  return form;
}

async function adminItems() {
  requireRole('admin');
  const q = queryOf().q || '';
  const found = q ? await api('GET', '/api/items?q=' + encodeURIComponent(q)) : null;
  const results = found
    ? (found.items.length
      ? table([
        { label: 'Item', grow: true, value: i => link('/admin/items/' + i.id, { class: 'named q' + i.quality }, thumb(i.icon, i.name, 'q' + i.quality), h('span', null, i.name)) },
        { label: 'ID', num: true, value: i => mono(String(i.id)) },
        { label: 'Type', opt: true, value: i => i.subType || i.typeName },
      ], found.items, { onrow: i => navigate('/admin/items/' + i.id) })
      : emptyState('search', 'No item matches', 'Try part of the name, or its ID.'))
    : emptyState('box', 'Find an item', 'Its holders, its movements in the ledger and its duplication checks.');
  const check = h('div', null, emptyState('shield', 'Duplication check', 'Compares what is held of every item with what the ledger let in, and looks for one instance in two places.'));
  const run = h('button', { type: 'button', class: 'btn secondary sm', onclick: async () => {
    run.classList.add('busy');
    try { check.replaceChildren(dupeTable(await api('GET', '/api/admin/items/dupes'))); }
    catch (e) { toast(e.message); }
    finally { run.classList.remove('busy'); }
  } }, h('i', { class: 'spin' }), 'Run the check');
  return adminLayout('item-tracking',
    pageHead({ crumbs: adminCrumbs({ label: 'Item tracking' }), title: 'Item tracking',
      subtitle: 'Where an item is held now, how it moved and whether more of it exists than the ledger explains. Read only.', actions: densityToggle() }),
    h('div', { class: 'stack' },
      h('section', { class: 'card' }, h('div', { class: 'card-head' }, itemSearch(q)), results),
      h('section', { class: 'card' }, h('div', { class: 'card-head' }, h('h2', null, 'Duplication check'), h('div', { class: 'actions' }, run)), check)));
}

function dupeTable(report) {
  const since = report.ledgerSince ? ['Ledger since ', stamp(report.ledgerSince)] : 'The ledger is empty';
  if (!report.items.length)
    return h('div', null, emptyState('shield', 'Nothing flagged', `${plural(report.checked, 'different item')} held, none beyond what the ledger explains.`), h('div', { class: 'card-foot' }, h('span', null, since)));
  return h('div', null, table([
    { label: 'Item', grow: true, value: r => link('/admin/items/' + r.item.id, { class: 'named q' + r.item.quality }, thumb(r.item.icon, r.item.name, 'q' + r.item.quality), h('span', null, r.item.name)) },
    { label: 'Held', num: true, value: r => num(r.held) },
    { label: 'Ledger', num: true, opt: true, value: r => num(r.net) },
    { label: 'Flags', value: r => h('span', { class: 'row' }, [...new Set(r.flags.map(f => f.kind))].map(k => chip(k === 'Unrecorded' ? 'event' : 'banned', k))) },
  ], report.items, { onrow: r => navigate('/admin/items/' + r.item.id) }),
  h('div', { class: 'card-foot' }, h('span', null, mono(num(report.items.length)), ' flagged of ', mono(num(report.checked)), ' held'), h('span', null, since)));
}

async function adminItem(id) {
  requireRole('admin');
  const t = await api('GET', `/api/admin/items/${encodeURIComponent(id)}/trace`);
  const item = t.item, l = t.ledger;
  const holders = t.currency
    ? emptyState('coins', 'A currency', 'Currencies are held in the characters\' purses, not as items; the ledger below shows how they moved.')
    : t.holders.length
      ? table([
        { label: 'Where', value: x => placeChip(x.place) },
        { label: 'Holder', grow: true, value: x => [x.characterId ? link('/characters/' + x.characterId, { class: 'row-link' }, x.owner) : h('b', null, x.owner),
          x.instances.length ? h('span', { class: 'guids mono', title: x.instances.join(', ') }, x.instances.map(g => '#' + g).join(' ')) : null] },
        { label: 'Account', opt: true, value: x => x.account ? link('/admin/accounts/' + encodeURIComponent(x.account), { class: 'text-link mono' }, x.account) : h('span', { class: 'muted' }, '-') },
        { label: 'Count', num: true, value: x => h('span', { class: 'num strong' }, num(x.count)) },
      ], t.holders)
      : emptyState('box', 'Nobody holds it', 'Not in any bag, mailbox, listing, stall or auction.');
  const signed = n => h('span', { class: 'num strong ' + (n > 0 ? 'pos' : n < 0 ? 'neg' : '') }, (n > 0 ? '+' : '') + num(n));
  const recent = t.recent.length
    ? table([
      { label: 'Time', value: e => stamp(e.utc) },
      { label: 'Holder', value: e => e.owner },
      { label: 'Reason', grow: true, value: e => [e.reason, h('span', { class: 'sub' }, e.leg + ' · ' + e.place)] },
      { label: 'Change', num: true, value: e => signed(e.delta) },
      { label: 'Counterparty', opt: true, value: e => e.counterparty || h('span', { class: 'muted' }, '-') },
    ], t.recent, { short: true })
    : emptyState('coins', 'The ledger never saw it move', null);
  return adminLayout('item-tracking',
    pageHead({ crumbs: adminCrumbs({ href: '/admin/items', label: 'Item tracking' }, { label: item.name }), title: item.name,
      art: thumb(item.icon, item.name, 'q' + item.quality, 'xl'), after: [chip('q' + item.quality, item.qualityName), t.flags.length ? chip('banned', 'Flagged') : chip('active', 'No flag')],
      meta: [['ID', mono(String(item.id))], ['Type', item.subType || item.typeName]],
      actions: [link('/items/' + item.id, { class: 'btn secondary sm' }, icon('book'), 'Item page'), densityToggle()] }),
    h('section', { class: 'kpis', 'aria-label': 'Totals' },
      kpi('Held now', 'box', t.currency ? '-' : num(t.held), null, t.currency ? 'in purses' : plural(t.holders.length, 'holder')),
      kpi('Entered', 'arrow', '+' + num(l.entered), null, 'ledger sources'),
      kpi('Left', 'back', num(l.left), null, 'ledger sinks'),
      kpi('Ledger net', 'coins', num(l.net), null, l.starterKits ? `+${num(l.starterKits)} in unrecorded starter kits`
        : l.first ? ['since ', stamp(l.first)] : 'no ledger entry', { warn: !t.currency && t.held > l.explained })),
    h('div', { class: 'stack' },
      t.flags.length ? card('Flags', null, flagList(t.flags), 'Inferred rules: see the panel guide.') : null,
      card('Holders', null, holders),
      card('Latest movements', link('/admin/economy?asset=' + item.id, { class: 'more' }, 'Economy log', icon('arrow')), recent)));
}

registerAdminPage({ id: 'item-tracking', label: 'Item tracking', icon: 'search', path: '/admin/items', pattern: /^\/admin\/items$/, page: adminItems, role: 'admin' });
routes.push([/^\/admin\/items\/(\d+)$/, adminItem, 'admin']);
