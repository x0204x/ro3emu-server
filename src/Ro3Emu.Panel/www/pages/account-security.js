// Account security (/account/security): two-factor authentication with an authenticator app and recovery codes, the
// browsers signed in to this panel with "log out everywhere", and the account's latest logins. Also the code step of
// a login with 2FA (twoFactorStep, called by app.js's login page) and the notice for staff who must turn 2FA on.
'use strict';

routes.push([/^\/account\/security$/, securityPage, null]);

/** A 6-digit code field (or a recovery code): one-time-code autofill on phones. */
function codeField(label, name = 'code') {
  return field(label, name, 'text', { autocomplete: 'one-time-code', inputmode: 'text', maxlength: '24', class: 'input code-input', placeholder: '123 456' });
}

/** The login's second step: the code from the app or a recovery code, for the challenge the password opened. */
function twoFactorStep(result) {
  // The page keeps posting with the visitor's token until the session exists.
  if (result.csrf) state.session = { ...state.session, csrf: result.csrf };
  return authCard('Two-factor authentication', 'Enter the 6-digit code your authenticator app shows for this account, or one of your recovery codes.',
    form([codeField('Code')], 'Log in', async values => {
      adopt(await api('POST', '/api/login/2fa', { challenge: result.challenge, code: values.code }));
      navigate(nextPath(), true);
      toast('Welcome back, ' + state.session.account + '.');
    }), ['Lost your phone and your codes? Ask an Admin to reset it. ', link('/login', null, 'Start over')]);
}

/** Shown to a GM or Admin of a server that requires 2FA before the admin area opens. */
function twoFactorBanner() {
  return h('div', { class: 'notice', role: 'status' }, icon('alert'),
    h('div', null, h('b', null, 'Turn on two-factor authentication'),
      'This server requires it for GM and Admin accounts. The admin area opens once it is on.'),
    h('div', { class: 'actions' }, link('/account/security', { class: 'btn primary sm' }, icon('key'), 'Set it up')));
}

function recoveryList(codes) {
  return [h('p', { class: 'hint' }, 'Recovery codes, each good once. Keep them somewhere safe: they are not shown again.'),
    h('div', { class: 'codes', 'aria-label': 'Recovery codes' }, codes.map(c => h('span', null, c)))];
}

/** The server's QR code ({ size, path }: the dark modules as one SVG path), drawn as SVG elements (the page's policy
 *  allows no inline style); dark on light in both themes, as a camera reads it best. */
function qrCode(qr) {
  if (!qr) return null;
  const svg = document.createElementNS(SVG, 'svg');
  svg.setAttribute('class', 'qr');
  svg.setAttribute('viewBox', `0 0 ${qr.size} ${qr.size}`);
  svg.setAttribute('role', 'img');
  svg.setAttribute('aria-label', 'QR code of the key, for the authenticator app');
  svg.setAttribute('shape-rendering', 'crispEdges');
  const back = document.createElementNS(SVG, 'rect');
  back.setAttribute('class', 'qr-light');
  back.setAttribute('width', String(qr.size));
  back.setAttribute('height', String(qr.size));
  const dark = document.createElementNS(SVG, 'path');
  dark.setAttribute('class', 'qr-dark');
  dark.setAttribute('d', qr.path);
  svg.append(back, dark);
  return svg;
}

/** The 2FA card: setting it up (secret, app, first code), or the codes and turning it off. */
function twoFactorCard(state2fa) {
  const host = h('div');
  const status = state2fa.enabled ? chip('active', 'On') : chip('', 'Off');
  const show = (...nodes) => host.replaceChildren(...nodes.flat(Infinity).filter(Boolean));
  const start = async button => {
    const setup = await busy(button, () => api('POST', '/api/account/2fa/begin'));
    const groups = setup.secret.match(/.{1,4}/g).join(' ');
    show(h('ol', { class: 'steps' },
      h('li', null, 'Open an authenticator app (Google Authenticator, Microsoft Authenticator, Aegis, 1Password, ...).'),
      h('li', null, 'Scan this QR code with it. Or add an account by hand with the key below (time based, 6 digits), or ',
        h('a', { class: 'text-link', href: setup.uri }, 'open it in the app'), ' on this device.'),
      h('li', null, 'Type the code the app shows.')),
    qrCode(setup.qr),
    h('code', { class: 'secret' }, groups),
    form([codeField('Code from the app')], 'Turn on', async values => {
      const done = await api('POST', '/api/account/2fa/enable', { code: values.code });
      toast('Two-factor authentication is on.');
      await loadSession();
      status.replaceWith(chip('active', 'On'));
      show(recoveryList(done.recoveryCodes), link('/account/security', { class: 'btn secondary sm' }, 'Done'));
    }));
  };
  if (!state2fa.enabled) {
    const button = h('button', { type: 'button', class: 'btn primary sm', onclick: () => start(button).catch(e => toast(e.message)) },
      h('i', { class: 'spin' }), icon('key'), 'Set up two-factor authentication');
    show(h('p', { class: 'hint' }, 'A code from your phone at every login, besides the password.'), button);
  } else {
    const codes = form([codeField('Current code or recovery code')], 'New recovery codes', async values => {
      const result = await api('POST', '/api/account/2fa/recovery', { code: values.code });
      show(recoveryList(result.recoveryCodes), link('/account/security', { class: 'btn secondary sm' }, 'Done'));
    }, 'secondary');
    const off = form([codeField('Current code or recovery code', 'off')], 'Turn off', async values => {
      await api('POST', '/api/account/2fa/disable', { code: values.off });
      toast('Two-factor authentication is off.');
      await loadSession();
      route();
    }, 'danger');
    show(facts([['Status', chip('active', 'On')], ['Recovery codes left', h('span', { class: 'num' }, num(state2fa.recoveryLeft))]]),
      state2fa.required && h('p', { class: 'hint' }, 'Required for your role on this server: turning it off closes the admin area to you.'),
      h('div', { class: 'action' }, codes), h('div', { class: 'action' }, off));
  }
  return card('Two-factor authentication', status, body(host));
}

