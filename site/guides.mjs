// Guide pages (static, no script): what players search for around the app. One page per guide and language, with
// the same look as the home page, their own SEO tags (canonical, hreflang, Open Graph, JSON-LD) and links back to
// the home page. Used by prerender.mjs: buildGuides(out) writes the pages and returns their sitemap entries.
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const BASE = 'https://fortnite-chest-tracker.com/';
const REPO_DL = 'https://github.com/ayman-studio/fortnite-chest-tracker/releases/latest';
const LANGS = ['en', 'fr', 'es'];
const HOME = { en: '', fr: 'fr/', es: 'es/' };
const LOCALE = { en: 'en-GB', fr: 'fr-FR', es: 'es-ES' };
const OG_LOCALE = { en: 'en_GB', fr: 'fr_FR', es: 'es_ES' };
const PUBLISHED = '2026-11-10';

// Epic's rates, per chest opened in a normal match (same as main.js and TreasureOdds.cs); boost = Treasure Hours
const ODDS = [
  { key: 'pirate', odds: 137500, boost: 1.5 }, { key: 'gold', odds: 1562500, boost: 7.5 }, { key: 'vehicle', odds: 7813, boost: 10 },
  { key: 'pickaxe', odds: 568 }, { key: 'jam', odds: 391 }, { key: 'loading', odds: 272 }, { key: 'gizmo', odds: 127 },
];
const AFTER = [500, 2000, 10000];

