// Admin: the report inbox. The players' in-game reports (/bug in chat and the Feedback button) with a status, an
// assignee and notes; a report shows who sent it, where and when, and the server's log lines about that player.
'use strict';

registerAdminPage({ id: 'reports', label: 'Reports', icon: 'alert', path: '/admin/reports', pattern: /^\/admin\/reports$/, page: adminReportsPage, role: 'moderator' });
routes.push([/^\/admin\/reports\/(\d+)$/, adminReportPage, 'admin']);

const reportStatuses = [['new', 'New'], ['in-progress', 'In progress'], ['done', 'Done'], ['wont-fix', "Won't fix"]];
const statusName = status => (reportStatuses.find(([id]) => id === status) || [status, status])[1];
const statusChip = status => chip(status, statusName(status));

async function adminReportsPage() {
  requireRole('moderator');
  const params = new URLSearchParams(location.search);
  const status = params.get('status') || '', page = Number(params.get('page')) || 1;
  const result = await api('GET', `/api/admin/reports?status=${encodeURIComponent(status)}&page=${page}`);
  const href = (s, p) => '/admin/reports' + (s || p > 1 ? '?' + new URLSearchParams({ ...(s ? { status: s } : {}), ...(p > 1 ? { page: p } : {}) }) : '');
  const all = Object.values(result.counts).reduce((a, b) => a + b, 0);
  const tabs = h('div', { class: 'seg', role: 'tablist', 'aria-label': 'Status' },
    link(href('', 1), { role: 'tab', 'aria-selected': String(!status) }, 'All ', mono(num(all))),
    reportStatuses.map(([id, label]) => link(href(id, 1), { role: 'tab', 'aria-selected': String(status === id) }, label, ' ', mono(num(result.counts[id] || 0)))));
  const open = r => navigate('/admin/reports/' + r.id);
  const rows = result.reports.length
    ? table([
      { label: '#', num: true, value: r => link('/admin/reports/' + r.id, { class: 'row-link mono' }, '#' + r.id) },
      { label: 'Time', opt: true, value: r => stamp(r.time) },
      { label: 'Player', value: r => [h('span', { class: 'strong' }, r.character || '-'), h('span', { class: 'sub mono' }, r.account || '')] },
      { label: 'Report', grow: true, wrap: true, value: r => r.text },
      { label: 'Map', opt: true, value: r => r.map || h('span', { class: 'muted' }, '-') },
      { label: 'Status', value: r => statusChip(r.status) },
      { label: 'Assignee', opt: true, value: r => r.assignee ? mono(r.assignee) : h('span', { class: 'muted' }, '-') },
    ], result.reports, { onrow: open })
    : emptyState('alert', status ? 'No report has this status' : 'No reports yet', 'Players report with /bug in chat or the Feedback button of the menu.');
  return adminLayout('reports',
    pageHead({ crumbs: adminCrumbs({ label: 'Reports' }), title: 'Reports', actions: densityToggle(),
      subtitle: 'What players reported from inside the game, newest first. A Discord webhook, if set, still gets each one.' }),
    h('section', { class: 'card' }, h('div', { class: 'card-head' }, h('div', { class: 'toolbar' }, tabs)), rows,
      h('div', { class: 'card-foot' }, pager(result, p => href(status, p)))));
}

/**
 * A server log line ("05:48:12.345 info Category: text", the time of day in UTC, RecentLogBuffer) with its time in
 * local time. The lines come from the minutes before the report, so the date is the report's UTC date, or the day
 * before when the line's time is later than the report's.
 */
function logLineLocal(line, reportIso) {
  const m = /^(\d{2}):(\d{2}):(\d{2})(\.\d+)?(?= )/.exec(line);
  if (!m) return line;
  const report = new Date(reportIso);
  const at = new Date(Date.UTC(report.getUTCFullYear(), report.getUTCMonth(), report.getUTCDate(), +m[1], +m[2], +m[3]));
  if (at - report > 60_000) at.setUTCDate(at.getUTCDate() - 1);
  return `${pad(at.getHours())}:${pad(at.getMinutes())}:${pad(at.getSeconds())}${m[4] || ''}` + line.slice(m[0].length);
}

