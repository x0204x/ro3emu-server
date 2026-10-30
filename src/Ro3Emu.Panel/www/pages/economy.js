// The admin's economy log: the server's ledger of every item and currency movement (economy_entry with its
// operation_receipt), read-only, filtered by account, character, item and days (/api/admin/economy).
'use strict';

async function adminEconomy() {
  requireAdmin();
  const query = queryOf();
  const page = Number(query.page) || 1;
  const result = await api('GET', '/api/admin/economy' + listHref('', { ...query, page }));
  const href = p => listHref('/admin/economy', { ...query, page: p });
  const text = (name, label, width) => h('input', { class: 'input', name, value: query[name] || '', placeholder: label, 'aria-label': label,
    spellcheck: 'false', autocapitalize: 'off', size: width });
  const date = (name, label) => h('input', { class: 'input', name, type: 'date', value: query[name] || '', 'aria-label': label, title: label });
  const form = h('form', { class: 'filters', role: 'search' },
    text('account', 'Account', 14), text('character', 'Character', 14), text('asset', 'Item name or ID', 16),
    h('span', { class: 'lv' }, date('from', 'From (UTC day)'), '-', date('to', 'To (UTC day)')),
    h('button', { type: 'submit', class: 'btn primary sm' }, 'Filter'),
    Object.entries(query).some(([k, v]) => k !== 'page' && v) ? link('/admin/economy', { class: 'btn secondary sm' }, 'Reset') : null);
  form.addEventListener('submit', e => {
    e.preventDefault();
    navigate(listHref('/admin/economy', Object.fromEntries([...new FormData(form)].map(([k, v]) => [k, String(v).trim()]))));
  });
  const signed = n => h('span', { class: 'num strong ' + (n > 0 ? 'pos' : n < 0 ? 'neg' : '') }, (n > 0 ? '+' : '') + num(n));
  const rows = result.entries.length
    ? table([
      { label: 'Time', value: e => stamp(e.utc) },
      { label: 'Account', opt: true, value: e => e.account ? link('/admin/accounts/' + encodeURIComponent(e.account), { class: 'text-link mono' }, e.account) : '-' },
      { label: 'Holder', value: e => e.owner },
      { label: 'Reason', value: e => [e.reason, h('span', { class: 'sub mono' }, e.command)] },
      { label: 'Item', grow: true, value: e => [link('/items/' + e.assetId, { class: 'text-link strong' }, e.asset), h('span', { class: 'sub mono' }, '#' + e.assetId)] },
      { label: 'Change', num: true, value: e => signed(e.delta) },
      { label: 'Where', opt: true, value: e => [e.place, h('span', { class: 'sub' }, e.leg)] },
      { label: 'Counterparty', opt: true, value: e => e.counterparty || h('span', { class: 'muted' }, '-') },
    ], result.entries)
    : emptyState('coins', 'Nothing recorded here', 'The ledger lists every item and currency the server moved: drops, shops, mail, trades, crafting.');
  const assetNote = result.assets.length === 1 ? 'of ' + result.assets[0].name
    : result.assets.length > 1 ? `of ${result.assets.length} matching items` : 'all items and currencies mixed';
  return adminLayout('economy',
    pageHead({ crumbs: adminCrumbs({ label: 'Economy log' }), title: 'Economy log',
      subtitle: 'Every item and currency movement the server recorded, newest first. Read-only.', actions: densityToggle() }),
    h('section', { class: 'kpis', 'aria-label': 'Totals' },
      kpi('Entries', 'list', num(result.total), null, 'matching the filter'),
      kpi('In', 'arrow', '+' + num(result.in), null, assetNote),
      kpi('Out', 'back', num(result.out), null, assetNote),
      kpi('Net', 'coins', (result.in + result.out > 0 ? '+' : '') + num(result.in + result.out), null, assetNote)),
    h('section', { class: 'card' }, h('div', { class: 'card-head' }, form), rows,
      h('div', { class: 'card-foot' }, pager(result, href))));
}

adminSections.push(['economy', '/admin/economy', 'Economy log', 'coins']);
{
  const later = laterSections.findIndex(([, label]) => label === 'Economy log');
  if (later >= 0) laterSections.splice(later, 1);
}
routes.push([/^\/admin\/economy$/, adminEconomy, 'admin']);
