// Admin: broadcasts, the GM marquee on every online player's screen, now and optionally again on a schedule.
'use strict';

registerAdminPage({ id: 'broadcasts', label: 'Broadcasts', icon: 'bell', path: '/admin/broadcasts', pattern: /^\/admin\/broadcasts$/, page: adminBroadcastsPage });

async function adminBroadcastsPage() {
  requireAdmin();
  const { broadcasts } = await api('GET', '/api/admin/broadcasts');
  const confirmHost = h('div');
  const repeat = selectField('Repeat', 'every', [['0', 'Only now'], ['5', 'Every 5 minutes'], ['10', 'Every 10 minutes'], ['15', 'Every 15 minutes'],
    ['30', 'Every 30 minutes'], ['60', 'Every hour'], ['180', 'Every 3 hours']], '0');
  const send = form([
    field('Message', 'text', 'text', { maxlength: '200', placeholder: 'The server restarts in 10 minutes.' }),
    h('div', { class: 'grid tools-tight' },
      h('div', { class: 'span-6' }, repeat),
      h('div', { class: 'span-6' }, field('Times after this one', 'repeats', 'number', { min: '0', max: '100', value: '0' }))),
    confirmHost],
  'Broadcast', async (values, node) => {
    const every = Number(values.every) || 0, repeats = every > 0 ? Math.max(1, Number(values.repeats) || 0) : 0;
    const what = repeats > 0 ? `now and ${repeats} more time${repeats === 1 ? '' : 's'}, every ${every} min` : 'now';
    if (!await confirmBox(confirmHost, `Show "${values.text}" to every player in the game ${what}?`, 'Broadcast')) return;
    const result = await api('POST', '/api/admin/broadcasts', { text: values.text, everyMinutes: every, repeats });
    toast(`Shown to ${num(result.reached)} player${result.reached === 1 ? '' : 's'}.`);
    node.reset();
    route();
  });
  const cancel = async b => {
    await api('POST', `/api/admin/broadcasts/${b.id}/cancel`);
    toast('Repeats cancelled.');
    route();
  };
  const active = broadcasts.length
    ? table([
      { label: 'Message', grow: true, wrap: true, value: b => b.text },
      { label: 'Every', num: true, value: b => [num(b.everyMinutes), ' min'] },
      { label: 'Left', num: true, value: b => num(b.remaining) },
      { label: 'Next', value: b => stamp(b.next) },
      { label: 'By', opt: true, value: b => mono(b.admin) },
      { label: '', value: b => h('button', { type: 'button', class: 'btn secondary sm', onclick: () => cancel(b).catch(e => toast(e.message)) }, 'Cancel') },
    ], broadcasts)
    : emptyState('bell', 'Nothing repeats', 'A broadcast with a repeat shows up here until its last showing.');
  return adminLayout('broadcasts',
    pageHead({ crumbs: adminCrumbs({ label: 'Broadcasts' }), title: 'Broadcasts',
      subtitle: 'A one-line notice on the screen of everyone in the game (the GM marquee). Repeats live in the server\'s memory and end with a restart. Every broadcast is in the audit log.' }),
    h('div', { class: 'grid' },
      h('div', { class: 'span-5' }, card('Send', null, body(send))),
      h('div', { class: 'span-7' }, card('Repeating', null, active))));
}