const esc = (s) => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
const attr = (s) => esc(s).replace(/"/g, '&quot;');
const json = (o) => JSON.stringify(o).replace(/</g, '\\u003c');
const nbsp = (s) => s.replace(/ /g, ' ');
const nf = (L, n) => nbsp(new Intl.NumberFormat(LOCALE[L], { useGrouping: 'always' }).format(n));
const pct = (L, p) => {
  const o = p >= 0.1 ? { maximumFractionDigits: 0 } : p >= 0.01 ? { maximumFractionDigits: 1 } : { maximumSignificantDigits: 2 };
  return nbsp(new Intl.NumberFormat(LOCALE[L], { style: 'percent', ...o }).format(p > 0.995 ? 0.99 : p));
};
const chance = (chests, odds) => 1 - Math.pow(1 - 1 / odds, chests);
const half = (odds) => Math.ceil(Math.log(0.5) / Math.log(1 - 1 / odds));
const pirate = ODDS[0];

// Texts shared by every guide
const UI = {
  en: { home: 'Home', guides: 'Guides', download: 'Download', dl_long: 'Download for Windows', free: 'Free · Windows 10 / 11 · Open source',
    see_app: 'See the app', other: 'Other guide', fan: 'Fan project, not affiliated with or endorsed by Epic Games. Fortnite is a trademark of Epic Games, Inc.',
    by: 'Made by', updated: 'Updated', idea: 'Original idea: <a href="https://github.com/LED-N/Fortnite-ChestCounter" rel="noopener">Fortnite-ChestCounter</a> by LED-N' },
  fr: { home: 'Accueil', guides: 'Guides', download: 'Télécharger', dl_long: 'Télécharger pour Windows', free: 'Gratuit · Windows 10 / 11 · Open source',
    see_app: "Voir l'appli", other: 'Autre guide', fan: "Projet de fan, ni affilié ni approuvé par Epic Games. Fortnite est une marque d'Epic Games, Inc.",
    by: 'Créé par', updated: 'Mis à jour le', idea: 'Idée originale : <a href="https://github.com/LED-N/Fortnite-ChestCounter" rel="noopener">Fortnite-ChestCounter</a> de LED-N' },
  es: { home: 'Inicio', guides: 'Guías', download: 'Descargar', dl_long: 'Descargar para Windows', free: 'Gratis · Windows 10 / 11 · Código abierto',
    see_app: 'Ver la app', other: 'Otra guía', fan: 'Proyecto de fans, no afiliado ni respaldado por Epic Games. Fortnite es una marca de Epic Games, Inc.',
    by: 'Creado por', updated: 'Actualizado el', idea: 'Idea original: <a href="https://github.com/LED-N/Fortnite-ChestCounter" rel="noopener">Fortnite-ChestCounter</a> de LED-N' },
};

const TREASURE = {
  en: { pirate: 'Pirate token', gold: 'Gold outfit', vehicle: 'Vehicle & decal', pickaxe: 'Pickaxe or back bling', jam: 'Jam track', loading: 'Loading screen', gizmo: 'Gizmo' },
  fr: { pirate: 'Jeton pirate', gold: 'Tenue dorée', vehicle: 'Véhicule et décalcomanie', pickaxe: 'Pioche ou sac à dos', jam: 'Piste musicale', loading: 'Écran de chargement', gizmo: 'Gizmo' },
  es: { pirate: 'Ficha pirata', gold: 'Atuendo dorado', vehicle: 'Vehículo y calcomanía', pickaxe: 'Pico o mochila', jam: 'Pista musical', loading: 'Pantalla de carga', gizmo: 'Gizmo' },
};

// ---------------------------------------------------------------- season calendar (the app's season.json)
const SEASON = JSON.parse(readFileSync(join(dirname(fileURLToPath(import.meta.url)), '..', 'src', 'FortniteChestTracker', 'Web', 'wwwroot', 'season.json'), 'utf8'));
const S_TXT = {
  en: { title: 'Fortnite Treasure Hunters: Treasure Hours, weekly prizes and odds · Chest Tracker',
    desc: 'Every Treasure Hour of the Treasure Hunters season in your local time, with its boosts, a live countdown, the prizes of each week and how the pirate token drops.',
    h1: 'Treasure Hunters: Treasure Hours and weekly prizes', kicker: 'Season calendar',
    lead: 'When the next Treasure Hour starts in your time zone, what it boosts, and what each week of the hunt gives away.',
    next: 'Next Treasure Hour', now: 'Treasure Hour on now', ends: 'ends in', starts: 'starts in', seasonEnd: 'End of the season in', over: 'The season is over.',
    hoursTitle: 'Every Treasure Hour', when: 'When (your time)', boosts: 'Boosts', status: '', expected: 'usual time, not confirmed yet', past: 'over', live: 'NOW',
    pirate: 'Pirate token', gold: 'Gold outfit', vehicle: 'Vehicle',
    weeksTitle: 'Prizes week by week', week: 'Week', unknown: 'Prizes not announced yet: this page updates as soon as they are.',
    type: { outfit: 'Outfit', pickaxe: 'Pickaxe', backbling: 'Back bling', vehicle: 'Vehicle', jam: 'Jam track', loading: 'Loading screen' }, oneIn: '1 in',
    rulesTitle: 'Good to know',
    rules: ['During a Treasure Hour, each chest you open or break gives you more chances for the boosted treasures: the pirate token, the gold outfit and the vehicle of the week.',
      'A pirate token found by anyone in your party is shared with the whole party.', 'Ranked matches give no treasure-hunt prizes.',
      'The weekly prizes change every Sunday: what you did not get one week is gone.'],
    app: 'Chest Tracker counts every chest you open from your replays and works out your real chance, Treasure Hour boosts included. It can also notify you 10 minutes before each Treasure Hour.' },
  fr: { title: 'Fortnite Treasure Hunters : Heures du trésor, prix de la semaine et chances · Chest Tracker',
    desc: "Toutes les Heures du trésor de la saison Treasure Hunters à ton heure locale, avec leurs bonus, un compte à rebours, les prix de chaque semaine et comment tombe le jeton pirate.",
    h1: 'Treasure Hunters : Heures du trésor et prix de la semaine', kicker: 'Calendrier de la saison',
    lead: "Quand commence la prochaine Heure du trésor chez toi, ce qu'elle booste, et ce que chaque semaine de la chasse fait gagner.",
    next: 'Prochaine Heure du trésor', now: 'Heure du trésor en cours', ends: 'se termine dans', starts: 'commence dans', seasonEnd: 'Fin de la saison dans', over: 'La saison est terminée.',
    hoursTitle: 'Toutes les Heures du trésor', when: 'Quand (ton heure)', boosts: 'Bonus', status: '', expected: 'horaire habituel, pas encore confirmé', past: 'passée', live: 'EN COURS',
    pirate: 'Jeton pirate', gold: 'Tenue dorée', vehicle: 'Véhicule',
    weeksTitle: 'Les prix semaine par semaine', week: 'Semaine', unknown: "Prix pas encore annoncés : la page se met à jour dès qu'ils le sont.",
    type: { outfit: 'Tenue', pickaxe: 'Pioche', backbling: 'Sac à dos', vehicle: 'Véhicule', jam: 'Piste musicale', loading: 'Écran de chargement' }, oneIn: '1 sur',
    rulesTitle: 'Bon à savoir',
    rules: ["Pendant une Heure du trésor, chaque coffre ouvert ou cassé te donne plus de chances pour les trésors boostés : le jeton pirate, la tenue dorée et le véhicule de la semaine.",
      "Un jeton pirate trouvé par un membre de ton escouade est partagé avec toute l'escouade.", "Les parties classées ne donnent aucun prix de la chasse au trésor.",
      "Les prix de la semaine changent chaque dimanche : ce que tu n'as pas eu une semaine est perdu."],
    app: "Chest Tracker compte chaque coffre que tu ouvres grâce à tes replays et calcule ta vraie chance, bonus des Heures du trésor compris. Il peut aussi te prévenir 10 minutes avant chaque Heure du trésor." },
  es: { title: 'Fortnite Treasure Hunters: Horas del tesoro, premios semanales y probabilidades · Chest Tracker',
    desc: 'Todas las Horas del tesoro de la temporada Treasure Hunters en tu hora local, con sus bonificaciones, una cuenta atrás, los premios de cada semana y cómo sale la ficha pirata.',
    h1: 'Treasure Hunters: Horas del tesoro y premios semanales', kicker: 'Calendario de la temporada',
    lead: 'Cuándo empieza la próxima Hora del tesoro en tu zona horaria, qué bonifica y qué da cada semana de la búsqueda.',
    next: 'Próxima Hora del tesoro', now: 'Hora del tesoro en curso', ends: 'termina en', starts: 'empieza en', seasonEnd: 'Fin de la temporada en', over: 'La temporada ha terminado.',
    hoursTitle: 'Todas las Horas del tesoro', when: 'Cuándo (tu hora)', boosts: 'Bonificaciones', status: '', expected: 'horario habitual, aún sin confirmar', past: 'terminada', live: 'EN CURSO',
    pirate: 'Ficha pirata', gold: 'Atuendo dorado', vehicle: 'Vehículo',
    weeksTitle: 'Los premios semana a semana', week: 'Semana', unknown: 'Premios aún sin anunciar: la página se actualiza en cuanto se anuncien.',
    type: { outfit: 'Atuendo', pickaxe: 'Pico', backbling: 'Mochila', vehicle: 'Vehículo', jam: 'Pista musical', loading: 'Pantalla de carga' }, oneIn: '1 de',
    rulesTitle: 'Conviene saber',
    rules: ['Durante una Hora del tesoro, cada cofre que abres o rompes te da más posibilidades para los tesoros bonificados: la ficha pirata, el atuendo dorado y el vehículo de la semana.',
      'Una ficha pirata encontrada por alguien de tu escuadrón se comparte con todo el escuadrón.', 'Las partidas clasificatorias no dan premios de la búsqueda del tesoro.',
      'Los premios semanales cambian cada domingo: lo que no conseguiste una semana se pierde.'],
    app: 'Chest Tracker cuenta cada cofre que abres gracias a tus repeticiones y calcula tu probabilidad real, con las bonificaciones de las Horas del tesoro. También puede avisarte 10 minutos antes de cada Hora del tesoro.' },
};
const ODDS_BY_KEY = { odds_pirate: 137500, odds_gold: 1562500, odds_vehicle: 7813, odds_pickaxe: 568, odds_jam: 391, odds_loading: 272 };
const utcSlot = (a, b) => `${new Date(a).toISOString().slice(0, 16).replace('T', ' ')}–${new Date(b).toISOString().slice(11, 16)} UTC`;

function seasonPage(L) {
  const T = S_TXT[L];
  const boostText = (b) => [['odds_pirate', T.pirate], ['odds_gold', T.gold], ['odds_vehicle', T.vehicle]]
    .filter(([k]) => b[k] > 1).map(([k, n]) => `${esc(n)} <b>×${nf(L, b[k])}</b>`).join(' · ');
  const hours = SEASON.treasureHours.map((h) => `<tr data-start="${h.start}" data-end="${h.end}"><td data-slot="${h.start}|${h.end}">${esc(utcSlot(h.start, h.end))}</td><td>${boostText(h.boosts)}</td><td class="g-st">${h.confirmed ? '' : `<span class="g-exp">${esc(T.expected)}</span>`}</td></tr>`).join('\n');
  const weeks = SEASON.weeks.map((w, i) => {
    const end = i + 1 < SEASON.weeks.length ? SEASON.weeks[i + 1].start : SEASON.end;
    const prizes = w.prizes.length
      ? `<div class="g-prizes">${w.prizes.map((p) => `<figure class="g-prize"><img src="/img/${esc(p.img)}" alt="${attr(p.name)}" loading="lazy"><figcaption><b>${esc(p.name)}</b><span>${esc(T.type[p.key] || p.key)}${ODDS_BY_KEY[p.odds] ? ` · ${esc(T.oneIn)} ${nf(L, ODDS_BY_KEY[p.odds])}` : ''}</span></figcaption></figure>`).join('')}</div>`
      : `<p class="g-unknown">${esc(T.unknown)}</p>`;
    return `<section class="g-week" data-start="${w.start}" data-end="${end}"><div class="g-week-h">${w.art ? `<img src="/img/${esc(w.art)}" alt="" width="44" height="44">` : ''}<div><b>${esc(T.week)} ${i + 1} · ${esc(w.title)}</b><span data-slot="${w.start}|${end}" data-days="1">${esc(utcSlot(w.start, end))}</span></div></div>${prizes}</section>`;
  }).join('\n');
  const script = `<script>(function(){var L=document.documentElement.lang,loc={en:'en-GB',fr:'fr-FR',es:'es-ES'}[L]||'en-GB',D={en:'d',fr:'j',es:'d'}[L]||'d';
var df=new Intl.DateTimeFormat(loc,{weekday:'short',day:'numeric',month:'short'}),tf=new Intl.DateTimeFormat(loc,{hour:'2-digit',minute:'2-digit'});
document.querySelectorAll('[data-slot]').forEach(function(el){var p=el.dataset.slot.split('|'),a=new Date(p[0]),b=new Date(p[1]);el.textContent=el.dataset.days?df.format(a)+' → '+df.format(b):df.format(a)+', '+tf.format(a)+'–'+tf.format(b);});
function left(ms){var m=Math.max(0,Math.floor(ms/6e4)),d=Math.floor(m/1440),h=Math.floor(m%1440/60),mi=m%60,s=Math.max(0,Math.floor(ms/1e3)%60);return d>0?d+' '+D+' '+h+' h':h>0?h+' h '+String(mi).padStart(2,'0'):mi+' min '+String(s).padStart(2,'0')+' s';}
var rows=[].slice.call(document.querySelectorAll('tr[data-start]')),weeks=[].slice.call(document.querySelectorAll('.g-week')),box=document.getElementById('g-next'),end=Date.parse(${JSON.stringify(SEASON.end)});
function tick(){var now=Date.now(),cur=null,next=null;rows.forEach(function(r){var a=Date.parse(r.dataset.start),b=Date.parse(r.dataset.end);r.className=now>=b?'past':now>=a?'now':'';if(now>=a&&now<b)cur=r;else if(a>now&&!next)next=r;});
weeks.forEach(function(w){w.classList.toggle('now',now>=Date.parse(w.dataset.start)&&now<Date.parse(w.dataset.end));});
var r=cur||next;box.classList.toggle('on',!!cur);box.querySelector('.g-next-t').textContent=cur?${JSON.stringify(T.now)}:r?${JSON.stringify(T.next)}:${JSON.stringify(T.over)};
box.querySelector('.g-next-c').textContent=r?(cur?${JSON.stringify(T.ends)}:${JSON.stringify(T.starts)})+' '+left(Date.parse(cur?r.dataset.end:r.dataset.start)-now):'';
box.querySelector('.g-next-w').textContent=r?r.cells[0].textContent:'';box.querySelector('.g-next-b').innerHTML=r?r.cells[1].innerHTML:'';
document.getElementById('g-end').textContent=now<end?${JSON.stringify(T.seasonEnd)}+' '+left(end-now):${JSON.stringify(T.over)};}
tick();setInterval(tick,1000);})();</script>`;
  return {
    title: T.title, desc: T.desc, h1: T.h1, kicker: T.kicker, lead: T.lead,
    body: `<div class="g-next" id="g-next"><span class="g-next-t">${esc(T.next)}</span><b class="g-next-c"></b><span class="g-next-w"></span><span class="g-next-b"></span><span class="g-end" id="g-end"></span></div>
<h2>${esc(T.hoursTitle)}</h2>
<div class="g-table-wrap"><table class="g-table g-hours"><thead><tr><th>${esc(T.when)}</th><th>${esc(T.boosts)}</th><th></th></tr></thead><tbody>
${hours}
</tbody></table></div>
<h2>${esc(T.weeksTitle)}</h2>
${weeks}
<h2>${esc(T.rulesTitle)}</h2>
<ul>${T.rules.map((r) => `<li>${esc(r)}</li>`).join('')}</ul>
<p>${esc(T.app)}</p>
${script}`,
  };
}

// ---------------------------------------------------------------- the guides
const GUIDES = {
  season: {
    path: { en: 'guides/treasure-hunters/', fr: 'fr/guides/chasse-au-tresor/', es: 'es/guides/caza-del-tesoro/' },
    page: seasonPage,
  },
  odds: {
    path: { en: 'guides/pirate-token-odds/', fr: 'fr/guides/chances-jeton-pirate/', es: 'es/guides/probabilidad-ficha-pirata/' },
    page: (L) => {
      const T = TREASURE[L];
      const p500 = pct(L, chance(500, pirate.odds)), p2k = pct(L, chance(2000, pirate.odds)), p10k = pct(L, chance(10000, pirate.odds));
      const h = nf(L, half(pirate.odds)), hm = nf(L, Math.round(half(pirate.odds) / 20)), th = nf(L, Math.round(pirate.odds / pirate.boost));
      const table = `<div class="g-table-wrap"><table class="g-table">
<thead><tr><th>${{ en: 'Treasure', fr: 'Trésor', es: 'Tesoro' }[L]}</th><th>${{ en: '1 chest in', fr: '1 coffre sur', es: '1 cofre de cada' }[L]}</th>${AFTER.map((n) => `<th>${{ en: `After ${nf(L, n)} chests`, fr: `Après ${nf(L, n)} coffres`, es: `Tras ${nf(L, n)} cofres` }[L]}</th>`).join('')}<th>${{ en: '50% chance at', fr: '50 % de chance à', es: '50 % de probabilidad a' }[L]}</th></tr></thead>
<tbody>${ODDS.map((o) => `<tr${o.key === 'pirate' ? ' class="hl"' : ''}><th scope="row">${o.key === 'pirate' ? '<img src="/img/token.webp" alt="" width="22" height="22">' : ''}${esc(T[o.key])}</th><td>${nf(L, o.odds)}</td>${AFTER.map((n) => `<td>${pct(L, chance(n, o.odds))}</td>`).join('')}<td>${nf(L, half(o.odds))}</td></tr>`).join('\n')}</tbody>
</table></div>`;
      const boosts = ODDS.filter((o) => o.boost).map((o) => `${esc(T[o.key])} ×${nf(L, o.boost)}`).join(', ');
      return {
        en: {
          title: `Fortnite pirate token odds: 1 chest in ${nf(L, pirate.odds)} · Chest Tracker`,
          desc: `Epic's real drop rates for every Fortnite chest treasure (pirate token, gold outfit, vehicle, pickaxe, jam track) and your chance after 500, 2,000 or 10,000 chests.`,
          h1: 'Fortnite pirate token odds', kicker: 'Epic\'s real rates',
          lead: 'Epic announced how often each treasure drops from a chest. Here is what those rates mean for you, chest after chest.',
          body: `<h2>The rates, per chest opened</h2>
<p>Per chest opened in a normal match, outside Treasure Hours. Every chest is a new draw: the table shows your chance of getting each treasure at least once.</p>
${table}
<h2>How your chance adds up</h2>
<p>The chance of getting a treasure at least once after <i>N</i> chests is <b>1 − (1 − 1/rate)<sup>N</sup></b>. For the pirate token (1 chest in ${nf(L, pirate.odds)}), that is about <b>${p500}</b> after 500 chests, <b>${p2k}</b> after 2,000 and <b>${p10k}</b> after 10,000.</p>
<p>To reach a 50% chance, you need about <b>${h} chests</b>: around ${hm} matches at 20 chests per match. Bad luck does not build up: each new chest has exactly the same odds as the first one.</p>
<h2>Treasure Hours</h2>
<p>During Treasure Hours, Epic raises some rates: ${boosts}. The pirate token then drops 1 chest in ${th}.</p>
<h2>How many chests have you opened?</h2>
<p>Fortnite does not show it anywhere. Chest Tracker reads the replays Fortnite saves on your PC, counts every chest you open and works out your real chance for each treasure, match after match.</p>`,
        },
        fr: {
          title: `Chances du jeton pirate Fortnite : 1 coffre sur ${nf(L, pirate.odds)} · Chest Tracker`,
          desc: `Les vrais taux d'Epic pour chaque trésor des coffres Fortnite (jeton pirate, tenue dorée, véhicule, pioche, piste musicale) et tes chances après 500, 2 000 ou 10 000 coffres.`,
          h1: 'Chances du jeton pirate dans Fortnite', kicker: "Les vrais taux d'Epic",
          lead: "Epic a annoncé à quelle fréquence chaque trésor tombe d'un coffre. Voici ce que ces taux veulent dire pour toi, coffre après coffre.",
          body: `<h2>Les taux, par coffre ouvert</h2>
<p>Par coffre ouvert en partie normale, hors Heures du trésor. Chaque coffre est un nouveau tirage : le tableau donne ta chance d'avoir chaque trésor au moins une fois.</p>
${table}
<h2>Comment ta chance grimpe</h2>
<p>La chance d'avoir un trésor au moins une fois après <i>N</i> coffres vaut <b>1 − (1 − 1/taux)<sup>N</sup></b>. Pour le jeton pirate (1 coffre sur ${nf(L, pirate.odds)}), ça donne environ <b>${p500}</b> après 500 coffres, <b>${p2k}</b> après 2 000 et <b>${p10k}</b> après 10 000.</p>
<p>Pour atteindre 50 % de chance, il faut environ <b>${h} coffres</b> : à peu près ${hm} parties à 20 coffres par partie. La malchance ne s'accumule pas : chaque nouveau coffre a exactement les mêmes chances que le premier.</p>
<h2>Heures du trésor</h2>
<p>Pendant les Heures du trésor, Epic augmente certains taux : ${boosts}. Le jeton pirate tombe alors 1 coffre sur ${th}.</p>
<h2>Combien de coffres as-tu ouverts ?</h2>
<p>Fortnite ne l'affiche nulle part. Chest Tracker lit les replays que Fortnite enregistre sur ton PC, compte chaque coffre que tu ouvres et calcule ta vraie chance pour chaque trésor, partie après partie.</p>`,
        },
        es: {
          title: `Probabilidad de la ficha pirata en Fortnite: 1 cofre de cada ${nf(L, pirate.odds)} · Chest Tracker`,
          desc: `Las tasas reales de Epic para cada tesoro de los cofres de Fortnite (ficha pirata, atuendo dorado, vehículo, pico, pista musical) y tu probabilidad tras 500, 2.000 o 10.000 cofres.`,
          h1: 'Probabilidad de la ficha pirata en Fortnite', kicker: 'Las tasas reales de Epic',
          lead: 'Epic anunció cada cuánto sale cada tesoro de un cofre. Esto es lo que significan esas tasas para ti, cofre tras cofre.',
          body: `<h2>Las tasas, por cofre abierto</h2>
<p>Por cofre abierto en una partida normal, fuera de las Horas del tesoro. Cada cofre es un nuevo sorteo: la tabla muestra tu probabilidad de conseguir cada tesoro al menos una vez.</p>
${table}
<h2>Cómo sube tu probabilidad</h2>
<p>La probabilidad de conseguir un tesoro al menos una vez tras <i>N</i> cofres es <b>1 − (1 − 1/tasa)<sup>N</sup></b>. Para la ficha pirata (1 cofre de cada ${nf(L, pirate.odds)}), eso da cerca del <b>${p500}</b> tras 500 cofres, el <b>${p2k}</b> tras 2.000 y el <b>${p10k}</b> tras 10.000.</p>
<p>Para llegar a un 50 % de probabilidad hacen falta unos <b>${h} cofres</b>: unas ${hm} partidas a 20 cofres por partida. La mala suerte no se acumula: cada cofre nuevo tiene exactamente la misma probabilidad que el primero.</p>
<h2>Horas del tesoro</h2>
<p>Durante las Horas del tesoro, Epic sube algunas tasas: ${boosts}. La ficha pirata sale entonces 1 cofre de cada ${th}.</p>
<h2>¿Cuántos cofres has abierto?</h2>
<p>Fortnite no lo muestra en ningún sitio. Chest Tracker lee las repeticiones que Fortnite guarda en tu PC, cuenta cada cofre que abres y calcula tu probabilidad real para cada tesoro, partida tras partida.</p>`,
        },
      }[L];
    },
  },
  replays: {
    path: { en: 'guides/fortnite-replays/', fr: 'fr/guides/replays-fortnite/', es: 'es/guides/repeticiones-fortnite/' },
    page: (L) => {
      const folder = '<code class="g-path">%LOCALAPPDATA%\\FortniteGame\\Saved\\Demos</code>';
      return {
        en: {
          title: 'Turn on Fortnite replays & find the replay folder · Chest Tracker',
          desc: 'Turn on Record Replays in Fortnite, find where replays are saved on Windows (%LOCALAPPDATA%\\FortniteGame\\Saved\\Demos) and see why a replay can be missing.',
          h1: 'Fortnite replays: turn them on and find them', kicker: 'Guide',
          lead: 'Fortnite can save a replay of every match on your PC. The option is not always on, and it is what Chest Tracker reads to count your chests.',
          body: `<h2>Turn on replays</h2>
<ol class="g-steps">
<li>In Fortnite, open <b>Settings</b>.</li>
<li>Go to the <b>Game</b> tab.</li>
<li>Scroll down to the <b>Replays</b> section.</li>
<li>Set <b>Record Replays</b> to <b>On</b>.</li>
</ol>
<p>Every match you play from then on is saved. Matches played before are not.</p>
<h2>Where are Fortnite replays saved?</h2>
<p>On Windows, in this folder:</p>
<p>${folder}</p>
<p>Paste it in the File Explorer address bar to open it. Each match is a <code>.replay</code> file. Fortnite deletes its oldest replays after a while to make room: keep a copy of the ones you care about (Chest Tracker can archive them for you).</p>
<h2>A replay is missing?</h2>
<ul>
<li><b>Record Replays was off</b> during that match: only matches played after you turn it on are saved.</li>
<li><b>The match is not over yet</b>: Fortnite writes the replay while you play, but only finishes it when the match ends.</li>
<li><b>Fortnite deleted it</b> to make room for newer ones.</li>
</ul>
<h2>What Chest Tracker does with them</h2>
<p>Once a match ends, Chest Tracker reads its replay, counts the chests you opened and adds them to your total. It only reads the files: it never touches the game.</p>`,
        },
        fr: {
          title: 'Activer les replays Fortnite et trouver leur dossier · Chest Tracker',
          desc: 'Active « Enregistrer les replays » dans Fortnite, trouve où les replays sont enregistrés sur Windows (%LOCALAPPDATA%\\FortniteGame\\Saved\\Demos) et pourquoi un replay peut manquer.',
          h1: 'Replays Fortnite : les activer et les retrouver', kicker: 'Guide',
          lead: "Fortnite peut enregistrer un replay de chaque partie sur ton PC. L'option n'est pas toujours activée, et c'est ce que Chest Tracker lit pour compter tes coffres.",
          body: `<h2>Activer les replays</h2>
<ol class="g-steps">
<li>Dans Fortnite, ouvre les <b>Paramètres</b>.</li>
<li>Va dans l'onglet <b>Jeu</b>.</li>
<li>Descends jusqu'à la section <b>Replays</b>.</li>
<li>Mets <b>Enregistrer les replays</b> sur <b>Activé</b>.</li>
</ol>
<p>Chaque partie jouée à partir de là est enregistrée. Celles d'avant ne le sont pas.</p>
<h2>Où sont enregistrés les replays Fortnite ?</h2>
<p>Sur Windows, dans ce dossier :</p>
<p>${folder}</p>
<p>Colle-le dans la barre d'adresse de l'Explorateur de fichiers pour l'ouvrir. Chaque partie est un fichier <code>.replay</code>. Fortnite efface ses replays les plus anciens au bout d'un moment pour faire de la place : garde une copie de ceux qui comptent pour toi (Chest Tracker peut les archiver pour toi).</p>
<h2>Il manque un replay ?</h2>
<ul>
<li><b>« Enregistrer les replays » était désactivé</b> pendant cette partie : seules les parties jouées après l'activation sont enregistrées.</li>
<li><b>La partie n'est pas finie</b> : Fortnite écrit le replay pendant que tu joues, mais ne le termine qu'à la fin de la partie.</li>
<li><b>Fortnite l'a effacé</b> pour faire de la place aux plus récents.</li>
</ul>
<h2>Ce que Chest Tracker en fait</h2>
<p>À la fin d'une partie, Chest Tracker lit son replay, compte les coffres que tu as ouverts et les ajoute à ton total. Il lit seulement les fichiers : il ne touche jamais au jeu.</p>`,
        },
        es: {
          title: 'Activar las repeticiones de Fortnite y su carpeta · Chest Tracker',
          desc: 'Activa «Grabar repeticiones» en Fortnite, encuentra dónde se guardan las repeticiones en Windows (%LOCALAPPDATA%\\FortniteGame\\Saved\\Demos) y por qué puede faltar una.',
          h1: 'Repeticiones de Fortnite: actívalas y encuéntralas', kicker: 'Guía',
          lead: 'Fortnite puede guardar una repetición de cada partida en tu PC. La opción no siempre está activada, y es lo que lee Chest Tracker para contar tus cofres.',
          body: `<h2>Activar las repeticiones</h2>
<ol class="g-steps">
<li>En Fortnite, abre los <b>Ajustes</b>.</li>
<li>Ve a la pestaña <b>Juego</b>.</li>
<li>Baja hasta la sección <b>Repeticiones</b>.</li>
<li>Pon <b>Grabar repeticiones</b> en <b>Sí</b>.</li>
</ol>
<p>Cada partida que juegues a partir de ahí se guarda. Las anteriores no.</p>
<h2>¿Dónde se guardan las repeticiones de Fortnite?</h2>
<p>En Windows, en esta carpeta:</p>
<p>${folder}</p>
<p>Pégala en la barra de direcciones del Explorador de archivos para abrirla. Cada partida es un archivo <code>.replay</code>. Fortnite borra sus repeticiones más antiguas pasado un tiempo para hacer sitio: guarda una copia de las que te importan (Chest Tracker puede archivarlas por ti).</p>
<h2>¿Falta una repetición?</h2>
<ul>
<li><b>«Grabar repeticiones» estaba desactivado</b> en esa partida: solo se guardan las partidas jugadas después de activarlo.</li>
<li><b>La partida no ha terminado</b>: Fortnite escribe la repetición mientras juegas, pero solo la termina al final de la partida.</li>
<li><b>Fortnite la borró</b> para hacer sitio a las más recientes.</li>
</ul>
<h2>Lo que hace Chest Tracker con ellas</h2>
<p>Al acabar una partida, Chest Tracker lee su repetición, cuenta los cofres que abriste y los suma a tu total. Solo lee los archivos: nunca toca el juego.</p>`,
        },
      }[L];
    },
  },
};

// ---------------------------------------------------------------- page
function render(id, L, version) {
  const g = GUIDES[id];
  const P = g.page(L);
  const U = UI[L];
  const url = BASE + g.path[L];
  const home = BASE + HOME[L];
  const ids = Object.keys(GUIDES);
  const otherId = ids[(ids.indexOf(id) + 1) % ids.length];
  const other = GUIDES[otherId];
  const otherTitle = other.page(L).h1;
  const og = `${BASE}og/og-${L}.jpg`;
  const ld = {
    '@context': 'https://schema.org',
    '@graph': [
      { '@type': 'Article', headline: P.h1, description: P.desc, inLanguage: L, url, mainEntityOfPage: url, image: og,
        datePublished: PUBLISHED, dateModified: new Date().toISOString().slice(0, 10),
        author: { '@type': 'Person', name: 'Ayman Studio', url: 'https://x.com/ayman_studioo' },
        publisher: { '@type': 'Organization', name: 'Fortnite - Chest Tracker', url: BASE, logo: { '@type': 'ImageObject', url: BASE + 'icons/icon-192.png' } } },
      { '@type': 'BreadcrumbList', itemListElement: [
        { '@type': 'ListItem', position: 1, name: 'Fortnite - Chest Tracker', item: home },
        { '@type': 'ListItem', position: 2, name: P.h1, item: url },
      ] },
    ],
  };
  const lang = LANGS.map((l) => `<a href="/${g.path[l]}" hreflang="${l}" lang="${l}"${l === L ? ' class="on" aria-current="true"' : ''}>${l.toUpperCase()}</a>`).join('');
  return `<!doctype html>
<html lang="${L}">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>${esc(P.title)}</title>
<meta name="description" content="${attr(P.desc)}">
<meta name="theme-color" content="#071447">
<link rel="canonical" href="${url}">
${LANGS.map((l) => `<link rel="alternate" hreflang="${l}" href="${BASE + g.path[l]}">`).join('\n')}
<link rel="alternate" hreflang="x-default" href="${BASE + g.path.en}">
<meta property="og:type" content="article">
<meta property="og:site_name" content="Fortnite - Chest Tracker">
<meta property="og:title" content="${attr(P.h1)}">
<meta property="og:description" content="${attr(P.desc)}">
<meta property="og:url" content="${url}">
<meta property="og:locale" content="${OG_LOCALE[L]}">
<meta property="og:image" content="${og}">
<meta property="og:image:width" content="1200">
<meta property="og:image:height" content="630">
<meta name="twitter:card" content="summary_large_image">
<meta name="twitter:title" content="${attr(P.h1)}">
<meta name="twitter:description" content="${attr(P.desc)}">
<meta name="twitter:image" content="${og}">
<script type="application/ld+json">${json(ld)}</script>
<link rel="icon" href="/icons/favicon-32.png" type="image/png" sizes="32x32">
<link rel="icon" href="/icons/favicon-48.png" type="image/png" sizes="48x48">
<link rel="icon" href="/icons/icon-192.png" type="image/png" sizes="192x192">
<link rel="apple-touch-icon" href="/icons/apple-touch-icon.png">
<link rel="preload" href="/fonts/anton-latin.woff2" as="font" type="font/woff2" crossorigin>
<link rel="stylesheet" href="/style.css">
</head>
<body class="guide-page">
<header class="nav solid">
  <div class="wrap nav-in">
    <a class="brand" href="/${HOME[L]}" aria-label="Fortnite - Chest Tracker">
      <img src="/img/relic-chest.webp" alt="" width="40" height="40">
      <span><small>FORTNITE</small><b>CHEST <em>TRACKER</em></b></span>
    </a>
    <div class="nav-right">
      <nav class="lang g-lang" aria-label="Language">${lang}</nav>
      <a class="btn btn-lime btn-sm" href="${REPO_DL}">${esc(U.download)}</a>
    </div>
  </div>
</header>
<main class="guide">
  <article class="wrap g-in">
    <nav class="g-crumbs" aria-label="Breadcrumb"><a href="/${HOME[L]}">${esc(U.home)}</a> <span aria-hidden="true">›</span> <span>${esc(U.guides)}</span></nav>
    <span class="eyebrow">${esc(P.kicker)}</span>
    <h1>${esc(P.h1)}</h1>
    <p class="g-lead">${esc(P.lead)}</p>
    ${P.body}
    <div class="g-cta">
      <p class="g-free">${esc(U.free)}</p>
      <div class="g-cta-row">
        <a class="btn btn-lime btn-xl" href="${REPO_DL}"><svg viewBox="0 0 24 24" aria-hidden="true" width="20" height="20"><path fill="currentColor" d="M3 5.5 10.5 4.4v7.1H3zm0 13L10.5 19.6v-7H3zm8.5 1.2L21 21v-8.4h-9.5zm0-15.4v7.2H21V3z"/></svg><span>${esc(U.dl_long)}</span></a>
        <a class="btn btn-ghost btn-xl" href="/${HOME[L]}">${esc(U.see_app)}</a>
      </div>
    </div>
    <p class="g-other">${esc(U.other)} : <a href="/${other.path[L]}">${esc(otherTitle)}</a></p>
  </article>
</main>
<footer class="foot">
  <div class="wrap g-foot">
    <p>${esc(U.fan)}</p>
    <p>${esc(U.by)} <b>Ayman Studio</b> · <a href="https://x.com/ayman_studioo" rel="me noopener">@ayman_studioo</a>${version ? ` · v${esc(version)}` : ''}</p>
    <p class="foot-idea">${U.idea}</p>
  </div>
</footer>
</body>
</html>
`.replace(' : <a', L === 'fr' ? ' : <a' : ': <a');
}

export function buildGuides(out, version) {
  const entries = [];
  const today = new Date().toISOString().slice(0, 10);
  for (const id of Object.keys(GUIDES)) {
    const g = GUIDES[id];
    for (const L of LANGS) {
      const dir = join(out, g.path[L]);
      mkdirSync(dir, { recursive: true });
      writeFileSync(join(dir, 'index.html'), render(id, L, version));
    }
    const alts = LANGS.map((l) => `    <xhtml:link rel="alternate" hreflang="${l}" href="${BASE + g.path[l]}"/>`).join('\n')
      + `\n    <xhtml:link rel="alternate" hreflang="x-default" href="${BASE + g.path.en}"/>`;
    for (const L of LANGS) entries.push(`  <url>\n    <loc>${BASE + g.path[L]}</loc>\n    <lastmod>${today}</lastmod>\n${alts}\n  </url>`);
  }
  return entries;
}

// For the home page: where each guide lives, per language (relative to the site root)
export const GUIDE_PATHS = Object.fromEntries(Object.entries(GUIDES).map(([id, g]) => [id, g.path]));
