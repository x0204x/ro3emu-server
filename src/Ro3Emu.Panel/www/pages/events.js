// The event calendar (/events): the timed activities from now to a week ahead as the server opens them, in the
// viewer's own time with live countdowns, and the season's long windows.
'use strict';

const sameDay = (a, b) => a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate();

/** Open now, starting later, or over, as a chip with a live countdown. */
function eventState(t) {
  const now = Date.now(), start = new Date(t.start).getTime(), end = new Date(t.end).getTime();
  if (now >= end) return chip('', 'Over');
  if (now >= start) return new Date(t.end).getFullYear() >= 2090 ? chip('online', 'Open')
    : h('span', { class: 'chip online' }, 'Open · ', countdown(t.end, 'ends in ', 'over'));
  return h('span', { class: 'chip info' }, countdown(t.start, 'in ', 'open'));
}

function timeRange(t) {
  const start = new Date(t.start), end = new Date(t.end);
  return end - start >= 86400000 - 60000 ? 'All day' : `${localTime(t.start)}-${localTime(t.end)}`;
}

/** Every occurrence of every event as one list, by start. */
function occurrences(events) {
  return events.flatMap(e => e.times.map(t => ({ event: e, time: t }))).sort((a, b) => new Date(a.time.start) - new Date(b.time.start));
}

/** The client's period text without its "Event Period: Undefined," lead (no end date is set). */
function period(text) {
  const rest = (text || '').replace(/^Event Period: Undefined,\s*/i, '');
  return rest ? rest[0].toUpperCase() + rest.slice(1) : '';
}

function eventName(e) {
  return h('span', { class: 'named' }, thumb(e.icon, e.name, 'event'), h('span', null, e.name));
}

async function eventsPage() {
  const cal = await api('GET', '/api/events');
  const all = occurrences(cal.events);
  const now = new Date();
  const open = all.filter(o => new Date(o.time.start) <= now && now < new Date(o.time.end));
  const next = all.find(o => new Date(o.time.start) > now);
  const today = all.filter(o => sameDay(new Date(o.time.start), now) || (new Date(o.time.start) < now && now < new Date(o.time.end)));
  const days = Array.from({ length: 7 }, (_, i) => new Date(now.getFullYear(), now.getMonth(), now.getDate() + i));
  const todayCard = card('Today', h('span', { class: 'muted' }, dayFormat.format(now)), today.length
    ? table([
      { label: 'When', value: o => mono(timeRange(o.time)) },
      { label: 'Event', grow: true, value: o => eventName(o.event) },
      { label: 'Level', num: true, opt: true, value: o => o.event.level ? num(o.event.level) : '-' },
      { label: 'Status', value: o => eventState(o.time) },
    ], today, { short: true })
    : emptyState('calendar', 'Nothing scheduled today', null));
  const week = h('div', { class: 'week' }, days.map(d => {
    const list = all.filter(o => sameDay(new Date(o.time.start), d));
    return h('section', { class: 'day' + (sameDay(d, now) ? ' today' : '') }, h('h3', null, dayFormat.format(d)),
      list.length ? h('ul', null, list.map(o => h('li', null, h('span', { class: 'mono t' }, timeRange(o.time)), h('span', { class: 'n' }, o.event.name))))
        : h('p', { class: 'muted' }, 'Nothing'));
  }));
  const forever = iso => new Date(iso).getFullYear() >= 2090;
  const season = cal.season.length
    ? table([
      { label: 'Event', grow: true, value: s => h('span', { class: 'strong' }, s.name) },
      { label: 'From', value: s => stamp(s.start) },
      { label: 'Until', value: s => forever(s.end) ? h('span', { class: 'muted' }, 'No end') : stamp(s.end) },
      { label: 'Status', value: s => eventState({ start: s.start, end: s.end }) },
    ], cal.season, { short: cal.season.length <= 16 })
    : emptyState('calendar', 'No season event is open or coming', null);
  const kinds = cal.events.length
    ? table([
      { label: 'Event', grow: true, value: e => eventName(e) },
      { label: 'When', wrap: true, value: e => period(e.period) || h('span', { class: 'muted' }, '-') },
      { label: 'Level', num: true, value: e => e.level ? num(e.level) : '-' },
      { label: 'Next', value: e => e.times.length ? eventState(e.times[0]) : h('span', { class: 'muted' }, 'Not this week') },
    ], cal.events, { short: true })
    : null;
  return [pageHead({ crumbs: dbCrumbs({ label: 'Event calendar' }), title: 'Event calendar',
    subtitle: `Times in your own time zone. The server runs on ${cal.zone}; a game day starts at ${pad(cal.dayStartHour)}:00 server time.`,
    actions: h('button', { type: 'button', class: 'btn secondary sm', onclick: route }, 'Refresh') }),
  cal.alwaysOpen ? card(null, null, body(h('p', { class: 'hint flush' }, icon('alert'), ' This server runs every activity all day (a testing switch is on), so the times below repeat around the clock.'))) : null,
  h('section', { class: 'kpis', 'aria-label': 'Events' },
    kpi('Open now', 'calendar', num(open.length), open.length === 1 ? 'event' : 'events', open.map(o => o.event.name).join(', ') || 'none right now'),
    kpiLive('Next up', 'clock', next ? countdown(next.time.start, '', 'now') : '-', null, next ? next.event.name + ' · ' + localTime(next.time.start) : 'nothing this week'),
    kpi('This week', 'list', num(all.length), all.length === 1 ? 'opening' : 'openings', plural(cal.events.length, 'timed event')),
    kpi('Season', 'star', num(cal.season.length), null, cal.openContent ? 'the server keeps the season open' : 'long-running events')),
  h('div', { class: 'grid' }, h('div', { class: 'span-7' }, todayCard), h('div', { class: 'span-5' }, card('Every timed event', null, kinds || emptyState('calendar', 'No timed events', null)))),
  card('The week ahead', null, body(week)),
  card('Season events', null, season, 'Card pools, passes and season events: open from the first date to the second.')];
}

routes.push([/^\/events$/, eventsPage, 'database']);
