// Admin: GM actions on one character (/admin/characters/<id>): kick, teleport, rename, items and currencies. The
// server runs each through its own paths and records it in the audit log. Also the account page's extra cards: its
// login history and 2FA, its deleted characters (restore) and links to the actions of each character.
'use strict';

registerAdminPage({ id: 'character-actions', label: 'Character actions', icon: 'user', pattern: /^\/admin\/characters\/(\d+)$/, page: adminCharacterPage, role: 'gm' });

/** A card whose body loads after the page is drawn (the account page's extras). */
function lazyCard(title, actions, load) {
  const host = h('div', { class: 'loading-sm' }, h('i', { class: 'spin', 'aria-label': 'Loading' }));
  load().then(node => host.replaceWith(node), e => host.replaceWith(body(h('p', { class: 'form-error' }, e.message))));
  return card(title, actions, host);
}

/** One action of the character page: a heading, a line on what it does, and its form. */
const action = (title, hint, content) => h('div', { class: 'action' }, h('h3', null, title), hint && h('p', { class: 'hint' }, hint), content);

async function adminCharacterPage(id) {
  requireRole('gm');
  const [{ sheet, account }, { maps }] = await Promise.all([api('GET', '/api/characters/' + id), api('GET', '/api/maps')]);
  const post = async (what, payload, after) => {
    const result = await api('POST', `/api/admin/characters/${id}/${what}`, payload);
    toast(result.message);
    if (after) after(); else route();
  };
  const kick = form([textField('Reason (for the audit log)', 'reason', '', { maxlength: '300' })], 'Kick', v => post('kick', { reason: v.reason }), 'danger');
  const mapOptions = maps.map(m => [String(m.id), `${m.name} (${m.id})`]);
  const teleport = form([
    selectField('Map', 'map', mapOptions, String(sheet.mapId)),
    h('div', { class: 'pair' }, field('X (optional)', 'x', 'number', { step: 'any', required: false }), field('Z (optional)', 'z', 'number', { step: 'any', required: false }))],
  'Teleport', v => post('teleport', { map: Number(v.map), ...(v.x !== '' && v.z !== '' ? { x: Number(v.x), z: Number(v.z) } : {}) }), 'secondary');
  const rename = form([field('New name', 'name', 'text', { maxlength: '16', minlength: '2', autocomplete: 'off', value: sheet.name })],
    'Rename', v => post('rename', { name: v.name.trim() }), 'secondary');
  const items = rewardEditor();
  const giveForm = (sign, label, look) => {
    const node = h('form', { class: 'row', novalidate: true },
      h('button', { type: 'submit', class: 'btn sm ' + look }, h('i', { class: 'spin' }), label));
    node.addEventListener('submit', async e => {
      e.preventDefault();
      const list = items.value().filter(r => r.count > 0);
      if (!list.length) { toast('Choose an item or type an amount of Zeny first.'); return; }
      await busy(node.querySelector('button'), async () => {
        for (const r of list) {
          try { await post('items', { item: r.item, count: sign * r.count }, () => {}); } catch (x) { toast(x.message); return; }
        }
        items.reset();
        route();
      });
    });
    return node;
  };
  const currencies = sheet.currencies.length
    ? h('p', { class: 'hint' }, sheet.currencies.map(c => `${c.name} ${num(c.amount)}`).join(' · ')) : null;
  return adminLayout('accounts',
    pageHead({ crumbs: adminCrumbs({ href: '/admin/accounts', label: 'Accounts' },
      { href: '/admin/accounts/' + encodeURIComponent(account), label: account }, { label: sheet.name }),
    title: sheet.name, after: sheet.online ? chip('online', 'Online') : chip('', 'Offline'),
    subtitle: `${sheet.class} · level ${sheet.level} · ${sheet.map}`, actions: link('/characters/' + id, { class: 'btn secondary sm' }, icon('user'), 'Character sheet') }),
    h('div', { class: 'grid' },
      h('div', { class: 'span-7' }, card('Items and currencies', null, body(
        h('p', { class: 'hint' }, sheet.online ? 'Through the GM grant: the player sees the bag change at once. Zeny and currencies are items here too.'
          : 'Only while the character is in the game. Send items by Mail & rewards instead: they arrive at the next login.'),
        currencies, items.node, h('div', { class: 'row' }, giveForm(1, 'Give', 'primary'), giveForm(-1, 'Take away', 'secondary'))))),
      h('div', { class: 'span-5' }, card('Actions', null, body(
        action('Kick', sheet.online ? 'Disconnects the player at once; they can log in again.' : 'Only while the character is in the game.', kick),
        action('Teleport', 'In the game: moves the character now. Out of it: it logs in there. Without coordinates, the map\'s arrival point.', teleport),
        action('Rename', '2 to 16 characters, free in any letter case. Only while the character is out of the game; the old name is free at once.', rename))))));
}

