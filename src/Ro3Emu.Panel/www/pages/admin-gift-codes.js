// Admin: gift codes (docs/systems/gift-codes.md): list with redemptions, create typed or generated codes, disable.
'use strict';

registerAdminPage({ id: 'gift-codes', label: 'Gift codes', icon: 'gift', path: '/admin/gift-codes', pattern: /^\/admin\/gift-codes$/, page: adminGiftCodesPage });

const giftStates = { active: 'active', scheduled: 'info', expired: '', 'used up': '', disabled: 'banned' };

async function adminGiftCodesPage() {
  requireAdmin();
  const { codes } = await api('GET', '/api/admin/gift-codes');
  let mode = 'type';
  const typed = areaField('Codes, one per line', 'codes', '', { rows: '3', spellcheck: 'false', autocapitalize: 'characters' });
  const generated = h('div', { class: 'grid tools-tight', hidden: true },
    h('div', { class: 'span-6' }, field('How many', 'count', 'number', { min: '1', max: '500', value: '10' })),
    h('div', { class: 'span-6' }, textField('Prefix (optional)', 'prefix', '', { maxlength: '12', autocapitalize: 'characters' })));
  const seg = h('div', { class: 'seg', role: 'group', 'aria-label': 'Codes' }, [['type', 'Type codes'], ['generate', 'Generate']].map(([value, label]) =>
    h('button', { type: 'button', 'aria-pressed': String(value === mode), onclick: e => {
      mode = value;
      for (const b of seg.children) b.setAttribute('aria-pressed', String(b === e.currentTarget));
      typed.hidden = mode !== 'type';
      generated.hidden = mode !== 'generate';
    } }, label)));
  const rewards = rewardEditor();
  const created = h('div', { 'aria-live': 'polite' });
  const create = form([
    h('div', { class: 'field' }, seg), typed, generated, rewards.node,
    h('div', { class: 'grid tools-tight' },
      h('div', { class: 'span-6' }, field('Max uses (0 = no limit)', 'maxUses', 'number', { min: '0', value: '0' })),
      h('div', { class: 'span-6' }, field('Uses per account', 'perAccount', 'number', { min: '1', value: '1' })),
      h('div', { class: 'span-6' }, field('Starts (optional)', 'starts', 'datetime-local', { required: false })),
      h('div', { class: 'span-6' }, field('Expires (optional)', 'expires', 'datetime-local', { required: false }))),
    textField('Note (optional)', 'note', '', { maxlength: '60' })],
  'Create codes', async (values, node) => {
    const result = await api('POST', '/api/admin/gift-codes', {
      codes: mode === 'type' ? values.codes : null, generateCount: mode === 'generate' ? Number(values.count) || 0 : 0, prefix: values.prefix,
      rewards: rewards.value(), maxUses: Number(values.maxUses) || 0, perAccount: Number(values.perAccount) || 1,
      starts: fromLocalInput(values.starts), expires: fromLocalInput(values.expires), note: values.note,
    });
    created.replaceChildren(h('div', { class: 'field' }, h('span', { class: 'label' }, `Created ${result.created.length}`),
      h('textarea', { class: 'input mono', rows: String(Math.min(8, result.created.length)), readonly: true, 'aria-label': 'Created codes' }, result.created.join('\n'))));
    toast(`${result.created.length} code${result.created.length === 1 ? '' : 's'} created.`);
    node.reset();
    rewards.reset();
    await refresh();
  });
  const listHost = h('div');
  const refresh = async () => {
    const data = await api('GET', '/api/admin/gift-codes');
    listHost.replaceChildren(codeTable(data.codes, refresh));
  };
  listHost.replaceChildren(codeTable(codes, refresh));
  return adminLayout('gift-codes',
    pageHead({ crumbs: adminCrumbs({ label: 'Gift codes' }), title: 'Gift codes', actions: densityToggle(),
      subtitle: 'Codes players type in the game\'s Redemption Code screen; the rewards arrive by mail. Changes are in the audit log.' }),
    card('Codes', null, listHost),
    h('div', { class: 'tools-gap-top' }, card('Create codes', null, body(create, created))));
}

function codeTable(codes, refresh) {
  if (!codes.length) return emptyState('gift', 'No gift codes yet', 'Create one below.');
  const toggle = async (code, disable) => {
    await api('POST', `/api/admin/gift-codes/${encodeURIComponent(code.code)}/${disable ? 'disable' : 'enable'}`);
    toast(`${code.code} ${disable ? 'disabled' : 'enabled'}.`);
    await refresh();
  };
  return table([
    { label: 'Code', value: c => mono(c.code, { title: c.note || null }) },
    { label: 'Rewards', grow: true, wrap: true, value: c => c.rewardText || h('span', { class: 'muted' }, 'none') },
    { label: 'Uses', num: true, value: c => [num(c.uses), c.maxUses > 0 ? ' / ' + num(c.maxUses) : ''] },
    { label: 'Per account', num: true, opt: true, value: c => num(c.perAccount) },
    { label: 'Expires', opt: true, value: c => c.expires ? stamp(c.expires) : h('span', { class: 'muted' }, 'never') },
    { label: 'State', value: c => chip(giftStates[c.state] ?? '', c.state) },
    { label: '', value: c => h('button', { type: 'button', class: 'btn secondary sm', onclick: () => toggle(c, !c.disabled).catch(e => toast(e.message)) },
      c.disabled ? 'Enable' : 'Disable') },
  ], codes);
}
