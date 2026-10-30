// A character's sheet (/characters/<id>): what the server keeps for it, read only. A player opens the characters of
// their own account, an admin every character, and the admin also its mailbox (/characters/<id>/mail).
'use strict';

/** An item the character holds, with its icon, count and flags. */
function heldName(item) {
  return [itemName(item.itemId, item.name, item.quality, false, item.icon),
    item.locked || item.bound ? h('span', { class: 'sub' }, [item.locked && 'Locked', item.bound && 'Bound'].filter(Boolean).join(' · ')) : null];
}

function equipmentTable(equipment) {
  return equipment.length
    ? table([
      { label: 'Slot', value: e => h('span', { class: 'muted' }, e.slot) },
      { label: 'Item', grow: true, value: e => [heldName(e.item),
        e.enchantments.length ? h('span', { class: 'sub' }, e.enchantments.map(s => `${s.name} ${s.value}`).join(' · ')) : null] },
      { label: 'Strengthen', num: true, value: e => e.strengthen > 0 ? '+' + e.strengthen : h('span', { class: 'muted' }, '-') },
      { label: 'Refine', num: true, opt: true, value: e => e.refine > 0 ? num(e.refine) : h('span', { class: 'muted' }, '-') },
    ], equipment, { short: true })
    : emptyState('shield', 'Nothing equipped', null);
}

function bagCards(bags) {
  if (!bags.length) return card('Bags', null, emptyState('box', 'The bags are empty', null));
  return bags.map(bag => card(bag.name, h('span', { class: 'muted' }, num(bag.items.length) + (bag.items.length === 1 ? ' stack' : ' stacks')),
    table([
      { label: 'Item', grow: true, value: heldName },
      { label: 'ID', num: true, opt: true, value: i => mono(String(i.itemId)) },
      { label: 'Count', num: true, value: i => num(i.count) },
    ], bag.items, { short: bag.items.length <= 12 })));
}

function skillTable(skills) {
  return skills.length
    ? table([
      { label: 'Skill', grow: true, value: s => h('span', { class: 'named plain' }, thumb(s.icon, s.name, ''), h('span', null, s.name)) },
      { label: 'Level', num: true, value: s => num(s.level) },
    ], skills, { short: skills.length <= 16 })
    : emptyState('sparkle', 'No skills learned', null);
}

async function characterPage(id) {
  const { sheet: c, admin } = await api('GET', '/api/characters/' + encodeURIComponent(id));
  const zeny = c.currencies.find(x => x.itemId === 1);
  const crumbs = admin
    ? [{ href: '/admin', label: 'Admin' }, { href: '/admin/accounts/' + encodeURIComponent(c.account), label: c.account }, { label: c.name }]
    : [{ href: '/', label: 'Home' }, { href: '/account', label: 'My account' }, { label: c.name }];
  return [pageHead({ crumbs, title: c.name, art: thumb(c.classIcon, c.class, 'cls', 'xl'),
    after: [c.online ? chip('online', 'Online') : chip('', 'Offline'), chip('plain', c.class)],
    subtitle: c.live ? 'As it is in the game now.' : 'As the server last saved it.',
    meta: [['ID', mono(String(c.id))], admin && ['Account', link('/admin/accounts/' + encodeURIComponent(c.account), { class: 'text-link' }, c.account)],
      ['Map', mapName(c.mapId, c.map, c.mapImage)], ['Position', mono(`${c.x}, ${c.y}, ${c.z}`)]].filter(Boolean),
    actions: admin ? link('/characters/' + c.id + '/mail', { class: 'btn secondary sm' }, icon('mail'), 'Mailbox') : null }),
  h('section', { class: 'kpis', 'aria-label': 'Key figures' },
    kpi('Base level', 'trophy', num(c.level), null, num(c.exp) + ' EXP into the level'),
    kpi('Job level', 'sparkle', num(c.jobLevel), null, num(c.jobExp) + ' job EXP into the level'),
    kpi('Astral level', 'star', num(c.paragonLevel), null, 'Paragon'),
    kpi('Zeny', 'coins', num(zeny?.amount ?? 0), null, c.currencies.length > 1 ? `${c.currencies.length - 1} other currencies` : 'no other currency')),
  h('div', { class: 'grid' },
    h('div', { class: 'span-8 stack' },
      card('Equipment', null, equipmentTable(c.equipment), 'Strengthen and refine levels belong to the slot, as in the game.'),
      bagCards(c.bags),
      card('Skills', null, skillTable(c.skills))),
    h('div', { class: 'span-4 stack' },
      card('Character', null, body(facts([['Class', c.class], ['Sex', c.sex], ['Created', stamp(c.created)], ['Last login', stamp(c.lastLogin)]]))),
      card('Stats', null, body(facts([...c.stats.map(s => [s.name, h('span', { class: 'num' }, s.value)]),
        ['Points to spend', h('span', { class: 'num' }, num(c.statPoints))]]))),
      card('Currencies', null, c.currencies.length
        ? body(facts(c.currencies.map(x => [h('span', { class: 'named plain' }, thumb(x.icon, x.name, 'q1'), h('span', null, x.name)), h('span', { class: 'num' }, num(x.amount))])))
        : emptyState('coins', 'No currency', null))))];
}

