'use strict';

// Fortnite - Chest Tracker web interface. Everything comes from the local counter (state pushed with Server-Sent Events);
// buttons send commands (POST /api/cmd/<name>) with the secret token of this run.
// Layout: top bar with tabs + pages that fit the window; lists are cut into pages of as many rows as the space allows.

const TOKEN = document.querySelector('meta[name="fct-token"]').content;

let S = null;          // latest state
let T = {};            // texts of the interface, in the counter's language
let textsLang = null;
const PAGES = ['home', 'matches', 'stats', 'treasures', 'settings'];
const OLD_PAGES = { estimate: 'estimate', overlay: 'overlay', journal: 'journal' }; // pages that moved into the settings
// Preferences of the page, kept by the browser. Versions 1.0.x stored them under another prefix: moved over once.
for (const key of ['page', 'goal', 'sub', 'range']) {
  const old = localStorage.getItem('cc-' + key);
  if (old !== null) {
    if (localStorage.getItem('fct-' + key) === null) localStorage.setItem('fct-' + key, old);
    localStorage.removeItem('cc-' + key);
  }
}
const ui = {
  page: PAGES.includes(localStorage.getItem('fct-page')) ? localStorage.getItem('fct-page') : OLD_PAGES[localStorage.getItem('fct-page')] ? 'settings' : 'home',
  goal: localStorage.getItem('fct-goal'), // treasure aimed at on the home page ('auto' = next milestone); else the counter's
  editPrize: null,      // prize id being renamed in the treasures list
  animate: true,        // play the entrance animations at the next render (first display, page change)
  sub: OLD_PAGES[localStorage.getItem('fct-page')] || localStorage.getItem('fct-sub') || 'general', // section of the settings
  range: Number(localStorage.getItem('fct-range')) === 7 ? 7 : 30,  // statistics chart: 7, 30 or 90 days
  mode: '',
  selected: null,       // match shown in the detail card
  pageIndex: {},        // page shown per list
  size: {},             // rows that fit per list (measured)
  draft: {},            // values typed in fields but not saved yet (kept across re-renders)
  setup: null,          // wizard answers
  lastActivity: null,
  prevTotal: null,
};

// ---- Helpers -------------------------------------------------------------------------------------
const $ = (sel, root = document) => root.querySelector(sel);
const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const t = (key, ...args) => {
  let s = T[key] ?? key;
  args.forEach((a, i) => { s = s.split(`{${i}}`).join(a); });
  return s;
};
const locale = () => ({ fr: 'fr-FR', en: 'en-GB', es: 'es-ES' }[S?.app.lang] || 'fr-FR');
// French formats use a narrow no-break space (U+202F) that some fonts lack: use a normal one.
const fixSpace = (s) => s.replace(/\u202f/g, '\u00a0');
// Thousands always grouped: Spanish leaves 4-digit numbers alone by default ("7813" next to "137.500").
const numFormat = (opts = {}) => new Intl.NumberFormat(locale(), { useGrouping: 'always', ...opts });
const nf = (n) => fixSpace(numFormat().format(n ?? 0));
const nf1 = (n) => fixSpace(numFormat({ minimumFractionDigits: 1, maximumFractionDigits: 1 }).format(n ?? 0));
const pctf = (n) => fixSpace(numFormat({ style: 'percent', maximumFractionDigits: 0 }).format(n ?? 0));
const date = (iso, opts) => new Intl.DateTimeFormat(locale(), opts).format(new Date(iso));
const dateTime = (iso) => date(iso, { day: '2-digit', month: 'short', hour: '2-digit', minute: '2-digit' });
const dayLabel = (iso) => date(iso, { weekday: 'short', day: 'numeric', month: 'short' });
const time = (iso) => date(iso, { hour: '2-digit', minute: '2-digit' });
// French: singular for 0 and 1; English and Spanish: singular for 1 only.
const single = (n) => (S?.app.lang === 'fr' ? n <= 1 : n === 1);
const count = (n, key) => `${nf(n)} ${single(n) ? t(key + 'One') : t(key + 'Many')}`;
const pct = (v) => (S?.app.lang === 'en' ? `${v}%` : `${v} %`);
const duration = (s) => (s == null ? '—' : `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`);
// Treasure hunt: chance of at least one drop after n chests at 1 in `odds` (rates announced by Epic, per chest).
const nfx = (n) => (n >= 0.1 || n === 0 ? nf1(n) : fixSpace(numFormat({ maximumSignificantDigits: 1 }).format(n)));
const dropChance = (n, odds) => 1 - Math.pow(1 - 1 / odds, n);
// Percentages that stay readable when tiny: 18 %, 2.4 %, 0.29 %, 0.025 %.
const pctx = (p) => {
  const o = p >= 0.1 ? { maximumFractionDigits: 0 } : p >= 0.01 ? { maximumFractionDigits: 1 } : { maximumSignificantDigits: 2 };
  return fixSpace(numFormat({ style: 'percent', ...o }).format(p > 0.995 ? 0.99 : p));
};
// The treasure aimed at: the one chosen on the home page (pirate token by default), or with 'auto' the next milestone,
// i.e. the first weekly treasure (most common first) that 1 drop on average has not reached yet.
function huntTarget(h, goal = ui.goal) {
  const chosen = h.odds.find((o) => o.key === goal);
  if (chosen) return chosen;
  const list = h.odds.filter((o) => o.key !== 'odds_gizmo').sort((a, b) => a.odds - b.odds);
  return list.find((o) => h.chests < o.odds) || list[list.length - 1];
}
// Play time as "2 370 h" or "45 min".
const hoursText = (hours) => (hours >= 1 ? `${nf(Math.round(hours))} h` : `${nf(Math.max(1, Math.round(hours * 60)))} min`);

// The 3D scene (scene.js, a module loaded after this script) follows the state through this event.
function updateScene() {
  if (!S) return;
  const h = S.hunt;
  const target = h ? huntTarget(h) : null;
  window.dispatchEvent(new CustomEvent('fct-scene', { detail: {
    visible: ui.page === 'home' && !S.setup, live: S.live.inProgress, total: S.stats.total, treasures: S.stats.treasures,
    chance: target ? dropChance(h.chests, target.odds) : 0,
    dug: target ? Math.min(1, h.chests / target.odds) : 0, instant: !!ui.sceneInstant, treasureLabel: t('sceneTreasure'),
    bubbles: {
      you: t('bub_you'), others: t('bub_others'),
      minerLines: [t('bub_miner'), t('bub_miner_2'), t('bub_miner_3'), t('bub_miner_4'), t('bub_miner_5'), t('bub_miner_6')],
      quitterLines: [t('bub_quitter'), t('bub_quitter_2'), t('bub_quitter_3'), t('bub_quitter_4'), t('bub_quitter_5'), t('bub_quitter_6')],
      parrot: t('bub_parrot'), crab: t('bub_crab'), monkey: t('bub_monkey'), dog: t('bub_dog'),
      captain: t('bub_captain'), lookout: t('bub_lookout'), cannon: t('bub_cannon'), token: t('bub_token'), chest: t('bub_chest'),
    },
  } }));
}
window.addEventListener('fct-scene-ready', updateScene);
// (the goal switch above asks for an instant move once; later updates animate again)
window.addEventListener('fct-scene', () => { ui.sceneInstant = false; });

const draft = (id, fallback) => (id in ui.draft ? ui.draft[id] : fallback ?? '');

// Chest kinds (stored French names) -> item rarity colour.
const RARITY = {
  'Coffre': 'legendary', 'Coffre rare': 'rare', 'Coffre enterré': 'uncommon',
  "Coffre d'extraction / reliques": 'epic', "Coffre des affiches d'esprit": 'mythic', 'Autre coffre': 'common',
};
const RARITY_VAR = { common: '--common', uncommon: '--uncommon', rare: '--rare', epic: '--epic', legendary: '--legendary', mythic: '--mythic' };
const rarityOf = (k) => RARITY[k] || 'common';

// Icons: 24×24, 2px strokes.
const ICONS = {
  home: '<rect x="3" y="3" width="7" height="9" rx="1.5"/><rect x="14" y="3" width="7" height="5" rx="1.5"/><rect x="14" y="12" width="7" height="9" rx="1.5"/><rect x="3" y="16" width="7" height="5" rx="1.5"/>',
  matches: '<path d="M9 6h12M9 12h12M9 18h12"/><path d="M4 6h.01M4 12h.01M4 18h.01"/>',
  stats: '<path d="M3 3v18h18"/><path d="m7 15 4-4 3 3 6-7"/>',
  estimate: '<path d="M3 12a9 9 0 1 0 3-6.7L3 8"/><path d="M3 3v5h5"/><path d="M12 7v5l3 2"/>',
  treasures: '<path d="M6 3h12l4 6-10 12L2 9z"/><path d="M2 9h20M12 21 8 9l4-6 4 6z"/>',
  overlay: '<rect x="2" y="4" width="20" height="13" rx="2"/><path d="M8 21h8M12 17v4"/>',
  journal: '<path d="M22 12h-4l-3 9L9 3l-3 9H2"/>',
  settings: '<circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.5-1.1 1.7 1.7 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.8.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.8V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z"/>',
  chest: '<path d="M4 11a5 5 0 0 1 5-5h6a5 5 0 0 1 5 5v8H4z"/><path d="M4 12h16"/><rect x="10" y="10" width="4" height="5" rx="1"/>',
  target: '<circle cx="12" cy="12" r="9"/><circle cx="12" cy="12" r="5"/><circle cx="12" cy="12" r="1"/>',
  bolt: '<path d="M13 2 4 14h7l-1 8 9-12h-7z"/>',
  star: '<path d="m12 3 2.8 5.7 6.2.9-4.5 4.4 1 6.2L12 17.3 6.5 20.2l1-6.2L3 9.6l6.2-.9z"/>',
  trophy: '<path d="M8 21h8M12 17v4M7 4h10v5a5 5 0 0 1-10 0z"/><path d="M17 5h3v2a3 3 0 0 1-3 3M7 5H4v2a3 3 0 0 0 3 3"/>',
  calendar: '<rect x="3" y="5" width="18" height="16" rx="2"/><path d="M3 10h18M8 3v4M16 3v4"/>',
  clock: '<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>',
  camera: '<path d="M4 8h3l2-3h6l2 3h3a1 1 0 0 1 1 1v10a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V9a1 1 0 0 1 1-1z"/><circle cx="12" cy="13.5" r="3.5"/>',
  crosshair: '<circle cx="12" cy="12" r="8"/><path d="M12 2v4M12 18v4M2 12h4M18 12h4"/>',
  hammer: '<path d="m15 12-8.5 8.5a2.1 2.1 0 0 1-3-3L12 9"/><path d="m17.6 15 4.4-4.4-6.6-6.6-4.4 4.4z"/>',
  refresh: '<path d="M21 12a9 9 0 1 1-2.6-6.4"/><path d="M21 3v6h-6"/>',
  power: '<path d="M18.4 6.6a9 9 0 1 1-12.8 0"/><path d="M12 2v10"/>',
  folder: '<path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2Z"/>',
  copy: '<rect x="9" y="9" width="12" height="12" rx="2"/><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"/>',
  link: '<path d="M14 3h7v7"/><path d="M10 14 21 3"/><path d="M21 14v5a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5"/>',
  left: '<path d="m15 18-6-6 6-6"/>',
  right: '<path d="m9 18 6-6-6-6"/>',
  up: '<path d="M12 19V5M5 12l7-7 7 7"/>',
  down: '<path d="M12 5v14M19 12l-7 7-7-7"/>',
  info: '<circle cx="12" cy="12" r="9"/><path d="M12 11v5M12 8h.01"/>',
  alert: '<path d="M12 3 2 20h20z"/><path d="M12 10v4M12 17h.01"/>',
  check: '<path d="m5 12 5 5 9-10"/>',
  key: '<circle cx="8" cy="15" r="4"/><path d="m11 12 9-9M16 7l3 3"/>',
  edit: '<path d="M12 20h9"/><path d="M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4z"/>',
};
const icon = (name) => `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${ICONS[name] || ''}</svg>`;
const button = (label, attrs = '', cls = '') => `<button class="btn ${cls}" ${attrs}>${label}</button>`;

