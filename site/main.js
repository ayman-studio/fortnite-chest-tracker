// Fortnite - Chest Tracker website. The texts are already in the page (one page per language, see prerender.mjs);
// this adds the latest release, the 3D island (started after the first paint, a still picture on phones), the HUD
// demo, the promises ticker, the loot cards, the quest track, the odds calculator, the gallery and the language switch.
(() => {
  const REPO = 'ayman-studio/fortnite-chest-tracker';
  const $ = (s, el = document) => el.querySelector(s);
  const $$ = (s, el = document) => [...el.querySelectorAll(s)];
  const LANGS = ['en', 'fr', 'es'];
  const lang = LANGS.includes(document.documentElement.lang) ? document.documentElement.lang : 'en';
  const TX = (window.I18N && window.I18N[lang]) || {};
  const t = (k, ...a) => String(TX[k] ?? k).replace(/\{(\d)\}/g, (_, i) => a[i]);
  const locale = { en: 'en-GB', fr: 'fr-FR', es: 'es-ES' }[lang];
  const nbsp = (s) => s.replace(/ /g, ' ');
  // Spanish leaves 4-digit numbers ungrouped by default ("2000" next to "12.345"): always group, like fr and en
  const nf = (n) => nbsp(new Intl.NumberFormat(locale, { useGrouping: 'always' }).format(n));
  // Percent like the app: whole from 10 %, one decimal from 1 %, else two significant digits
  const pct = (p) => {
    const o = p >= 0.1 ? { maximumFractionDigits: 0 } : p >= 0.01 ? { maximumFractionDigits: 1 } : { maximumSignificantDigits: 2 };
    return nbsp(new Intl.NumberFormat(locale, { style: 'percent', ...o }).format(p > 0.995 ? 0.99 : p));
  };
  const chance = (chests, odds) => 1 - Math.pow(1 - 1 / odds, Math.max(0, chests));
  const reducedMotion = matchMedia('(prefers-reduced-motion: reduce)');

  // Epic's rates (same as TreasureOdds.cs)
  const ODDS = [
    { key: 'gizmo', odds: 127 }, { key: 'loading', odds: 272 }, { key: 'jam', odds: 391 }, { key: 'pickaxe', odds: 568 },
    { key: 'vehicle', odds: 7813, boost: 10 }, { key: 'pirate', odds: 137500, boost: 1.5 }, { key: 'gold', odds: 1562500, boost: 7.5 },
  ];
  const PIRATE = 137500;

  // ---------------------------------------------------------------- language: each one has its own page
  $$('.lang button').forEach((b) => b.addEventListener('click', () => {
    const l = b.dataset.lang;
    try { localStorage.setItem('cct-lang', l); } catch { /* storage blocked */ }
    if (l !== lang) location.href = new URL(l === 'en' ? './' : `${l}/`, document.baseURI).href + location.hash;
  }));

  // ---------------------------------------------------------------- toast + copy
  let toastTimer = 0;
  function toast(msg) {
    const el = $('#toast');
    el.textContent = msg;
    el.classList.add('show');
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => el.classList.remove('show'), 3500);
  }
  async function copy(text, done) {
    try { await navigator.clipboard.writeText(text); toast(done); } catch { toast(`${t('copy_fail')} — ${text}`); }
  }

  // ---------------------------------------------------------------- latest release, stars, downloads (GitHub API)
  async function gh(path, pick) {
    const key = `cct-gh:${path}`;
    try {
      const c = JSON.parse(sessionStorage.getItem(key));
      if (c && Date.now() - c.at < 10 * 60 * 1000) return c.data;
    } catch { /* nothing cached */ }
    const r = await fetch(`https://api.github.com/${path}`, { headers: { Accept: 'application/vnd.github+json' } });
    if (!r.ok) throw new Error(String(r.status));
    const data = pick(await r.json());
    try { sessionStorage.setItem(key, JSON.stringify({ at: Date.now(), data })); } catch { /* storage full or blocked */ }
    return data;
  }
  async function loadRelease() {
    // The list (not /releases/latest, which answers 404 while there is no release yet)
    const rels = await gh(`repos/${REPO}/releases?per_page=30`, (list) => {
      const r = list.find((x) => !x.draft && !x.prerelease);
      const exe = r && ((r.assets || []).find((a) => a.name === 'FortniteChestTracker.exe') || (r.assets || []).find((a) => /\.exe$/i.test(a.name)));
      return {
        latest: r ? { tag: r.tag_name, url: r.html_url, date: r.published_at, exe: exe ? { url: exe.browser_download_url, size: exe.size, digest: exe.digest || '' } : null } : null,
        downloads: list.reduce((s, x) => s + (x.assets || []).reduce((n, a) => n + (a.download_count || 0), 0), 0),
      };
    }).catch(() => null);
    const repo = await gh(`repos/${REPO}`, (r) => ({ stars: r.stargazers_count })).catch(() => null);
    const rel = rels && rels.latest;
    if (rel) {
      if (rel.exe) $$('.dl').forEach((a) => { a.href = rel.exe.url; });
      $$('.ver').forEach((el) => { el.textContent = rel.tag; });
      if (rel.exe) $('#dl-size').textContent = t('size_mb', new Intl.NumberFormat(locale, { maximumFractionDigits: 1 }).format(rel.exe.size / 1048576));
      if (rel.date) $('#dl-date').textContent = t('released', new Intl.DateTimeFormat(locale, { dateStyle: 'medium' }).format(new Date(rel.date)));
      $('#dl-notes').href = rel.url;
      const sha = rel.exe && /^sha256:([0-9a-f]{64})$/i.exec(rel.exe.digest);
      if (sha && $('#vt-link')) $('#vt-link').href = `https://www.virustotal.com/gui/file/${sha[1].toLowerCase()}`;
      if (sha) {
        const b = $('#dl-sha');
        b.hidden = false;
        $('code', b).textContent = `${sha[1].slice(0, 10)}…`;
        b.title = sha[1];
        b.setAttribute('aria-label', `${t('sha_title')} (${sha[1]})`);
        b.addEventListener('click', () => copy(sha[1], t('sha_copied')));
      }
      $('#dl-facts').hidden = false;
    }
    // Honest numbers only, and only once they mean something
    const stars = repo ? repo.stars : 0;
    const downloads = rels ? rels.downloads : 0;
    if (stars >= 10) { $('#proof-stars').textContent = t('proof_stars', nf(stars)); $('#proof-stars').hidden = false; }
    if (downloads >= 100) { $('#proof-dl').textContent = t('proof_dl', nf(downloads)); $('#proof-dl').hidden = false; }
    $('#proof').hidden = !(stars >= 10 || downloads >= 100);
  }

  // ---------------------------------------------------------------- phones: the app is for Windows, send the link
  $$('.send-pc').forEach((b) => b.addEventListener('click', async () => {
    const url = ($('link[rel=canonical]') || {}).href || location.href.split('#')[0];
    if (navigator.share) {
      try { await navigator.share({ title: document.title, text: t('share_text'), url }); return; } catch (e) { if (e && e.name === 'AbortError') return; }
    }
    copy(url, t('link_copied'));
  }));

  // ---------------------------------------------------------------- nav
  const nav = $('#nav');
  const onScroll = () => nav.classList.toggle('solid', scrollY > 20);
  addEventListener('scroll', onScroll, { passive: true });
  onScroll();
  const menuBtn = $('#menu-btn');
  const links = $('#links');
  const setMenu = (open, refocus) => {
    links.classList.toggle('open', open);
    menuBtn.setAttribute('aria-expanded', String(open));
    if (!open && refocus) menuBtn.focus();
  };
  menuBtn.addEventListener('click', () => setMenu(!links.classList.contains('open')));
  $$('a', links).forEach((a) => a.addEventListener('click', () => setMenu(false)));
  document.addEventListener('keydown', (e) => { if (e.key === 'Escape' && links.classList.contains('open')) setMenu(false, true); });
  document.addEventListener('click', (e) => {
    if (links.classList.contains('open') && !links.contains(e.target) && !menuBtn.contains(e.target)) setMenu(false);
  });
  const sections = ['gallery', 'features', 'hud', 'odds', 'faq'].map((id) => document.getElementById(id));
  const navObs = new IntersectionObserver((entries) => {
    for (const e of entries) {
      if (e.isIntersecting) $$('a', links).forEach((a) => a.classList.toggle('on', a.hash === `#${e.target.id}`));
    }
  }, { rootMargin: '-45% 0px -50% 0px' });
  sections.forEach((s) => navObs.observe(s));

  // ---------------------------------------------------------------- reveal on scroll
  const revealObs = new IntersectionObserver((entries) => {
    for (const e of entries) if (e.isIntersecting) { e.target.classList.add('in'); revealObs.unobserve(e.target); }
  }, { rootMargin: '0px 0px -8% 0px' });
  $$('[data-reveal]').forEach((el) => revealObs.observe(el));
  // The quest track fills itself in, node after node, once on screen (CSS)
  revealObs.observe($('#quest'));

  // ---------------------------------------------------------------- promises ticker
  // A slow endless loop: the group of chips is cloned (hidden from screen readers) until it covers the band, and the
  // track slides by exactly one group. Paused on hover (CSS); a still, centred row with reduced motion.
  const mq = $('#marquee');
  const mqTrack = $('.mq-track', mq);
  const mqGroup = $('.mq-group', mq);
  let mqWidth = 0;
  function setupTicker() {
    $$('.mq-clone', mq).forEach((c) => c.remove());
    mq.classList.remove('run');
    mqWidth = mq.clientWidth;
    if (reducedMotion.matches) return;
    mq.classList.add('run');
    const w = mqGroup.getBoundingClientRect().width;
    if (!w) { mq.classList.remove('run'); return; }
    for (let i = Math.ceil(mqWidth / w); i > 0; i--) {
      const c = mqGroup.cloneNode(true);
      c.classList.add('mq-clone');
      c.setAttribute('aria-hidden', 'true');
      mqTrack.appendChild(c);
    }
    mq.style.setProperty('--shift', `${-w}px`);
    mq.style.setProperty('--dur', `${Math.round(w / 36)}s`); // about 36 px per second
  }
  setupTicker();
  if (document.fonts) document.fonts.ready.then(setupTicker);
  reducedMotion.addEventListener('change', setupTicker);
  let mqResize = 0;
  addEventListener('resize', () => {
    clearTimeout(mqResize);
    mqResize = setTimeout(() => { if (mq.clientWidth !== mqWidth) setupTicker(); }, 250);
  });

  // ---------------------------------------------------------------- loot cards lean towards the mouse
  $$('.loot-in').forEach((card) => {
    card.addEventListener('pointermove', (e) => {
      if (e.pointerType !== 'mouse' || reducedMotion.matches) return;
      const r = card.getBoundingClientRect();
      const x = (e.clientX - r.left) / r.width - 0.5;
      const y = (e.clientY - r.top) / r.height - 0.5;
      card.style.setProperty('--ry', `${(x * 10).toFixed(2)}deg`);
      card.style.setProperty('--rx', `${(-y * 8).toFixed(2)}deg`);
      card.style.setProperty('--mx', `${((x + 0.5) * 100).toFixed(1)}%`);
      card.style.setProperty('--my', `${((y + 0.5) * 100).toFixed(1)}%`);
    });
    card.addEventListener('pointerleave', () => { card.style.removeProperty('--rx'); card.style.removeProperty('--ry'); });
  });

  // ---------------------------------------------------------------- the HUD (hero + demo)
  // Fortnite's replay can only be read once the match is over: "?" while playing, then the chests land all at once.
  const demo = { playing: false, match: 23, plus: 23, total: 2371, session: 55, treasures: 17, hunt: 407, compact: false };
  const hero = { playing: false, match: 21, plus: 21, hunt: 2359 };
  const matchChests = () => 12 + Math.floor(Math.random() * 22);
  function hudHtml(s, compact) {
    const p = pct(chance(s.hunt, PIRATE));
    const segs = compact ? 16 : 12;
    const filled = Math.max(s.hunt > 0 ? 0.06 : 0, Math.min(1, s.hunt / PIRATE) * segs);
    const bar = Array.from({ length: segs }, (_, i) => `<i><s style="width:${Math.max(0, Math.min(1, filled - i)) * 100}%"></s></i>`).join('');
    const tag = s.playing ? `<span class="h-tag wait"><span>${t('h_playing')}</span></span>` : `<span class="h-tag"><span>${t('h_last')}</span></span>`;
    const head = `<div class="h-hero"><img class="h-chest" src="img/chest-pirate.webp" alt=""><b class="h-num${s.playing ? ' unknown' : ''}">${s.playing ? '?' : nf(s.match)}</b>
      <div class="h-side">${tag}${compact ? `<span class="h-pctline"><img src="img/token.webp" alt="">${p}</span>` : `<span class="h-word">${t('h_chests')}</span>`}</div><span class="h-plus" aria-hidden="true">+${nf(s.plus)}</span></div>`;
    if (compact) return `${head}<div class="h-bar" aria-hidden="true">${bar}</div>`;
    return `${head}
      <div class="h-goal"><img src="img/token.webp" alt=""><span class="h-name">${t('t_pirate').toUpperCase()}</span><span class="h-pct">${p}</span></div>
      <div class="h-bar" aria-hidden="true">${bar}</div>
      <div class="h-cap"><span>${nf(s.hunt)} / ${nf(PIRATE)} ${t('h_chests_word')}</span><span>${t('h_of', nf(PIRATE))}</span></div>
      <div class="h-stats"><div><b>${nf(s.total)}</b><small>${t('h_total')}</small></div><div><b>${nf(s.session)}</b><small>${t('h_session')}</small></div><div><b>★ ${nf(s.treasures)}</b><small>${t('h_treasures')}</small></div></div>`;
  }
  function renderDemo() {
    const d = $('#demo-hud');
    d.className = `hud${demo.playing ? '' : ' live'}${demo.compact ? ' compact' : ''}`;
    d.innerHTML = hudHtml(demo, demo.compact);
  }
  function renderHero() {
    const h = $('#hero-hud');
    h.className = `hud compact${hero.playing ? '' : ' live'} hero-hud`;
    h.innerHTML = hudHtml(hero, true);
  }
  const pop = (el) => { el.classList.remove('pop'); void el.offsetWidth; el.classList.add('pop'); };
  // "End of match": the replay becomes readable, its chests are added at once
  $('#demo-open').addEventListener('click', () => {
    const n = matchChests();
    Object.assign(demo, { playing: false, match: n, plus: n, total: demo.total + n, session: demo.session + n, hunt: demo.hunt + n });
    renderDemo();
    pop($('#demo-hud'));
  });
  $('#demo-new').addEventListener('click', () => { demo.playing = true; renderDemo(); });
  $$('#hud .seg button').forEach((b) => b.addEventListener('click', () => {
    demo.compact = b.dataset.style === 'compact';
    $$('#hud .seg button').forEach((x) => { x.classList.toggle('on', x === b); x.setAttribute('aria-pressed', String(x === b)); });
    renderDemo();
  }));

  // ---------------------------------------------------------------- the 3D island (scene.js from the app)
  // Phones and Save-Data get the still picture of the island (CSS); elsewhere the live scene starts after the page
  // has loaded and painted, with its three.js modules preloaded in parallel.
  const heroEl = $('.hero');
  const posterOnly = matchMedia('(max-width: 619px)').matches || !!(navigator.connection && navigator.connection.saveData);
  let sceneReady = false;
  let heroVisible = true;
  const island = { total: 2359, treasures: 16 };
  function sendScene(instant = false) {
    if (!sceneReady) return;
    const b = window.BUBBLES[lang];
    window.dispatchEvent(new CustomEvent('fct-scene', { detail: {
      visible: heroVisible, live: false, total: island.total, treasures: island.treasures,
      chance: chance(hero.hunt, PIRATE), dug: 0.62, instant, treasureLabel: b.sceneTreasure,
      bubbles: {
        you: b.bub_you, others: b.bub_others,
        minerLines: [b.bub_miner, b.bub_miner_2, b.bub_miner_3, b.bub_miner_4, b.bub_miner_5, b.bub_miner_6],
        quitterLines: [b.bub_quitter, b.bub_quitter_2, b.bub_quitter_3, b.bub_quitter_4, b.bub_quitter_5, b.bub_quitter_6],
        parrot: b.bub_parrot, crab: b.bub_crab, monkey: b.bub_monkey, dog: b.bub_dog,
        captain: b.bub_captain, lookout: b.bub_lookout, cannon: b.bub_cannon, token: b.bub_token, chest: b.bub_chest,
      },
    } }));
  }
  addEventListener('fct-scene-ready', () => { sceneReady = true; heroEl.classList.add('live3d'); sendScene(true); });
  function startScene() {
    for (const f of ['three.module.js', 'three.core.js', 'GLTFLoader.js', 'BufferGeometryUtils.js', 'SkeletonUtils.js']) {
      const l = document.createElement('link');
      l.rel = 'modulepreload';
      l.href = `vendor/three/${f}`;
      document.head.appendChild(l);
    }
    const s = document.createElement('script');
    s.type = 'module';
    s.src = 'scene.js';
    document.head.appendChild(s);
  }
  if (posterOnly) heroEl.classList.add('poster-only');
  else {
    const idle = (f) => ('requestIdleCallback' in window ? requestIdleCallback(f, { timeout: 1500 }) : setTimeout(f, 200));
    if (document.readyState === 'complete') idle(startScene);
    else addEventListener('load', () => idle(startScene), { once: true });
  }
  new IntersectionObserver(([e]) => {
    heroVisible = e.isIntersecting;
    sendScene();
  }, { threshold: 0.08 }).observe(heroEl);
  // A match is played, then counted: the hero HUD shows "+N", the chests fly to the pile and the cannon fires
  setInterval(() => {
    if (document.hidden || reducedMotion.matches) return; // the HUD stays on screen everywhere on the page
    if (!hero.playing) { hero.playing = true; renderHero(); return; }
    const n = matchChests();
    Object.assign(hero, { playing: false, match: n, plus: n, hunt: hero.hunt + n });
    island.total += n;
    renderHero();
    pop($('#hero-hud'));
    sendScene();
  }, 5000);

  // ---------------------------------------------------------------- credits and legal notice (footer)
  const legal = $('#legal');
  if (legal) {
    $('#legal-open').addEventListener('click', () => legal.showModal());
    $('#legal-close').addEventListener('click', () => legal.close());
    // a click on the dark backdrop closes it too
    legal.addEventListener('click', (e) => { if (e.target === legal) legal.close(); });
  }

  // ---------------------------------------------------------------- Treasure Hour strip (season.json, also read by the app)
  const strip = $('#th-strip');
  if (strip) {
    const D = { fr: 'j', en: 'd', es: 'd' }[lang] || 'd';
    const left = (ms) => {
      const m = Math.max(0, Math.floor(ms / 6e4)), d = Math.floor(m / 1440), h = Math.floor((m % 1440) / 60), mi = m % 60;
      return d > 0 ? `${d} ${D} ${h} h` : h > 0 ? `${h} h ${String(mi).padStart(2, '0')}` : `${mi} min ${String(Math.max(0, Math.floor(ms / 1e3) % 60)).padStart(2, '0')} s`;
    };
    fetch('season.json', { cache: 'no-cache' }).then((r) => (r.ok ? r.json() : null)).then((s) => {
      if (!s) return;
      const tick = () => {
        const now = Date.now();
        if (now >= Date.parse(s.end)) { strip.hidden = true; return; }
        const cur = s.treasureHours.find((h) => now >= Date.parse(h.start) && now < Date.parse(h.end));
        const next = s.treasureHours.find((h) => Date.parse(h.start) > now);
        strip.classList.toggle('on', !!cur);
        if (cur) $('#th-text').innerHTML = t('th_now', nf(cur.boosts.odds_pirate || 1), `<b>${left(Date.parse(cur.end) - now)}</b>`);
        else if (next) $('#th-text').innerHTML = t('th_next', `<b>${left(Date.parse(next.start) - now)}</b>`);
        strip.hidden = !cur && !next;
      };
      tick();
      setInterval(tick, 1000);
    }).catch(() => { /* no strip */ });
  }

  // ---------------------------------------------------------------- odds calculator
  const n = $('#calc-n');
  const r = $('#calc-r');
  const hours = $('#calc-h');
  const MAXC = 200000;
  const toRange = (v) => Math.round(Math.log10(1 + v) / Math.log10(1 + MAXC) * 1000);
  const fromRange = (x) => Math.round(Math.pow(1 + MAXC, x / 1000) - 1);
  function renderCalc() {
    const chests = Math.max(0, Math.min(10000000, Math.floor(Number(n.value) || 0)));
    const boost = (o) => (hours.checked && o.boost ? o.boost : 1);
    $('#calc-rows').innerHTML = ODDS.map((o) => {
      const odds = o.odds / boost(o);
      const c = chance(chests, odds);
      return `<div class="calc-row${o.key === 'pirate' ? ' hl' : ''}">
        <span class="name">${o.key === 'pirate' ? '<img src="img/token.webp" alt="">' : ''}${t(`t_${o.key}`)}${hours.checked && o.boost ? `<span class="boost">×${nf(o.boost)}</span>` : ''}</span>
        <span class="rate">${nf(Math.round(odds))}</span>
        <span class="ch"><i><s style="width:${(c * 100).toFixed(2)}%"></s></i><b>${pct(c)}</b></span></div>`;
    }).join('');
    const pirateOdds = PIRATE / (hours.checked ? 1.5 : 1);
    $('#calc-pirate').textContent = pct(chance(chests, pirateOdds));
    const half = Math.ceil(Math.log(0.5) / Math.log(1 - 1 / pirateOdds));
    $('#calc-half').textContent = t('calc_half', nf(half), nf(Math.round(half / 20)));
    r.style.setProperty('--p', `${(Number(r.value) / 10).toFixed(1)}%`);
    r.setAttribute('aria-valuetext', nf(chests));
  }
  n.addEventListener('input', () => { r.value = toRange(Number(n.value) || 0); renderCalc(); });
  r.addEventListener('input', () => { n.value = fromRange(Number(r.value)); renderCalc(); });
  hours.addEventListener('change', renderCalc);
  r.value = toRange(Number(n.value));

  // ---------------------------------------------------------------- online estimate by Epic name (Worker /api/estimate)
  const guess = $('#guess');
  if (guess) {
    const out = $('#guess-out');
    const PER_MATCH = 18;
    guess.addEventListener('submit', async (e) => {
      e.preventDefault();
      const name = $('#guess-name').value.trim();
      if (name.length < 3) return;
      out.className = 'guess-out';
      out.textContent = t('guess_wait', name);
      $('#guess-go').disabled = true;
      try {
        const res = await fetch(`/api/estimate?name=${encodeURIComponent(name)}`);
        const data = await res.json().catch(() => ({}));
        if (res.ok) {
          const matches = data.season ?? 0;
          if (!matches) { out.textContent = t('guess_none', data.name || name); return; }
          const chests = matches * PER_MATCH;
          n.value = chests;
          n.dispatchEvent(new Event('input'));
          out.className = 'guess-out ok';
          out.textContent = t('guess_ok', data.name || name, nf(matches), nf(chests));
        } else {
          out.className = 'guess-out err';
          out.textContent = t({ not_found: 'guess_404', private: 'guess_403', busy: 'guess_busy' }[data.error] || 'guess_off');
        }
      } catch {
        out.className = 'guess-out err';
        out.textContent = t('guess_off');
      } finally {
        $('#guess-go').disabled = false;
      }
    });
  }

  // ---------------------------------------------------------------- gallery (tabs; turns by itself while on screen)
  const tabs = $$('#gal-tabs [role=tab]');
  const SHOTS = tabs.map((b) => b.dataset.shot);
  let shot = 0;
  let galTimer = null;
  let userPicked = false;
  let galVisible = false;
  let galHover = false;
  let galFocus = false;
  function showShot(i, user, focus) {
    shot = (i + SHOTS.length) % SHOTS.length;
    const img = $('#gal-img');
    const k = SHOTS[shot];
    img.classList.add('fade');
    const next = new Image();
    next.src = `shots/${lang}-${k}.webp`;
    next.decode().catch(() => {}).finally(() => { img.src = next.src; img.alt = t(`g_${k}`); img.classList.remove('fade'); });
    $('#gal-cap').textContent = t(`gc_${k}`);
    tabs.forEach((b, j) => { b.classList.toggle('on', j === shot); b.setAttribute('aria-selected', String(j === shot)); b.tabIndex = j === shot ? 0 : -1; });
    $('#gal-panel').setAttribute('aria-labelledby', tabs[shot].id);
    if (focus) tabs[shot].focus();
    // Phones: the tabs are one row that scrolls sideways; keep the current one in view (without moving the page)
    const row = $('#gal-tabs');
    if (row.scrollWidth > row.clientWidth) {
      row.scrollTo({ left: tabs[shot].offsetLeft - (row.clientWidth - tabs[shot].offsetWidth) / 2, behavior: reducedMotion.matches ? 'auto' : 'smooth' });
    }
    if (user) { userPicked = true; galPlay(); }
  }
  // Stops for good once the visitor picks a picture; paused while hovered or focused, never with reduced motion
  function galPlay() {
    const run = galVisible && !userPicked && !galHover && !galFocus && !reducedMotion.matches && !document.hidden;
    if (run && !galTimer) galTimer = setInterval(() => showShot(shot + 1), 5000);
    else if (!run && galTimer) { clearInterval(galTimer); galTimer = null; }
  }
  tabs.forEach((b, i) => b.addEventListener('click', () => showShot(i, true)));
  $('#gal-tabs').addEventListener('keydown', (e) => {
    const step = { ArrowRight: 1, ArrowLeft: -1 }[e.key];
    if (step) showShot(shot + step, true, true);
    else if (e.key === 'Home') showShot(0, true, true);
    else if (e.key === 'End') showShot(SHOTS.length - 1, true, true);
    else return;
    e.preventDefault();
  });
  $('#gal-prev').addEventListener('click', () => showShot(shot - 1, true));
  $('#gal-next').addEventListener('click', () => showShot(shot + 1, true));
  const gal = $('#gal');
  gal.addEventListener('pointerenter', () => { galHover = true; galPlay(); });
  gal.addEventListener('pointerleave', () => { galHover = false; galPlay(); });
  gal.addEventListener('focusin', () => { galFocus = true; galPlay(); });
  gal.addEventListener('focusout', (e) => { if (!gal.contains(e.relatedTarget)) { galFocus = false; galPlay(); } });
  document.addEventListener('visibilitychange', galPlay);
  new IntersectionObserver(([e]) => { galVisible = e.isIntersecting; galPlay(); }, { threshold: 0.4 }).observe($('.window'));

  renderCalc();
  renderDemo();
  renderHero();
  loadRelease();
})();
