// Admin: server control. A restart, shutdown or backup restore at a set time with countdown notices in the game,
// maintenance mode (only staff get into the game), and the database backups: make one now, download, restore. A
// restore is a restart whose next start puts the backup back before the realm opens; nothing swaps the live database.
// The Updates section below them is pages/admin-updates.js.
'use strict';

registerAdminPage({ id: 'server', label: 'Server control', icon: 'pulse', path: '/admin/server', pattern: /^\/admin\/server$/, page: adminServerPage, role: 'admin' });

const scheduleKinds = { restart: 'Restart', shutdown: 'Shutdown', restore: 'Restore and restart', update: 'Update and restart', rollback: 'Roll back and restart' };
const megabytes1 = n => (n / (1 << 20)).toFixed(n < 10 << 20 ? 1 : 0);

function maintenanceCard(m) {
  const toggle = form([textField('Note for the audit log', 'note', m.note || '', { maxlength: '300' })],
    m.on ? 'End maintenance' : 'Start maintenance', async values => {
      await api('POST', '/api/admin/server/maintenance', { on: !m.on, note: values.note });
      toast(m.on ? 'Maintenance is over: everyone can log in.' : 'Maintenance: only staff can log in to the game now.');
      route();
    }, m.on ? 'primary' : 'danger');
  return card('Maintenance', m.on ? chip('banned', 'On') : chip('active', 'Off'), body(
    h('p', { class: 'hint' }, 'While it is on, a game login of an account without a staff role is refused with the game\'s maintenance notice. Players in the game stay; plan a restart to send them out.'),
    m.on && facts([['Since', stamp(m.since)], ['By', mono(m.by || '-')], ['Note', m.note || h('span', { class: 'muted' }, '-')]]),
    toggle));
}

function scheduleCard(scheduled) {
  if (scheduled) {
    const cancel = h('button', { type: 'button', class: 'btn secondary sm', onclick: async () => {
      try { await api('POST', '/api/admin/server/cancel'); toast('Cancelled; the players were told.'); route(); } catch (e) { toast(e.message); }
    } }, 'Cancel');
    return card('Planned', chip('event', scheduleKinds[scheduled.kind]), body(
      // The live countdown of pages/wiki.js (its ticker moves it); its own class here, not the countdown's.
      h('p', { class: 'planned-countdown' }, countdown(scheduled.at, 'In ', 'Stopping now')),
      facts([['At', stamp(scheduled.at)], ['By', mono(scheduled.by)], ['Reason', scheduled.reason || h('span', { class: 'muted' }, '-')],
        scheduled.backup && ['Backup', mono(scheduled.backup)], scheduled.version && ['Version', mono(scheduled.version)]].filter(Boolean)),
      h('div', { class: 'row tools-gap-top' }, cancel)));
  }
  const confirmHost = h('div');
  const plan = form([
    h('div', { class: 'pair' },
      selectField('What', 'kind', [['restart', 'Restart'], ['shutdown', 'Shutdown']], 'restart'),
      selectField('When', 'minutes', [['0', 'Now'], ['1', 'In 1 minute'], ['5', 'In 5 minutes'], ['10', 'In 10 minutes'], ['15', 'In 15 minutes'],
        ['30', 'In 30 minutes'], ['60', 'In 1 hour']], '10')),
    textField('Reason (shown in the game)', 'reason', '', { maxlength: '200', placeholder: 'Patch day.' }), confirmHost],
  'Plan it', async values => {
    if (!await confirmBox(confirmHost, `${scheduleKinds[values.kind]} ${values.minutes === '0' ? 'now' : 'in ' + values.minutes + ' min'}? Everyone in the game is told.`, 'Plan it')) return;
    await api('POST', '/api/admin/server/schedule', { kind: values.kind, minutes: Number(values.minutes), reason: values.reason });
    toast('Planned. The players are told.');
    route();
  }, 'danger');
  return card('Restart or shut down', null, body(
    h('p', { class: 'hint' }, 'The server tells the players in the game at once and again at 15, 10, 5, 3, 2 and 1 minutes and 30 and 10 seconds, then stops cleanly. A restart comes back only under a process manager (systemd, Docker, a Windows service); without one it stays stopped.'),
    plan));
}

function backupsCard(backups) {
  const make = h('button', { type: 'button', class: 'btn secondary sm', onclick: async () => {
    await busy(make, async () => {
      try { toast('Backup ' + (await api('POST', '/api/admin/backups')).name + ' made.'); route(); } catch (e) { toast(e.message); }
    });
  } }, h('i', { class: 'spin' }), icon('box'), 'Make a backup now');
  const confirmHost = h('div');
  const restore = async b => {
    if (!await confirmBox(confirmHost, `Restore ${b.name} (${stampText(b.created)})? The server restarts in 5 minutes and every change after the backup is lost. The database it replaces is kept in restore-saved.`, 'Restore')) return;
    try {
      await api('POST', '/api/admin/server/schedule', { kind: 'restore', minutes: 5, reason: 'Maintenance.', backup: b.name });
      toast('The restore is planned in 5 minutes.');
      route();
    } catch (e) { toast(e.message); }
  };
  return card('Backups', make, [h('div', { class: 'confirm-slot' }, confirmHost),
    backups.length
      ? table([
        { label: 'File', grow: true, value: b => [mono(b.name, { class: 'mono break' }), b.compressed ? h('span', { class: 'sub' }, 'compressed') : null] },
        { label: 'Made', opt: true, value: b => stamp(b.created) },
        { label: 'Size', num: true, value: b => [megabytes1(b.bytes), ' MB'] },
        { label: '', value: b => h('div', { class: 'row nowrap' },
          h('a', { class: 'btn secondary sm', href: '/api/admin/backups/' + encodeURIComponent(b.name), download: b.name }, 'Download'),
          h('button', { type: 'button', class: 'btn secondary sm', onclick: () => restore(b) }, 'Restore')) },
      ], backups)
      : emptyState('box', 'No backups yet', 'The server makes one at every start and every few hours (Ro3Emu:Backup).')]);
}

/** A time as the stamps show it, for a sentence. */
function stampText(iso) { return stamp(iso).textContent; }

async function adminServerPage() {
  requireRole('admin');
  const [s, updates] = await Promise.all([api('GET', '/api/admin/server'), api('GET', '/api/admin/updates')]);
  return adminLayout('server',
    pageHead({ crumbs: adminCrumbs({ label: 'Server control' }), title: 'Server control', actions: densityToggle(),
      subtitle: 'Restarts, maintenance, backups and updates. Every action here is in the audit log.' }),
    s.maintenance.on && h('div', { class: 'notice danger', role: 'status' }, icon('alert'),
      h('div', null, h('b', null, 'Maintenance is on'), 'Only staff can log in to the game.')),
    s.lastRestore && h('div', { class: 'notice', role: 'status' }, icon('clock'), h('div', null, h('b', null, 'Last restore'), s.lastRestore)),
    h('div', { class: 'grid' },
      h('div', { class: 'span-6' }, scheduleCard(s.scheduled)),
      h('div', { class: 'span-6' }, maintenanceCard(s.maintenance))),
    h('div', { class: 'grid' }, h('div', { class: 'span-12' }, backupsCard(s.backups))),
    h('h2', { class: 'section-title' }, 'Updates'),
    updatesSection(updates));
}