async function cmd(name, args) {
  try {
    const res = await fetch('/api/cmd/' + name, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-Tracker-Token': TOKEN },
      body: JSON.stringify(args ?? {}),
    });
    if (res.status === 403) { location.reload(); return { ok: false }; } // counter restarted: new token
    const r = await res.json();
    if (!r.ok && r.error) toast('error', t('error', r.error));
    return r;
  } catch (e) {
    toast('error', t('error', e.message));
    return { ok: false };
  }
}

function toast(level, text) {
  const box = $('#toasts');
  const el = document.createElement('div');
  el.className = 'toast';
  el.innerHTML = `<span class="lv ${esc(level)}"></span><span></span>`;
  el.lastChild.textContent = text;
  box.appendChild(el);
  while (box.children.length > 4) box.firstChild.remove();
  setTimeout(() => { el.classList.add('out'); setTimeout(() => el.remove(), 350); }, level === 'error' ? 9000 : 6000);
}

// Re-rendering replaces the HTML: keep the focused element (found again by its id, else by its data-* attributes:
// tabs, buttons of the wizard…) and the cursor of a field.
function focusSelector(a) {
  if (!a || a === document.body || !a.tagName) return null;
  if (a.id) return '#' + CSS.escape(a.id);
  const data = [...a.attributes].filter((x) => x.name.startsWith('data-')).map((x) => `[${x.name}="${CSS.escape(x.value)}"]`).join('');
  return data ? a.tagName.toLowerCase() + data : null;
}
function keepFocus(fn) {
  const a = document.activeElement;
  const selector = focusSelector(a);
  let sel = null;
  try { sel = a && a.id && a.selectionStart != null ? { start: a.selectionStart, end: a.selectionEnd } : null; } catch { /* number fields */ }
  fn();
  const b = selector && document.querySelector(selector);
  if (b && b !== document.activeElement) {
    b.focus({ preventScroll: true });
    try { if (sel) b.setSelectionRange(sel.start, sel.end); } catch { /* number fields */ }
  }
}

// ---- Fitting lists to the window -------------------------------------------------------------------
// A list is drawn in a .fit box (data-fit = key, data-row = row height, data-extra = header height): after the
// render the rows that fit are measured (with the height of a drawn row when there is one: the CSS makes rows
// smaller on short windows); when that changed, the page is drawn again with that page size.
function pageOf(key, total) {
  const size = Math.max(1, ui.size[key] || 8);
  const pages = Math.max(1, Math.ceil(total / size));
  const page = Math.min(ui.pageIndex[key] || 0, pages - 1);
  ui.pageIndex[key] = page;
  return { start: page * size, end: Math.min(total, page * size + size), page, pages, size };
}

function pager(key, total) {
  if (total === 0) return '';
  const p = pageOf(key, total);
  return `<div class="pager"><span>${esc(t('rangeOf', nf(p.start + 1), nf(p.end), nf(total)))}</span>
    ${p.pages > 1 ? `<div class="row">${button(icon('left'), `data-page="${key}:-1" aria-label="${esc(t('previous'))}"${p.page === 0 ? ' disabled' : ''}`, 'btn-sm btn-icon')}
    <span>${esc(t('pageOf', p.page + 1, p.pages))}</span>
    ${button(icon('right'), `data-page="${key}:1" aria-label="${esc(t('next'))}"${p.page >= p.pages - 1 ? ' disabled' : ''}`, 'btn-sm btn-icon')}</div>` : ''}</div>`;
}

function measure() {
  let changed = false;
  document.querySelectorAll('.fit[data-fit]').forEach((el) => {
    const row = el.querySelector('.list-row, tbody tr, .feed-row');
    const rowHeight = (row && row.offsetHeight) || Number(el.dataset.row) || 44;
    const rows = Math.max(1, Math.floor((el.clientHeight - (Number(el.dataset.extra) || 0)) / rowHeight));
    if (ui.size[el.dataset.fit] !== rows) { ui.size[el.dataset.fit] = rows; changed = true; }
  });
  return changed;
}

// ---- Shell ------------------------------------------------------------------------------------------
function render() {
  if (!S || textsLang !== S.app.lang) return;
  document.documentElement.lang = S.app.lang;
  document.title = S.live.inProgress ? `● ${t('statusLive')} · Fortnite - Chest Tracker` : `${nf(S.stats.total)} · Fortnite - Chest Tracker`;
  for (let pass = 0; pass < 3; pass++) {
    keepFocus(() => {
      $('#app').innerHTML = `<div class="app${ui.animate ? ' anim' : ''}">${topbar()}<main class="main">${pageHead()}<div class="banners">${banners()}</div>${page()}</main></div>`;
      $('#modal-root').innerHTML = S.setup ? setupModal() : ui.share ? shareModal() : '';
    });
    if (!measure()) break;
  }
  // A window (wizard, share) keeps the focus: the page behind is inert, the first control is focused when it opens
  const modalBox = $('#modal-root .modal');
  $('#app').inert = !!modalBox;
  if (modalBox && !modalBox.contains(document.activeElement)) {
    modalBox.querySelector('input:not(:disabled), select:not(:disabled), button:not(:disabled), a[href]')?.focus({ preventScroll: true });
  }
  ui.animate = false;
  updateScene();
  // The total counts up to its new value and pops
  const total = $('#kpi-total');
  if (ui.prevTotal != null && S.stats.total > ui.prevTotal && total) countUp(total, ui.prevTotal, S.stats.total);
  ui.prevTotal = S.stats.total;
}

function status() {
  const st = S.status;
  if (st === 'analyzing' && S.progress) return { cls: 'busy', text: t('statusAnalyzing', S.progress.done, S.progress.total), hint: t('statusAnalyzingHint') };
  if (st === 'live') return { cls: 'live', text: t('statusLive'), hint: t('liveWaitingHint') };
  if (st === 'finishing') return { cls: 'busy', text: t('statusFinishing'), hint: t('statusFinishingHint') };
  if (st === 'setup') return { cls: 'off', text: t('statusSetup'), hint: '' };
  return { cls: '', text: t('statusIdle'), hint: t('statusIdleHint') };
}

function greeting() {
  const h = new Date().getHours();
  return h >= 5 && h < 12 ? t('greetMorning') : h >= 12 && h < 18 ? t('greetAfternoon') : t('greetEvening');
}

// Top bar: logo, the five tabs (icon + name), then the counter's status and the two global actions.
function topbar() {
  const st = status();
  const tab = (k, i) => `<button class="tab${ui.page === k ? ' on' : ''}" data-nav="${k}" title="${esc(t('page_' + k))} (${i + 1})">${icon(k)}<span>${esc(t('page_' + k))}</span></button>`;
  const profiles = S.profiles.length > 1
    ? `<select id="profile" data-change="profile" aria-label="${esc(t('profile'))}">${S.profiles.map((p) => `<option value="${esc(p.key)}"${p.current ? ' selected' : ''}>${esc(p.name)}</option>`).join('')}</select>`
    : '';
  return `<header class="topbar${profiles ? ' has-profiles' : ''}">
    <div class="logo"><img src="/img/relic-chest.webp" alt=""><div><small>Fortnite</small><b>Chest<span>Tracker</span></b></div></div>
    <nav class="tabs">${PAGES.map(tab).join('')}</nav>
    <div class="spacer"></div>
    ${profiles}
    <div class="status" title="${esc(st.hint)}"><span class="dot ${st.cls}"></span><span>${esc(st.text)}</span></div>
    <button class="icon-btn" data-cmd="scan" title="${esc(t('scanNow'))}" aria-label="${esc(t('scanNow'))}">${icon('refresh')}</button>
    <button class="icon-btn" data-act="quit" title="${esc(t('quit'))}" aria-label="${esc(t('quit'))}">${icon('power')}</button>
  </header>`;
}

// Settings gather the less used tools: general settings, overlay, older matches (estimate) and the activity log.
const SUBS = ['general', 'overlay', 'estimate', 'journal'];

function pageHead() {
  if (ui.page === 'home') return '';
  const subs = ui.page === 'settings'
    ? `<div class="seg big" role="tablist">${SUBS.map((k) => `<button class="${ui.sub === k ? 'on' : ''}" data-sub="${k}">${esc(k === 'general' ? t('general') : t('page_' + k))}</button>`).join('')}</div>`
    : '';
  return `<header class="page-head"><h1>${esc(t('page_' + ui.page))}</h1>${subs}</header>`;
}

// One notice at a time (two of them crushed the pages on small windows): the most important first, then a "+N" button
// shows the next one.
const NOTICE_RANK = { error: 0, progress: 1, warning: 2, success: 3, info: 4 };
function banners() {
  const ic = { warning: ['alert', 't-gold'], error: ['alert', 't-red'], success: ['check', 't-green'], info: ['info', 't-blue'] };
  const items = S.notices.map((n) => {
    const [i, cls] = ic[n.level] || ic.info;
    return { rank: NOTICE_RANK[n.level] ?? 4, html: (more) => `<div class="banner ${esc(n.level)}"><div class="ic ${cls}">${icon(i)}</div><div class="txt">${esc(n.text)}</div>${n.action ? button(esc(n.actionLabel), `data-notice="${esc(n.action)}"`, 'btn-sm btn-primary') : ''}${more}</div>` };
  });
  if (S.progress && S.progress.total > 1) {
    const p = Math.round((100 * S.progress.done) / S.progress.total);
    items.push({ rank: NOTICE_RANK.progress, html: (more) => `<div class="banner info"><div class="spin sm"></div><div class="txt"><b>${esc(t('analyzingTitle', S.progress.done, S.progress.total))}</b> <span class="faint">${esc(t('analyzingText'))}</span><div class="progress"><i style="width:${p}%"></i></div></div>${more}</div>` });
  }
  if (!items.length) return '';
  items.sort((a, b) => a.rank - b.rank);
  const shown = items[(ui.notice || 0) % items.length];
  const more = items.length > 1 ? button(`+${items.length - 1}`, `data-act="notice-next" title="${esc(t('noticeNext'))}" aria-label="${esc(t('noticeNext'))}"`, 'btn-sm notice-more') : '';
  return shown.html(more);
}

function page() {
  if (ui.page === 'settings') return ({ general: settings, overlay, estimate, journal }[ui.sub] || settings)();
  return ({ home, matches, stats, treasures }[ui.page] || home)();
}

// ---- Components -----------------------------------------------------------------------------------
const card = (cls, title, body, { hint = '', actions = '' } = {}) =>
  `<section class="card ${cls}">${title ? `<div class="card-h"><h3>${esc(title)}</h3>${hint ? `<span class="hint">${esc(hint)}</span>` : ''}${actions ? `<div class="actions">${actions}</div>` : ''}</div>` : ''}<div class="card-b">${body}</div></section>`;

const empty = (title, text = '') => `<div class="empty"><div><b>${esc(title)}</b>${esc(text)}</div></div>`;

function kpi(cls, ic, tone, label, value, foot, id = '') {
  // tone: 't-gold' | 't-blue' | 't-green' | 't-violet' | 't-red' -> full gradient card
  return `<section class="card kpi ${cls} k-${tone.slice(2)}"><div class="kpi-bg" aria-hidden="true">${icon(ic)}</div><div class="kpi-top"><div class="kpi-icon">${icon(ic)}</div><div class="kpi-label">${esc(label)}</div></div>
    <div class="kpi-value num"${id ? ` id="${id}"` : ''}>${value}</div><div class="kpi-foot">${foot}</div></section>`;
}

function delta(now, before, unit = 'pct') {
  if (!before) return '';
  const d = (now - before) / before;
  if (Math.abs(d) > 9.99) return ''; // the other period was nearly empty: a percentage means nothing
  if (Math.abs(d) < 0.005) return `<span class="badge b-neutral">=</span>`;
  const up = d > 0;
  return `<span class="badge ${up ? 'b-green' : 'b-red'}">${icon(up ? 'up' : 'down')}${esc(unit === 'pct' ? pctf(Math.abs(d)) : nf(Math.abs(now - before)))}</span>`;
}

function placeBadge(m) {
  if (m.rank === 1) return `<span class="badge b-gold">${icon('trophy')}${esc(t('top1'))}</span>`;
  if (!m.rank) return `<span class="badge b-neutral">—</span>`;
  const cls = m.rank <= 5 ? 'b-green' : m.rank <= 15 ? 'b-blue' : 'b-neutral';
  return `<span class="badge ${cls}">${esc(m.placement)}</span>`;
}

