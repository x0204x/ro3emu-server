// Admin: suspicious activity (/admin/suspicious). Accounts whose requests the server refused as forged or impossible
// (anti-cheat refusals: walks too fast, swings during a swing, quest steps reported from afar, floods), characters that
// gained far more than the realm's median in the window, and for an Admin the item duplication check. Read only.
'use strict';

registerAdminPage({ id: 'suspicious', label: 'Suspicious activity', icon: 'flag', path: '/admin/suspicious', pattern: /^\/admin\/suspicious$/, page: adminSuspiciousPage, role: 'moderator' });

const suspicionWindows = [[1, '1 h'], [6, '6 h'], [24, '24 h'], [168, '7 days']];
const suspicionChip = kind => chip({ 'Refused requests': 'banned', Duplication: 'banned', 'Zeny gain': 'event', 'Item gain': 'event', 'EXP gain': 'info' }[kind] || 'plain', kind);

function suspicionWho(r) {
  if (r.kind === 'Duplication') return link('/admin/items/' + r.itemId, { class: 'row-link' }, r.character || ('Item ' + r.itemId));
  return [r.characterId ? link('/characters/' + r.characterId, { class: 'row-link' }, r.character || ('#' + r.characterId)) : h('span', { class: 'strong' }, '-'),
    r.account ? link('/admin/accounts/' + encodeURIComponent(r.account), { class: 'sub mono' }, r.account) : null];
}

function suspicionDetail(r) {
  return [h('span', null, r.detail),
    r.counts.length ? h('span', { class: 'row counts' }, r.counts.map(c => chip('plain', `${c.kind} ${num(c.count)}`))) : null];
}

async function adminSuspiciousPage() {
  requireRole('moderator');
  const q = new URLSearchParams(location.search);
  const hours = Number(q.get('hours')) || 0, dupes = q.get('dupes') === '1';
  const report = await api('GET', '/api/admin/suspicious?' + new URLSearchParams({ ...(hours ? { hours } : {}), ...(dupes ? { dupes: 1 } : {}) }));
  const t = report.thresholds;
  const href = (hrs, d) => '/admin/suspicious?' + new URLSearchParams({ hours: hrs, ...(d ? { dupes: 1 } : {}) });
  const windows = h('div', { class: 'seg', role: 'tablist', 'aria-label': 'Window' },
    suspicionWindows.map(([value, label]) => link(href(value, dupes), { role: 'tab', 'aria-selected': String(t.windowHours === value) }, label)));
  const rows = report.rows.length
    ? table([
      { label: 'Alert', value: r => suspicionChip(r.kind) },
      { label: 'Who', value: suspicionWho },
      { label: 'Value', num: true, value: r => h('span', { class: 'num strong' }, num(r.value)) },
      { label: 'Listed from', num: true, opt: true, value: r => r.threshold ? num(r.threshold) : h('span', { class: 'muted' }, '-') },
      { label: 'Detail', grow: true, wrap: true, value: suspicionDetail },
      { label: 'Last', opt: true, value: r => r.last ? stamp(r.last) : h('span', { class: 'muted' }, '-') },
    ], report.rows)
    : emptyState('shield', 'Nothing suspicious', `No account reached ${plural(t.refusals, 'refused request')} and no character gained past the thresholds in the window.`);
  const admin = mayUse('admin');
  const dupeButton = admin && !report.duplication
    ? link(href(t.windowHours, true), { class: 'btn secondary sm' }, icon('search'), 'Add the duplication check') : null;
  return adminLayout('suspicious',
    pageHead({ crumbs: adminCrumbs({ label: 'Suspicious activity' }), title: 'Suspicious activity', actions: [windows, densityToggle()],
      subtitle: 'Refused forged or impossible requests per account, and gains far above the realm\'s median, since ' + day(report.since) + '. Read only.' }),
    h('section', { class: 'kpis', 'aria-label': 'Totals' },
      kpi('Listed', 'flag', num(report.rows.length), null, report.duplication ? 'duplication check included' : 'without the duplication check', { warn: report.rows.length > 0 }),
      kpi('Accounts refused', 'ban', num(report.accounts), null, 'at least one refusal in the window'),
      kpi('Median Zeny', 'coins', num(t.zenyMedian), null, 'per character that gained any'),
      kpi('Median EXP', 'sparkle', num(t.expMedian), null, 'base EXP per character')),
    h('div', { class: 'stack' },
      h('section', { class: 'card' }, h('div', { class: 'card-head' }, h('h2', null, 'Alerts'), h('div', { class: 'actions' }, dupeButton)), rows,
        h('div', { class: 'card-foot' }, h('span', null, 'The server log has each refusal as "Anticheat <kind> account ...".'))),
      card('Thresholds', null, body(facts([
        ['Window', plural(t.windowHours, 'hour')],
        ['Refused requests', `${num(t.refusals)} or more per account`],
        ['Gains', `${t.gainFactor} × the median, and at least the floor`],
        ['Zeny', `${num(t.zenyFloor)} floor · median ${num(t.zenyMedian)}`],
        ['Items', `${num(t.itemFloor)} floor · median ${num(t.itemMedian)}`],
        ['Base EXP', `${num(t.expFloor)} floor · median ${num(t.expMedian)}`],
      ])), 'Set in the server\'s Ro3Emu:AntiCheat:Alerts.')));
}
