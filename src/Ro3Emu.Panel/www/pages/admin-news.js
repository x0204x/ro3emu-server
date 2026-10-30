// Admin: the news editor (panel_news), read by the public News and Home pages.
'use strict';

registerAdminPage({ id: 'news', label: 'News editor', icon: 'pen', path: '/admin/news', pattern: /^\/admin\/news$/, page: adminNewsPage });

const newsTags = [['news', 'News'], ['event', 'Event'], ['maintenance', 'Maintenance'], ['patch', 'Patch']];

async function adminNewsPage() {
  requireAdmin();
  const { news } = await api('GET', '/api/admin/news');
  const editorHost = h('div');
  const edit = item => {
    editorHost.replaceChildren(newsEditor(item));
    editorHost.querySelector('input')?.focus();
  };
  const remove = async item => {
    const host = editorHost;
    if (!await confirmBox(host, `Delete "${item.title}"? Players no longer see it.`, 'Delete')) { edit(null); return; }
    await api('POST', `/api/admin/news/${item.id}/delete`);
    toast('Deleted.');
    route();
  };
  const now = Date.now();
  const list = news.length
    ? table([
      { label: 'Title', grow: true, wrap: true, value: n => h('span', { class: 'strong' }, n.title) },
      { label: 'Category', opt: true, value: n => chip(n.tag, n.tag) },
      { label: 'Published', value: n => [stamp(n.published), new Date(n.published).getTime() > now ? [' ', chip('info', 'scheduled')] : null] },
      { label: 'Pinned', opt: true, value: n => n.pinned ? chip('info', 'Pinned') : h('span', { class: 'muted' }, '-') },
      { label: '', value: n => h('div', { class: 'row' },
        h('button', { type: 'button', class: 'btn secondary sm', onclick: () => edit(n) }, 'Edit'),
        h('button', { type: 'button', class: 'btn secondary sm', onclick: () => remove(n).catch(e => toast(e.message)) }, 'Delete')) },
    ], news)
    : emptyState('pen', 'No news yet', 'Write the first one on the right; the public News and Home pages show it.');
  edit(null);
  return adminLayout('news',
    pageHead({ crumbs: adminCrumbs({ label: 'News editor' }), title: 'News editor', actions: densityToggle(),
      subtitle: 'Pinned items stay on top. A publish time in the future keeps an item hidden until then. Changes are in the audit log.' }),
    h('div', { class: 'grid' },
      h('div', { class: 'span-7' }, card('All news', null, list)),
      h('div', { class: 'span-5' }, card('Write', h('button', { type: 'button', class: 'btn secondary sm', onclick: () => edit(null) }, 'New'), body(editorHost)))));
}

function newsEditor(item) {
  return form([
    h('p', { class: 'hint' }, item ? ['Editing ', mono('#' + item.id)] : 'A new item.'),
    field('Title', 'title', 'text', { value: item?.title ?? '', maxlength: '120' }),
    h('div', { class: 'grid tools-tight' },
      h('div', { class: 'span-6' }, selectField('Category', 'tag', newsTags, item?.tag ?? 'news')),
      h('div', { class: 'span-6' }, field('Publish', 'published', 'datetime-local', { value: toLocalInput(item?.published ?? new Date().toISOString()) }))),
    checkbox('Pin to the top', 'pinned', item?.pinned),
    areaField('Text', 'body', item?.body ?? '', { rows: '8', maxlength: '10000' })],
  item ? 'Save changes' : 'Publish', async values => {
    await api('POST', '/api/admin/news', { id: item?.id ?? 0, title: values.title, body: values.body, tag: values.tag,
      published: fromLocalInput(values.published), pinned: values.pinned === 'on' });
    toast(item ? 'Saved.' : 'Published.');
    route();
  });
}