function areaChart(days) {
  const n = days.length;
  const raw = Math.max(1, ...days.map((d) => d.chests));
  const step = raw <= 30 ? 10 : raw <= 60 ? 20 : raw <= 150 ? 50 : raw <= 300 ? 100 : 250;
  const max = Math.ceil(raw / step) * step;
  const pts = days.map((d, i) => [n === 1 ? 500 : (i / (n - 1)) * 1000, 100 - (d.chests / max) * 100]);
  let line = `M${pts[0][0]},${pts[0][1]}`;
  for (let i = 1; i < n; i++) {
    const [x0, y0] = pts[i - 1];
    const [x1, y1] = pts[i];
    const cx = (x0 + x1) / 2;
    line += ` C${cx},${y0} ${cx},${y1} ${x1},${y1}`;
  }
  const area = `${line} L1000,100 L0,100 Z`;
  const ticks = [0, 1, 2, 3].map((i) => (max / 3) * i);
  const grid = ticks.map((v) => `<div class="grid-line" style="bottom:${(v / max) * 100}%"></div>`).join('');
  const ylabels = ticks.map((v) => `<span style="top:${100 - (v / max) * 100}%">${nf(Math.round(v))}</span>`).join('');
  const cols = days.map((d) => `<div class="col"><div class="vline"></div><div class="pt" style="bottom:${(d.chests / max) * 100}%"></div>
    <div class="tip" style="bottom:${(d.chests / max) * 100}%"><b>${esc(count(d.chests, 'chest'))}</b>${esc(dayLabel(d.date))}${d.matches ? ` · ${esc(count(d.matches, 'match'))}` : ''}</div></div>`).join('');
  const labelEvery = Math.ceil(n / 6);
  const xl = days.filter((_, i) => i % labelEvery === 0 || i === n - 1).map((d, i, arr) => `<span>${esc(i === arr.length - 1 ? t('today') : date(d.date, { day: 'numeric', month: 'short' }))}</span>`).join('');
  return `<div class="area"><div class="yl">${ylabels}</div><div class="plot">${grid}
    <svg viewBox="0 0 1000 100" preserveAspectRatio="none" aria-hidden="true"><defs>
      <linearGradient id="ga" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#c6ff3d" stop-opacity=".35"/><stop offset=".6" stop-color="#3ee6ff" stop-opacity=".1"/><stop offset="1" stop-color="#3ee6ff" stop-opacity="0"/></linearGradient>
      <linearGradient id="gl" x1="0" y1="0" x2="1" y2="0"><stop offset="0" stop-color="#3ee6ff"/><stop offset="1" stop-color="#c6ff3d"/></linearGradient></defs>
    <path d="${area}" fill="url(#ga)"/><path class="line" d="${line}" fill="none" stroke="url(#gl)" stroke-width="3" vector-effect="non-scaling-stroke"/></svg>
    <div class="cols">${cols}</div></div><div class="xl">${xl}</div></div>`;
}

function donut(items, totalLabel) {
  const total = items.reduce((a, x) => a + x.count, 0);
  if (!total) return empty(t('noMatches'));
  const r = 60;
  const c = 2 * Math.PI * r;
  let offset = 0;
  const segs = items.map((x) => {
    const len = (x.count / total) * c;
    const gap = items.length > 1 ? Math.min(3, len / 2) : 0;
    const s = `<circle cx="80" cy="80" r="${r}" fill="none" stroke="var(${RARITY_VAR[rarityOf(x.key)]})" stroke-width="18" stroke-dasharray="${Math.max(0, len - gap)} ${c}" stroke-dashoffset="${-offset}"/>`;
    offset += len;
    return s;
  }).join('');
  const legend = items.map((x) => `<div class="it r-${rarityOf(x.key)}"><span class="legend-dot"></span><span class="nm">${esc(x.name)}</span><span class="v num">${nf(x.count)}</span><span class="p">${pctf(x.count / total)}</span></div>`).join('');
  return `<div class="donut-wrap"><div class="donut"><svg viewBox="0 0 160 160"><circle cx="80" cy="80" r="${r}" fill="none" stroke="var(--item)" stroke-width="18"/>${segs}</svg>
    <div class="center"><b class="num">${nf(total)}</b><span>${esc(totalLabel)}</span></div></div><div class="legend">${legend}</div></div>`;
}

function matchCard(m, cls, title, compact = false) {
  if (!m) return card(cls, title, empty(t('noMatchYet'), t('noMatchesHint')));
  const kinds = m.kinds.slice().sort((a, b) => b.count - a.count);
  const total = kinds.reduce((a, k) => a + k.count, 0) || 1;
  const stack = kinds.map((k) => `<i class="r-${rarityOf(k.key)}" style="width:${(k.count / total) * 100}%"></i>`).join('');
  const legend = kinds.map((k) => `<div class="it r-${rarityOf(k.key)}"><span class="legend-dot"></span><span class="nm">${esc(k.name)}</span><span class="v num">${nf(k.count)}</span><span class="p">${pctf(k.count / total)}</span></div>`).join('');
  const fact = (ic, k, v) => `<div class="fact"><div class="k">${icon(ic)}${esc(k)}</div><div class="v num">${v}</div></div>`;
  const tr = m.treasures.map((x) => `<span class="badge b-violet">${icon('star')}${esc(x.name)}</span>`).join(' ');
  return card(`${cls} match-card cq`, title, `
    <div class="row wrap" style="gap:8px 16px">${placeBadge(m)}<span class="chip">${icon('matches')}${esc(m.mode)}</span></div>
    <div class="big num" style="margin:16px 0">${nf(m.chests)}<small>${esc(t('chestsWord'))}</small></div>
    ${compact ? `<div class="faint small">${[m.kills != null ? count(m.kills, 'elim') : null, m.durationSeconds != null ? duration(m.durationSeconds) : null, m.destroyed ? t('destroyedShort', nf(m.destroyed)) : null].filter(Boolean).map(esc).join(' · ')}</div>` : ''}
    <div class="facts hide-tiny${compact ? ' hidden' : ''}">${fact('crosshair', t('eliminations'), m.kills != null ? nf(m.kills) : '—')}${fact('clock', t('duration'), duration(m.durationSeconds))}</div>
    ${kinds.length && !compact ? `<div class="divider"></div><div class="stack">${stack}</div><div class="legend${kinds.length > 3 ? ' many' : ''}" style="margin-top:12px">${legend}</div>` : ''}
    ${tr && !compact ? `<div class="row wrap" style="gap:6px;margin-top:12px">${tr}</div>` : ''}
    ${compact ? '' : lootTimeline(m)}`, { hint: dateTime(m.when) });
}

// Looting over the match: chests per minute of the match clock (the replay's, like the times of the treasures), the
// first chest and the chests of the first 5 minutes (shaded). Only for records that know when each chest was opened.
function lootTimeline(m) {
  const ts = m.chestTimes;
  if (!ts || !ts.length) return '';
  const minutes = Math.max(1, Math.ceil(Math.max(m.durationSeconds || 0, ts[ts.length - 1] + 1) / 60));
  const per = Array(minutes).fill(0);
  ts.forEach((s) => { per[Math.min(minutes - 1, Math.floor(s / 60))]++; });
  const max = Math.max(...per);
  const early = ts.filter((s) => s < 300).length;
  const bars = per.map((n, i) => `<i${n === max ? ' class="peak"' : ''} style="height:${n ? Math.max(10, (n / max) * 100).toFixed(0) : 0}%" title="${esc(`${i}–${i + 1} min · ${count(n, 'chest')}`)}"></i>`).join('');
  const step = minutes > 20 ? 10 : 5;
  const ticks = [];
  for (let k = 0; (minutes - k) / minutes >= 0.12; k += step) ticks.push(`<span style="left:${(k / minutes) * 100}%">${k}</span>`);
  ticks.push(`<span class="end">${minutes} min</span>`);
  return `<div class="divider"></div><div class="tl">
    <div class="tl-h"><span class="tl-t">${esc(t('tlTitle'))}</span>
      <span class="tag">${icon('clock')}${esc(t('tlFirst', duration(ts[0])))}</span>
      <span class="tag lime">${icon('bolt')}${esc(t('tlEarly', count(early, 'chest')))}</span></div>
    <div class="tl-plot"><b class="tl-early" style="width:${Math.min(100, (5 / minutes) * 100)}%"></b><div class="tl-bars">${bars}</div></div>
    <div class="tl-x">${ticks.join('')}</div></div>`;
}

// ---- Pages --------------------------------------------------------------------------------------------
// Home: three numbers above the 3D scene (a miner digging towards the pirate token), nothing else.
function home() {
  const s = S.stats;
  const e = S.estimate;
  const withEst = e.configured && e.include;

  const played = s.minutesPlayed ? `<span class="tag">${icon('clock')}${esc(t('hudPlayed', hoursText(s.minutesPlayed / 60)))}</span>` : '';
  const oldBtn = e.configured
    ? `<button class="btn btn-sm" data-nav="estimate">${icon('estimate')}${esc(t('oldIncluded', nf(e.estimated)))}</button>`
    : `<button class="btn btn-sm" data-nav="estimate">${icon('estimate')}${esc(t('importOld'))}</button>`;
  const total = `<section class="hud hud-total"><div class="hud-label"><img class="ico-img" src="/img/chest-pirate.webp" alt="">${esc(t('hudChests'))}</div>
    <div class="hud-big num" id="kpi-total">${withEst ? '≈' + nf(e.totalWithEstimate) : nf(s.total)}</div>
    <div class="hud-sub">${s.lastChests != null ? `<span class="tag lime">${icon('up')}${esc(t('hudLast', nf(s.lastChests)))}</span>` : ''}<span class="tag">${icon('bolt')}${esc(t('hudSession', count(s.session, 'chest')))}</span>${played}</div>
    <div class="hud-actions">${oldBtn}</div></section>`;

  const h = S.hunt;
  const target = huntTarget(h);
  const since = date(h.since, { day: 'numeric', month: 'long' });
  // Goal chips: the pirate token first and bigger, then the other treasures and "auto"
  const order = ['odds_pirate', 'odds_gold', 'odds_vehicle', 'odds_pickaxe', 'odds_jam', 'odds_loading', 'auto'];
  const chips = order.map((k) => {
    const o = h.odds.find((x) => x.key === k);
    const on = ui.goal === k;
    const label = k === 'auto' ? t('goalAuto') : t('short_' + k);
    const title = o ? `${t(o.key)} · 1 / ${nf(o.odds)}` : t('goalAutoHint');
    return `<button class="goal${k === 'odds_pirate' ? ' featured' : ''}${on ? ' on' : ''}" data-goal="${k}" title="${esc(title)}">${k === 'odds_pirate' ? '<img class="ico-img" src="/img/token.webp" alt="">' : ''}<span>${esc(label)}</span>${o ? `<b>${esc(pctx(dropChance(h.chests, o.odds)))}</b>` : ''}</button>`;
  }).join('');
  const heading = ui.goal === 'auto' ? t('huntNext', t(target.key)) : t('goalTitle', t(target.key));
  const token = `<section class="hud hud-token"><div class="hud-label">${target.key === 'odds_pirate' ? '<img class="ico-img" src="/img/token.webp" alt="">' : icon('star')}${esc(heading)}</div>
    <div class="row" style="align-items:baseline;gap:12px"><div class="hud-big num accent">${esc(pctx(dropChance(h.chests, target.odds)))}</div><div class="hud-note">${esc(t('huntChance'))}</div></div>
    <div class="dig"><i style="width:${Math.max(1.5, Math.min(1, h.chests / target.odds) * 100).toFixed(1)}%"></i></div>
    <div class="hud-foot">${esc(t('tokenDug', nf(h.chests), nf(target.odds), since))}</div>
    <div class="goals" role="group" aria-label="${esc(t('goalPick'))}">${chips}</div></section>`;

  let last;
  if (S.live.inProgress) {
    last = `<section class="hud hud-last live"><div class="hud-label"><span class="dot live"></span>${esc(t('liveTitle'))}</div>
      <div class="hud-big num">${S.live.chests != null ? nf(S.live.chests) : '…'}</div><div class="hud-sub"><span>${esc(t('hudLive'))}</span></div></section>`;
  } else {
    const m = S.matches[0];
    last = `<section class="hud hud-last"><div class="hud-label">${icon('clock')}${esc(t('lastMatch'))}</div>
      ${m ? `<div class="hud-big num">${nf(m.chests)}</div><div class="hud-sub">${placeBadge(m)}<span class="tag">${icon('matches')}${esc(m.mode)}</span>${m.durationSeconds != null ? `<span class="tag">${icon('clock')}${duration(m.durationSeconds)}</span>` : ''}</div>
      <div class="hud-actions"><button class="btn btn-sm" data-goto="${esc(m.id)}">${icon('right')}${esc(t('seeDetail'))}</button></div>` : `<div class="hud-sub" style="margin-top:12px"><span>${esc(t('noMatchYet'))}</span></div>`}</section>`;
  }

  return `<div class="page home-page">${total}${token}${last}<div class="stage"></div>${overlayDock()}
    <button class="share-fab" data-act="share" title="${esc(t('shareTitle'))}">${icon('camera')}<span>${esc(t('share'))}</span></button></div>`;
}

