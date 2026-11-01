// Admin: the Updates section of Server control. The installed version against the newest releases on GitHub, a
// checked download staged next to the data, and an install or rollback planned like a restart (the countdown in the
// game). What it may do depends on how the server runs; where it cannot install, it lists the steps to do it by hand.
// Release notes and every other value from GitHub reach the page as text.
'use strict';

const updateModes = { systemd: 'systemd', service: 'Windows service', docker: 'Docker', app: 'RO3Emu app', manual: 'Started by hand', source: 'Source build' };
const updatePhases = { downloading: 'Downloading', extracting: 'Unpacking', checking: 'Checking', failed: 'Download failed', staged: 'Ready to install',
  planned: 'Install planned', installing: 'Installing' };
const updateKinds = { install: ['Installed', 'active'], rollback: ['Rolled back', 'event'], started: ['Started', 'info'], failed: ['Failed', 'banned'] };
const updateRunning = ['downloading', 'extracting', 'checking', 'installing'];
const updateWhen = [['0', 'Now'], ['1', 'In 1 minute'], ['5', 'In 5 minutes'], ['10', 'In 10 minutes'], ['15', 'In 15 minutes'], ['30', 'In 30 minutes'], ['60', 'In 1 hour']];
/** Newer releases listed at once; the older ones open with a button. */
const updateReleasesShown = 5;
const updateMegabytes = n => (n / (1 << 20)).toFixed(1);
/** True when version a (1.0.9) is before b (1.0.10): the three numbers compared as numbers. */
const updateBefore = (a, b) => {
  const [x, y] = [a, b].map(v => String(v).split(/[.+-]/).slice(0, 3).map(Number));
  const i = x.findIndex((n, k) => n !== y[k]);
  return i >= 0 && x[i] < y[i];
};

/** The section, drawn from one state. While a download runs it asks again every second, until the page changes. */
function updatesSection(state) {
  const host = h('div', { class: 'updates' });
  let timer = 0;
  const refresh = async () => {
    try { draw(await api('GET', '/api/admin/updates')); } catch (e) { toast(e.message); }
  };
  const draw = u => {
    clearTimeout(timer);
    host.replaceChildren(...updateCards(u, refresh));
    if (updateRunning.includes(u.job.phase)) timer = setTimeout(() => { if (host.isConnected) refresh(); }, 1000);
  };
  draw(state);
  return host;
}

/** A POST of the section; its answer's message as a toast, then the state again. */
async function updateAction(path, payload, refresh) {
  try { toast((await api('POST', path, payload)).message); } catch (e) { toast(e.message); }
  await refresh();
}

function updateCards(u, refresh) {
  return [
    h('div', { class: 'grid' },
      h('div', { class: 'span-6' }, updateVersionCard(u, refresh)),
      h('div', { class: 'span-6' }, updateJobCard(u, refresh))),
    h('div', { class: 'grid' }, h('div', { class: 'span-12' }, updateReleasesCard(u, refresh))),
    h('div', { class: 'grid' },
      h('div', { class: 'span-6' }, updatePreviousCard(u)),
      h('div', { class: 'span-6' }, updateHistoryCard(u.history))),
  ];
}

function updateVersionCard(u, refresh) {
  const c = u.check;
  const check = h('button', { type: 'button', class: 'btn secondary sm', disabled: !c.enabled, onclick: async () => {
    await busy(check, async () => {
      try { const next = await api('POST', '/api/admin/updates/check'); toast(next.check.error || 'Checked.'); } catch (e) { toast(e.message); }
      await refresh();
    });
  } }, h('i', { class: 'spin' }), icon('clock'), 'Check now');
  const status = !c.checked ? chip('plain', 'Not checked') : u.newer.length ? chip('event', plural(u.newer.length, 'newer release')) : chip('active', 'Up to date');
  return card('Version', check, body(
    facts([
      ['Installed', [mono(u.installed.version), u.installed.commit && h('span', { class: 'sub' }, 'commit ' + u.installed.commit)]],
      ['Platform', mono(u.installed.platform)],
      ['Latest release', u.latest ? [mono(u.latest.tag), ' ', day(u.latest.published)] : h('span', { class: 'muted' }, '-')],
      ['Status', status],
      ['Checked', c.checked ? [stamp(c.checked), h('span', { class: 'sub' }, ago(c.checked))] : h('span', { class: 'muted' }, 'never')],
      c.next && ['Next check', stamp(c.next)],
      ['Releases of', mono(c.repository)],
    ].filter(Boolean)),
    !c.enabled && h('p', { class: 'hint tools-gap-top' }, 'Update checks are off (Ro3Emu:Updates:Check). No request leaves the server.'),
    c.error && h('div', { class: 'notice danger tools-gap-top', role: 'status' }, icon('alert'),
      h('div', null, h('b', null, 'The last check failed'), c.error, ' The list shows the last good answer.')),
    h('div', { class: 'update-mode tools-gap-top' }, chip(u.hosting.canInstall ? 'active' : 'plain', updateModes[u.hosting.mode] || u.hosting.mode),
      h('p', null, u.hosting.summary))));
}