async function adminReportPage(id) {
  requireRole('moderator');
  const r = await api('GET', '/api/admin/reports/' + id);
  const triage = form([
    selectField('Status', 'status', reportStatuses, r.status),
    textField('Assignee', 'assignee', r.assignee || '', { placeholder: 'Account', maxlength: '64', autocomplete: 'off' }),
    areaField('Note', 'note', '', { maxlength: '1000', rows: '3', placeholder: 'What you found or did (optional)' })],
  'Save', async values => {
    const change = {};
    if (values.status !== r.status) change.status = values.status;
    if ((values.assignee || '').trim() !== (r.assignee || '')) change.assignee = values.assignee.trim();
    if (values.note.trim()) change.note = values.note.trim();
    if (!Object.keys(change).length) throw new Error('Nothing changed.');
    await api('POST', '/api/admin/reports/' + id, change);
    toast('Report updated.');
    route();
  });
  const take = r.assignee === state.session.account ? null
    : h('button', { type: 'button', class: 'btn secondary sm', onclick: () => api('POST', '/api/admin/reports/' + id,
      { assignee: state.session.account, ...(r.status === 'new' ? { status: 'in-progress' } : {}) }).then(() => { toast('Assigned to you.'); route(); }, e => toast(e.message)) }, 'Take it');
  const position = mono(`${r.x.toFixed(0)}, ${r.z.toFixed(0)}`);
  return adminLayout('reports',
    pageHead({ crumbs: adminCrumbs({ href: '/admin/reports', label: 'Reports' }, { label: '#' + r.id }), title: 'Report #' + r.id,
      after: statusChip(r.status), actions: take,
      meta: [['Sent', stamp(r.time)], ['Via', r.source === 'button' ? 'Feedback button' : '/bug in chat']] }),
    h('div', { class: 'grid' },
      h('div', { class: 'span-8 stack' },
        card('What the player wrote', r.truncated ? chip('', 'Cut to the length limit') : null, body(h('p', { class: 'quote' }, r.text))),
        card('Server log about the player', h('span', { class: 'muted' }, plural(r.context.length, 'line')),
          body(r.context.length ? h('pre', { class: 'log' }, r.context.map(l => logLineLocal(l, r.time)).join('\n'))
            : h('p', { class: 'hint' }, 'No log lines named this player in the minutes before the report.')),
          r.context.length ? 'Times in your local time.' : null)),
      h('div', { class: 'span-4 stack' },
        card('Player', null, body(facts([
          ['Character', r.characterId ? link('/characters/' + r.characterId, { class: 'text-link' }, r.character || '#' + r.characterId) : '-'],
          ['Account', r.account ? link('/admin/accounts/' + encodeURIComponent(r.account), { class: 'text-link mono' }, r.account) : '-'],
          ['Level', h('span', { class: 'num' }, num(r.level))],
          ['Map', r.scene ? link('/maps/' + r.scene, { class: 'text-link' }, r.map || 'Map ' + r.scene) : '-'],
          ['Position', position], ['Line', h('span', { class: 'num' }, num(r.line))],
          r.instance ? ['Instance', mono(String(r.instance))] : null].filter(Boolean)))),
        card('Triage', null, body(r.notes.length
          ? h('ul', { class: 'notes' }, r.notes.map(n => h('li', null, h('div', { class: 'm' }, mono(n.author), stamp(n.utc)), h('p', null, n.text))))
          : null, triage),
        h('span', null, r.updated ? ['Last change by ', mono(r.updatedBy), ', ', ago(r.updated)] : 'Nobody has looked at it yet.')))));
}