function matches() {
  if (!S.matches.length) return `<div class="page" style="grid-template-rows:minmax(0,1fr)">${card('s12', t('page_matches'), empty(t('noMatches'), t('noMatchesHint')))}</div>`;
  const modes = [...new Set(S.matches.map((m) => m.mode))];
  const list = ui.mode ? S.matches.filter((m) => m.mode === ui.mode) : S.matches;
  const p = pageOf('matches', list.length);
  if (!list.some((m) => m.id === ui.selected)) ui.selected = list[p.start]?.id ?? null;
  const filter = modes.length > 1
    ? `<select id="mode-filter" data-change="mode" aria-label="${esc(t('mode'))}"><option value="">${esc(t('allModes'))}</option>${modes.map((m) => `<option${m === ui.mode ? ' selected' : ''}>${esc(m)}</option>`).join('')}</select>`
    : '';
  const rows = list.slice(p.start, p.end).map((m) => `<tr data-select="${esc(m.id)}" class="${m.id === ui.selected ? 'sel' : ''}">
    <td>${esc(dateTime(m.when))}</td><td>${esc(m.mode)}</td><td>${placeBadge(m)}</td><td class="r strong">${nf(m.chests)}</td>
    <td>${m.treasures.map((x) => `<span class="star${x.golden ? ' gold' : ''}" title="${esc(x.name)}">${icon('star').replace('<svg', '<svg style="width:15px;height:15px;display:inline-block"')}</span>`).join(' ')}</td></tr>`).join('');
  const table = card('s8', t('allMatches'), `<div class="fit" data-fit="matches" data-row="45" data-extra="37"><table class="tbl">
    <colgroup><col style="width:22%"><col><col style="width:16%"><col style="width:12%"><col style="width:12%"></colgroup>
    <thead><tr><th>${esc(t('date'))}</th><th>${esc(t('mode'))}</th><th>${esc(t('placement'))}</th><th class="r">${esc(t('chests'))}</th><th>${esc(t('treasure'))}</th></tr></thead>
    <tbody>${rows}</tbody></table></div>${pager('matches', list.length)}`, { hint: !ui.mode && S.stats.matches > S.matches.length ? t('latestOf', nf(S.matches.length), nf(S.stats.matches)) : count(list.length, 'match'), actions: filter });
  return `<div class="page" style="grid-template-rows:minmax(0,1fr)">${table}${matchCard(list.find((m) => m.id === ui.selected), 's4', t('matchDetail'))}</div>`;
}

function stats() {
  const s = S.stats;
  // Nothing counted yet: one friendly message instead of zeros, an empty chart and blank tiles
  if (!s.matches) {
    return `<div class="page" style="grid-template-rows:minmax(0,1fr)">${card('s12', '', `<div class="empty-hero"><img src="/img/chest-pirate.webp" alt="">
      <h2>${esc(t('statsEmpty'))}</h2><p>${esc(t('statsEmptyHint'))}</p></div>`)}</div>`;
  }
  const winRate = s.matches ? s.wins / s.matches : 0;
  const kpis = [
    kpi('s3', 'target', 't-blue', t('kpiAverage'), nf1(s.average), `<span>${esc(t('averageDetail', count(s.matches, 'match')))}</span>`),
    kpi('s3', 'trophy', 't-gold', t('best'), nf(s.best), `<span>${esc(t('bestDetail'))}</span>`),
    kpi('s3', 'calendar', 't-green', t('bestDay'), nf(s.bestDay), `<span>${esc(s.bestDayDate ? dayLabel(s.bestDayDate) : '—')}</span>`),
    kpi('s3', 'star', 't-violet', t('wins'), nf(s.wins), `<span>${esc(pctf(winRate))} ${esc(t('winRateShort'))}</span>`),
  ].join('');

  const days = S.perDay;
  const sum = (arr) => arr.reduce((a, d) => a + d.chests, 0);
  const range = ui.range;
  const shown = days.slice(-range);
  const seg = `<div class="seg" role="group">${[7, 30].map((rg) => `<button class="${range === rg ? 'on' : ''}" data-range="${rg}">${esc(t('days' + rg))}</button>`).join('')}</div>`;
  const chart = card('s8 cq', t('perDay'), areaChart(shown), { hint: t('periodTotal', count(sum(shown), 'chest')), actions: seg });

  const kinds = card('s4 cq', t('byKind'), S.byKind.length ? donut(S.byKind, t('chestsWord')) : empty(t('noMatches')));

  return `<div class="page stats-page" style="grid-template-rows:auto minmax(0,1fr) minmax(0,1fr)">${kpis}${chart}${kinds}${lootCard('s8 cq loot-card')}${milestonesCard('s4 cq')}</div>`;
}

// Milestones: for each counter, the last round number reached (ticked) and the way to the next one.
function milestonesCard(cls) {
  const label = { chests: t('msChests'), best: t('msBest'), matches: t('msMatches'), treasures: t('msTreasures') };
  const ic = { chests: 'chest', best: 'trophy', matches: 'matches', treasures: 'star' };
  const tiles = (S.milestones || []).map((m) => {
    const p = m.next ? Math.min(1, m.value / m.next) : 1;
    return `<div class="ms${p >= 0.9 ? ' near' : ''}"><div class="ms-k">${icon(ic[m.key])}<span>${esc(label[m.key])}</span>${m.reached ? `<span class="ms-done" title="${esc(t('msReached', nf(m.reached)))}">${icon('check')}${esc(nf(m.reached))}</span>` : ''}</div>
      <div class="ms-v num"><b>${esc(nf(m.value))}</b><span>${esc(m.next ? `/ ${nf(m.next)}` : t('msAll'))}</span></div>
      <div class="ms-bar"><i style="width:${(p * 100).toFixed(1)}%"></i></div></div>`;
  }).join('');
  return card(cls, t('msTitle'), `<div class="ms-grid">${tiles}</div>`, { hint: t('msHint') });
}

// Escaped text whose {0}, {1}… are replaced by bits of HTML (numbers set in bold, for instance).
const fillHtml = (text, ...html) => html.reduce((s, h, i) => s.split(`{${i}}`).join(h), esc(text));
// Percent with a no-break space (French and Spanish "18 %" must not break).
const pctNb = (v) => pct(v).replace(' ', ' ');

// Do chests help you win? One row per range of chests opened: the bar grows with a better placement (top 18 % fills
// 82 %), then wins and eliminations; below, the two ends compared in plain words.
function lootCard(cls) {
  const b = S.chestsVsPlacement || [];
  if (b.length < 2) return card(cls, t('lootTitle'), empty(t('lootEmpty'), t('lootEmptyHint')));
  const range = (x) => (x.to == null ? `${nf(x.from)}+` : `${nf(x.from)}–${nf(x.to)}`);
  const top = (x) => pctNb(Math.max(1, Math.round(x.averageTop * 100)));
  const best = Math.min(...b.map((x) => x.averageTop));
  const rows = b.map((x) => `<div class="loot-row${x.averageTop === best ? ' best' : ''}"><span class="loot-k num">${esc(range(x))}</span>
    <div class="loot-bar"><i style="width:${Math.max(2, (1 - x.averageTop) * 100).toFixed(1)}%"></i></div>
    <b class="loot-top num" title="${esc(t('lootPlaceHint'))}">${esc(t('lootTop', top(x)))}</b>
    <span class="loot-meta">${esc([count(x.matches, 'match'), t('lootWins', pctf(x.winRate)), x.averageKills != null ? t('lootKills', nf1(x.averageKills)) : null].filter(Boolean).join(' · '))}</span></div>`).join('');
  const hi = b[b.length - 1];
  const lo = b[0];
  const low = lo.from === 0 && lo.to != null ? t('lootLowUnder', nf(lo.to + 1)) : t('lootLowRange', range(lo));
  const say = fillHtml(t('lootTakeaway'), `<b>${esc(range(hi))}</b>`, `<b>${esc(top(hi))}</b>`, `<b>${esc(top(lo))}</b>`, esc(low));
  return card(cls, t('lootTitle'), `<div class="loot">${rows}</div><div class="loot-say">${icon('bolt')}<span>${say}</span></div>`, { hint: t('lootHint') });
}

function estimate() {
  const e = S.estimate;
  const val = (k) => draft('est-' + k, e.matches[k] ?? '');
  const field = (k, label) => `<label class="field">${esc(label)}<input type="number" min="0" id="est-${k}" data-draft value="${esc(val(k))}" data-enter="est-save" placeholder="0"></label>`;
  const form = card('s5 est-card', t('estTitle'), `<p class="faint est-intro">${esc(t('estIntro'))}</p>
    <div class="steps-list" title="${esc(t('estIntro'))}">
      <div class="step"><div class="n">1</div><div><h4>${esc(t('estStep1'))}</h4><p>${esc(t('estStep1Hint'))}</p>
        <div class="row"><input type="text" id="est-player" data-draft value="${esc(draft('est-player', e.player ?? ''))}" placeholder="${esc(t('epicName'))}" style="flex:1">
        ${button(`${icon('link')}${esc(t('openFortniteGg'))}`, 'data-act="open-gg"')}</div></div></div>
      <div class="step"><div class="n">2</div><div><h4>${esc(t('estStep2'))}</h4><p>${esc(t('estStep2Hint'))}</p>
        <div class="form-grid">${field('solo', 'Solo')}${field('duo', t('modeDuo'))}${field('trio', t('modeTrio'))}${field('squad', t('modeSquad'))}${field('other', t('modeOther'))}${field('total', t('modeTotal'))}</div>
        <div class="est-go">${button(esc(t('estCompute')), 'id="est-save" data-act="est-save"', 'btn-primary')}</div></div></div>
      <div class="step"><div class="n">${icon('key')}</div><div><h4>${esc(t('estAuto'))}</h4><p>${esc(t('estAutoHint'))} <a href="#" data-open="fortniteapi">${esc(t('getKey'))}</a></p>
        <div class="row"><input type="password" id="est-key" data-draft value="${esc(draft('est-key', ''))}" placeholder="${esc(e.hasApiKey ? t('keySaved') : 'xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx')}" style="flex:1" autocomplete="off" data-enter="est-fetch">
        ${button(esc(e.fetching ? t('fetching') : t('fetch')), `id="est-fetch" data-act="est-fetch"${e.fetching ? ' disabled' : ''}`)}</div>
        ${e.error ? `<div class="err">${esc(e.error)}</div>` : ''}</div></div>
    </div>`);

  let result;
  if (!e.configured) {
    result = card('s7', t('estResult'), empty(t('estNone'), t('estNoneHint')));
  } else {
    const fact = (ic, k, v) => `<div class="fact"><div class="k">${icon(ic)}${esc(k)}</div><div class="v num">${v}</div></div>`;
    const rows = e.parts.map((p) => `<tr><td>${esc(p.mode)}</td><td class="r">${nf(p.played)}</td><td class="r">${nf(p.tracked)}</td><td class="r">${nf1(p.average)}</td><td class="r strong">${nf(p.estimated)}</td></tr>`).join('');
    result = card('s7 est-card', t('estResult'), `
      <div class="est-hero"><div class="big num">≈${nf(e.estimated)}</div><span class="badge b-gold" style="margin-bottom:8px">${esc(t('estRange', nf(e.low), nf(e.high)))}</span></div>
      <div class="faint" style="margin-top:6px">${esc(t('estChestsLabel', count(e.untracked, 'match')))}</div>
      <div class="facts est-facts">${fact('matches', t('estPlayed'), nf(e.played))}${fact('check', t('estTracked'), nf(e.tracked))}${fact('chest', t('estGrandTotal'), '≈' + nf(e.totalWithEstimate))}</div>
      <div class="est-table"><table class="tbl static"><thead><tr><th>${esc(t('mode'))}</th><th class="r">${esc(t('estPlayedShort'))}</th><th class="r">${esc(t('estTrackedShort'))}</th><th class="r">${esc(t('average'))}</th><th class="r">${esc(t('estEstimatedShort'))}</th></tr></thead><tbody>${rows}</tbody></table></div>
      <div class="divider"></div>
      <div class="setting" style="padding:0 0 12px"><div class="what"><b>${esc(t('estInclude'))}</b><span>${esc(t('estIncludeHint'))}</span></div><label class="switch"><input type="checkbox" id="est-include" data-change="est-include"${e.include ? ' checked' : ''}><span></span></label></div>
      <div class="row between"><span class="faint small">${esc(t('estSource', e.source === 'api' ? 'fortnite-api.com' : 'fortnite.gg', e.player ?? '—', e.updatedUtc ? dateTime(e.updatedUtc) : '—'))}</span>${button(esc(t('estClear')), 'data-act="est-clear"', 'btn-sm btn-ghost btn-danger')}</div>
      <p class="faint small est-method">${esc(t('estMethod'))}</p>`);
  }
  return `<div class="page" style="grid-template-rows:minmax(0,1fr)">${form}${result}</div>`;
}

