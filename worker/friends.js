// Friends and squads of the app ("Potes" tab, squad overlay): one Durable Object with its own SQLite database holds the
// players who share their chests, their friendships, the squads and the matches of the last days.
//
// No Epic login: an app is identified by the Epic account id read in its replays plus a random key it made the first
// time (only its SHA-256 is stored). The first app to use an Epic id owns it; another PC joins with the same key.
// Stored per player: Epic id and name, outfit, a summary of the season (chests, matches, treasure odds, treasures
// found) and the matches of the last 3 days (chests, length, placement). Teammates are only kept when they have the app
// too (to suggest them as friends). Turning sharing off in the app deletes all of it (POST /api/friends/forget).
//
// Live: each app keeps a WebSocket open (/api/friends/live, hibernated: free while idle). It only carries a doorbell:
// "changed" when something the player sees changed (a friend's match, a request, the squad); the app then syncs.
// Limits per IP and per player (requests, name lookups, new accounts) keep scripts from abusing the hub.
//
// Names: an app could claim any name, so names are checked with the public Fortnite stats (fortnite-api.com, the
// FORTNITE_API_KEY secret): when the account's stats are public its real name replaces the one sent and the player is
// "verified"; a friend request by name goes to the Epic account that really has that name. Names are cleaned (no
// control or direction characters) and the summaries friends see are rebuilt field by field (no free-form data).

import { DurableObject } from 'cloudflare:workers';

const EPIC_ID = /^[0-9a-f]{32}$/;
const KEY = /^[0-9a-f]{64}$/;
const SKIN = /^[A-Za-z0-9_-]{1,120}$/;
const SESSION = /^[0-9A-Za-z_.:-]{1,80}$/;
const HOUR = 3600e3;
const SQUAD_IDLE = 6 * HOUR;      // a squad nobody played in for 6 h is closed
const MATCHES_KEPT = 72 * HOUR;   // matches kept for "last match" and the squad session
const MATES_KEPT = 48 * HOUR;     // teammates suggested as friends for 2 days
const PLAYING_STALE = 3 * 60e3;   // "in a match" only while the app keeps saying so
// Durable Objects bill (and cap) the rows written: an app syncing with nothing new writes nothing, except "seen" from
// time to time (fresh enough for "in a match" while playing, for the session and "seen 5 min ago" otherwise).
const SEEN_PLAYING = 60e3;
const SEEN_IDLE = 10 * 60e3;
const SQUAD_TOUCH = 5 * 60e3;     // a squad's "touched" is moved forward at most every 5 min (closed after 6 h idle)
const CLEANUP_EVERY = 60e3;
const MAX_FRIENDS = 100;
const MAX_SQUAD = 4;
const MAX_SOCKETS = 3;            // per player (several PCs, a restart before the old socket closed)

// [limit, window ms] per bucket; the key is the IP or the player.
const LIMITS = {
  ip: [240, 60e3],          // every request of one IP (an app makes a few per minute)
  live: [30, 60e3],         // WebSocket connections opened by one IP
  register: [10, 3600e3],   // new players registered from one IP
  lookupIp: [60, 3600e3],   // friend requests by name from one IP
  lookup: [30, 3600e3],     // friend requests by name from one player
};

const text = (v, max) => (typeof v === 'string' ? v.trim().slice(0, max) : '');
// A display name: no control, format or direction characters (they could disguise a name), single spaces, 40 max.
const cleanName = (v) => (typeof v === 'string' ? v.replace(/[\p{C}\p{Zl}\p{Zp}]/gu, '').replace(/\s+/g, ' ').trim().slice(0, 40) : '');
const num = (v, min, max) => (Number.isFinite(v) ? Math.min(max, Math.max(min, v)) : undefined);

// What friends see of a player, rebuilt from known fields only (the app's summary, see FriendsSummary in the app).
function cleanSummary(s) {
  if (!s || typeof s !== 'object' || Array.isArray(s)) return null;
  // est: includes the season's matches without replay the player added as an estimate (shown "≈")
  const counts = (o) => (o && typeof o === 'object'
    ? { chests: num(o.chests, 0, 1e7), matches: num(o.matches, 0, 1e6), best: num(o.best, 0, 1000), ...(o.est === true ? { est: true } : {}) }
    : undefined);
  const odds = {};
  if (s.odds && typeof s.odds === 'object') {
    for (const [k, v] of Object.entries(s.odds).slice(0, 16)) if (/^odds_[a-z]{2,20}$/.test(k) && Number.isFinite(v)) odds[k] = Math.min(1, Math.max(0, v));
  }
  const treasures = Array.isArray(s.treasures) ? s.treasures.slice(0, 20).filter((t) => t && typeof t === 'object')
    .map((t) => ({ n: cleanName(String(t.n ?? '')).slice(0, 80) || '?', g: t.g === true, t: num(t.t, 0, 4e12) })) : [];
  const last = s.last && typeof s.last === 'object'
    ? { chests: num(s.last.chests, 0, 1000), ended: num(s.last.ended, 0, 4e12), dur: num(s.last.dur, 0, 7200), place: num(s.last.place, 1, 200) } : null;
  return { v: 1, season: counts(s.season), session: counts(s.session), total: counts(s.total), odds, treasures, last };
}

