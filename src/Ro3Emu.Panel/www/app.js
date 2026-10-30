// RO3Emu web panel: a small client-side router over the panel's JSON API (/api/...). Every value from the server
// reaches the page through textContent or an attribute, never as HTML.
'use strict';

const state = { session: null };
const view = document.getElementById('view');

// ---------------------------------------------------------------- DOM helpers

function h(tag, attrs, ...children) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(attrs || {})) {
    if (value == null || value === false) continue;
    if (key === 'class') node.className = value;
    else if (key.startsWith('on')) node.addEventListener(key.slice(2), value);
    else node.setAttribute(key, value === true ? '' : value);
  }
  for (const child of children.flat(Infinity)) {
    if (child == null || child === false) continue;
    node.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return node;
}

const SVG = 'http://www.w3.org/2000/svg';
function icon(name) {
  const svg = document.createElementNS(SVG, 'svg');
  svg.setAttribute('class', 'i');
  svg.setAttribute('aria-hidden', 'true');
  const use = document.createElementNS(SVG, 'use');
  use.setAttribute('href', '#i-' + name);
  svg.append(use);
  return svg;
}

const link = (href, attrs, ...children) => h('a', { href, 'data-link': true, ...attrs }, ...children);
const mono = (text, attrs) => h('span', { class: 'mono', ...attrs }, text);

function render(...children) {
  view.replaceChildren(...children.flat(Infinity).filter(Boolean));
  view.focus({ preventScroll: true });
}

let toastTimer = 0;
function toast(text) {
  let node = document.querySelector('.toast');
  if (!node) { node = h('div', { class: 'toast', role: 'status' }); document.body.append(node); }
  node.textContent = text;
  node.classList.add('show');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => node.classList.remove('show'), 2600);
}

// ---------------------------------------------------------------- formatting

const numberFormat = new Intl.NumberFormat('en-US');
const num = n => numberFormat.format(n ?? 0);
const pad = n => String(n).padStart(2, '0');

/** A technical timestamp in local time, 2026-10-30 12:22; the full ISO time in its tooltip. */
function stamp(iso) {
  if (!iso) return h('span', { class: 'muted' }, '-');
  const d = new Date(iso);
  return mono(`${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}`, { title: d.toISOString() });
}

function day(iso) {
  const d = new Date(iso);
  return mono(`${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`, { title: d.toISOString() });
}

function duration(seconds) {
  const d = Math.floor(seconds / 86400), hr = Math.floor(seconds % 86400 / 3600), m = Math.floor(seconds % 3600 / 60);
  return d > 0 ? `${d}d ${hr}h` : hr > 0 ? `${hr}h ${pad(m)}m` : `${Math.max(m, 0)}m`;
}

function ago(iso) {
  if (!iso) return 'never';
  const s = (Date.now() - new Date(iso).getTime()) / 1000;
  if (s < 90) return 'just now';
  if (s < 3600) return `${Math.round(s / 60)} min ago`;
  if (s < 86400) return `${Math.round(s / 3600)} h ago`;
  return `${Math.round(s / 86400)} d ago`;
}

const megabytes = n => Math.round(n / (1 << 20));

// ---------------------------------------------------------------- components

/** A status chip: online, active, banned, info, event, admin, or plain grey. */
const chip = (kind, text) => h('span', { class: 'chip ' + kind }, text);

function card(title, actions, body, foot) {
  return h('section', { class: 'card' },
    title || actions ? h('div', { class: 'card-head' }, title && h('h2', null, title), actions && h('div', { class: 'actions' }, actions)) : null,
    body, foot && h('div', { class: 'card-foot' }, foot));
}

const body = (...children) => h('div', { class: 'card-body' }, children);

function emptyState(iconName, title, text) {
  return h('div', { class: 'empty' }, icon(iconName), h('b', null, title), text && h('p', null, text));
}

/** Key figure: label, value with its unit (one face and size for every value), one line of detail. */
function kpi(label, iconName, value, unit, detail, opts = {}) {
  return h('div', { class: 'card kpi' + (opts.warn ? ' warn' : '') },
    h('span', { class: 'label' }, icon(iconName), label),
    h('div', { class: 'value' }, h('b', { title: String(value) }, value), unit && h('small', null, unit)),
    h('p', { class: 'detail' }, detail ?? ' '));
}

/**
 * A data table: columns of { label, value(row) -> node, num (right-aligned numbers), opt (hidden on phones),
 * grow }. Numbers right, text left; the header sticks while the rows scroll.
 */
function table(columns, rows, opts = {}) {
  return h('div', { class: 'table-wrap' + (opts.short ? ' short' : '') }, h('table', { class: 'data' },
    h('thead', null, h('tr', null, columns.map(c => h('th', { class: cls(c), scope: 'col' }, c.label)))),
    h('tbody', null, rows.map(row => h('tr', opts.onrow ? { class: 'clickable', onclick: e => { if (!e.target.closest('a')) opts.onrow(row); } } : null,
      columns.map(c => h('td', { class: cls(c) }, c.value(row))))))));
}
const cls = c => [c.num && 'num', c.opt && 'opt', c.grow && 'grow', c.wrap && 'wrap-text'].filter(Boolean).join(' ') || null;