// ---------------------------------------------------------------- the account page's extra cards

adminAccountCards.push((account, self) => lazyCard('Security', null, async () => {
  const s = await api('GET', `/api/admin/accounts/${encodeURIComponent(account.name)}/logins`);
  const admin = mayUse('admin') && !self;
  // Both ask first, in the card itself (confirmBox), like the other actions that cannot be taken back.
  const confirmHost = h('div', { class: 'confirm-after' });
  const reset = admin && s.twoFactor.enabled && h('button', { type: 'button', class: 'btn danger sm', onclick: async () => {
    if (!await confirmBox(confirmHost, `Turn off two-factor authentication of ${account.name}? Its browsers are signed out, and it logs in with the password alone until it sets 2FA up again.`, 'Reset 2FA')) return;
    try { await api('POST', `/api/admin/accounts/${encodeURIComponent(account.name)}/2fa-reset`); toast('Two-factor authentication is off for this account.'); route(); }
    catch (e) { toast(e.message); }
  } }, 'Reset 2FA');
  const logout = admin && s.sessions > 0 && h('button', { type: 'button', class: 'btn secondary sm', onclick: async () => {
    if (!await confirmBox(confirmHost, `Sign ${account.name} out of ${plural(s.sessions, 'panel browser')}? The game is not affected.`, 'Log out')) return;
    try { const { ended } = await api('POST', `/api/admin/accounts/${encodeURIComponent(account.name)}/logout`); toast(`${plural(ended, 'panel session')} ended.`); route(); }
    catch (e) { toast(e.message); }
  } }, icon('out'), 'Log out of the panel');
  return h('div', null, body(facts([
    ['Two-factor', s.twoFactor.enabled ? chip('active', 'On') : chip('', 'Off')],
    ['Panel sessions', h('span', { class: 'num' }, num(s.sessions))]]),
  (reset || logout) && [h('div', { class: 'row tools-gap-top' }, reset, logout), confirmHost]),
  loginsTable(s.logins.slice(0, 8), s.addresses));
}));

adminAccountCards.push(account => lazyCard('Deleted characters', null, async () => {
  const { characters } = await api('GET', `/api/admin/accounts/${encodeURIComponent(account.name)}/deleted`);
  if (!characters.length) return body(h('p', { class: 'hint' }, 'None: the server keeps a deleted character, and lists it here.'));
  const restore = async c => {
    try { toast((await api('POST', `/api/admin/characters/${c.id}/restore`)).message); route(); } catch (e) { toast(e.message); }
  };
  return table([
    { label: 'Name', grow: true, value: c => [h('span', { class: 'strong' }, c.name), h('span', { class: 'sub' }, `${c.class} · level ${c.level}`)] },
    { label: 'Deleted', opt: true, value: c => stamp(c.deleted) },
    { label: '', value: c => !mayUse('gm') ? null : c.nameFree
      ? h('button', { type: 'button', class: 'btn secondary sm', onclick: () => restore(c) }, 'Restore')
      : h('span', { class: 'muted', title: 'Another character took its name since.' }, 'Name taken') },
  ], characters, { short: true });
}));

adminAccountCards.push(account => mayUse('gm') && account.characters.length > 0 && card('Character actions', null,
  h('ul', { class: 'rows' }, account.characters.map(c => h('li', null, h('span', { class: 't' }, c.name),
    c.online ? chip('online', 'Online') : chip('', 'Offline'),
    h('span', { class: 'm' }, link('/admin/characters/' + c.id, { class: 'more' }, 'Kick, teleport, rename, items', icon('arrow'))))))));