/** The download or the staged release: progress, the failure, or what is ready and how it gets installed. */
function updateJobCard(u, refresh) {
  const job = u.job;
  if (['downloading', 'extracting', 'checking'].includes(job.phase)) {
    const cancel = job.phase === 'downloading' && h('button', { type: 'button', class: 'btn secondary sm',
      onclick: () => updateAction('/api/admin/updates/cancel', {}, refresh) }, 'Cancel');
    return card(updatePhases[job.phase] + ' ' + job.tag, cancel, body(
      h('progress', { class: 'update-progress', max: String(Math.max(job.total, 1)), value: String(job.phase === 'downloading' ? job.done : job.total) }),
      h('p', { class: 'hint tools-gap-top' }, job.phase === 'downloading'
        ? `${updateMegabytes(job.done)} of ${updateMegabytes(job.total)} MB. The file is checked against the SHA-256 GitHub lists for it.`
        : job.phase === 'extracting' ? 'Unpacking into the staging folder.' : 'Checking the files and the server\'s version.')));
  }
  if (job.phase === 'failed') {
    const discard = h('button', { type: 'button', class: 'btn secondary sm', onclick: () => updateAction('/api/admin/updates/discard', {}, refresh) }, 'Clear');
    return card(updatePhases.failed, discard, body(h('div', { class: 'notice danger', role: 'status' }, icon('alert'),
      h('div', null, h('b', null, job.tag || 'The download'), job.message)), h('p', { class: 'hint' }, 'Nothing was installed. Try the download again below.')));
  }
  if (!u.staged) {
    return card('Download', null, body(emptyState('box', 'Nothing downloaded',
      u.hosting.canDownload ? 'Download a newer release below; it is checked and unpacked here, and installed only when you say so.' : u.hosting.summary)));
  }
  return updateStagedCard(u, refresh);
}

function updateStagedCard(u, refresh) {
  const s = u.staged;
  const planned = ['planned', 'installing'].includes(u.job.phase);
  const discard = !planned && h('button', { type: 'button', class: 'btn secondary sm', onclick: () => updateAction('/api/admin/updates/discard', {}, refresh) }, 'Delete');
  const how = planned
    ? h('p', { class: 'hint tools-gap-top' }, u.job.phase === 'installing' ? 'The server is installing it and restarts now.' : 'The install is planned; Planned above shows the countdown and can cancel it.')
    : u.hosting.canInstall ? updateInstallForm(s) : updateSteps(u.hosting);
  return card(updatePhases[planned ? u.job.phase : 'staged'], discard, body(
    facts([['Release', mono(s.tag)], ['Server version', mono(s.version)], ['Size', [updateMegabytes(s.bytes), ' MB']], ['Downloaded', stamp(s.staged)],
      ['SHA-256', mono(s.sha256, { class: 'mono break' })], ['Folder', mono(s.folder, { class: 'mono break' })]]),
    how));
}

function updateInstallForm(s) {
  const confirmHost = h('div');
  return form([
    h('p', { class: 'hint tools-gap-top' }, 'At the end of the countdown the server makes a database backup, swaps its files (the running version is kept) and restarts into ' + s.tag + '.'),
    selectField('When', 'installMinutes', updateWhen, '10'),
    textField('Reason (shown in the game)', 'installReason', '', { maxlength: '200', placeholder: 'Update to ' + s.tag + '.' }), confirmHost],
  'Install ' + s.tag, async values => {
    const when = values.installMinutes === '0' ? 'now' : 'in ' + values.installMinutes + ' min';
    if (!await confirmBox(confirmHost, `Install ${s.tag} ${when}? Everyone in the game is told, then the server backs up the database and restarts into the new version.`, 'Install')) return;
    await api('POST', '/api/admin/updates/install', { minutes: Number(values.installMinutes), reason: values.installReason });
    toast('The install is planned. The players are told.');
    route();
  }, 'danger');
}

/** The install by hand, where the server cannot restart itself into new files. */
function updateSteps(hosting) {
  return h('div', { class: 'tools-gap-top' },
    h('p', { class: 'hint' }, 'This server cannot install by itself. To install the checked files:'),
    h('ol', { class: 'steps' }, hosting.steps.map(step => h('li', null, step))));
}