/** Compact or comfortable rows, remembered in this browser. */
function densityToggle() {
  const set = value => {
    document.body.dataset.density = value;
    try { localStorage.setItem('ro3p-density', value); } catch { /* private mode: not remembered */ }
    for (const b of group.querySelectorAll('button')) b.setAttribute('aria-pressed', String(b.dataset.value === value));
  };
  const current = document.body.dataset.density || 'compact';
  const group = h('div', { class: 'seg', role: 'group', 'aria-label': 'Row density' },
    [['compact', 'Compact'], ['comfortable', 'Comfortable']].map(([value, label]) =>
      h('button', { type: 'button', 'data-value': value, 'aria-pressed': String(value === current), onclick: () => set(value) }, label)));
  return group;
}
try { document.body.dataset.density = localStorage.getItem('ro3p-density') || 'compact'; } catch { document.body.dataset.density = 'compact'; }

/** Breadcrumb, page title (after a picture, art) and the actions row: the top of every page. */
function pageHead({ crumbs, title, big, art, after, subtitle, meta, actions }) {
  return h('header', { class: 'page-head' },
    h('div', { class: 'titles' },
      crumbs && h('nav', { class: 'crumbs', 'aria-label': 'Breadcrumb' }, crumbs.map((c, i) => [
        i > 0 && h('span', { class: 'sep', 'aria-hidden': 'true' }, '/'),
        c.href ? link(c.href, null, c.label) : h('span', { 'aria-current': 'page' }, c.label)])),
      h('div', { class: 'title-row' }, art, h('h1', { class: 'title' + (big ? ' big' : '') }, title), after),
      subtitle && h('p', { class: 'subtitle' }, subtitle),
      meta && h('dl', { class: 'meta' }, meta.map(([k, v]) => h('div', null, h('dt', null, k), h('dd', null, v))))),
    actions && h('div', { class: 'actions' }, actions));
}

function pager(result, href) {
  const pages = Math.max(1, Math.ceil(result.total / result.pageSize));
  return [h('span', null, mono(`${num(result.total)}`), result.total === 1 ? ' row' : ' rows', pages > 1 ? [' · page ', mono(String(result.page)), ' of ', mono(String(pages))] : null),
    pages > 1 && h('div', { class: 'actions' },
      result.page > 1 ? link(href(result.page - 1), { class: 'btn secondary sm' }, icon('back'), 'Newer') : null,
      result.page < pages ? link(href(result.page + 1), { class: 'btn secondary sm' }, 'Older', icon('arrow')) : null)];
}

function facts(pairs) {
  return h('dl', { class: 'facts' }, pairs.map(([k, v]) => [h('dt', null, k), h('dd', null, v)]));
}

// ---------------------------------------------------------------- API

class ApiError extends Error {
  constructor(message, status) { super(message); this.status = status; }
}

async function api(method, path, payload, retried) {
  const options = { method, headers: { Accept: 'application/json' }, credentials: 'same-origin' };
  if (method === 'POST') {
    options.headers['Content-Type'] = 'application/json';
    options.headers['X-CSRF-Token'] = state.session?.csrf ?? '';
    options.body = JSON.stringify(payload ?? {});
  }
  const response = await fetch(path, options);
  const data = await response.json().catch(() => ({}));
  // An expired token (a restart, a login elsewhere): take a fresh one and send once more.
  if (response.status === 403 && method === 'POST' && !retried) {
    await loadSession();
    return api(method, path, payload, true);
  }
  if (!response.ok) throw new ApiError(data.error || `The server answered ${response.status}.`, response.status);
  return data;
}

async function loadSession() {
  state.session = await api('GET', '/api/session');
  drawChrome();
  return state.session;
}

function adopt(session) {
  state.session = session;
  drawChrome();
}

// ---------------------------------------------------------------- chrome

function authControls(menu) {
  const s = state.session;
  if (s?.account)
    return [link('/account', { class: menu ? 'btn secondary' : 'me', title: 'Your account' },
      menu ? icon('user') : h('i', { class: 'avatar' }, icon('user')), h('span', null, s.account)),
    h('button', { type: 'button', class: menu ? 'btn secondary' : 'icon-btn', title: 'Log out', 'aria-label': 'Log out', onclick: logout },
      icon('out'), menu ? 'Log out' : null)];
  return [link('/login', { class: 'btn secondary sm' }, 'Log in'),
    s?.registration ? link('/register', { class: 'btn accent sm' }, 'Sign up') : null];
}

