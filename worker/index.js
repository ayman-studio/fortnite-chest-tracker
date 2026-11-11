// Cloudflare Worker in front of the static site: only /api/* runs code (wrangler.jsonc: run_worker_first), every
// other path is served from the assets. /api/estimate?name=<Epic name> returns the matches of the season and of all
// time from the player's PUBLIC Fortnite stats (fortnite-api.com, key stored as the FORTNITE_API_KEY secret), so the
// website can estimate their chests without the app. No Epic login, nothing stored: answers are cached 15 minutes.

const JSON_HEADERS = { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'public, max-age=900', 'x-content-type-options': 'nosniff' };
const reply = (status, body) => new Response(JSON.stringify(body), { status, headers: JSON_HEADERS });

async function stats(name, window, key) {
  const url = `https://fortnite-api.com/v2/stats/br/v2?name=${encodeURIComponent(name)}&timeWindow=${window}`;
  const res = await fetch(url, { headers: { Authorization: key, 'User-Agent': 'fortnite-chest-tracker.com' } });
  let body = null;
  try { body = await res.json(); } catch { /* not JSON */ }
  return { status: res.status, body };
}

const matchesOf = (body) => {
  const all = body?.data?.stats?.all;
  const n = all?.overall?.matches;
  return Number.isFinite(n) ? n : null;
};

async function estimate(url, env, ctx) {
  const name = (url.searchParams.get('name') || '').trim();
  if (name.length < 3 || name.length > 32) return reply(400, { error: 'name' });
  if (!env.FORTNITE_API_KEY) return reply(503, { error: 'not_configured' });

  const cacheKey = new Request(`https://cache.fortnite-chest-tracker.com/estimate/${encodeURIComponent(name.toLowerCase())}`);
  const cache = caches.default;
  const hit = await cache.match(cacheKey);
  if (hit) return hit;

  const [season, lifetime] = await Promise.all([stats(name, 'season', env.FORTNITE_API_KEY), stats(name, 'lifetime', env.FORTNITE_API_KEY)]);
  let res;
  if (season.status === 200 || lifetime.status === 200) {
    res = reply(200, {
      name: season.body?.data?.account?.name || lifetime.body?.data?.account?.name || name,
      season: season.status === 200 ? matchesOf(season.body) : null,
      lifetime: lifetime.status === 200 ? matchesOf(lifetime.body) : null,
    });
  } else {
    const status = season.status;
    res = reply(status === 404 ? 404 : status === 403 ? 403 : status === 429 ? 429 : 502,
      { error: status === 404 ? 'not_found' : status === 403 ? 'private' : status === 429 ? 'busy' : 'unavailable' });
  }
  if (res.status === 200 || res.status === 404 || res.status === 403) ctx.waitUntil(cache.put(cacheKey, res.clone()));
  return res;
}

export default {
  async fetch(request, env, ctx) {
    const url = new URL(request.url);
    if (url.pathname === '/api/estimate' && request.method === 'GET') {
      try {
        return await estimate(url, env, ctx);
      } catch {
        return reply(502, { error: 'unavailable' });
      }
    }
    if (url.pathname.startsWith('/api/')) return reply(404, { error: 'not_found' });
    return env.ASSETS.fetch(request);
  },
};