// Drop rates announced by Epic, with the player's chance and the expected count from the chests of the season.
function oddsCard() {
  const h = S.hunt;
  const since = date(h.since, { day: 'numeric', month: 'long' });
  const rows = h.odds.map((o) => `<tr><td>${esc(t(o.key))}</td><td class="r num">${nf(o.odds)}</td>
    <td class="r">${o.hourBoost ? `<span class="badge b-gold">×${esc(nf1(o.hourBoost).replace(/[.,]0$/, ''))}</span>` : '<span class="faint">—</span>'}</td>
    <td class="r strong">${esc(pctx(dropChance(h.chests, o.odds)))}</td><td class="r num">${esc(nfx(o.expected ?? h.chests / o.odds))}</td></tr>`).join('');
  const luck = `<div class="luck-col"><div class="luck-h">${esc(t('luckTitle'))}</div>
    ${h.combined ? luckMeter('star', t(single(h.gizmos) ? 'foundTreasuresOne' : 'foundTreasuresMany', nf(h.gizmos), nf1(h.gizmosExpected)), h.gizmoLuck) + `<p class="faint small" style="margin:0">${esc(t('luckCombinedHint'))}</p>`
      : luckMeter('star', t(single(h.gizmos) ? 'foundGizmosOne' : 'foundGizmosMany', nf(h.gizmos), nf1(h.gizmosExpected)), h.gizmoLuck)
        + luckMeter('treasures', t(single(h.rewards) ? 'foundRewardsOne' : 'foundRewardsMany', nf(h.rewards), nf1(h.rewardsExpected)), h.rewardLuck)}</div>`;
  const note = t('oddsNote', nf(h.chests), since);
  return card('s12 odds-card', t('oddsTitle'), `<div class="odds-grid"><table class="tbl static compact" title="${esc(note)}"><colgroup><col><col style="width:16%"><col style="width:16%"><col style="width:16%"><col style="width:12%"></colgroup>
    <thead><tr><th>${esc(t('treasure'))}</th><th class="r">${esc(t('oddsPer'))}</th><th class="r">${esc(t('oddsHours'))}</th><th class="r">${esc(t('oddsChance'))}</th><th class="r">${esc(t('oddsExpected'))}</th></tr></thead>
    <tbody>${rows}</tbody></table>${luck}</div>
    <div class="faint small odds-note">${esc(note)}</div>`, { hint: t('oddsHint') });
}

// Found vs expected, then "luckier than X % of players" (Poisson rank, computed by the counter) on a gauge whose middle
// is the average luck. Nothing but the count while too few drops were expected to tell.
function luckMeter(ic, label, luck) {
  const sentence = luck == null ? '' : fillHtml(t('luckBetter'), `<b class="num">${esc(pctNb(luck))}</b>`);
  return `<div class="luck"${luck == null ? '' : ` title="${esc(t('luckHint'))}"`}><div class="luck-k">${icon(ic)}<span>${esc(label)}</span></div>
    ${luck == null ? '' : `<div class="luck-gauge${luck >= 50 ? ' good' : ''}"><i style="width:${luck}%"></i><s></s><b style="left:${luck}%"></b></div><div class="luck-v">${sentence}</div>`}</div>`;
}

function treasures() {
  const p = pageOf('treasures', S.treasures.length);
  const rows = S.treasures.slice(p.start, p.end).map((x) => {
    const editing = x.id && ui.editPrize === x.id;
    const main = editing
      ? `<div class="row" style="gap:8px"><input type="text" id="prize-edit" data-draft value="${esc(draft('prize-edit', x.name))}" placeholder="${esc(t('prizePlaceholder'))}" style="flex:1;height:34px" data-enter="prize-save">
        ${button(esc(t('save')), `id="prize-save" data-prize="${esc(x.id)}" data-input="prize-edit"`, 'btn-sm btn-primary')}${button(esc(t('cancel')), 'data-rename=""', 'btn-sm btn-ghost')}</div>`
      : `<b>${esc(x.name)}</b><span>${esc(dateTime(x.when))} · ${esc(x.source)}</span>`;
    const unnamed = x.golden && S.prizes.some((pz) => pz.id === x.id && pz.unnamed);
    const rename = x.id && !editing ? `<button class="btn btn-sm btn-ghost" data-rename="${esc(x.id)}" title="${esc(t('renameHint'))}">${icon('edit')}${esc(unnamed ? t('nameIt') : t('rename'))}</button>` : '';
    return `<div class="list-row"><div class="ico ${x.golden ? 't-gold' : 't-violet'}">${x.golden ? '<img class="ico-img" src="/img/chest-pirate-gold.webp" alt="">' : icon('star')}</div><div class="main-t">${main}</div>${rename}
      ${x.golden ? `<span class="badge b-gold">${esc(t('goldenChest'))}</span>` : `<span class="badge b-violet">${esc(t('gizmo'))}</span>`}</div>`;
  }).join('');
  const list = card('s12', t('treasuresTitle'), S.treasures.length
    ? `<div class="fit" data-fit="treasures" data-row="56">${rows}</div>${pager('treasures', S.treasures.length)}`
    : empty(t('noTreasures'), t('treasureLegend')), { hint: count(S.treasures.length, 'treasure') });
  return `<div class="page treasures-page" style="grid-template-rows:auto minmax(200px,1fr)">${oddsCard()}${list}</div>`;
}

// Preview of the in-game overlay: the very picture the counter draws for the game window (and OBS)
function ovPreview(mini = false) {
  return `<img class="hud-img${mini ? ' mini' : ''}" src="/hud.png?v=${esc(S.overlay.hud)}" srcset="/hud.png?v=${esc(S.overlay.hud)} 2x" alt="" draggable="false">`;
}

// Full HUD or one compact line
function ovStyleSeg() {
  const st = S.overlay.style || 'full';
  return `<div class="seg" role="group" aria-label="${esc(t('overlayStyle'))}">${['full', 'compact'].map((k) => `<button class="${st === k ? 'on' : ''}" data-ovstyle="${k}">${esc(t('overlayStyle_' + k))}</button>`).join('')}</div>`;
}

// Home: the overlay is a main feature, so its switches sit right on the home page (bottom left)
function overlayDock() {
  const o = S.overlay;
  if (!o.available) return '';
  const on = o.running && o.visible;
  const sw = (id, change, checked, label, disabled) => `<label class="dock-sw"><span>${esc(label)}</span><span class="switch"><input type="checkbox" id="${id}" data-change="${change}"${checked ? ' checked' : ''}${disabled ? ' disabled' : ''}><span></span></span></label>`;
  return `<section class="ov-dock">
    <div class="dock-hud">${ovPreview(true)}</div>
    <div class="dock-main">
      <div class="dock-title">${icon('overlay')}${esc(t('overlayDock'))}<span class="dot ${on ? '' : 'off'}"></span></div>
      <div class="dock-sws">${sw('dock-ov-visible', 'ov-visible', on, t('overlayShowShort2'), false)}${sw('dock-ov-locked', 'ov-locked', o.locked, t('overlayLockShort'), !o.running)}</div>
      <div class="dock-style">${ovStyleSeg()}</div>
      <div class="dock-keys">${o.toggleHotkey ? `<span class="kbd">${esc(t('keyToggle'))}</span>` : ''}${o.lockHotkey ? `<span class="kbd">${esc(t('keyLock'))}</span>` : ''}</div>
    </div>
    <button class="icon-btn" data-nav="overlay" title="${esc(t('overlaySettings'))}" aria-label="${esc(t('overlaySettings'))}">${icon('settings')}</button>
  </section>`;
}

function overlay() {
  const o = S.overlay;
  const sw = (id, change, checked, label, hint, disabled) => `<div class="setting"><div class="what"><b>${esc(label)}</b><span>${esc(hint)}</span></div><label class="switch"><input type="checkbox" id="${id}" data-change="${change}"${checked ? ' checked' : ''}${disabled ? ' disabled' : ''}><span></span></label></div>`;
  const sel = (id, values, current) => `<select id="${id}" data-change="${id}">${values.map((v) => `<option value="${v}"${v === current ? ' selected' : ''}>${pct(v)}</option>`).join('')}</select>`;
  const key = (combo, ok, text) => `<div class="row between" style="padding:8px 0"><span class="muted">${esc(text)}</span>${ok ? `<span class="kbd key">${esc(combo)}</span>` : `<span class="row" style="gap:8px"><span class="faint small">${esc(t('hotkeyUnavailable'))}</span><span class="kbd key" style="opacity:.5;text-decoration:line-through">${esc(combo)}</span></span>`}</div>`;
  const controls = card('s7', t('overlay'), `
    ${sw('ov-visible', 'ov-visible', o.running && o.visible, t('overlayShow'), t('overlayShowHint'), false)}
    ${sw('ov-locked', 'ov-locked', o.locked, t('overlayLock'), t('overlayLockHint'), !o.running)}
    <div class="setting"><div class="what"><b>${esc(t('overlayStyle'))}</b><span>${esc(t('overlayStyleHint'))}</span></div>${ovStyleSeg()}</div>
    <div class="setting"><div class="what"><b>${esc(t('overlaySize'))}</b></div>${sel('ov-size', S.settings.sizes, o.size)}</div>
    <div class="setting"><div class="what"><b>${esc(t('overlayOpacity'))}</b></div>${sel('ov-opacity', S.settings.opacities, o.opacity)}</div>
    <div class="section-title">${esc(t('hotkeys'))}</div>
    ${key(t('keyToggle'), o.toggleHotkey, t('hotkeyToggleText'))}${key(t('keyLock'), o.lockHotkey, t('hotkeyLockText'))}`);
  const previewCard = card('s5', t('overlayPreview'), `<div class="ov-stage">${ovPreview()}</div>
    <div class="section-title">${esc(t('obs'))}</div><div class="row"><input type="text" id="obs-path" readonly value="${esc(o.obsPath)}" style="flex:1;font-size:12px">${button(`${icon('copy')}${esc(t('copy'))}`, 'data-copy="obs-path"')}</div>`);
  return `<div class="page" style="grid-template-rows:minmax(0,1fr)">${controls}${previewCard}</div>`;
}

function journal() {
  const all = S.activity.slice().reverse();
  const p = pageOf('journal', all.length);
  const rows = all.slice(p.start, p.end).map((e) => `<div class="feed-row"><span class="tm">${esc(time(e.time))}</span><span class="lv ${esc(e.level)}"></span><span class="x" title="${esc(e.text)}">${esc(e.text)}</span></div>`).join('');
  return `<div class="page" style="grid-template-rows:minmax(0,1fr)">${card('s12', t('activity'), all.length ? `<div class="fit" data-fit="journal" data-row="39">${rows}</div>${pager('journal', all.length)}` : empty(t('noActivity')), { hint: t('journalHint') })}</div>`;
}

