// Pre-renders the website at build time: one page per language (/ in English, /fr/, /es/), with every
// data-i18n text filled in from i18n.js, the SEO and social tags (canonical, hreflang, Open Graph, Twitter, JSON-LD)
// and a sitemap. Run by build.sh: node site/prerender.mjs <out>  (the template is <out>/index.html).
import { readFileSync, writeFileSync, mkdirSync, rmSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import vm from 'node:vm';

const OUT = process.argv[2] || '_site';
const HERE = dirname(fileURLToPath(import.meta.url));
const REPO = 'ayman-studio/fortnite-chest-tracker';
// The site lives at the root of its own domain (Cloudflare Pages); every page uses relative links (sub-pages add <base href="../">)
const BASE = 'https://fortnite-chest-tracker.com/';
const LANGS = ['en', 'fr', 'es'];
const URLS = { en: BASE, fr: BASE + 'fr/', es: BASE + 'es/' };
const OG_LOCALE = { en: 'en_GB', fr: 'fr_FR', es: 'es_ES' };

const sandbox = { window: {} };
vm.runInNewContext(readFileSync(join(HERE, 'i18n.js'), 'utf8'), sandbox);
const { I18N, BUBBLES } = sandbox.window;
const template = readFileSync(join(OUT, 'index.html'), 'utf8');

const esc = (s) => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
const attr = (s) => esc(s).replace(/"/g, '&quot;');
const json = (o) => JSON.stringify(o).replace(/</g, '\\u003c');

// Latest release (for the JSON-LD version): optional, the page fetches the live one anyway. Works without a token
// (GITHUB_TOKEN only raises the rate limit) and never fails the build: no answer in 8 s, an error or no release = no version
async function latestVersion() {
  try {
    const headers = { Accept: 'application/vnd.github+json', 'User-Agent': 'chest-tracker-site-build' };
    if (process.env.GITHUB_TOKEN) headers.Authorization = `Bearer ${process.env.GITHUB_TOKEN}`;
    const r = await fetch(`https://api.github.com/repos/${REPO}/releases?per_page=10`, { headers, signal: AbortSignal.timeout(8000) });
    if (!r.ok) return null;
    const rel = (await r.json()).find((x) => !x.draft && !x.prerelease);
    return rel ? String(rel.tag_name).replace(/^v/i, '') : null;
  } catch { return null; }
}

function fill(html, T) {
  const get = (k) => { if (T[k] == null) throw new Error(`missing text "${k}"`); return T[k]; };
  // <tag data-i18n="key">…</tag> (text) and <tag data-i18n-html="key">…</tag> (markup from i18n.js)
  html = html.replace(/<([a-z0-9]+)(\s[^>]*?)?\sdata-i18n(-html)?="(\w+)"([^>]*)>([\s\S]*?)<\/\1>/g,
    (m, tag, a1 = '', isHtml, key, a2) => `<${tag}${a1}${a2}>${isHtml ? get(key) : esc(get(key))}</${tag}>`);
  // data-i18n-attr="aria-label:key,alt:key2"
  html = html.replace(/<[a-z0-9]+\s[^>]*?data-i18n-attr="([^"]+)"[^>]*>/g, (tag, spec) => {
    tag = tag.replace(/\sdata-i18n-attr="[^"]+"/, '');
    for (const pair of spec.split(',')) {
      const [name, key] = pair.split(':');
      const re = new RegExp(`\\s${name}="[^"]*"`);
      const v = ` ${name}="${attr(get(key))}"`;
      tag = re.test(tag) ? tag.replace(re, v) : tag.replace(/^<[a-z0-9]+/, (t) => t + v);
    }
    return tag;
  });
  if (/data-i18n/.test(html)) throw new Error('unfilled data-i18n left: ' + html.match(/.{60}data-i18n.{60}/)[0]);
  return html;
}

// Marks that scripts run (the reveal-on-scroll hides sections only then), and sends a visitor to their language: ?lang=xx anywhere, or on the English page the saved choice / the browser language
const redirect = `<script>document.documentElement.classList.add('js');(function(){var L=['en','fr','es'],c=document.documentElement.lang,w=null,s=null,q=new URLSearchParams(location.search).get('lang');try{s=localStorage.getItem('cct-lang')}catch(e){}
if(L.indexOf(q)>=0){w=q;try{localStorage.setItem('cct-lang',q)}catch(e){}}else if(c==='en'){if(L.indexOf(s)>=0)w=s;else if(!s){var n=navigator.languages||[navigator.language];for(var i=0;i<n.length;i++){var x=(n[i]||'').slice(0,2).toLowerCase();if(L.indexOf(x)>=0){w=x;break}}}}
if(w&&(w!==c||q))location.replace(new URL(w==='en'?'./':w+'/',document.baseURI).href+location.hash)})()</script>`;

