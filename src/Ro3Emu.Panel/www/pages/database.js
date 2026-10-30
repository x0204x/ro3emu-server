// Shared pieces of the item, monster and map databases and the economy log (pages/items.js, monsters.js, maps.js,
// economy.js), built on app.js's helpers. Pictures come from the operator's own client (the data export's icons/
// folder, served under /img/); RO3Emu ships none. Without one, an item or monster is the initial of its name on a tint.
'use strict';

/** The initial of a name on a tint: an item's quality (q1-q6) or a monster's rank; size '', 'lg' or 'xl'. */
function glyph(name, kind, size) {
  const letter = (String(name).match(/[\p{L}\p{N}]/u) || ['?'])[0];
  return h('span', { class: 'glyph ' + (kind || '') + (size ? ' ' + size : ''), 'aria-hidden': 'true' }, letter);
}

/** The picture at src (an /img/ address from the server), or the glyph when there is none or it does not load. */
function thumb(src, name, kind, size) {
  if (!src) return glyph(name, kind, size);
  const img = h('img', { class: 'thumb ' + (kind || '') + (size ? ' ' + size : ''), src, alt: '', loading: 'lazy', decoding: 'async' });
  img.addEventListener('error', () => img.replaceWith(glyph(name, kind, size)), { once: true });
  return img;
}

/** An item's name in its quality's colour, with its icon; a link to its page unless plain. */
function itemName(id, name, quality, plain, iconSrc) {
  const content = [thumb(iconSrc, name, 'q' + quality), h('span', null, name)];
  return plain ? h('span', { class: 'named q' + quality }, content)
    : link('/items/' + id, { class: 'named q' + quality }, content);
}

const rankNames = { normal: 'Normal', mini: 'Mini boss', boss: 'Boss', mvp: 'MVP' };
const rankChip = rank => rank && rank !== 'normal' ? chip(rank, rankNames[rank]) : null;

function monsterName(id, name, rank, iconSrc) {
  return link('/monsters/' + id, { class: 'named' }, thumb(iconSrc, name, rank !== 'normal' ? rank : ''), h('span', null, name));
}

/** A map's name with its outline picture, linking to its page. */
function mapName(id, name, image) {
  return id ? link('/maps/' + id, { class: 'named' }, thumb(image, name, 'map'), h('span', null, name)) : h('span', null, name);
}

/** A list row's range, "2-5", or its one value. */
const range = (low, high, format = num) => high > low ? `${format(low)}-${format(high)}` : format(low);

/** A list row standing for several variants of its name: a small "4 variants" chip after the name. */
const variantsChip = count => count > 1 ? h('span', { class: 'chip plain variants-chip' }, `${count} variants`) : null;

/** The game's other ids for the same entry, in one small line: "IDs 3084401 · 3084402". */
function variantLine(ids) {
  return ids && ids.length > 1 ? h('span', { class: 'variants mono' }, 'IDs ' + ids.join(' · ')) : null;
}

/** A chance per kill, 0 to 1, as a percentage with as many decimals as a rare drop needs. */
function percent(chance) {
  const p = chance * 100;
  if (p >= 99.995) return '100%';
  if (p >= 10) return p.toFixed(1).replace(/\.0$/, '') + '%';
  if (p >= 0.01) return p.toFixed(2).replace(/\.?0+$/, '') + '%';
  return '<0.01%';
}

const countRange = (min, max) => min === max ? num(min) : `${num(min)}-${num(max)}`;

/** The search params of the current page, as a plain object. */
const queryOf = () => Object.fromEntries(new URLSearchParams(location.search));

/** A list URL from its filters; empty values are left out. */
function listHref(path, values) {
  const params = new URLSearchParams(Object.entries(values).filter(([, v]) => v !== '' && v != null && v !== 0 && v !== '0'));
  const text = params.toString();
  return text ? path + '?' + text : path;
}

/** Previous / next for a page of results. */
function listPager(result, href, noun) {
  const pages = Math.max(1, Math.ceil(result.total / result.pageSize));
  return [h('span', null, mono(num(result.total)), ' ', result.total === 1 ? noun : noun + 's',
    pages > 1 ? [' · page ', mono(String(result.page)), ' of ', mono(String(pages))] : null),
  pages > 1 && h('div', { class: 'actions' },
    result.page > 1 ? link(href(result.page - 1), { class: 'btn secondary sm' }, icon('back'), 'Previous') : null,
    result.page < pages ? link(href(result.page + 1), { class: 'btn secondary sm' }, 'Next', icon('arrow')) : null)];
}

/**
 * The filter row of a list: a search box and the given controls; submitting navigates to the list with the form's
 * values (page 1). A reset link appears once a filter is set.
 */
function filterForm(path, query, controls, placeholder) {
  const set = Object.entries(query).some(([k, v]) => k !== 'page' && v);
  const form = h('form', { class: 'filters', role: 'search' },
    h('span', { class: 'search' }, icon('search'), h('input', {
      class: 'input', name: 'q', value: query.q || '', placeholder, 'aria-label': placeholder, spellcheck: 'false', autocapitalize: 'off',
    })),
    controls,
    h('button', { type: 'submit', class: 'btn primary sm' }, 'Search'),
    set ? link(path, { class: 'btn secondary sm' }, 'Reset') : null);
  form.addEventListener('submit', e => {
    e.preventDefault();
    const values = Object.fromEntries([...new FormData(form)].map(([k, v]) => [k, String(v).trim()]));
    navigate(listHref(path, values));
  });
  return form;
}

/** A select of options [{ id, name }] with an "any" first choice. */
function optionSelect(name, label, options, value, anyLabel) {
  return h('select', { class: 'input', name, 'aria-label': label },
    h('option', { value: '' }, anyLabel),
    options.map(o => h('option', { value: String(o.id), selected: String(o.id) === String(value || '') }, o.name)));
}

/** Level from / to. */
function levelRange(query) {
  const box = (name, label) => h('input', { class: 'input', name, type: 'number', min: '0', max: '9999', inputmode: 'numeric',
    value: query[name] || '', placeholder: label, 'aria-label': 'Level ' + label.toLowerCase() });
  return h('span', { class: 'lv' }, 'Lv', box('min', 'From'), '-', box('max', 'To'));
}