/** The server's branding (Ro3Emu:Panel:Branding): name, logo, footer and accent colour, each only when set. */
function drawBrand(server) {
  const name = server?.name || 'RO3Emu';
  document.getElementById('brand-name').textContent = name;
  document.getElementById('foot-name').textContent = name;
  const logo = document.getElementById('brand-logo');
  if (server?.logo && logo.getAttribute('src') !== server.logo) logo.src = server.logo;
  logo.hidden = !server?.logo;
  document.getElementById('brand-star').style.display = server?.logo ? 'none' : '';
  if (server?.footer) document.getElementById('foot-text').textContent = server.footer;
  // CSSOM, not a style attribute: the page's policy allows no inline style.
  if (server?.accent) for (const token of ['--blue', '--blue-ink']) document.documentElement.style.setProperty(token, server.accent);
}

function drawChrome() {
  const s = state.session;
  drawBrand(s?.server);
  document.getElementById('nav-admin').hidden = !s?.admin;
  document.getElementById('auth').replaceChildren(...authControls(false).filter(Boolean));
  document.getElementById('menu-auth').replaceChildren(...authControls(true).filter(Boolean));
}

async function logout() {
  try { await api('POST', '/api/logout'); } catch { /* the session is gone either way */ }
  await loadSession();
  navigate('/');
  toast('You are logged out.');
}

function markNav(section) {
  for (const a of document.querySelectorAll('[data-nav]'))
    if (a.dataset.nav === section) a.setAttribute('aria-current', 'page'); else a.removeAttribute('aria-current');
}

const menuButton = document.getElementById('menu');
const nav = document.getElementById('nav');
function setMenu(open) {
  nav.classList.toggle('open', open);
  menuButton.setAttribute('aria-expanded', String(open));
}
menuButton.addEventListener('click', () => setMenu(!nav.classList.contains('open')));
document.addEventListener('keydown', e => { if (e.key === 'Escape' && nav.classList.contains('open')) { setMenu(false); menuButton.focus(); } });

// ---------------------------------------------------------------- router

const routes = [
  [/^\/$/, homePage, 'home'],
  [/^\/online$/, onlinePage, 'online'],
  [/^\/rankings$/, rankingsPage, 'rankings'],
  [/^\/news$/, newsPage, 'news'],
  [/^\/login$/, loginPage, null],
  [/^\/register$/, registerPage, null],
  [/^\/account$/, accountPage, null],
  [/^\/admin$/, adminDashboard, 'admin'],
  [/^\/admin\/accounts$/, adminAccounts, 'admin'],
  [/^\/admin\/accounts\/([^/]+)$/, adminAccount, 'admin'],
  [/^\/admin\/audit$/, adminAudit, 'admin'],
];
const titles = { online: "Who's online", rankings: 'Rankings', news: 'News', admin: 'Admin' };

let routeToken = 0;
async function route() {
  setMenu(false);
  const path = location.pathname.replace(/\/+$/, '') || '/';
  const match = routes.map(([re, page, section]) => [path.match(re), page, section]).find(([m]) => m);
  const token = ++routeToken;
  if (!match) { markNav(null); render(notFound()); return; }
  const [m, page, section] = match;
  markNav(section);
  render(h('div', { class: 'loading', 'aria-live': 'polite' }, h('i', { class: 'spin', 'aria-label': 'Loading' })));
  try {
    const content = await page(...m.slice(1).map(decodeURIComponent));
    if (token === routeToken && content) render(content);
  } catch (error) {
    if (token !== routeToken) return;
    if (error.status === 401) { navigate('/login?next=' + encodeURIComponent(location.pathname + location.search), true); return; }
    render(failure(error));
  }
  document.title = (state.session?.server?.name || 'RO3Emu') + (section && section !== 'home' ? ' · ' + titles[section] : '');
}

function navigate(to, replace) {
  if (replace) history.replaceState(null, '', to); else history.pushState(null, '', to);
  scrollTo({ top: 0 });
  route();
}

