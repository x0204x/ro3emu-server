// Admin: in-game mail with item attachments to one character, an account's characters or everyone.
'use strict';

registerAdminPage({ id: 'mail', label: 'Mail & rewards', icon: 'mail', path: '/admin/mail', pattern: /^\/admin\/mail$/, page: adminMailPage });

async function adminMailPage() {
  requireAdmin();
  const kinds = [['character', 'One character'], ['account', "An account's characters"], ['everyone', 'Everyone']];
  let kind = 'character';
  const name = h('input', { class: 'input', id: 'f-target', name: 'target', spellcheck: 'false', autocapitalize: 'off', placeholder: 'Character name' });
  const nameField = h('div', { class: 'field' }, h('label', { class: 'label', for: 'f-target' }, 'Name'), name);
  const online = checkbox('Only characters in the game now', 'online', false);
  const minLevel = h('input', { class: 'input', type: 'number', id: 'f-minlevel', min: '0', value: '0' });
  const levelField = h('div', { class: 'field', hidden: true }, h('label', { class: 'label', for: 'f-minlevel' }, 'From base level'), minLevel);
  const preview = h('div', { class: 'hint', 'aria-live': 'polite' }, 'Check who gets it before you send.');
  const seg = h('div', { class: 'seg', role: 'group', 'aria-label': 'Recipients' }, kinds.map(([value, label]) =>
    h('button', { type: 'button', 'aria-pressed': String(value === kind), onclick: e => {
      kind = value;
      for (const b of seg.children) b.setAttribute('aria-pressed', String(b === e.currentTarget));
      nameField.hidden = kind === 'everyone';
      levelField.hidden = kind !== 'everyone';
      name.placeholder = kind === 'account' ? 'Account name' : 'Character name';
    } }, label)));
  const target = () => ({ kind, name: name.value.trim(), onlineOnly: online.querySelector('input').checked, minLevel: Number(minLevel.value) || 0 });
  const check = async () => {
    const r = await api('POST', '/api/admin/mail/recipients', { target: target() });
    preview.replaceChildren(r.count === 0 ? 'Nobody matches.'
      : [mono(num(r.count)), r.count === 1 ? ' character: ' : ' characters: ', r.sample.join(', '), r.count > r.sample.length ? ', ...' : '']);
    return r.count;
  };
  const rewards = rewardEditor();
  const confirmHost = h('div');
  const send = h('button', { type: 'submit', class: 'btn primary block' }, h('i', { class: 'spin' }), 'Send mail');
  const error = h('p', { class: 'form-error', role: 'alert', hidden: true });
  const letter = h('form', { novalidate: true },
    textField('Sender', 'sender', 'GM', { maxlength: '24' }), field('Title', 'title', 'text', { maxlength: '60' }),
    areaField('Text', 'text', '', { maxlength: '1000', rows: '4' }), rewards.node, confirmHost, error, send);
  letter.addEventListener('submit', async event => {
    event.preventDefault();
    error.hidden = true;
    await busy(send, async () => {
      try {
        const values = Object.fromEntries(new FormData(letter));
        const payload = { target: target(), title: values.title, text: values.text, sender: values.sender, attachments: rewards.value() };
        if (kind !== 'character') {
          const count = await check();
          if (count === 0 || !await confirmBox(confirmHost, `This mail goes to ${num(count)} characters. Send it?`, `Send to ${num(count)}`)) return;
          Object.assign(payload, { confirm: true, expected: count });
        }
        const result = await api('POST', '/api/admin/mail', payload);
        toast(`Mail delivered to ${num(result.delivered)}${result.failed ? `, ${num(result.failed)} failed (see the server log)` : ''}.`);
        letter.reset();
        rewards.reset();
      } catch (e) { error.textContent = e.message; error.hidden = false; }
    });
  });
  return adminLayout('mail',
    pageHead({ crumbs: adminCrumbs({ label: 'Mail & rewards' }), title: 'Mail & rewards',
      subtitle: 'In-game mail from the server, with items and Zeny attached. Players in the game get it at once; the others when they log in. Every send is in the audit log.' }),
    h('div', { class: 'grid' },
      h('div', { class: 'span-5' }, card('Recipients', null, body(
        h('div', { class: 'field' }, seg), nameField, levelField, online,
        h('div', { class: 'row tools-gap' }, h('button', { type: 'button', class: 'btn secondary sm', onclick: () => check().catch(e => { preview.textContent = e.message; }) }, 'Check recipients')),
        preview))),
      h('div', { class: 'span-7' }, card('Letter', null, body(letter)))));
}