// Public Fortnite stats of an account (by id or by name): its real id and name, null when private or unknown.
const LOOKUPS = new Map();
async function fortniteAccount(env, by, value) {
  if (!env.FORTNITE_API_KEY) return { status: 'off' };
  const cacheKey = by + ':' + value.toLowerCase();
  const hit = LOOKUPS.get(cacheKey);
  if (hit && Date.now() - hit.at < HOUR) return hit.r;
  const url = by === 'id' ? `https://fortnite-api.com/v2/stats/br/v2/${encodeURIComponent(value)}`
    : `https://fortnite-api.com/v2/stats/br/v2?name=${encodeURIComponent(value)}`;
  let r;
  try {
    const res = await fetch(url, { headers: { Authorization: env.FORTNITE_API_KEY, 'User-Agent': 'fortnite-chest-tracker.com' }, signal: AbortSignal.timeout(5000) });
    const body = await res.json().catch(() => null);
    const acc = body?.data?.account;
    r = res.status === 200 && typeof acc?.id === 'string' && EPIC_ID.test(acc.id.toLowerCase())
      ? { status: 'found', id: acc.id.toLowerCase(), name: cleanName(acc.name) }
      : { status: res.status === 404 ? 'none' : res.status === 403 ? 'private' : 'error' };
  } catch {
    r = { status: 'error' };
  }
  if (r.status !== 'error') {
    if (LOOKUPS.size > 5000) LOOKUPS.clear();
    LOOKUPS.set(cacheKey, { at: Date.now(), r });
  }
  return r;
}
const int = (v, min, max) => (Number.isFinite(v) ? Math.min(max, Math.max(min, Math.round(v))) : null);
const skinOf = (v) => (typeof v === 'string' && SKIN.test(v) ? v : null);

async function sha256(hex) {
  const bytes = new TextEncoder().encode(hex);
  const digest = await crypto.subtle.digest('SHA-256', bytes);
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, '0')).join('');
}

class Fail extends Error {
  constructor(status, code) {
    super(code);
    this.status = status;
    this.code = code;
  }
}

export class FriendsHub extends DurableObject {
  constructor(ctx, env) {
    super(ctx, env);
    this.sql = ctx.storage.sql;
    this.hits = new Map();
    this.touched = new Set();
    // A ping from the app is answered without waking the hub (keeps the connection alive for free).
    ctx.setWebSocketAutoResponse(new WebSocketRequestResponsePair('ping', 'pong'));
    this.sql.exec(`
      CREATE TABLE IF NOT EXISTS players (id TEXT PRIMARY KEY, key_hash TEXT NOT NULL, name TEXT NOT NULL, name_lc TEXT NOT NULL,
        skin TEXT, summary TEXT, playing INTEGER NOT NULL DEFAULT 0, seen INTEGER NOT NULL, created INTEGER NOT NULL);
      CREATE INDEX IF NOT EXISTS players_name ON players(name_lc);
      CREATE TABLE IF NOT EXISTS friends (a TEXT NOT NULL, b TEXT NOT NULL, ok INTEGER NOT NULL DEFAULT 0, at INTEGER NOT NULL, PRIMARY KEY (a, b));
      CREATE INDEX IF NOT EXISTS friends_b ON friends(b);
      CREATE TABLE IF NOT EXISTS matches (id TEXT NOT NULL, sid TEXT NOT NULL, ended INTEGER NOT NULL, chests INTEGER NOT NULL,
        dur INTEGER, place INTEGER, skin TEXT, PRIMARY KEY (id, sid));
      CREATE INDEX IF NOT EXISTS matches_ended ON matches(ended);
      CREATE TABLE IF NOT EXISTS mates (id TEXT NOT NULL, mate TEXT NOT NULL, seen INTEGER NOT NULL, PRIMARY KEY (id, mate));
      CREATE TABLE IF NOT EXISTS squads (id TEXT PRIMARY KEY, since INTEGER NOT NULL, touched INTEGER NOT NULL);
      CREATE TABLE IF NOT EXISTS members (id TEXT PRIMARY KEY, squad TEXT NOT NULL, joined INTEGER NOT NULL);
      CREATE INDEX IF NOT EXISTS members_squad ON members(squad);
      CREATE TABLE IF NOT EXISTS invites (squad TEXT NOT NULL, from_id TEXT NOT NULL, to_id TEXT NOT NULL, at INTEGER NOT NULL, PRIMARY KEY (squad, to_id));
    `);
    // 1 = the name was checked against the account's public Fortnite stats
    if (!this.rows('PRAGMA table_info(players)').some((c) => c.name === 'verified')) {
      this.sql.exec('ALTER TABLE players ADD COLUMN verified INTEGER NOT NULL DEFAULT 0');
    }
  }