function settings() {
  const s = S.settings;
  const sw = (id, checked, label, hint, disabled) => `<div class="setting"><div class="what"><b>${esc(label)}</b>${hint ? `<span>${esc(hint)}</span>` : ''}</div><label class="switch"><input type="checkbox" id="${id}" data-change="${id}"${checked ? ' checked' : ''}${disabled ? ' disabled' : ''}><span></span></label></div>`;
  const general = card('s4', t('general'), `
    <div class="setting"><div class="what"><b>${esc(t('language'))}</b></div><select id="set-lang" data-change="set-lang">${s.languages.map((l) => `<option value="${l.code}"${l.code === s.language ? ' selected' : ''}>${esc(l.name)}</option>`).join('')}</select></div>
    ${sw('set-openui', s.openUiOnStart, t('openUiOnStart'), t('openUiOnStartHint'))}
    ${sw('set-fortnite', s.startWithFortnite, t('startWithFortnite'), s.canStartWithFortnite ? t('startWithFortniteHint') : t('startWithFortniteUnavailable'), !s.canStartWithFortnite)}
    ${S.overlay.available ? sw('set-overlay', s.showOverlay, t('overlayEnabled'), t('overlayEnabledHint')) : ''}
    ${installRow(s)}`);
  const replays = card('s4', t('replays'), `
    <label class="field">${esc(t('demosFolder'))}<div class="row"><input type="text" id="set-demos" data-draft value="${esc(draft('set-demos', s.demosFolder))}" style="flex:1" data-enter="set-demos-save">
      ${button(icon('folder'), `data-open="demos" title="${esc(t('openFolder'))}" aria-label="${esc(t('openFolder'))}"`, 'btn-icon')}</div></label>
    <div class="row between" style="margin-top:10px"><span class="${S.app.demosExists ? 'ok' : 'ko'} small">${esc(S.app.demosExists ? t('folderFound') : t('folderMissing'))}</span>${button(esc(t('save')), 'id="set-demos-save" data-act="save-demos"', 'btn-sm')}</div>
    ${sw('set-archive', !!s.archiveFolder, t('archiveOn'), t('archiveHint'))}
    ${s.archiveFolder ? `<div class="row" style="gap:8px;margin-top:-4px"><input type="text" id="set-archive" data-draft value="${esc(draft('set-archive', s.archiveFolder))}" style="flex:1;font-size:12.5px" data-enter="set-archive-save">
      ${button(icon('folder'), `data-open="archive" title="${esc(t('openFolder'))}" aria-label="${esc(t('openFolder'))}"`, 'btn-icon')}${button(esc(t('save')), 'id="set-archive-save" data-act="save-archive"', 'btn-sm')}</div>
    <p class="faint small" style="margin:6px 0 0">${esc(t('archiveCount', count(s.archiveCount, 'replay')))}</p>` : ''}
    <div class="section-title">${esc(t('data'))}</div>
    <div class="row wrap" style="gap:8px">${button(`${icon('folder')}${esc(t('openData'))}`, 'data-open="data"', 'btn-sm')}${button(`${icon('alert')}${esc(t('openErrors'))}`, 'data-open="errors"', 'btn-sm')}</div>
    <p class="faint small" style="margin:12px 0 0;word-break:break-all">${esc(S.app.dataFolder)}</p>`);
  const u = S.update;
  let updates;
  if (!u.available) {
    updates = `<div class="setting"><div class="what"><b>${esc(t('updates'))}</b><span>${esc(t('updatesUnavailable'))}</span></div></div>`;
  } else {
    const st = u.installing ? t('installing') : u.checking ? t('checking') : u.foundVersion ? t('updateFound', u.foundVersion) : u.lastCheck === 'upToDate' ? t('upToDate') : u.lastCheck === 'unreachable' ? t('unreachable') : '';
    updates = `<div class="setting"><div class="what"><b>${esc(t('updates'))}</b><span>${esc(t('updatesHint'))}</span></div>
      <select id="set-update" data-change="set-update">${['prompt', 'auto', 'off'].map((m) => `<option value="${m}"${m === u.mode ? ' selected' : ''}>${esc(t('upd_' + m))}</option>`).join('')}</select></div>
      <div class="setting"><div class="what"><span>${esc(st)}</span></div><div class="row" style="gap:8px">
      ${u.foundVersion ? button(esc(t('install', u.foundVersion)), `data-cmd="update/install"${u.installing ? ' disabled' : ''}`, 'btn-sm btn-primary') : ''}
      ${button(esc(t('checkNow')), `data-cmd="update/check"${u.checking || u.mode === 'off' ? ' disabled' : ''}`, 'btn-sm')}</div></div>`;
  }
  const about = card('s4', t('about'), `${updates}<div class="divider"></div>
    <div class="row" style="gap:12px"><img src="/img/relic-chest.webp" alt="" style="width:40px;height:40px"><div><b>Fortnite - Chest Tracker ${esc(S.app.version)}</b><div class="faint small"><a href="#" data-open="repo">GitHub</a> · <a href="#" data-open="issues">${esc(t('reportBug'))}</a> · <a href="/notices" target="_blank" rel="noopener">${esc(t('licenses'))}</a></div></div></div>
    <p class="faint small" style="margin:16px 0 0">${esc(t('notAffiliated'))}</p>
    <p class="faint small" style="margin:8px 0 0">${esc(t('credits'))}</p>`);
  return `<div class="page" style="grid-template-rows:minmax(0,1fr);align-items:start">${general}${replays}${about}</div>`;
}

// "Install on this PC": installed copy (Start menu, desktop, Windows' installed apps) or a button to install this one.
function installRow(s) {
  if (s.installed) {
    return `<div class="setting"><div class="what"><b>${esc(t('installedTitle'))}</b><span>${esc(t('installedHint'))}</span></div>${button(esc(t('uninstall')), 'data-act="uninstall"', 'btn-sm')}</div>`;
  }
  if (!s.canInstall) return '';
  return `<div class="setting"><div class="what"><b>${esc(t('installTitle'))}</b><span>${esc(t(s.installedElsewhere ? 'installElsewhereHint' : 'installHint'))}</span></div>${button(esc(t(s.installedElsewhere ? 'reinstallNow' : 'installNow')), 'data-act="install"', 'btn-sm btn-primary')}</div>`;
}

// ---- Setup wizard (first launch) -------------------------------------------------------------------
function modal(title, body, foot = '', steps = '') {
  return `<div class="backdrop"><div class="modal" role="dialog" aria-modal="true" aria-label="${esc(title)}">
    <header><h1>${esc(title)}</h1></header>${steps}<div class="body">${body}</div>${foot ? `<div class="foot">${foot}</div>` : ''}</div></div>`;
}

function setupSteps() {
  const w = ui.setup;
  return [0, 1, 2, 3, 4].filter((i) => (i !== 2 || w.check.replays > 0) && (i !== 3 || S.setup.canStartWithFortnite));
}

function setupModal() {
  if (!ui.setup) ui.setup = { step: 0, folder: S.setup.demosFolder, check: { exists: S.setup.demosExists, replays: S.setup.existingReplays }, analyze: true, fortnite: S.setup.canStartWithFortnite, install: S.setup.canInstall };
  const w = ui.setup;
  const steps = setupSteps();
  const pos = steps.indexOf(w.step);
  const bar = `<div class="steps">${steps.map((_, i) => `<i class="${i <= pos ? 'on' : ''}"></i>`).join('')}</div>`;
  const back = pos > 0 ? button(esc(t('back')), 'data-setup="back"', 'btn-ghost') : '';
  const next = button(esc(t('next')), 'data-setup="next"', 'btn-primary');
  const choice = (key, value, selected, title, hint) => `<button class="choice${selected ? ' sel' : ''}" data-setup-choice="${key}" data-value="${value}"><b>${esc(title)}</b>${hint ? `<span>${esc(hint)}</span>` : ''}</button>`;
  let body;
  let foot = back + next;
  switch (w.step) {
    case 0:
      body = `<div class="welcome"><img src="/img/relic-chest.webp" alt=""><div><b style="font-size:16px">${esc(t('setupWelcome'))}</b><div class="faint">${esc(t('setupIntro'))}</div></div></div>
        <div class="section-title">Langue · Language · Idioma</div><div class="choices">${S.settings.languages.map((l) => choice('lang', l.code, l.code === S.app.lang, l.name, '')).join('')}</div>`;
      foot = next;
      break;
    case 1:
      body = `<div class="muted">${esc(t('setupReplaysText'))}</div>
        <div class="row" style="margin-top:12px"><input type="text" id="setup-folder" data-draft value="${esc(draft('setup-folder', w.folder))}" style="flex:1" data-enter="setup-check">${button(esc(t('check')), 'id="setup-check" data-setup="check"')}</div>
        <div style="margin-top:8px" class="small ${w.check.exists ? 'ok' : 'ko'}">${esc(w.check.exists ? t('folderFoundCount', count(w.check.replays, 'replay')) : t('folderMissing'))}</div>
        <div class="callout"><b>${esc(t('important'))}</b> ${esc(t('setupReplaysImportant'))}</div>`;
      break;
    case 2:
      body = `<div class="muted">${esc(t('setupExistingText', count(w.check.replays, 'replay')))}</div><div class="choices">
        ${choice('analyze', 'yes', w.analyze, t('setupAnalyzeYes'), t('setupAnalyzeYesHint'))}${choice('analyze', 'no', !w.analyze, t('setupAnalyzeNo'), '')}</div>`;
      break;
    case 3:
      body = `<div class="muted">${esc(t('setupFortniteText'))}</div><div class="choices">
        ${choice('fortnite', 'yes', w.fortnite, t('setupFortniteYes'), t('setupFortniteYesHint'))}${choice('fortnite', 'no', !w.fortnite, t('setupFortniteNo'), t('setupFortniteNoHint'))}</div>`;
      break;
    default:
      body = `<div class="welcome"><img src="/img/relic-chest.webp" alt=""><div class="muted">${esc(t('setupDoneText'))}</div></div>
        <ul class="muted" style="margin:16px 0 0;padding-left:18px;display:grid;gap:8px;font-size:13px"><li>${esc(t('setupTipTray'))}</li><li>${esc(t('setupTipOverlay'))}</li><li>${esc(t('setupTipKeys'))}</li></ul>
        ${S.setup.canInstall ? `<label class="check"><input type="checkbox" id="setup-install" data-change="setup-install"${w.install ? ' checked' : ''}><span><b>${esc(t('setupInstall'))}</b><span>${esc(t('setupInstallHint'))}</span></span></label>` : ''}`;
      foot = back + button(esc(t('start')), 'data-setup="finish"', 'btn-primary');
  }
  const titles = [t('setupLanguage'), t('setupReplays'), t('setupExisting'), t('setupFortnite'), t('setupDone')];
  return modal(titles[Math.min(w.step, 4)], body, foot, bar);
}

function setupMove(dir) {
  const steps = setupSteps();
  const pos = Math.max(0, steps.indexOf(ui.setup.step));
  ui.setup.step = steps[Math.min(steps.length - 1, Math.max(0, pos + dir))];
  render();
}

async function setupCheckFolder() {
  const input = $('#setup-folder');
  if (input) ui.setup.folder = input.value.trim();
  const r = await cmd('setup/check-folder', { path: ui.setup.folder });
  if (r.ok && r.data) ui.setup.check = r.data;
  render();
}

// ---- Share card: the player's stats over a shot of the 3D scene, as a PNG to download or copy -------------
const CARD_W = 1600;
const CARD_H = 900;
const SCENE_H = 600;

// Asks the scene (scene.js) for a picture; null when there is no 3D (no WebGL, models not loaded).
function sceneShot(width, height, dug) {
  return new Promise((resolve) => {
    const timer = setTimeout(() => resolve(null), 4000);
    window.dispatchEvent(new CustomEvent('fct-snapshot', { detail: { width, height, dug, resolve: (url) => { clearTimeout(timer); resolve(url); } } }));
  });
}
const loadImage = (src) => new Promise((resolve, reject) => { const i = new Image(); i.onload = () => resolve(i); i.onerror = reject; i.src = src; });

let shareShots = {};                  // one 3D shot per goal (the tunnel is dug for that goal)
const cardTexts = {};                  // interface texts per language, for cards in another language
async function textsFor(lang) {
  if (lang === S.app.lang) return T;
  if (!cardTexts[lang]) cardTexts[lang] = await (await fetch('/api/texts?lang=' + encodeURIComponent(lang))).json();
  return cardTexts[lang];
}
// Switches texts and number/date formats to another language for a synchronous block, then puts them back.
function inLang(lang, texts, fn) {
  const keep = { T, lang: S.app.lang };
  T = texts;
  S.app.lang = lang;
  try { return fn(); } finally { T = keep.T; S.app.lang = keep.lang; }
}