async function characterMailPage(id) {
  const query = queryOf();
  const page = Number(query.page) || 1;
  const box = await api('GET', `/api/admin/characters/${encodeURIComponent(id)}/mail?page=${page}`);
  const pages = Math.max(1, Math.ceil(box.total / box.pageSize));
  const letters = box.mails.length
    ? h('div', { class: 'stack' }, box.mails.map(m => card(null, null, body(
      h('div', { class: 'mail-head' }, h('b', null, m.title || '(no title)'),
        h('span', { class: 'm' }, m.read ? chip('', 'Read') : chip('info', 'Unread'), m.attachments.length ? (m.claimed ? chip('', 'Claimed') : chip('event', 'Not claimed')) : null)),
      h('p', { class: 'mail-meta' }, 'From ', h('b', null, m.sender), ' · ', stamp(m.sent), m.expires ? [' · expires ', stamp(m.expires)] : ' · never expires'),
      m.text && h('p', { class: 'desc' }, m.text),
      m.attachments.length ? h('ul', { class: 'mail-items' }, m.attachments.map(a => h('li', null, itemName(a.itemId, a.name, a.quality, false, a.icon), mono('×' + num(a.count))))) : null))))
    : emptyState('mail', box.readable ? 'The mailbox is empty' : 'The mailbox could not be read', box.readable ? null : 'The stored mailbox is not in a shape this server reads.');
  return adminLayout('accounts',
    pageHead({ crumbs: [{ href: '/admin', label: 'Admin' }, { href: '/characters/' + box.characterId, label: box.character }, { label: 'Mailbox' }],
      title: 'Mailbox of ' + box.character, subtitle: 'Read only, newest first. The server keeps a letter until the game removes it.' }),
    letters,
    pages > 1 && h('div', { class: 'card-foot plain' }, h('span', null, mono(num(box.total)), ' letters · page ', mono(String(page)), ' of ', mono(String(pages))),
      h('div', { class: 'actions' },
        page > 1 ? link(`/characters/${box.characterId}/mail?page=${page - 1}`, { class: 'btn secondary sm' }, icon('back'), 'Newer') : null,
        page < pages ? link(`/characters/${box.characterId}/mail?page=${page + 1}`, { class: 'btn secondary sm' }, 'Older', icon('arrow')) : null)));
}

routes.push([/^\/characters\/(\d+)$/, characterPage, 'account'], [/^\/characters\/(\d+)\/mail$/, characterMailPage, 'admin']);
titles.account = titles.account || 'Account';