function seo(L, T, version) {
  const og = `${BASE}og/og-${L}.jpg`;
  const app = {
    '@context': 'https://schema.org', '@type': 'SoftwareApplication', name: 'Fortnite - Chest Tracker',
    description: T.meta_desc, url: URLS[L], inLanguage: L, image: og,
    operatingSystem: 'Windows 10, Windows 11', applicationCategory: 'GameApplication',
    downloadUrl: `https://github.com/${REPO}/releases/latest`, license: 'https://opensource.org/licenses/MIT', isAccessibleForFree: true,
    offers: { '@type': 'Offer', price: '0', priceCurrency: 'EUR' },
    author: { '@type': 'Person', name: 'ayman-studio', url: 'https://github.com/ayman-studio' },
    ...(version ? { softwareVersion: version } : {}),
  };
  return [
    `<link rel="canonical" href="${URLS[L]}">`,
    ...LANGS.map((l) => `<link rel="alternate" hreflang="${l}" href="${URLS[l]}">`),
    `<link rel="alternate" hreflang="x-default" href="${URLS.en}">`,
    '<meta property="og:type" content="website">',
    '<meta property="og:site_name" content="Fortnite - Chest Tracker">',
    `<meta property="og:title" content="${attr(T.meta_title)}">`,
    `<meta property="og:description" content="${attr(T.meta_desc)}">`,
    `<meta property="og:url" content="${URLS[L]}">`,
    `<meta property="og:locale" content="${OG_LOCALE[L]}">`,
    ...LANGS.filter((l) => l !== L).map((l) => `<meta property="og:locale:alternate" content="${OG_LOCALE[l]}">`),
    `<meta property="og:image" content="${og}">`,
    '<meta property="og:image:type" content="image/jpeg">',
    '<meta property="og:image:width" content="1200">',
    '<meta property="og:image:height" content="630">',
    `<meta property="og:image:alt" content="${attr(T.og_alt)}">`,
    '<meta name="twitter:card" content="summary_large_image">',
    `<meta name="twitter:title" content="${attr(T.meta_title)}">`,
    `<meta name="twitter:description" content="${attr(T.meta_desc)}">`,
    `<meta name="twitter:image" content="${og}">`,
    `<meta name="twitter:image:alt" content="${attr(T.og_alt)}">`,
    `<script type="application/ld+json">${json(app)}</script>`,
  ].join('\n');
}

// Only the texts main.js still writes at run time are inlined (the rest is already in the page)
const RUNTIME = /^(h_|t_|g_|gc_|calc_half$|size_mb$|released$|sha_|proof_|share_text$|link_copied$|copy_fail$)/;
const runtimeTexts = (T) => Object.fromEntries(Object.entries(T).filter(([k]) => RUNTIME.test(k)));

const version = await latestVersion();
for (const L of LANGS) {
  const T = I18N[L];
  const sub = L !== 'en';
  let html = template
    .replace('<html lang="en">', `<html lang="${L}">`)
    .replace('<!--BASE-->', () => (sub ? '<base href="../">' : ''))
    .replace('<!--LANG-REDIRECT-->', () => redirect)
    .replace('<!--SEO-->', () => seo(L, T, version))
    .replace('<!--I18N-->', () => `<script>window.I18N=${json({ [L]: runtimeTexts(T) })};window.BUBBLES=${json({ [L]: BUBBLES[L] })};</script>`)
    .replace(/shots\/en-/g, `shots/${L}-`)
    .replace(new RegExp(`(data-lang="${L}" lang="${L}" aria-label="[^"]+") aria-pressed="false"`), '$1 aria-pressed="true" class="on"');
  if (sub) html = html.replace(/href="#/g, `href="${L}/#`); // same-page anchors, with <base href="../">
  html = fill(html, T).replace(/\n{3,}/g, '\n\n');
  const dir = sub ? join(OUT, L) : OUT;
  mkdirSync(dir, { recursive: true });
  writeFileSync(join(dir, 'index.html'), html);
}

const today = new Date().toISOString().slice(0, 10);
const alts = LANGS.map((l) => `    <xhtml:link rel="alternate" hreflang="${l}" href="${URLS[l]}"/>`).join('\n') + `\n    <xhtml:link rel="alternate" hreflang="x-default" href="${URLS.en}"/>`;
writeFileSync(join(OUT, 'sitemap.xml'), `<?xml version="1.0" encoding="UTF-8"?>
<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9" xmlns:xhtml="http://www.w3.org/1999/xhtml">
${LANGS.map((l) => `  <url>\n    <loc>${URLS[l]}</loc>\n    <lastmod>${today}</lastmod>\n${alts}\n  </url>`).join('\n')}
</urlset>
`);
rmSync(join(OUT, 'i18n.js'), { force: true }); // the texts of each page are inlined in it
console.log(`pre-rendered ${LANGS.join(', ')}${version ? ` (release ${version})` : ' (no release found)'}`);