async function makeCard(goal, lang = S.app.lang) {
  try { await document.fonts.load('48px Anton'); } catch { /* falls back to Impact */ }
  const texts = await textsFor(lang);
  const h0 = S.hunt;
  const aim = huntTarget(h0, goal);
  const shot = shareShots[goal] ?? (shareShots[goal] = await sceneShot(CARD_W, SCENE_H, Math.min(1, h0.chests / aim.odds)));
  let shotImage = null;
  if (shot) { try { shotImage = await loadImage(shot); } catch { /* card without the scene */ } }
  const art = {};
  for (const [k, src] of [['logo', '/img/relic-chest.webp'], ['token', '/img/token.webp'], ['chest', '/img/chest-pirate.webp']]) {
    try { art[k] = await loadImage(src); } catch { /* drawn without it */ }
  }
  const c = document.createElement('canvas');
  c.width = CARD_W;
  c.height = CARD_H;
  inLang(lang, texts, () => drawCard(c, goal, shotImage, art));
  return new Promise((resolve) => c.toBlob((blob) => resolve(blob), 'image/png'));
}

function drawCard(c, goal, shotImage, art = {}) {
  const g = c.getContext('2d');
  const display = (px) => `${px}px Anton, Impact, sans-serif`;
  const text = (px, weight = 600) => `${weight} ${px}px "Segoe UI Variable Text", "Segoe UI", sans-serif`;

  // Sky, then the scene in the lower part
  const sky = g.createLinearGradient(0, 0, 0, CARD_H);
  sky.addColorStop(0, '#071447');
  sky.addColorStop(0.34, '#0d2a86');
  sky.addColorStop(0.68, '#1752cf');
  sky.addColorStop(1, '#2d8ff0');
  g.fillStyle = sky;
  g.fillRect(0, 0, CARD_W, CARD_H);
  const glow = g.createRadialGradient(CARD_W / 2, CARD_H + 120, 50, CARD_W / 2, CARD_H + 120, 700);
  glow.addColorStop(0, 'rgba(80, 220, 255, .5)');
  glow.addColorStop(1, 'rgba(80, 220, 255, 0)');
  g.fillStyle = glow;
  g.fillRect(0, 0, CARD_W, CARD_H);
  if (shotImage) g.drawImage(shotImage, 0, CARD_H - SCENE_H, CARD_W, SCENE_H);

  // Header: name of the tool, player and date
  const s = S.stats;
  const e = S.estimate;
  const h = S.hunt;
  const target = huntTarget(h, goal);
  g.textBaseline = 'alphabetic';
  g.fillStyle = 'rgba(188, 208, 245, .9)';
  g.font = text(18, 700);
  const tx = art.logo ? 56 + 92 : 56;
  if (art.logo) g.drawImage(art.logo, 50, 30, 88, 88);
  g.fillText('FORTNITE', tx, 64);
  g.font = display(46);
  g.fillStyle = '#f6f3ff';
  g.fillText('CHEST', tx, 112);
  g.fillStyle = '#c6ff3d';
  g.fillText('TRACKER', tx + g.measureText('CHEST ').width, 112);
  const who = s.player || S.estimate.player || '';
  g.textAlign = 'right';
  g.fillStyle = '#f6f3ff';
  g.font = display(40);
  if (who) g.fillText(who.toUpperCase(), CARD_W - 56, 100);
  g.fillStyle = 'rgba(188, 208, 245, .9)';
  g.font = text(18);
  g.fillText(date(new Date().toISOString(), { day: 'numeric', month: 'long', year: 'numeric' }), CARD_W - 56, who ? 130 : 100);
  g.textAlign = 'left';

  // Stat tiles
  const withEst = e.configured && e.include;
  const tiles = [
    { label: t('hudChests'), value: withEst ? '≈' + nf(e.totalWithEstimate) : nf(s.total), accent: false, art: art.chest },
    { label: t('short_' + target.key), value: pctx(dropChance(h.chests, target.odds)), accent: true, sub: t('shareRate', nf(target.odds)), art: target.key === 'odds_pirate' ? art.token : null },
    { label: t('timePlayed'), value: s.minutesPlayed ? hoursText(s.minutesPlayed / 60) : '—', accent: false },
    { label: t('matches'), value: nf(s.matches), accent: false },
    { label: t('bestOneMatch'), value: nf(s.best), accent: false },
    { label: t('treasures'), value: nf(s.treasures), accent: false },
  ];
  const gap = 16;
  const tw = (CARD_W - 112 - gap * (tiles.length - 1)) / tiles.length;
  tiles.forEach((tile, i) => {
    const x = 56 + i * (tw + gap);
    const y = 160;
    g.fillStyle = 'rgba(8, 22, 74, .78)';
    g.strokeStyle = 'rgba(150, 190, 255, .22)';
    g.lineWidth = 1.5;
    g.beginPath();
    g.roundRect(x, y, tw, 138, 18);
    g.fill();
    g.stroke();
    g.fillStyle = 'rgba(188, 208, 245, .95)';
    g.font = display(19);
    let label = tile.label.toUpperCase();
    while (g.measureText(label).width > tw - 40 && label.length > 4) label = label.slice(0, -2) + '…';
    g.fillText(label, x + 20, y + 38);
    g.fillStyle = tile.accent ? '#c6ff3d' : '#f6f3ff';
    let size = 54;
    g.font = display(size);
    while (g.measureText(tile.value).width > tw - 40 && size > 24) { size -= 2; g.font = display(size); }
    g.fillText(tile.value, x + 20, y + 98);
    if (tile.art) g.drawImage(tile.art, x + tw - 64, y + 12, 52, 52);
    if (tile.sub) {
      g.fillStyle = 'rgba(188, 208, 245, .9)';
      g.font = text(15, 600);
      g.fillText(tile.sub, x + 20, y + 124);
    }
  });
  // The website, bottom right, on a small dark pill so it reads over the scene
  if (S.app.site) {
    g.font = text(20, 700);
    const w = g.measureText(S.app.site).width + 36;
    g.fillStyle = 'rgba(7, 16, 54, .72)';
    g.beginPath();
    g.roundRect(CARD_W - w - 40, CARD_H - 64, w, 40, 20);
    g.fill();
    g.fillStyle = '#c6ff3d';
    g.fillText(S.app.site, CARD_W - w - 22, CARD_H - 37);
  }
}

async function buildShare() {
  const sh = ui.share;
  sh.making = true;
  render();
  let blob = null;
  try { blob = await makeCard(sh.goal); } catch { /* shown as an error below */ }
  if (ui.share !== sh) return;        // closed meanwhile
  if (!blob) { sh.making = false; toast('error', t('shareFailed')); closeShare(); return; }
  if (sh.url) URL.revokeObjectURL(sh.url);
  Object.assign(sh, { blob, url: URL.createObjectURL(blob), making: false });
  render();
}

function openShare() {
  if (ui.share) return;
  shareShots = {};
  ui.share = { goal: ui.goal };       // starts with the goal of the home page, can be changed in the window
  buildShare();
}

function shareGoal(goal) {
  if (!ui.share || ui.share.making || ui.share.goal === goal) return;
  ui.share.goal = goal;
  buildShare();
}

function closeShare() {
  if (ui.share?.url) URL.revokeObjectURL(ui.share.url);
  ui.share = null;
  shareShots = {};
  render();
  $('.share-fab')?.focus({ preventScroll: true }); // back where the window was opened
}

function shareModal() {
  const sh = ui.share;
  const file = `fortnite-chest-tracker-${new Date().toISOString().slice(0, 10)}.png`;
  const h = S.hunt;
  const order = ['odds_pirate', 'odds_gold', 'odds_vehicle', 'odds_pickaxe', 'odds_jam', 'odds_loading', 'auto'];
  const chips = order.map((k) => {
    const o = h.odds.find((x) => x.key === k);
    const label = k === 'auto' ? t('goalAuto') : t('short_' + k);
    return `<button class="goal${k === 'odds_pirate' ? ' featured' : ''}${sh.goal === k ? ' on' : ''}" data-share-goal="${k}"${sh.making ? ' disabled' : ''}>${k === 'odds_pirate' ? '<img class="ico-img" src="/img/token.webp" alt="">' : ''}<span>${esc(label)}</span>${o ? `<b>${esc(pctx(dropChance(h.chests, o.odds)))}</b>` : ''}</button>`;
  }).join('');
  const picker = `<div class="share-pick"><span>${esc(t('shareGoal'))}</span><div class="goals">${chips}</div></div>`;
  const body = picker + (sh.url
    ? `<div class="share-frame${sh.making ? ' busy' : ''}"><img class="share-preview" src="${sh.url}" alt="${esc(t('shareTitle'))}">${sh.making ? '<div class="spin"></div>' : ''}</div>`
    : `<div class="share-wait"><div class="spin"></div><span>${esc(t('shareMaking'))}</span></div>`);
  const foot = sh.making || !sh.url ? button(esc(t('cancel')), 'data-act="share-close"', 'btn-ghost')
    : `${button(esc(t('cancel')), 'data-act="share-close"', 'btn-ghost')}
       <a class="btn" href="${sh.url}" download="${esc(file)}">${icon('down')}${esc(t('shareDownload'))}</a>
       ${button(`${icon('copy')}${esc(t('shareCopy'))}`, 'data-act="share-copy"', 'btn-primary')}`;
  return `<div class="backdrop" data-share-backdrop><div class="modal share-modal" role="dialog" aria-modal="true" aria-label="${esc(t('shareTitle'))}">
    <header><h1>${esc(t('shareTitle'))}</h1></header><div class="body">${body}</div><div class="foot">${foot}</div></div></div>`;
}

async function copyShare() {
  try {
    await navigator.clipboard.write([new ClipboardItem({ 'image/png': ui.share.blob })]);
    toast('success', t('shareCopied'));
  } catch {
    toast('error', t('shareCopyFailed'));
  }
}

// ---- Little game-like effects ----------------------------------------------------------------------------
const reducedMotion = matchMedia('(prefers-reduced-motion: reduce)').matches;

function countUp(el, from, to) {
  if (reducedMotion) return;
  const start = performance.now();
  const withApprox = el.textContent.trim().startsWith('≈');
  const tick = (now) => {
    const k = Math.min(1, (now - start) / 900);
    const v = Math.round(from + (to - from) * (1 - Math.pow(1 - k, 3)));
    if (document.body.contains(el)) el.textContent = (withApprox ? '≈' : '') + nf(v);
    if (k < 1) requestAnimationFrame(tick);
    else { el.classList.remove('num-pop'); void el.offsetWidth; el.classList.add('num-pop'); }
  };
  requestAnimationFrame(tick);
}

// Each click throws a few sparks and a ring, in the colours of the site
document.addEventListener('pointerdown', (e) => {
  if (reducedMotion || e.button !== 0) return;
  const colors = ['#c6ff3d', '#3ee6ff', '#ffb83a', '#ffffff'];
  const ring = document.createElement('div');
  ring.className = 'click-ring';
  ring.style.left = `${e.clientX}px`;
  ring.style.top = `${e.clientY}px`;
  document.body.appendChild(ring);
  setTimeout(() => ring.remove(), 500);
  for (let i = 0; i < 8; i++) {
    const s = document.createElement('div');
    s.className = 'spark';
    const a = (i / 8) * Math.PI * 2 + Math.random() * 0.5;
    const d = 26 + Math.random() * 22;
    s.style.left = `${e.clientX}px`;
    s.style.top = `${e.clientY}px`;
    s.style.background = colors[i % colors.length];
    s.style.setProperty('--dx', `${Math.cos(a) * d}px`);
    s.style.setProperty('--dy', `${Math.sin(a) * d}px`);
    document.body.appendChild(s);
    setTimeout(() => s.remove(), 600);
  }
}, { passive: true });

// ---- Events ------------------------------------------------------------------------------------------
function go(pageName) {
  if (OLD_PAGES[pageName]) { ui.sub = pageName; pageName = 'settings'; }
  if (pageName !== ui.page) ui.animate = true;
  ui.page = pageName;
  localStorage.setItem('fct-page', pageName);
  render();
}

function estimateMatches() {
  const m = {};
  ['solo', 'duo', 'trio', 'squad', 'other', 'total'].forEach((k) => {
    const v = parseInt(($(`#est-${k}`)?.value || '').trim(), 10);
    if (v > 0) m[k] = v;
  });
  return m;
}

