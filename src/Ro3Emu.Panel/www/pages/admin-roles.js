// Admin: staff roles. Moderators see accounts and characters and handle bans and reports; GMs also use the game tools
// and character actions; Admins also roles, server control and backups. Roles from the server's configuration are
// shown but changed only there.
'use strict';

registerAdminPage({ id: 'roles', label: 'Staff roles', icon: 'key', path: '/admin/roles', pattern: /^\/admin\/roles$/, page: adminRolesPage, role: 'admin' });

const roleSources = { config: 'the configuration (Ro3Emu:Panel:Roles)', 'gm-accounts': 'Ro3Emu:GmAccounts', panel: 'the panel' };

async function adminRolesPage() {
  requireRole('admin');
  const { staff } = await api('GET', '/api/admin/roles');
  const set = async (account, role) => {
    await api('POST', '/api/admin/roles', { account, role });
    toast(role === 'none' ? `${account} has no staff role now.` : `${account} is ${roleName(role)} now.`);
    route();
  };
  const grant = form([
    field('Account', 'staff', 'search', { autocomplete: 'off', maxlength: '64', placeholder: 'Account to give a role' }),
    selectField('Role', 'role', [['moderator', 'Moderator'], ['gm', 'GM'], ['admin', 'Admin']], 'moderator')],
  'Give the role', async values => set(values.staff.trim(), values.role));
  const rows = staff.length
    ? table([
      { label: 'Account', grow: true, value: s => [link('/admin/accounts/' + encodeURIComponent(s.account), { class: 'row-link mono' }, s.account),
        h('span', { class: 'sub' }, s.grantedBy ? ['Given by ', s.grantedBy, ' · ', stamp(s.granted)] : 'From ' + roleSources[s.source])] },
      { label: 'Role', value: s => roleChip(s.role) },
      { label: '2FA', value: s => s.twoFactor ? chip('active', 'On') : chip('', 'Off') },
      { label: 'Change', value: s => s.source !== 'panel' || s.account === state.session.account ? h('span', { class: 'muted' }, '-')
        : h('select', { class: 'input', 'aria-label': 'Role of ' + s.account, onchange: e => set(s.account, e.target.value).catch(x => toast(x.message)) },
          [['moderator', 'Moderator'], ['gm', 'GM'], ['admin', 'Admin'], ['none', 'Remove']].map(([v, t]) => h('option', { value: v, selected: v === s.role }, t))) },
    ], staff)
    : emptyState('key', 'No staff yet', 'List your own account in Ro3Emu:GmAccounts to administer the server.');
  return adminLayout('roles',
    pageHead({ crumbs: adminCrumbs({ label: 'Staff roles' }), title: 'Staff roles', actions: densityToggle(),
      subtitle: 'Who may use the admin area, and how much of it. Every change is in the audit log.' }),
    h('div', { class: 'grid' },
      h('div', { class: 'span-8' }, card('Staff', null, rows)),
      h('div', { class: 'span-4 stack' },
        card('Give a role', null, body(h('p', { class: 'hint' }, 'The account must exist. A role it already has from the configuration cannot be changed here.'), grant)),
        card('What each role may do', null, body(h('ul', { class: 'notes' },
          [['moderator', 'Accounts and characters (read), bans, reports, the audit log.'],
            ['gm', 'Also mail, gift codes, the GM console, news, broadcasts, the economy log and character actions.'],
            ['admin', 'Also staff roles, server control, backups and 2FA resets.']].map(([role, text]) =>
            h('li', null, h('div', { class: 'm' }, roleChip(role)), h('p', null, text)))))))));
}