  rows(query, ...args) {
    return this.sql.exec(query, ...args).toArray();
  }

  one(query, ...args) {
    return this.rows(query, ...args)[0] ?? null;
  }

  // Throws "busy" when the bucket is over its limit for this key.
  limit(bucket, key) {
    const [max, window] = LIMITS[bucket];
    const now = Date.now();
    const k = bucket + '|' + key;
    let h = this.hits.get(k);
    if (!h || now - h.start > window) {
      if (this.hits.size > 20000) {
        for (const [hk, hv] of this.hits) if (now - hv.start > 3600e3) this.hits.delete(hk);
      }
      h = { start: now, n: 0 };
      this.hits.set(k, h);
    }
    if (++h.n > max) throw new Fail(429, 'busy');
  }

  async fetch(request) {
    const op = new URL(request.url).pathname.replace(/^\/api\/friends\//, '');
    const ip = request.headers.get('x-fct-ip') || '?';
    if (op === 'live') {
      try {
        this.limit('live', ip);
        return await this.live(request);
      } catch (e) {
        if (e instanceof Fail) return reply(e.status, { error: e.code });
        throw e;
      }
    }
    let body;
    try {
      body = await request.json();
    } catch {
      return reply(400, { error: 'json' });
    }
    this.touched = new Set();
    try {
      this.limit('ip', ip);
      if (op === 'friend/add' && typeof body.target !== 'string') this.limit('lookupIp', ip);
      const me = await this.auth(body, op === 'sync', ip);
      if (op === 'friend/add' && typeof body.target !== 'string') this.limit('lookup', me);
      this.cleanup();
      switch (op) {
        case 'sync': await this.sync(me, body); break;
        case 'friend/add': await this.addFriend(me, body); break;
        case 'friend/answer': await this.answerFriend(me, body); break;
        case 'friend/remove': await this.removeFriend(me, body); break;
        case 'squad/invite': await this.invite(me, body); break;
        case 'squad/answer': this.answerInvite(me, body); break;
        case 'squad/leave': this.leave(me); break;
        case 'forget': this.forget(me); this.ring(me); return reply(200, { ok: true });
        default: return reply(404, { error: 'not_found' });
      }
      this.ring(me);
      return reply(200, this.state(me));
    } catch (e) {
      if (e instanceof Fail) return reply(e.status, { error: e.code });
      throw e;
    }
  }

  // The Epic id and its key. The first sync of an unknown id registers it (with a name).
  async auth(body, mayRegister, ip = '?') {
    const id = typeof body.id === 'string' ? body.id.toLowerCase() : '';
    const key = typeof body.key === 'string' ? body.key.toLowerCase() : '';
    if (!EPIC_ID.test(id) || !KEY.test(key)) throw new Fail(400, 'id');
    const hash = await sha256(key);
    const player = this.one('SELECT id, key_hash FROM players WHERE id = ?', id);
    if (player) {
      if (player.key_hash !== hash) throw new Fail(409, 'taken');
      return id;
    }
    const name = cleanName(body.name);
    if (!mayRegister || !name) throw new Fail(401, 'unknown');
    this.limit('register', ip);
    const now = Date.now();
    this.sql.exec('INSERT INTO players (id, key_hash, name, name_lc, skin, seen, created) VALUES (?, ?, ?, ?, ?, ?, ?)',
      id, hash, name, name.toLowerCase(), skinOf(body.skin), now, now);
    await this.checkName(id);
    return id;
  }

  // The real name of the account when its stats are public: replaces the one the app sent, and marks it verified.
  async checkName(id) {
    const acc = await fortniteAccount(this.env, 'id', id);
    if (acc.status === 'found' && acc.id === id && acc.name) {
      this.sql.exec('UPDATE players SET name = ?, name_lc = ?, verified = 1 WHERE id = ?', acc.name, acc.name.toLowerCase(), id);
    } else if (acc.status === 'private' || acc.status === 'none') {
      this.sql.exec('UPDATE players SET verified = 0 WHERE id = ?', id);
    }
  }

  // WebSocket of an app (auth in headers, the player must exist). Tagged with the player's id.
  async live(request) {
    if (request.headers.get('upgrade') !== 'websocket') throw new Fail(426, 'websocket');
    const me = await this.auth({ id: request.headers.get('x-fct-id'), key: request.headers.get('x-fct-key') }, false);
    const open = this.ctx.getWebSockets(me);
    for (const old of open.slice(0, Math.max(0, open.length - MAX_SOCKETS + 1))) {
      try { old.close(1000, 'replaced'); } catch { /* already closed */ }
    }
    const pair = new WebSocketPair();
    this.ctx.acceptWebSocket(pair[1], [me]);
    return new Response(null, { status: 101, webSocket: pair[0] });
  }

  webSocketMessage(ws, message) {
    if (message !== 'ping') ws.close(1003, 'unexpected');
  }

  webSocketClose(ws, code) {
    try { ws.close(code === 1005 ? 1000 : code, 'bye'); } catch { /* closed */ }
  }

  // Players whose view changed during this request (rung once it is done, except the player who asked).
  touch(...ids) {
    for (const id of ids.flat()) if (id) this.touched.add(id);
  }

  // Everyone who sees this player: friends, squad mates and those invited to the squad.
  watchers(me) {
    const ids = new Set(this.friendsOf(me));
    const m = this.one('SELECT squad FROM members WHERE id = ?', me);
    if (m) {
      for (const r of this.rows('SELECT id FROM members WHERE squad = ?', m.squad)) ids.add(r.id);
      for (const r of this.rows('SELECT to_id FROM invites WHERE squad = ?', m.squad)) ids.add(r.to_id);
    }
    ids.delete(me);
    return [...ids];
  }

  ring(me) {
    for (const id of this.touched) {
      if (id === me) continue;
      for (const ws of this.ctx.getWebSockets(id)) {
        try { ws.send('changed'); } catch { /* closing */ }
      }
    }
    this.touched = new Set();
  }

  cleanup() {
    const now = Date.now();
    if (now - (this.cleaned || 0) < CLEANUP_EVERY) return;
    this.cleaned = now;
    this.sql.exec('DELETE FROM matches WHERE ended < ?', now - MATCHES_KEPT);
    this.sql.exec('DELETE FROM mates WHERE seen < ?', now - MATES_KEPT);
    const idle = this.rows('SELECT id FROM squads WHERE touched < ?', now - SQUAD_IDLE);
    for (const s of idle) this.dropSquad(s.id);
  }

  dropSquad(squad) {
    this.sql.exec('DELETE FROM members WHERE squad = ?', squad);
    this.sql.exec('DELETE FROM invites WHERE squad = ?', squad);
    this.sql.exec('DELETE FROM squads WHERE id = ?', squad);
  }

  // ---- Updates from the app ------------------------------------------------------------------------------

  async sync(me, body) {
    const now = Date.now();
    const name = cleanName(body.name);
    let summary = null;
    const clean = cleanSummary(body.summary);
    if (clean) {
      summary = JSON.stringify(clean);
      if (summary.length > 8000) summary = null;
    }
    const before = this.one('SELECT name, skin, summary, playing, verified, seen FROM players WHERE id = ?', me);
    // A verified name only changes through Fortnite; another name sent is checked again (a real rename, or not)
    const renamed = name && before && name.toLowerCase() !== before.name.toLowerCase();
    // Only what changed is written (a sync every 10-60 s per app would otherwise rewrite the row and its name index)
    const playing = body.playing === true ? 1 : 0;
    const skin = skinOf(body.skin);
    const sets = [];
    const args = [];
    if (name && name !== before?.name) { sets.push('name = ?', 'name_lc = ?'); args.push(name, name.toLowerCase()); }
    if (skin && skin !== before?.skin) { sets.push('skin = ?'); args.push(skin); }
    if (summary && summary !== before?.summary) { sets.push('summary = ?'); args.push(summary); }
    if (playing !== before?.playing) { sets.push('playing = ?'); args.push(playing); }
    if (sets.length || !before || now - before.seen > (playing ? SEEN_PLAYING : SEEN_IDLE)) { sets.push('seen = ?'); args.push(now); }
    if (sets.length) this.sql.exec(`UPDATE players SET ${sets.join(', ')} WHERE id = ?`, ...args, me);

    // An app sends its matches of the last days again when it restarts: only new or changed ones are written.
    const matches = Array.isArray(body.matches) ? body.matches.slice(-30) : [];
    let changed = 0;
    for (const m of matches) {
      const sid = typeof m?.sid === 'string' && SESSION.test(m.sid) ? m.sid : null;
      const ended = Number.isFinite(m?.ended) && m.ended > now - MATCHES_KEPT && m.ended < now + 60e3 ? Math.round(m.ended) : null;
      const chests = int(m?.chests, 0, 500);
      if (!sid || ended == null || chests == null) continue;
      const written = this.sql.exec(`INSERT INTO matches (id, sid, ended, chests, dur, place, skin) VALUES (?, ?, ?, ?, ?, ?, ?)
        ON CONFLICT (id, sid) DO UPDATE SET ended = excluded.ended, chests = excluded.chests, dur = excluded.dur, place = excluded.place, skin = excluded.skin
        WHERE ended IS NOT excluded.ended OR chests IS NOT excluded.chests OR dur IS NOT excluded.dur OR place IS NOT excluded.place OR skin IS NOT excluded.skin`,
      me, sid, ended, chests, int(m.dur, 0, 7200), int(m.place, 1, 200), skinOf(m.skin));
      written.toArray();
      if (written.rowsWritten > 0) changed++;
      // Teammates are kept only when they have the app (suggested as friends), never the others.
      const mates = Array.isArray(m.mates) ? m.mates.slice(0, 8) : [];
      for (const mate of mates) {
        const mid = typeof mate === 'string' ? mate.toLowerCase() : '';
        if (EPIC_ID.test(mid) && mid !== me && this.one('SELECT 1 AS x FROM players WHERE id = ?', mid)) {
          this.sql.exec(`INSERT INTO mates (id, mate, seen) VALUES (?, ?, ?)
            ON CONFLICT (id, mate) DO UPDATE SET seen = excluded.seen WHERE excluded.seen > seen`, me, mid, Math.min(ended, now));
        }
      }
    }
    const membership = this.one('SELECT squad FROM members WHERE id = ?', me);
    if (membership && (changed || playing)) {
      this.sql.exec('UPDATE squads SET touched = ? WHERE id = ? AND touched < ?', now, membership.squad, now - SQUAD_TOUCH);
    }
    // Friends only hear about what they see: a new match, "in a match", the summary, the name or the outfit.
    if (renamed) {
      this.sql.exec('UPDATE players SET verified = 0 WHERE id = ?', me); // until Fortnite confirms the new name
      await this.checkName(me);
    }
    const after = this.one('SELECT name, skin, summary, playing FROM players WHERE id = ?', me);
    if (changed || !before || before.name !== after.name || before.skin !== after.skin || before.summary !== after.summary || before.playing !== after.playing) {
      this.touch(this.watchers(me));
    }
  }

  async target(body, me) {
    const id = typeof body.target === 'string' ? body.target.toLowerCase() : '';
    if (EPIC_ID.test(id)) {
      if (id === me) throw new Fail(400, 'self');
      if (!this.one('SELECT 1 AS x FROM players WHERE id = ?', id)) throw new Fail(404, 'not_found');
      return id;
    }
    const name = cleanName(body.name).toLowerCase();
    if (name.length < 3) throw new Fail(400, 'name');
    // By name, the request goes to the Epic account that really has this name (public stats), never to an app that
    // only claims it. Accounts with private stats can only be found by name when one single player uses it.
    let found = this.one('SELECT id FROM players WHERE name_lc = ? AND verified = 1', name);
    if (!found) {
      const acc = await fortniteAccount(this.env, 'name', name);
      if (acc.status === 'found') {
        found = this.one('SELECT id FROM players WHERE id = ?', acc.id);
        if (found && acc.name) this.sql.exec('UPDATE players SET name = ?, name_lc = ?, verified = 1 WHERE id = ?', acc.name, acc.name.toLowerCase(), acc.id);
      } else if (acc.status !== 'none') {
        const same = this.rows('SELECT id FROM players WHERE name_lc = ? AND verified = 0 LIMIT 2', name);
        if (same.length === 1) found = same[0];
      }
    }
    if (!found) throw new Fail(404, 'not_found');
    if (found.id === me) throw new Fail(400, 'self');
    return found.id;
  }

  friendsOf(me) {
    return this.rows('SELECT b AS id FROM friends WHERE a = ? AND ok = 1 UNION SELECT a AS id FROM friends WHERE b = ? AND ok = 1', me, me).map((r) => r.id);
  }

  areFriends(a, b) {
    return !!this.one('SELECT 1 AS x FROM friends WHERE ((a = ? AND b = ?) OR (a = ? AND b = ?)) AND ok = 1', a, b, b, a);
  }

  async addFriend(me, body) {
    const other = await this.target(body, me);
    const mine = this.one('SELECT ok FROM friends WHERE a = ? AND b = ?', me, other);
    const theirs = this.one('SELECT ok FROM friends WHERE a = ? AND b = ?', other, me);
    if (mine?.ok || theirs?.ok || mine) return;
    this.touch(other);
    if (theirs) {
      // they had asked already: asking back accepts
      this.sql.exec('UPDATE friends SET ok = 1, at = ? WHERE a = ? AND b = ?', Date.now(), other, me);
      return;
    }
    if (this.friendsOf(me).length >= MAX_FRIENDS) throw new Fail(400, 'too_many');
    this.sql.exec('INSERT INTO friends (a, b, ok, at) VALUES (?, ?, 0, ?)', me, other, Date.now());
  }

  async answerFriend(me, body) {
    const other = await this.target(body, me);
    if (!this.one('SELECT 1 AS x FROM friends WHERE a = ? AND b = ? AND ok = 0', other, me)) throw new Fail(404, 'no_request');
    this.touch(other);
    if (body.accept === true) {
      if (this.friendsOf(me).length >= MAX_FRIENDS) throw new Fail(400, 'too_many');
      this.sql.exec('UPDATE friends SET ok = 1, at = ? WHERE a = ? AND b = ?', Date.now(), other, me);
    } else {
      this.sql.exec('DELETE FROM friends WHERE a = ? AND b = ?', other, me);
    }
  }

  async removeFriend(me, body) {
    const other = await this.target(body, me);
    this.touch(other);
    this.sql.exec('DELETE FROM friends WHERE (a = ? AND b = ?) OR (a = ? AND b = ?)', me, other, other, me);
    this.sql.exec('DELETE FROM invites WHERE (from_id = ? AND to_id = ?) OR (from_id = ? AND to_id = ?)', me, other, other, me);
    this.sql.exec('DELETE FROM mates WHERE (id = ? AND mate = ?) OR (id = ? AND mate = ?)', me, other, other, me);
  }

  async invite(me, body) {
    const other = await this.target(body, me);
    if (!this.areFriends(me, other)) throw new Fail(403, 'not_friends');
    const now = Date.now();
    const mine = this.one('SELECT squad FROM members WHERE id = ?', me);
    // They invited me already: inviting them back means joining them.
    const theirInvite = this.one('SELECT squad FROM invites WHERE from_id = ? AND to_id = ?', other, me);
    if (theirInvite && theirInvite.squad !== mine?.squad) {
      this.join(me, theirInvite.squad);
      return;
    }
    let squad = mine?.squad;
    if (squad && this.one('SELECT 1 AS x FROM members WHERE id = ? AND squad = ?', other, squad)) return;
    // Started from the last match played together: that match counts in the squad session.
    const since = int(body.since, now - 3 * HOUR, now) ?? now;
    if (!squad) {
      squad = crypto.randomUUID();
      this.sql.exec('INSERT INTO squads (id, since, touched) VALUES (?, ?, ?)', squad, since, now);
      this.sql.exec('INSERT OR REPLACE INTO members (id, squad, joined) VALUES (?, ?, ?)', me, squad, now);
    } else {
      this.sql.exec('UPDATE squads SET since = MIN(since, ?), touched = ? WHERE id = ?', since, now, squad);
    }
    if (this.one('SELECT COUNT(*) AS n FROM members WHERE squad = ?', squad).n >= MAX_SQUAD) throw new Fail(400, 'squad_full');
    this.sql.exec('INSERT OR REPLACE INTO invites (squad, from_id, to_id, at) VALUES (?, ?, ?, ?)', squad, me, other, now);
    this.touch(other, this.watchers(me));
  }

  answerInvite(me, body) {
    const squad = typeof body.squad === 'string' ? body.squad : '';
    const invite = this.one('SELECT squad FROM invites WHERE squad = ? AND to_id = ?', squad, me);
    if (!invite) throw new Fail(404, 'no_invite');
    this.touch(this.rows('SELECT id FROM members WHERE squad = ?', squad).map((r) => r.id));
    if (body.accept === true) this.join(me, squad);
    else this.sql.exec('DELETE FROM invites WHERE squad = ? AND to_id = ?', squad, me);
  }

  join(me, squad) {
    if (!this.one('SELECT 1 AS x FROM squads WHERE id = ?', squad)) throw new Fail(404, 'no_invite');
    if (this.one('SELECT COUNT(*) AS n FROM members WHERE squad = ?', squad).n >= MAX_SQUAD) throw new Fail(400, 'squad_full');
    this.leave(me);
    const now = Date.now();
    this.sql.exec('INSERT OR REPLACE INTO members (id, squad, joined) VALUES (?, ?, ?)', me, squad, now);
    this.sql.exec('DELETE FROM invites WHERE to_id = ? AND squad = ?', me, squad);
    this.sql.exec('UPDATE squads SET touched = ? WHERE id = ?', now, squad);
    this.touch(this.watchers(me));
  }

  leave(me) {
    const m = this.one('SELECT squad FROM members WHERE id = ?', me);
    if (!m) return;
    this.touch(this.watchers(me));
    this.sql.exec('DELETE FROM members WHERE id = ?', me);
    this.sql.exec('DELETE FROM invites WHERE squad = ? AND from_id = ?', m.squad, me);
    const left = this.one('SELECT COUNT(*) AS n FROM members WHERE squad = ?', m.squad).n;
    const pending = this.one('SELECT COUNT(*) AS n FROM invites WHERE squad = ?', m.squad).n;
    // a squad of one with nobody invited is no squad
    if (left === 0 || (left === 1 && pending === 0)) this.dropSquad(m.squad);
  }

  forget(me) {
    this.touch(this.watchers(me));
    this.touch(this.rows('SELECT b AS id FROM friends WHERE a = ? UNION SELECT a AS id FROM friends WHERE b = ?', me, me).map((r) => r.id));
    this.leave(me);
    this.sql.exec('DELETE FROM invites WHERE to_id = ? OR from_id = ?', me, me);
    this.sql.exec('DELETE FROM friends WHERE a = ? OR b = ?', me, me);
    this.sql.exec('DELETE FROM mates WHERE id = ? OR mate = ?', me, me);
    this.sql.exec('DELETE FROM matches WHERE id = ?', me);
    this.sql.exec('DELETE FROM players WHERE id = ?', me);
  }

  // ---- What the app shows -----------------------------------------------------------------------------------

  player(id) {
    const p = this.one('SELECT id, name, skin, summary, playing, seen, verified FROM players WHERE id = ?', id);
    if (!p) return null;
    let summary = null;
    try { summary = p.summary ? JSON.parse(p.summary) : null; } catch { /* old or broken: none */ }
    const now = Date.now();
    return { id: p.id, name: p.name, skin: p.skin, verified: !!p.verified, playing: !!p.playing && now - p.seen < PLAYING_STALE, seen: p.seen, summary };
  }

  lastMatch(id, since = 0) {
    const m = this.one('SELECT sid, ended, chests, dur, place, skin FROM matches WHERE id = ? AND ended >= ? ORDER BY ended DESC LIMIT 1', id, since);
    return m ? { sid: m.sid, ended: m.ended, chests: m.chests, dur: m.dur, place: m.place, skin: m.skin } : null;
  }

  state(me) {
    const now = Date.now();
    const friends = this.friendsOf(me).map((id) => {
      const p = this.player(id);
      return p && { ...p, last: this.lastMatch(id) };
    }).filter(Boolean);
    const incoming = this.rows('SELECT a AS id, at FROM friends WHERE b = ? AND ok = 0 ORDER BY at DESC', me)
      .map((r) => ({ ...this.player(r.id), at: r.at })).filter((p) => p.id)
      .map(({ id, name, skin, at }) => ({ id, name, skin, at }));
    const outgoing = this.rows('SELECT b AS id, at FROM friends WHERE a = ? AND ok = 0 ORDER BY at DESC', me)
      .map((r) => ({ ...this.player(r.id), at: r.at })).filter((p) => p.id)
      .map(({ id, name, skin, at }) => ({ id, name, skin, at }));
    const related = new Set([...friends.map((f) => f.id), ...incoming.map((f) => f.id), ...outgoing.map((f) => f.id)]);
    const suggest = this.rows('SELECT mate, MAX(seen) AS seen FROM mates WHERE id = ? GROUP BY mate ORDER BY seen DESC LIMIT 20', me)
      .filter((r) => !related.has(r.mate))
      .map((r) => this.player(r.mate)).filter(Boolean).slice(0, 8)
      .map(({ id, name, skin }) => ({ id, name, skin }));

    let squad = null;
    const membership = this.one('SELECT squad FROM members WHERE id = ?', me);
    if (membership) {
      const s = this.one('SELECT id, since FROM squads WHERE id = ?', membership.squad);
      if (s) {
        const members = this.rows('SELECT id, joined FROM members WHERE squad = ? ORDER BY joined', s.id).map((m) => {
          const p = this.player(m.id);
          if (!p) return null;
          const session = this.one('SELECT COUNT(*) AS n, COALESCE(SUM(chests), 0) AS chests FROM matches WHERE id = ? AND ended >= ?', m.id, s.since);
          return { id: p.id, name: p.name, skin: p.skin, playing: p.playing, last: this.lastMatch(m.id, s.since), sessionChests: session.chests, sessionMatches: session.n };
        }).filter(Boolean);
        const invited = this.rows('SELECT to_id FROM invites WHERE squad = ?', s.id).map((r) => this.player(r.to_id)).filter(Boolean)
          .map(({ id, name, skin }) => ({ id, name, skin }));
        squad = { id: s.id, since: s.since, members, invited };
      }
    }
    const invites = this.rows('SELECT squad, from_id, at FROM invites WHERE to_id = ? AND squad != ? ORDER BY at DESC', me, membership?.squad ?? '')
      .map((r) => {
        const from = this.player(r.from_id);
        if (!from) return null;
        const members = this.rows('SELECT id FROM members WHERE squad = ?', r.squad).map((m) => this.player(m.id)?.name).filter(Boolean);
        return { squad: r.squad, from: { id: from.id, name: from.name, skin: from.skin }, members, at: r.at };
      }).filter(Boolean);
    return { me, now, friends, incoming, outgoing, suggest, squad, invites };
  }
}

const HEADERS = { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store', 'x-content-type-options': 'nosniff' };
const reply = (status, body) => new Response(JSON.stringify(body), { status, headers: HEADERS });

/** /api/friends/* (POST, JSON) → the single hub. */
export async function friends(request, env) {
  const live = new URL(request.url).pathname === '/api/friends/live';
  if (live ? request.method !== 'GET' : request.method !== 'POST') return reply(405, { error: 'method' });
  if (!env.HUB) return reply(503, { error: 'not_configured' });
  const ip = request.headers.get('cf-connecting-ip') || '?';
  if (env.LIMIT_FRIENDS && !(await env.LIMIT_FRIENDS.limit({ key: ip })).success) return reply(429, { error: 'busy' });
  const headers = new Headers(request.headers);
  headers.set('x-fct-ip', ip);
  const hub = env.HUB.get(env.HUB.idFromName('hub'));
  if (live) return hub.fetch(new Request(request, { headers }));
  const body = await readCapped(request, 64 * 1024);
  if (body == null) return reply(413, { error: 'too_big' });
  headers.delete('content-length');
  return hub.fetch(new Request(request.url, { method: 'POST', headers, body }));
}

/** The body, or null when it is bigger than max bytes (read in chunks: a chunked upload has no content-length). */
export async function readCapped(request, max) {
  if (Number(request.headers.get('content-length') || 0) > max) return null;
  if (!request.body) return new Uint8Array(0);
  const reader = request.body.getReader();
  const parts = [];
  let size = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    size += value.byteLength;
    if (size > max) { reader.cancel().catch(() => {}); return null; }
    parts.push(value);
  }
  const out = new Uint8Array(size);
  let at = 0;
  for (const p of parts) { out.set(p, at); at += p.byteLength; }
  return out;
}

/** /api/skin/<outfit id>.png: the small icon of an outfit (fortnite-api.com), cached at the edge for a month. */
export async function skin(url, ctx, env, ip) {
  if (env?.LIMIT_SKINS && !(await env.LIMIT_SKINS.limit({ key: ip || '?' })).success) return new Response('busy', { status: 429 });
  const m = url.pathname.match(/^\/api\/skin\/([A-Za-z0-9_-]{1,120})\.png$/);
  if (!m) return new Response('not found', { status: 404 });
  const id = m[1].toLowerCase();
  const cacheKey = new Request(`https://cache.fortnite-chest-tracker.com/skin/${id}.png`);
  const cache = caches.default;
  const hit = await cache.match(cacheKey);
  if (hit) return hit;
  let res;
  try {
    const up = await fetch(`https://fortnite-api.com/images/cosmetics/br/${id}/smallicon.png`, { headers: { 'User-Agent': 'fortnite-chest-tracker.com' } });
    const type = up.headers.get('content-type') || '';
    if (up.ok && type.startsWith('image/png')) {
      res = new Response(await up.arrayBuffer(), { status: 200, headers: { 'content-type': 'image/png', 'cache-control': 'public, max-age=2592000', 'x-content-type-options': 'nosniff' } });
    } else {
      res = new Response('not found', { status: 404, headers: { 'cache-control': 'public, max-age=86400' } });
    }
  } catch {
    return new Response('unavailable', { status: 502 });
  }
  ctx.waitUntil(cache.put(cacheKey, res.clone()));
  return res;
}