function clearDrafts(prefix) {
  Object.keys(ui.draft).filter((k) => k.startsWith(prefix)).forEach((k) => delete ui.draft[k]);
}

document.addEventListener('input', (e) => {
  if (e.target.matches('[data-draft]') && e.target.id) ui.draft[e.target.id] = e.target.value;
});

document.addEventListener('click', async (e) => {
  if (e.target.matches && e.target.matches('[data-share-backdrop]')) { closeShare(); return; }
  const pickGoal = e.target.closest && e.target.closest('[data-share-goal]');
  if (pickGoal) { shareGoal(pickGoal.dataset.shareGoal); return; }
  const el = e.target.closest('[data-cmd],[data-act],[data-open],[data-nav],[data-sub],[data-range],[data-ovstyle],[data-goal],[data-rename],[data-page],[data-select],[data-goto],[data-notice],[data-copy],[data-prize],[data-setup],[data-setup-choice]');
  if (!el || el.disabled) return;
  if (el.dataset.nav) { go(el.dataset.nav); return; }
  if (el.dataset.goal) { ui.goal = el.dataset.goal; localStorage.setItem('fct-goal', ui.goal); ui.sceneInstant = true; cmd('goal', { key: ui.goal }); render(); return; }
  if (el.dataset.rename != null) { ui.editPrize = el.dataset.rename || null; delete ui.draft['prize-edit']; render(); if (ui.editPrize) { const f = document.querySelector('.list-row input[data-draft]'); if (f) { f.focus(); f.select(); } } return; }
  if (el.dataset.sub) { ui.sub = el.dataset.sub; localStorage.setItem('fct-sub', ui.sub); render(); return; }
  if (el.dataset.ovstyle) {
    if (S.overlay.style !== el.dataset.ovstyle) { S.overlay.style = el.dataset.ovstyle; render(); cmd('settings', { overlayStyle: el.dataset.ovstyle }); }
    return;
  }
  if (el.dataset.range) { ui.range = Number(el.dataset.range); localStorage.setItem('fct-range', ui.range); render(); return; }
  if (el.dataset.page) {
    const [key, dir] = el.dataset.page.split(':');
    ui.pageIndex[key] = (ui.pageIndex[key] || 0) + Number(dir);
    render();
    return;
  }
  if (el.dataset.select) { ui.selected = el.dataset.select; render(); return; }
  if (el.dataset.goto) {
    ui.mode = '';
    ui.selected = el.dataset.goto;
    const i = S.matches.findIndex((m) => m.id === el.dataset.goto);
    ui.pageIndex.matches = Math.floor(Math.max(0, i) / Math.max(1, ui.size.matches || 10));
    go('matches');
    return;
  }
  if (el.dataset.cmd) {
    el.disabled = true;
    await cmd(el.dataset.cmd);
    el.disabled = false;
    return;
  }
  if (el.dataset.open) { e.preventDefault(); cmd('open', { what: el.dataset.open }); return; }
  if (el.dataset.notice) {
    const a = el.dataset.notice;
    if (a === 'ui:settings') go('settings');
    else if (a === 'ui:profile') { const p = $('#profile'); if (p) { p.focus(); try { p.showPicker(); } catch { /* not allowed */ } } }
    else cmd(a);
    return;
  }
  if (el.dataset.copy) {
    const input = document.getElementById(el.dataset.copy);
    try { await navigator.clipboard.writeText(input.value); toast('success', t('copied')); } catch { input.select(); }
    return;
  }
  if (el.dataset.prize) {
    const input = document.getElementById(el.dataset.input);
    const r = await cmd('prize', { id: el.dataset.prize, name: input.value.trim() });
    if (r.ok) { delete ui.draft[el.dataset.input]; ui.editPrize = null; toast('success', t('saved')); render(); }
    return;
  }
  if (el.dataset.setupChoice) {
    const w = ui.setup;
    const v = el.dataset.value;
    if (el.dataset.setupChoice === 'lang') { await cmd('setup/language', { language: v }); return; }
    if (el.dataset.setupChoice === 'analyze') w.analyze = v === 'yes';
    if (el.dataset.setupChoice === 'fortnite') w.fortnite = v === 'yes';
    render();
    return;
  }
  if (el.dataset.setup) {
    const a = el.dataset.setup;
    if (a === 'back') setupMove(-1);
    else if (a === 'check') setupCheckFolder();
    else if (a === 'next') {
      const input = $('#setup-folder');
      if (ui.setup.step === 1 && input && input.value.trim() !== ui.setup.folder) await setupCheckFolder();
      setupMove(1);
    } else if (a === 'finish') {
      el.disabled = true;
      const w = ui.setup;
      await cmd('setup/finish', { language: S.app.lang, demosFolder: w.folder, analyzeExisting: w.analyze, startWithFortnite: w.fortnite && S.setup.canStartWithFortnite, install: w.install && S.setup.canInstall });
    }
    return;
  }
  switch (el.dataset.act) {
    case 'save-archive': {
      const v = $('#set-archive').value.trim();
      if (v) { const r = await cmd('settings', { archiveFolder: v }); if (r.ok) { delete ui.draft['set-archive']; toast('success', t('saved')); } }
      break;
    }
    case 'save-demos': {
      const v = $('#set-demos').value.trim();
      if (v) { const r = await cmd('settings', { demosFolder: v }); if (r.ok) { delete ui.draft['set-demos']; toast('success', t('saved')); } }
      break;
    }
    case 'open-gg': {
      const player = $('#est-player').value.trim();
      if (player) await cmd('estimate/save-player', { player });
      cmd('open', { what: 'fortnitegg' });
      break;
    }
    case 'est-save': {
      const r = await cmd('estimate/save', { player: $('#est-player').value.trim(), matches: estimateMatches() });
      if (r.ok) clearDrafts('est-');
      break;
    }
    case 'est-fetch': {
      const r = await cmd('estimate/fetch', { player: $('#est-player').value.trim(), apiKey: $('#est-key').value.trim() });
      if (r.ok) { delete ui.draft['est-key']; delete ui.draft['est-player']; }
      break;
    }
    case 'est-clear':
      if (confirm(t('estClearConfirm'))) { await cmd('estimate/clear'); clearDrafts('est-'); }
      break;
    case 'notice-next': ui.notice = (ui.notice || 0) + 1; render(); break;
    case 'share': openShare(); break;
    case 'share-close': closeShare(); break;
    case 'share-copy': copyShare(); break;
    case 'quit':
      if (confirm(t('quitConfirm'))) await cmd('quit');
      break;
    case 'install':
      el.disabled = true;
      if (!(await cmd('install')).ok) el.disabled = false; // else the counter restarts from the installed copy: the page reloads
      break;
    case 'uninstall':
      if (confirm(t('uninstallConfirm', S.app.dataFolder))) { el.disabled = true; await cmd('uninstall'); }
      break;
    default: break;
  }
});

document.addEventListener('change', (e) => {
  const el = e.target.closest('[data-change]');
  if (!el) return;
  const v = el.type === 'checkbox' ? el.checked : el.value;
  switch (el.dataset.change) {
    case 'profile': cmd('profile', { key: v }); break;
    case 'mode': ui.mode = v; ui.pageIndex.matches = 0; ui.selected = null; render(); break;
    case 'ov-visible': cmd('overlay', { visible: v }); break;
    case 'ov-locked': cmd('overlay', { locked: v }); break;
    case 'ov-size': cmd('settings', { overlaySize: Number(v) }); break;
    case 'ov-opacity': cmd('settings', { overlayOpacity: Number(v) }); break;
    case 'set-lang': cmd('settings', { language: v }); break;
    case 'set-openui': cmd('settings', { openUiOnStart: v }); break;
    case 'set-fortnite': cmd('settings', { startWithFortnite: v }); break;
    case 'set-overlay': cmd('settings', { showOverlay: v }); break;
    case 'set-update': cmd('settings', { updateMode: v }); break;
    case 'set-archive': cmd('settings', { archiveFolder: v ? '*' : '' }); break;
    case 'est-include': cmd('settings', { includeEstimate: v }); break;
    case 'setup-install': if (ui.setup) ui.setup.install = v; break;
    default: break;
  }
});

document.addEventListener('keydown', (e) => {
  const typing = e.target.matches && e.target.matches('input, select, textarea');
  if (!typing && !e.ctrlKey && !e.altKey && !e.metaKey && /^[1-5]$/.test(e.key) && S && !S.setup && !ui.share) { go(PAGES[Number(e.key) - 1]); return; }
  if (e.key === 'Escape' && ui.share) { closeShare(); return; }
  if (e.key === 'Escape' && ui.editPrize) { ui.editPrize = null; render(); return; }
  if (e.key === 'Enter' && e.target.dataset && e.target.dataset.enter) {
    e.preventDefault();
    const b = document.getElementById(e.target.dataset.enter);
    if (b) b.click();
  }
});

let resizeTimer = null;
window.addEventListener('resize', () => {
  clearTimeout(resizeTimer);
  resizeTimer = setTimeout(render, 120);
});

// ---- Live connection -----------------------------------------------------------------------------------
async function loadTexts(lang) {
  const r = await fetch('/api/texts?lang=' + encodeURIComponent(lang));
  T = await r.json();
  textsLang = lang;
  document.querySelectorAll('[data-t]').forEach((el) => { el.textContent = t(el.dataset.t); });
}

function newActivity(state) {
  const last = state.activity.length ? state.activity[state.activity.length - 1].id : 0;
  if (ui.lastActivity == null) { ui.lastActivity = last; return; }
  state.activity.filter((e) => e.id > ui.lastActivity && ['success', 'treasure', 'error'].includes(e.level))
    .slice(-3).forEach((e) => toast(e.level, e.text));
  ui.lastActivity = Math.max(ui.lastActivity, last);
}

async function onState(state) {
  S = state;
  // The overlay shows the goal chosen here: the counter follows the page (once at start, then on each choice).
  // (a browser that never chose one, or another port, takes the counter's goal instead of resetting it)
  if (!ui.goalSent && S.overlay) {
    ui.goalSent = true;
    if (!ui.goal) ui.goal = S.overlay.goal || 'odds_pirate';
    else if (S.overlay.goal !== ui.goal) cmd('goal', { key: ui.goal });
  }
  if (textsLang !== S.app.lang) await loadTexts(S.app.lang);
  newActivity(state);
  // A push while a button is pressed or a list is open would replace them under the mouse (lost click, list closed):
  // the page is redrawn just after instead.
  if (ui.pressing || ui.selectOpen) { ui.renderLater = true; return; }
  render();
}
function renderIfLate() {
  if (!ui.renderLater || ui.pressing || ui.selectOpen) return;
  ui.renderLater = false;
  render();
}
// A drop-down list is "open" from the press on it until a choice, Escape/Enter/Tab, or the focus leaves it.
const listClosed = () => { ui.selectOpen = false; setTimeout(renderIfLate, 0); };
document.addEventListener('pointerdown', (e) => {
  ui.pressing = true;
  ui.selectOpen = e.target instanceof HTMLSelectElement ? !ui.selectOpen : false;
}, true);
document.addEventListener('pointerup', () => { ui.pressing = false; setTimeout(renderIfLate, 0); }, true);
document.addEventListener('pointercancel', () => { ui.pressing = false; renderIfLate(); }, true);
document.addEventListener('keydown', (e) => { if (['Escape', 'Enter', 'Tab'].includes(e.key)) listClosed(); }, true);
document.addEventListener('focusout', listClosed, true);
document.addEventListener('change', listClosed, true);
// Alt+Tab while a button is held, page hidden: nothing is "pressed" any more
window.addEventListener('blur', () => { ui.pressing = false; listClosed(); });
document.addEventListener('visibilitychange', () => { ui.pressing = false; listClosed(); });

let offlineTimer = null;
let lostConnection = false;
function connect() {
  const es = new EventSource('/api/events');
  es.addEventListener('state', (e) => {
    // Back after the counter was closed or restarted (update): reload to get the new page and token.
    if (lostConnection) { location.reload(); return; }
    clearTimeout(offlineTimer);
    offlineTimer = null;
    onState(JSON.parse(e.data));
  });
  es.onerror = () => {
    if (S) lostConnection = true;
    if (!offlineTimer) offlineTimer = setTimeout(() => { $('#offline').hidden = false; }, 2500);
  };
}

loadTexts(navigator.language?.slice(0, 2) || 'fr').catch(() => {}).finally(connect);