/** "Edge on Windows" from a User-Agent header: enough to recognise one's own browsers. */
function browserName(agent) {
  if (!agent) return 'Unknown browser';
  const browser = /Edg\//.test(agent) ? 'Edge' : /OPR\//.test(agent) ? 'Opera' : /Firefox\//.test(agent) ? 'Firefox'
    : /Chrome\//.test(agent) ? 'Chrome' : /Safari\//.test(agent) ? 'Safari' : 'A browser';
  const system = /Android/.test(agent) ? 'Android' : /iPhone|iPad/.test(agent) ? 'iOS' : /Windows/.test(agent) ? 'Windows'
    : /Mac OS X/.test(agent) ? 'macOS' : /Linux/.test(agent) ? 'Linux' : '';
  return system ? `${browser} on ${system}` : browser;
}

function sessionsCard(sessions, addresses) {
  const end = async s => { await api('POST', `/api/account/sessions/${encodeURIComponent(s.id)}/end`); toast('That browser is signed out.'); route(); };
  const endOthers = async () => {
    const { ended } = await api('POST', '/api/account/sessions/end-others');
    toast(ended ? `${plural(ended, 'other browser')} signed out.` : 'No other browser was signed in.');
    route();
  };
  return card('Signed-in browsers', h('button', { type: 'button', class: 'btn secondary sm', onclick: () => endOthers().catch(e => toast(e.message)) }, icon('out'), 'Log out everywhere else'),
    table([
      { label: 'Browser', grow: true, value: s => [h('span', { title: s.agent }, browserName(s.agent)), ' ', s.current && chip('online', 'This one')] },
      addresses && { label: 'Address', opt: true, value: s => s.address ? mono(s.address) : h('span', { class: 'muted' }, '-') },
      { label: 'Signed in', opt: true, value: s => stamp(s.created) },
      { label: 'Last seen', value: s => ago(s.lastSeen) },
      { label: '', value: s => s.current ? null : h('button', { type: 'button', class: 'btn secondary sm', onclick: () => end(s).catch(e => toast(e.message)) }, 'Sign out') },
    ].filter(Boolean), sessions, { short: true }),
    'The game client is not listed: a ban or a password change ends its session too.');
}

/** The account's latest logins: when, panel or game, and the address where the server keeps them. */
function loginsTable(logins, addresses) {
  return logins.length
    ? table([
      { label: 'Time', value: l => stamp(l.utc) },
      { label: 'Where', value: l => chip(l.source === 'game' ? 'info' : 'plain', l.source === 'game' ? 'Game' : 'Panel') },
      addresses && { label: 'Address', grow: true, value: l => l.address ? mono(l.address) : h('span', { class: 'muted' }, '-') },
    ].filter(Boolean), logins, { short: true })
    : emptyState('clock', 'No logins recorded yet', null);
}

async function securityPage() {
  if (!state.session?.account) throw new ApiError('Please log in.', 401);
  const s = await api('GET', '/api/account/security');
  return [pageHead({ crumbs: [{ href: '/', label: 'Home' }, { href: '/account', label: 'Account' }, { label: 'Security' }], title: 'Security',
    subtitle: 'Two-factor authentication, the browsers signed in to this panel and your latest logins.', actions: densityToggle() }),
  s.twoFactor.required && !s.twoFactor.enabled && twoFactorBanner(),
  h('div', { class: 'grid' },
    h('div', { class: 'span-5' }, twoFactorCard(s.twoFactor)),
    h('div', { class: 'span-7 stack' }, sessionsCard(s.sessions, s.addresses),
      card('Latest logins', null, loginsTable(s.logins, s.addresses),
        s.addresses ? null : 'This server keeps no addresses of logins.')))];
}