function updateReleasesCard(u, refresh) {
  const busyNow = !!u.job.phase && !['idle', 'staged', 'failed'].includes(u.job.phase);
  const rows = u.newer.map((r, i) => {
    const download = u.hosting.canDownload && r.asset && r.digest && !busyNow && u.staged?.tag !== r.tag
      && h('button', { type: 'button', class: 'btn secondary sm', onclick: e => busy(e.currentTarget,
        () => updateAction('/api/admin/updates/download', { tag: r.tag }, refresh)) }, h('i', { class: 'spin' }), icon('box'), 'Download');
    return h('li', { class: 'release', hidden: i >= updateReleasesShown },
      h('div', { class: 'release-head' },
        h('div', { class: 'release-title' }, h('b', null, r.name), h('span', { class: 'sub' }, [r.tag, ' · ', day(r.published).textContent,
          r.asset ? ` · ${updateMegabytes(r.bytes)} MB` : ''])),
        h('div', { class: 'row' },
          !r.asset && chip('banned', 'No package for ' + u.installed.platform),
          r.asset && !r.digest && chip('banned', 'No checksum'),
          r.url.startsWith('https://') && h('a', { class: 'btn secondary sm', href: r.url, target: '_blank', rel: 'noopener noreferrer' }, 'On GitHub'),
          download)),
      h('details', { class: 'release-notes' }, h('summary', null, 'Release notes'), h('div', { class: 'release-text' }, r.notes || 'No notes.')));
  });
  const empty = !u.check.enabled ? emptyState('clock', 'Checks are off', 'Turn on Ro3Emu:Updates:Check to see newer releases here.')
    : !u.check.checked ? emptyState('clock', 'Not checked yet', 'Check now asks GitHub for the releases.')
      : emptyState('sparkle', 'Up to date', 'No release is newer than ' + u.installed.version + '.');
  const more = rows.length > updateReleasesShown && h('button', { type: 'button', class: 'btn secondary sm', onclick: () => {
    for (const row of rows) row.hidden = false;
    more.remove();
  } }, 'Show ' + plural(rows.length - updateReleasesShown, 'older release'));
  return card('Newer releases', more, body(rows.length ? h('ul', { class: 'releases' }, rows) : empty));
}

function updatePreviousCard(u) {
  const p = u.previous;
  if (!p) return card('Previous version', null, body(emptyState('clock', 'None kept', 'An install keeps the version it replaces here, to roll back to.')));
  const confirmHost = h('div');
  const back = updateBefore(p.version, u.installed.version) ? 'Roll back to ' + p.version : 'Switch to ' + p.version;
  const rollback = u.hosting.canInstall && !['planned', 'installing'].includes(u.job.phase) && form([
    selectField('When', 'rollbackMinutes', updateWhen, '5'),
    textField('Reason (shown in the game)', 'rollbackReason', '', { maxlength: '200', placeholder: 'Back to ' + p.version + '.' }),
    p.backup && checkbox('Also restore the database backup made before the update', 'rollbackDatabase', false),
    p.backup && h('p', { class: 'hint' }, 'The database is not versioned: each start adds the tables and columns it needs and never removes any. An older server '
      + 'usually opens a database a newer one used, but nothing checks it, and data the newer version wrote may not mean the same to it. Restoring the '
      + 'backup is the sure way back and loses every change since the update; the database it replaces is kept in restore-saved.'),
    confirmHost],
  back, async values => {
    const restore = values.rollbackDatabase === 'on';
    const when = values.rollbackMinutes === '0' ? 'now' : 'in ' + values.rollbackMinutes + ' min';
    if (!await confirmBox(confirmHost, `${back} ${when}?` + (restore ? ' The database goes back to ' + p.backup + ' and every change since is lost.' : ' The database stays as it is.'), back)) return;
    await api('POST', '/api/admin/updates/rollback', { minutes: Number(values.rollbackMinutes), reason: values.rollbackReason, restoreDatabase: restore });
    toast('The rollback is planned. The players are told.');
    route();
  }, 'danger');
  return card('Previous version', null, body(
    facts([['Version', mono(p.version)], ['Kept since', stamp(p.saved)],
      ['Database backup', p.backup ? [mono(p.backup, { class: 'mono break' }), h('span', { class: 'sub' }, updateMegabytes(p.backupBytes) + ' MB, made before the update')]
        : h('span', { class: 'muted' }, 'none')]]),
    rollback || h('p', { class: 'hint tools-gap-top' }, 'Its files are in data/updates/previous. With the server stopped, Ro3Emu.Server --rollback-update puts them back.')));
}

function updateHistoryCard(history) {
  return card('Install history', null, history.length
    ? table([
      { label: 'Time', value: e => stamp(e.utc) },
      { label: 'What', value: e => chip((updateKinds[e.kind] || [e.kind, 'plain'])[1], (updateKinds[e.kind] || [e.kind])[0]) },
      { label: 'Version', value: e => mono(e.from === e.to ? e.to : e.from + ' to ' + e.to) },
      { label: 'By', opt: true, value: e => mono(e.by) },
      { label: 'Detail', opt: true, wrap: true, grow: true, value: e => e.detail },
    ], history)
    : body(emptyState('list', 'Nothing installed yet', 'Installs and rollbacks are listed here and in the audit log.')));
}
