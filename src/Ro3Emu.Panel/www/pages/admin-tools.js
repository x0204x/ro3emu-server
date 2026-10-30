// Shared pieces of the admin tools (mail, gift codes, GM console, news, broadcasts): the reward editor, date inputs,
// a checkbox and the inline confirmation. Uses the helpers of app.js, loaded before this file.
'use strict';

/** "2026-10-30T14:06" for an <input type=datetime-local> from an ISO time (local time), or ''. */
function toLocalInput(iso) {
  if (!iso) return '';
  const d = new Date(iso);
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

/** The ISO time of a datetime-local value, or null when it is empty. */
const fromLocalInput = value => value ? new Date(value).toISOString() : null;

function checkbox(label, name, checked) {
  return h('label', { class: 'check' }, h('input', { type: 'checkbox', name, checked: !!checked }), h('span', null, label));
}

function selectField(label, name, options, value) {
  const id = 'f-' + name;
  return h('div', { class: 'field' }, h('label', { class: 'label', for: id }, label),
    h('select', { class: 'input', id, name }, options.map(([v, text]) => h('option', { value: v, selected: String(v) === String(value) }, text))));
}

function textField(label, name, value, extra) {
  return field(label, name, 'text', { value: value ?? '', required: false, ...extra });
}

function areaField(label, name, value, extra) {
  const id = 'f-' + name;
  return h('div', { class: 'field' }, h('label', { class: 'label', for: id }, label),
    h('textarea', { class: 'input', id, name, ...extra }, value ?? ''));
}

/**
 * Items with counts (mail attachments, gift code rewards): a search by name or id over the server's item table, the
 * chosen rows, and Zeny (item 1, the currency !zeny adds) as a plain number. value() gives [{ item, count }].
 */
function rewardEditor(initial = []) {
  const rows = initial.map(r => ({ ...r }));
  const list = h('div', { class: 'reward-list' });
  const results = h('div', { class: 'reward-results', role: 'listbox', 'aria-label': 'Items found' });
  const query = h('input', { class: 'input', type: 'search', placeholder: 'Item name or id', 'aria-label': 'Find an item', spellcheck: 'false' });
  let timer = 0;
  const draw = () => list.replaceChildren(...(rows.length ? rows.map((row, i) => h('div', { class: 'reward-row' },
    thumb(row.icon, row.name || String(row.item), 'q1'), mono(String(row.item)), h('span', { class: 'reward-name' }, row.name || 'Item ' + row.item),
    h('input', { class: 'input', type: 'number', min: '1', value: String(row.count), 'aria-label': 'Count of ' + (row.name || row.item),
      oninput: e => { row.count = Number(e.target.value); } }),
    h('button', { type: 'button', class: 'icon-btn', 'aria-label': 'Remove', onclick: () => { rows.splice(i, 1); draw(); } }, icon('x'))))
    : [h('p', { class: 'hint' }, 'No items yet.')]));
  const search = async () => {
    const q = query.value.trim();
    if (!q) { results.replaceChildren(); return; }
    const { items } = await api('GET', '/api/admin/items?q=' + encodeURIComponent(q));
    results.replaceChildren(...(items.length ? items.map(item => h('button', { type: 'button', class: 'reward-hit', role: 'option',
      onclick: () => { if (!rows.some(r => r.item === item.id)) rows.push({ item: item.id, name: item.name, icon: item.icon, count: 1 }); query.value = ''; results.replaceChildren(); draw(); } },
    thumb(item.icon, item.name || String(item.id), 'q1'), mono(String(item.id)), ' ', item.name || 'Item ' + item.id)) : [h('span', { class: 'hint' }, 'No item matches.')]));
  };
  query.addEventListener('input', () => { clearTimeout(timer); timer = setTimeout(() => search().catch(e => toast(e.message)), 250); });
  query.addEventListener('keydown', e => { if (e.key === 'Enter') e.preventDefault(); });
  const zeny = h('input', { class: 'input', type: 'number', min: '0', value: '', placeholder: '0', id: 'f-zeny', 'aria-label': 'Zeny' });
  draw();
  return {
    node: h('div', { class: 'reward-editor' },
      h('div', { class: 'field' }, h('span', { class: 'label' }, 'Items'), list, h('div', { class: 'reward-search' }, icon('search'), query), results),
      h('div', { class: 'field' }, h('label', { class: 'label', for: 'f-zeny' }, 'Zeny'), zeny)),
    value: () => [...rows.map(r => ({ item: r.item, count: r.count })), ...(Number(zeny.value) > 0 ? [{ item: 1, count: Number(zeny.value) }] : [])],
    reset: () => { rows.length = 0; zeny.value = ''; draw(); },
  };
}

/** A highlighted box asking to confirm an action that reaches many players; resolves true on Confirm. */
function confirmBox(host, message, confirmLabel) {
  return new Promise(resolve => {
    const done = answer => { box.remove(); resolve(answer); };
    const box = h('div', { class: 'confirm', role: 'alertdialog', 'aria-live': 'assertive' }, h('p', null, icon('alert'), message),
      h('div', { class: 'row' }, h('button', { type: 'button', class: 'btn danger sm', onclick: () => done(true) }, confirmLabel),
        h('button', { type: 'button', class: 'btn secondary sm', onclick: () => done(false) }, 'Cancel')));
    host.replaceChildren(box);
    box.querySelector('button').focus();
  });
}

/** The form's submit button state while an action runs outside form(). */
async function busy(button, work) {
  button.classList.add('busy');
  button.disabled = true;
  try { return await work(); } finally { button.classList.remove('busy'); button.disabled = false; }
}