document.addEventListener('click', event => {
  const a = event.target.closest('a[data-link]');
  if (!a || event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
  event.preventDefault();
  navigate(a.getAttribute('href'));
});
addEventListener('popstate', route);

function notFound() {
  return [pageHead({ title: 'Page not found' }),
    card(null, null, emptyState('search', 'This page does not exist', 'The link may be old.'),
      link('/', { class: 'btn secondary sm' }, icon('back'), 'Back home'))];
}

function failure(error) {
  return [pageHead({ title: error.status === 403 ? 'Not allowed' : 'Something went wrong' }),
    card(null, null, emptyState('alert', error.message, null))];
}

// ---------------------------------------------------------------- shared tables

/** A character's class with its emblem (pages/database.js thumb: a letter when the server has no picture). */
const classCol = { label: 'Class', opt: true, value: r => r.class ? h('span', { class: 'named plain' }, thumb(r.classIcon, r.class, 'cls'), h('span', null, r.class)) : '' };
const levelCol = { label: 'Level', num: true, value: r => num(r.level) };

function onlineTable(players, admin, limit) {
  const rows = limit ? players.slice(0, limit) : players;
  return table([
    { label: 'Name', grow: true, value: p => h('span', { class: 'strong' }, p.name) },
    classCol, levelCol,
    { label: 'Map', opt: !limit, value: p => p.mapId ? link('/maps/' + p.mapId, { class: 'text-link' }, p.map) : p.map },
    admin && !limit && { label: 'Account', opt: true, value: p => link('/admin/accounts/' + encodeURIComponent(p.account), { class: 'text-link mono' }, p.account) },
  ].filter(Boolean), rows, { short: !!limit });
}

function boardTable(board, limit) {
  const info = board.info;
  const rows = limit ? board.rows.slice(0, limit) : board.rows;
  return table([
    { label: '#', value: r => h('span', { class: 'rank' + (r.rank <= 3 ? ' r' + r.rank : '') }, r.rank) },
    { label: info.guild ? 'Guild' : 'Name', grow: true, value: r => h('span', { class: 'strong' }, r.name) },
    !info.guild && classCol,
    { label: info.guild ? 'Guild level' : 'Level', num: true, value: r => num(r.level) },
    { label: info.score, num: true, value: r => num(r.score) },
  ].filter(Boolean), rows, { short: !!limit });
}

// ---------------------------------------------------------------- home

async function homePage() {
  const [status, news, online, level] = await Promise.all([api('GET', '/api/status'), api('GET', '/api/news?limit=5'),
    api('GET', '/api/online'), api('GET', '/api/rankings/level')]);
  const s = state.session;
  const mine = s?.account ? await api('GET', '/api/account').catch(() => null) : null;
  const head = pageHead({
    title: status.name, big: true, after: chip('online', 'Online'), subtitle: status.tagline,
    meta: [['Area', status.area], ['Version', mono(status.version || '-')], ['Started', stamp(status.startedUtc)]],
  });
  const kpis = h('section', { class: 'kpis', 'aria-label': 'Server figures' },
    kpi('Online now', 'users', num(status.players), status.players === 1 ? 'player' : 'players', 'characters in the world'),
    kpi('Uptime', 'clock', duration(status.uptimeSeconds), null, ['since ', stamp(status.startedUtc)]),
    kpi('Accounts', 'user', num(status.accounts), null, 'registered on this realm'),
    kpi('Characters', 'trophy', num(status.characters), null, 'created in the game'));
  const newsCard = card('Latest news', link('/news', { class: 'more' }, 'All news', icon('arrow')),
    news.news.length
      ? h('ul', { class: 'rows' }, news.news.map(n => h('li', null, h('span', { class: 't' }, n.title), day(n.published),
        h('span', { class: 'm' }, n.pinned && chip('info', 'Pinned'), chip(n.tag, n.tag), n.author && ['by ', n.author]))))
      : emptyState('book', 'No news yet', 'Announcements from the team will show up here.'));
  const onlineCard = card("Who's online", link('/online', { class: 'more' }, 'All', icon('arrow')),
    online.players.length ? onlineTable(online.players, false, 6) : emptyState('users', 'Nobody is in the world', null),
    online.players.length > 6 ? `${num(online.players.length - 6)} more in the world` : null);
  const levelCard = card('Top 5 by level', link('/rankings', { class: 'more' }, 'Rankings', icon('arrow')),
    level.rows.length ? boardTable(level, 5) : emptyState('trophy', 'Nobody is ranked yet', null));
  const meCard = card('Your characters', mine && link('/account', { class: 'more' }, 'Account', icon('arrow')),
    mine
      ? (mine.characters.length
        ? table([{ label: 'Name', grow: true, value: c => h('span', { class: 'strong' }, c.name) }, levelCol,
          { label: 'Status', value: c => c.online ? chip('online', 'Online') : chip('', 'Offline') }], mine.characters.slice(0, 5), { short: true })
        : emptyState('user', 'No characters yet', 'Create one in the game.'))
      : body(h('p', { class: 'hint' }, 'Log in with the account name and password you play with to see your characters and change your password.'),
        h('div', { class: 'row' }, link('/login', { class: 'btn primary sm' }, 'Log in'),
          s?.registration ? link('/register', { class: 'btn secondary sm' }, 'Create an account') : null)));
  return [head, kpis,
    h('div', { class: 'grid' }, h('div', { class: 'span-7' }, newsCard), h('div', { class: 'span-5' }, onlineCard)),
    h('div', { class: 'grid' }, h('div', { class: 'span-7' }, levelCard), h('div', { class: 'span-5' }, meCard))];
}

// ---------------------------------------------------------------- public pages

async function onlinePage() {
  const { players } = await api('GET', '/api/online');
  return [pageHead({ crumbs: [{ href: '/', label: 'Home' }, { label: "Who's online" }], title: "Who's online",
    subtitle: `${num(players.length)} ${players.length === 1 ? 'character is' : 'characters are'} in the world. Refreshed every 5 seconds at most.`,
    actions: [densityToggle(), h('button', { type: 'button', class: 'btn secondary sm', onclick: route }, 'Refresh')] }),
  card(null, null, players.length ? onlineTable(players, state.session?.admin) : emptyState('users', 'Nobody is in the world right now', 'Characters show up here while they play.'))];
}

async function rankingsPage() {
  const params = new URLSearchParams(location.search);
  const { boards, classes } = await api('GET', '/api/rankings');
  const current = boards.find(b => b.id === params.get('board')) || boards[0];
  const cls = current.byClass ? params.get('class') || '' : '';
  const board = await api('GET', `/api/rankings/${encodeURIComponent(current.id)}${cls ? '?class=' + encodeURIComponent(cls) : ''}`);
  const href = (id, c) => '/rankings?board=' + encodeURIComponent(id) + (c ? '&class=' + encodeURIComponent(c) : '');
  const tabs = h('div', { class: 'seg', role: 'tablist', 'aria-label': 'Ranking' }, boards.map(b =>
    link(href(b.id, b.byClass ? cls : ''), { role: 'tab', 'aria-selected': String(b.id === current.id) }, b.title)));
  const filter = current.byClass && classes.length
    ? h('select', { class: 'input', 'aria-label': 'Class', onchange: e => navigate(href(current.id, e.target.value)) },
      h('option', { value: '' }, 'Every class'), classes.map(c => h('option', { value: String(c.id), selected: String(c.id) === cls }, c.name)))
    : null;
  return [pageHead({ crumbs: [{ href: '/', label: 'Home' }, { label: 'Rankings' }], title: 'Rankings',
    subtitle: 'From the realm itself: the level board every 30 seconds, the others as the game refreshes them.', actions: densityToggle() }),
  h('section', { class: 'card' },
    h('div', { class: 'card-head' }, h('div', { class: 'toolbar' }, tabs, filter && h('div', { class: 'end' }, filter))),
    board.rows.length ? boardTable(board) : emptyState('trophy', 'Nobody is ranked here yet', 'The board fills as characters play.'),
    h('div', { class: 'card-foot' }, h('span', null, mono(num(board.rows.length)), ' ranked · top 100 shown'), h('span', null, current.score)))];
}

async function newsPage() {
  const { news } = await api('GET', '/api/news?limit=50');
  return [pageHead({ crumbs: [{ href: '/', label: 'Home' }, { label: 'News' }], title: 'News' }),
    news.length
      ? h('div', { class: 'stack' }, news.map(n => h('article', { class: 'card article' }, body(
        h('div', { class: 'm' }, n.pinned && chip('info', 'Pinned'), chip(n.tag, n.tag), stamp(n.published), n.author && ['by ', n.author]),
        h('h2', null, n.title), h('p', { class: 'text' }, n.body)))))
      : card(null, null, emptyState('book', 'No news yet', 'Announcements from the team will show up here.'))];
}

// ---------------------------------------------------------------- forms

function form(fields, submitLabel, onsubmit, look = 'primary') {
  const error = h('p', { class: 'form-error', role: 'alert', hidden: true });
  const button = h('button', { type: 'submit', class: 'btn block ' + look }, h('i', { class: 'spin' }), submitLabel);
  const node = h('form', { novalidate: true }, fields, error, button);
  node.addEventListener('submit', async event => {
    event.preventDefault();
    error.hidden = true;
    button.classList.add('busy');
    try { await onsubmit(Object.fromEntries(new FormData(node)), node); }
    catch (e) { error.textContent = e.message; error.hidden = false; }
    finally { button.classList.remove('busy'); }
  });
  return node;
}

function field(label, name, type, extra) {
  const id = 'f-' + name;
  return h('div', { class: 'field' }, h('label', { class: 'label', for: id }, label),
    h('input', { class: 'input', id, name, type, required: true, spellcheck: 'false', autocapitalize: 'off', ...extra }));
}

function nextPath() {
  const next = new URLSearchParams(location.search).get('next') || '/account';
  return next.startsWith('/') && !next.startsWith('//') ? next : '/account';
}

function authCard(title, lead, content, foot) {
  return h('section', { class: 'card auth-card' }, body(h('h1', { class: 'title' }, title), h('p', { class: 'hint' }, lead), content,
    foot && h('p', { class: 'foot' }, foot)));
}

async function loginPage() {
  if (state.session?.account) { navigate(nextPath(), true); return null; }
  return authCard('Log in', 'Use the account name and password you play with.', form([
    field('Account name', 'account', 'text', { autocomplete: 'username', maxlength: '64' }),
    field('Password', 'password', 'password', { autocomplete: 'current-password' })],
  'Log in', async values => {
    adopt(await api('POST', '/api/login', { account: values.account, password: values.password }));
    navigate(nextPath(), true);
    toast('Welcome back, ' + state.session.account + '.');
  }), state.session?.registration && ['New here? ', link('/register', null, 'Create an account')]);
}

async function registerPage() {
  if (state.session?.account) { navigate('/account', true); return null; }
  if (!state.session?.registration)
    return authCard('Sign-up is closed', 'On this server your first login in the game creates your account.', null, null);
  return authCard('Create an account', 'The account you log in to the game with. Characters are created in the game.', form([
    field('Account name', 'account', 'text', { autocomplete: 'username', maxlength: '64' }),
    field('Password', 'password', 'password', { autocomplete: 'new-password', minlength: '6', maxlength: '128' }),
    field('Password again', 'again', 'password', { autocomplete: 'new-password' })],
  'Create account', async values => {
    if (values.password !== values.again) throw new Error('The two passwords are not the same.');
    adopt(await api('POST', '/api/register', { account: values.account, password: values.password }));
    navigate('/account', true);
    toast('Your account is ready. Log in to the game with it.');
  }, 'accent'), ['Already have one? ', link('/login', null, 'Log in')]);
}

// ---------------------------------------------------------------- my account

function characterTable(characters, withId) {
  return table([
    withId && { label: 'ID', opt: true, value: c => mono(String(c.id)) },
    { label: 'Name', grow: true, value: c => link('/characters/' + c.id, { class: 'row-link' }, c.name) },
    classCol, levelCol,
    { label: 'Job', num: true, opt: true, value: c => num(c.jobLevel) },
    { label: 'Map', opt: true, value: c => c.map },
    { label: 'Guild', opt: true, value: c => c.guild || h('span', { class: 'muted' }, '-') },
    { label: 'Status', value: c => c.online ? chip('online', 'Online') : chip('', 'Offline') },
    { label: 'Last played', opt: true, value: c => stamp(c.lastLogin) },
  ].filter(Boolean), characters, { onrow: c => navigate('/characters/' + c.id) });
}

async function accountPage() {
  const account = await api('GET', '/api/account');
  const password = form([
    field('Current password', 'current', 'password', { autocomplete: 'current-password' }),
    field('New password', 'next', 'password', { autocomplete: 'new-password', minlength: '6', maxlength: '128' }),
    field('New password again', 'again', 'password', { autocomplete: 'new-password' })],
  'Change password', async (values, node) => {
    if (values.next !== values.again) throw new Error('The two new passwords are not the same.');
    await api('POST', '/api/account/password', { current: values.current, next: values.next });
    node.reset();
    toast('Password changed. Use the new one in the game too.');
  });
  return [pageHead({ crumbs: [{ href: '/', label: 'Home' }, { label: 'Account' }], title: account.name,
    after: account.admin ? chip('admin', 'Admin') : null,
    actions: [densityToggle(), account.admin && link('/admin', { class: 'btn secondary sm' }, icon('shield'), 'Admin area')] }),
  h('div', { class: 'grid' },
    h('div', { class: 'span-8' }, card('Characters', null,
      account.characters.length ? characterTable(account.characters) : emptyState('user', 'No characters yet', 'Create one in the game; it shows up here.'))),
    h('div', { class: 'span-4 stack' },
      card('Account', null, body(facts([['Name', mono(account.name)], ['Created', stamp(account.created)], ['Last played', stamp(account.lastLogin)],
        ['Characters', h('span', { class: 'num' }, num(account.characters.length))]]))),
      card('Change password', null, body(h('p', { class: 'hint' }, 'Other browsers signed in to this panel are signed out when you change it.'), password))))];
}

// ---------------------------------------------------------------- admin

const adminSections = [
  ['dashboard', '/admin', 'Dashboard', 'pulse'],
  ['accounts', '/admin/accounts', 'Accounts', 'users'],
  ['audit', '/admin/audit', 'Audit log', 'list'],
];
const laterSections = [['items', 'Items & monsters', 'box'], ['mail', 'Mail & rewards', 'mail'], ['gift-codes', 'Gift codes', 'gift'],
  ['gm', 'GM console', 'terminal'], ['economy', 'Economy log', 'coins'], ['news', 'News editor', 'pen'], ['broadcasts', 'Broadcasts', 'bell']];

/**
 * An admin page from its own file (www/pages/*.js, loaded after this one): a sidebar entry, which replaces the
 * "Coming later" entry of the same id, and its route. Call it while the scripts load, before the first route.
 */
function registerAdminPage({ id, label, icon: iconName, path, pattern, page }) {
  adminSections.push([id, path, label, iconName]);
  const later = laterSections.findIndex(([laterId]) => laterId === id);
  if (later >= 0) laterSections.splice(later, 1);
  routes.push([pattern, page, 'admin']);
}

function adminLayout(current, ...content) {
  const side = h('nav', { class: 'card side', 'aria-label': 'Admin' },
    h('span', { class: 'label' }, 'Admin'),
    adminSections.map(([id, href, label, name]) => link(href, { 'aria-current': id === current ? 'page' : null }, icon(name), label)),
    laterSections.length > 0 && [h('hr'), h('span', { class: 'label' }, 'Coming later')],
    laterSections.map(([, label, name]) => h('span', { class: 'later', 'aria-disabled': 'true' }, icon(name), label)));
  return h('div', { class: 'admin' }, side, h('div', { class: 'admin-main' }, content));
}

const adminCrumbs = (...rest) => [{ href: '/', label: 'Home' }, { href: '/admin', label: 'Admin' }, ...rest];

function requireAdmin() {
  if (!state.session?.account) throw new ApiError('Please log in.', 401);
  if (!state.session.admin) throw new ApiError('Only an admin can open this page.', 403);
}

async function adminDashboard() {
  requireAdmin();
  const [d, status] = await Promise.all([api('GET', '/api/admin/dashboard'), api('GET', '/api/status')]);
  const most = Math.max(1, ...d.maps.map(m => m.players));
  const maps = d.maps.length
    ? table([
      { label: 'Map', grow: true, value: m => m.map },
      { label: 'Players', num: true, value: m => num(m.players) },
      { label: 'Share', opt: true, value: m => { const bar = h('i'); bar.style.width = Math.round(m.players / most * 100) + '%'; return h('span', { class: 'share' }, bar); } },
    ], d.maps, { short: true })
    : emptyState('users', 'Nobody is in the world', null);
  return adminLayout('dashboard',
    pageHead({ crumbs: adminCrumbs({ label: 'Dashboard' }), title: 'Dashboard', subtitle: 'The server\'s own counters since it started or since the last metrics reset.',
      actions: h('button', { type: 'button', class: 'btn secondary sm', onclick: route }, 'Refresh') }),
    h('section', { class: 'kpis', 'aria-label': 'Players' },
      kpi('In the world', 'users', num(d.inWorld), d.inWorld === 1 ? 'player' : 'players', `${num(d.loading)} loading`),
      kpi('Character screen', 'user', num(d.login), null, 'signed in, no character yet'),
      kpi('Accounts', 'shield', num(d.accounts), null, `${num(d.characters)} characters`),
      kpi('Connections', 'pulse', num(d.connections), null, 'micro and game gates')),
    h('section', { class: 'kpis', 'aria-label': 'Server' },
      kpi('Tick p99', 'clock', d.tickP99Ms.toFixed(1), 'ms', `p50 ${d.tickP50Ms.toFixed(1)} ms · max ${d.tickMaxMs.toFixed(0)} ms`, { warn: d.tickP99Ms > 50 }),
      kpi('Ticks', 'pulse', d.ticksPerSecond.toFixed(1), '/ s', `${num(d.overruns)} overruns · queue ${num(d.queueLength)}`),
      kpi('Memory', 'box', num(megabytes(d.workingSetBytes)), 'MB', `${d.cpuSeconds.toFixed(0)} s CPU · ${num(d.gen2Collections)} gen 2 GCs`),
      kpi('Errors', 'alert', num(d.exceptions + d.errorLogs), null, `${num(d.exceptions)} exceptions · ${num(d.errorLogs)} error logs`, { warn: d.exceptions + d.errorLogs > 0 })),
    h('div', { class: 'grid' },
      h('div', { class: 'span-7' }, card('Busiest maps', null, maps)),
      h('div', { class: 'span-5' }, card('Server', null, body(facts([
        ['Game loop', chip(d.phase === 'Accepting' ? 'online' : 'banned', d.phase)],
        ['Database commits', h('span', { class: 'num' }, num(d.dbCommits))],
        ['Version', mono(status.version || '-')], ['Started', stamp(status.startedUtc)],
        ['Uptime', mono(duration(status.uptimeSeconds))]]))))));
}

async function adminAccounts() {
  requireAdmin();
  const params = new URLSearchParams(location.search);
  const q = params.get('q') || '', page = Number(params.get('page')) || 1;
  const result = await api('GET', `/api/admin/accounts?q=${encodeURIComponent(q)}&page=${page}`);
  const search = h('form', { class: 'row', role: 'search' },
    h('span', { class: 'search' }, icon('search'), h('input', { class: 'input', name: 'q', value: q, placeholder: 'Account name', 'aria-label': 'Account name', spellcheck: 'false', autocapitalize: 'off' })),
    h('button', { type: 'submit', class: 'btn primary sm' }, 'Search'));
  search.addEventListener('submit', e => { e.preventDefault(); navigate('/admin/accounts?q=' + encodeURIComponent(new FormData(search).get('q').trim())); });
  const open = a => navigate('/admin/accounts/' + encodeURIComponent(a.name));
  const rows = result.accounts.length
    ? table([
      { label: 'Account', grow: true, value: a => link('/admin/accounts/' + encodeURIComponent(a.name), { class: 'row-link mono' }, a.name) },
      { label: 'Created', opt: true, value: a => stamp(a.created) },
      { label: 'Characters', num: true, value: a => num(a.characters) },
      { label: 'Last played', opt: true, value: a => stamp(a.lastLogin) },
      { label: 'Status', value: a => a.banned ? chip('banned', 'Banned') : chip('active', 'Active') },
    ], result.accounts, { onrow: open })
    : emptyState('search', q ? 'No account matches' : 'No accounts yet', q ? 'Try part of the name.' : null);
  return adminLayout('accounts',
    pageHead({ crumbs: adminCrumbs({ label: 'Accounts' }), title: 'Accounts', actions: densityToggle() }),
    h('section', { class: 'card' }, h('div', { class: 'card-head' }, search), rows,
      h('div', { class: 'card-foot' }, pager(result, p => '/admin/accounts?q=' + encodeURIComponent(q) + '&page=' + p))));
}

async function adminAccount(name) {
  requireAdmin();
  const { account, admin } = await api('GET', '/api/admin/accounts/' + encodeURIComponent(name));
  const self = account.name === state.session.account;
  const status = account.ban ? chip('banned', 'Banned') : chip('active', 'Active');
  return adminLayout('accounts',
    pageHead({ crumbs: adminCrumbs({ href: '/admin/accounts', label: 'Accounts' }, { label: account.name }), title: account.name,
      after: [status, admin && chip('admin', 'Admin')], actions: densityToggle() }),
    h('div', { class: 'grid' },
      h('div', { class: 'span-8' }, card('Characters', null,
        account.characters.length ? characterTable(account.characters, true) : emptyState('user', 'No characters', null))),
      h('div', { class: 'span-4 stack' },
        card('Account', null, body(facts([['Name', mono(account.name)], ['Created', stamp(account.created)], ['Last played', stamp(account.lastLogin)],
          ['Role', admin ? 'Admin (GM list)' : 'Player']]))),
        card(account.ban ? 'Ban' : 'Ban this account', null,
          body(self ? h('p', { class: 'hint' }, 'This is your own account.') : account.ban ? unbanForm(account) : banForm(account))))));
}

function banForm(account) {
  const durations = [['1', '1 hour'], ['24', '1 day'], ['72', '3 days'], ['168', '7 days'], ['720', '30 days'], ['', 'No end']];
  return [h('p', { class: 'hint' }, 'The player is disconnected at once and cannot log in to the game or this panel until the ban ends. Recorded in the audit log.'),
    form([
      h('div', { class: 'field' }, h('label', { class: 'label', for: 'f-reason' }, 'Reason'),
        h('textarea', { class: 'input', id: 'f-reason', name: 'reason', required: true, maxlength: '300' })),
      h('div', { class: 'field' }, h('label', { class: 'label', for: 'f-hours' }, 'Duration'),
        h('select', { class: 'input', id: 'f-hours', name: 'hours' }, durations.map(([value, label]) => h('option', { value, selected: value === '168' }, label))))],
    'Ban account', async values => {
      const result = await api('POST', `/api/admin/accounts/${encodeURIComponent(account.name)}/ban`,
        { reason: values.reason, hours: values.hours ? Number(values.hours) : null });
      toast(result.kicked ? `Banned. ${result.kicked} game session closed.` : 'Banned.');
      route();
    }, 'danger')];
}

function unbanForm(account) {
  const ban = account.ban;
  return [facts([['Until', ban.until ? stamp(ban.until) : 'No end'], ['Since', stamp(ban.banned)], ['By', ban.by ? mono(ban.by) : '-']]),
    h('div', { class: 'field' }, h('span', { class: 'label' }, 'Reason'), h('p', null, ban.reason || 'No reason given.')),
    form([h('div', { class: 'field' }, h('label', { class: 'label', for: 'f-note' }, 'Note (optional)'),
      h('textarea', { class: 'input', id: 'f-note', name: 'reason', maxlength: '300' }))],
    'Lift the ban', async values => {
      await api('POST', `/api/admin/accounts/${encodeURIComponent(account.name)}/unban`, { reason: values.reason });
      toast('The ban is lifted.');
      route();
    }, 'secondary')];
}

async function adminAudit() {
  requireAdmin();
  const page = Number(new URLSearchParams(location.search).get('page')) || 1;
  const result = await api('GET', '/api/admin/audit?page=' + page);
  const rows = result.entries.length
    ? table([
      { label: 'Time', value: e => stamp(e.utc) },
      { label: 'Admin', opt: true, value: e => mono(e.admin) },
      { label: 'Action', value: e => chip(e.action, e.action) },
      { label: 'Account', value: e => link('/admin/accounts/' + encodeURIComponent(e.target), { class: 'text-link mono' }, e.target) },
      { label: 'Detail', opt: true, wrap: true, grow: true, value: e => e.detail },
    ], result.entries)
    : emptyState('list', 'Nothing recorded yet', 'Every admin action is listed here: who, what and when.');
  return adminLayout('audit',
    pageHead({ crumbs: adminCrumbs({ label: 'Audit log' }), title: 'Audit log', subtitle: 'Every change made in the admin area, newest first.', actions: densityToggle() }),
    h('section', { class: 'card' }, rows, h('div', { class: 'card-foot' }, pager(result, p => '/admin/audit?page=' + p))));
}

// ---------------------------------------------------------------- start

// The pages in their own files (pages/*.js, deferred after this one) add their routes, titles and admin sections
// first: the first route waits for them.
addEventListener('DOMContentLoaded', async () => {
  try { await loadSession(); } catch { /* the pages show the error */ }
  await route();
});
