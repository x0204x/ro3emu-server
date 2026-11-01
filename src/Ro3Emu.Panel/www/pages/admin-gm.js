// Admin: the GM console, any "!" chat command run as your own character or for an online one, with its replies.
'use strict';

registerAdminPage({ id: 'gm', label: 'GM console', icon: 'terminal', path: '/admin/gm', pattern: /^\/admin\/gm$/, page: adminGmPage });

/** Commands typed in this browser tab, newest last (arrow up and down walk through them). */
const gmTyped = [];

async function adminGmPage() {
  requireRole('gm');
  const page = Number(new URLSearchParams(location.search).get('page')) || 1;
  const [{ players }, history] = await Promise.all([api('GET', '/api/online'), api('GET', '/api/admin/gm/history?page=' + page)]);
  if (pastLastPage(history, p => '/admin/gm?page=' + p)) return null;
  // "My character" (the server's default, the admin account's own character) only while it is in the game; else the
  // first online character, or nothing to run as at all.
  const mine = players.find(p => p.account === state.session.account);
  const who = h('select', { class: 'input', id: 'f-as', 'aria-label': 'Run as', disabled: !players.length },
    mine && h('option', { value: '' }, 'My character (' + mine.name + ')'),
    players.filter(p => p !== mine).map(p => h('option', { value: p.name }, `${p.name} · ${p.class} ${p.level} · ${p.map}`)),
    !players.length && h('option', { value: '' }, 'Nobody is in the game'));
  const whoHint = !players.length ? h('p', { class: 'hint' }, 'A command runs as a character in the game. Nobody is in the game now: log in with a character, or wait for a player, then refresh.')
    : !mine ? h('p', { class: 'hint' }, 'Your account has no character in the game, so a command runs as the character chosen here.') : null;
  const line = h('input', { class: 'input mono', id: 'f-line', autocomplete: 'off', spellcheck: 'false', autocapitalize: 'off',
    placeholder: '!help', 'aria-label': 'Command', maxlength: '500' });
  const output = h('div', { class: 'console', role: 'log', 'aria-live': 'polite', 'aria-label': 'Replies' },
    h('p', { class: 'muted' }, 'Replies appear here. !help lists every command.'));
  let cursor = gmTyped.length;
  const run = h('button', { type: 'submit', class: 'btn primary' }, h('i', { class: 'spin' }), 'Run');
  const prompt = h('form', { class: 'console-input', novalidate: true }, line, run);
  prompt.addEventListener('submit', async event => {
    event.preventDefault();
    const text = line.value.trim();
    if (!text) return;
    gmTyped.push(text);
    cursor = gmTyped.length;
    await busy(run, async () => {
      try {
        const result = await api('POST', '/api/admin/gm', { character: who.value || null, line: text });
        output.append(h('div', { class: 'console-entry' },
          h('p', { class: 'console-cmd' }, mono((result.character || who.value || state.session.account) + ' > ' + (text.startsWith('!') ? text : '!' + text))),
          result.replies.length ? result.replies.map(r => h('p', { class: result.ran ? 'console-reply' : 'console-reply error' }, r))
            : h('p', { class: 'console-reply muted' }, '(no reply)')));
        line.value = '';
      } catch (e) {
        output.append(h('p', { class: 'console-reply error' }, e.message));
      }
      output.scrollTop = output.scrollHeight;
    });
    line.focus();
  });
  line.addEventListener('keydown', e => {
    if (e.key === 'ArrowUp' && cursor > 0) { cursor--; line.value = gmTyped[cursor]; e.preventDefault(); }
    else if (e.key === 'ArrowDown' && cursor < gmTyped.length) { cursor++; line.value = gmTyped[cursor] ?? ''; e.preventDefault(); }
  });
  const quick = ['!help', '!where', '!online'].map(c => h('button', { type: 'button', class: 'btn secondary sm', disabled: !players.length, onclick: () => { line.value = c; prompt.requestSubmit(); } }, mono(c)));
  const past = history.entries.length
    ? table([
      { label: 'Time', opt: true, value: e => stamp(e.utc) },
      { label: 'Character', value: e => e.target },
      { label: 'Command and first reply', grow: true, wrap: true, value: e => mono(e.detail) },
    ], history.entries)
    : emptyState('terminal', 'No commands yet', 'Every command run here is recorded.');
  return adminLayout('gm',
    pageHead({ crumbs: adminCrumbs({ label: 'GM console' }), title: 'GM console', actions: densityToggle(),
      subtitle: 'Runs a GM chat command as a character in the game, exactly as typed in chat, and shows the replies it sends. Every command is in the audit log.' }),
    card('Console', h('div', { class: 'row' }, quick), body(
      h('div', { class: 'field' }, h('label', { class: 'label', for: 'f-as' }, 'Run as'), who, whoHint),
      output, prompt)),
    h('section', { class: 'card tools-gap-top' }, h('div', { class: 'card-head' }, h('h2', null, 'Your past commands')), past,
      h('div', { class: 'card-foot' }, pager(history, p => '/admin/gm?page=' + p))));
}
