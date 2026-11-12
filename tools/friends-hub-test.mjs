// Checks of the friends hub (worker/friends.js) against a local server with an EMPTY database:
//   npx wrangler@4 dev --local --port 8789 --persist-to <new temp folder>   then   node tools/friends-hub-test.mjs http://127.0.0.1:8789
const B = process.argv[2] || 'http://127.0.0.1:8787';
const A = { id: 'a'.repeat(32), key: '1'.repeat(64), name: 'Alpha' };
const Bo = { id: 'b'.repeat(32), key: '2'.repeat(64), name: 'Bravo' };
const C = { id: 'c'.repeat(32), key: '3'.repeat(64), name: 'Charlie' };
async function call(who, op, extra = {}) {
  const r = await fetch(`${B}/api/friends/${op}`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ id: who.id, key: who.key, name: who.name, ...extra }) });
  const j = await r.json();
  return { status: r.status, ...j };
}
const ok = (c, m) => { if (!c) { console.log('FAIL', m); process.exitCode = 1; } else console.log('ok  ', m); };
const now = Date.now();
let r = await call(A, 'sync', { skin: 'Character_WeldFlush', playing: false, summary: { season: { chests: 100 } } });
ok(r.status === 200 && r.me === A.id, 'register A');
r = await call(Bo, 'sync', { skin: 'CID_115_Athena_Commando_M_CarbideBlue', matches: [{ sid: 's1', ended: now - 60000, chests: 12, dur: 900, place: 3, mates: [A.id, 'f'.repeat(32)] }] });
ok(r.status === 200, 'register B with a match and A as mate');
r = await call({ ...A, key: '9'.repeat(64) }, 'sync', {});
ok(r.status === 409 && r.error === 'taken', 'wrong key refused');
r = await call(C, 'friend/add', { name: 'alpha' });
ok(r.status === 401, 'unknown player cannot act before sync');
await call(C, 'sync', {});
r = await call(Bo, 'sync', {});
ok(r.suggest.some((s) => s.id === A.id) && r.suggest.length === 1, 'A suggested to B (unknown mate dropped)');
r = await call(Bo, 'friend/add', { name: 'ALPHA' });
ok(r.outgoing.length === 1 && r.outgoing[0].id === A.id && r.suggest.length === 0, 'B asks A by name');
r = await call(A, 'sync', {});
ok(r.incoming.length === 1 && r.incoming[0].name === 'Bravo', 'A sees the request');
r = await call(A, 'friend/answer', { target: Bo.id, accept: true });
ok(r.friends.length === 1 && r.friends[0].last?.chests === 12, 'A accepts, sees B last match');
r = await call(C, 'squad/invite', { target: A.id });
ok(r.status === 403, 'C cannot invite a non-friend');
r = await call(A, 'squad/invite', { target: Bo.id, since: now - 120000 });
ok(r.squad && r.squad.members.length === 1 && r.squad.invited[0].id === Bo.id, 'A invites B to a squad');
r = await call(Bo, 'sync', { playing: true });
ok(r.invites.length === 1 && r.invites[0].from.id === A.id, 'B sees the invite');
r = await call(Bo, 'squad/answer', { squad: r.invites[0].squad, accept: true });
ok(r.squad.members.length === 2 && r.squad.members.find((m) => m.id === Bo.id).sessionChests === 12, 'B joins, its match since the start counts');
r = await call(A, 'sync', { matches: [{ sid: 's1', ended: now - 30000, chests: 20, mates: [Bo.id] }] });
const me = r.squad.members.find((m) => m.id === A.id), b = r.squad.members.find((m) => m.id === Bo.id);
ok(me.sessionChests === 20 && b.playing === true && b.last.chests === 12, 'squad shows both, B playing');
r = await call(Bo, 'squad/leave');
ok(r.squad === null, 'B leaves');
r = await call(A, 'sync', {});
ok(r.squad === null, 'squad of one closed');
r = await call(A, 'friend/remove', { target: Bo.id });
ok(r.friends.length === 0, 'A removes B');
r = await call(Bo, 'forget');
ok(r.status === 200, 'B forgets');
r = await call(A, 'friend/add', { target: Bo.id });
ok(r.status === 404, 'B is gone');
const s = await fetch(`${B}/api/skin/Character_WeldFlush.png`);
ok(s.status === 200 && s.headers.get('content-type') === 'image/png', 'skin icon');
const s2 = await fetch(`${B}/api/skin/..%2Fx.png`);
ok(s2.status === 404, 'bad skin id refused');

// ---- live doorbell and limits
{
  const D = { id: 'e'.repeat(32), key: '7'.repeat(64), name: 'Echo' };
  const E = { id: 'f'.repeat(31) + 'e', key: '8'.repeat(64), name: 'Foxtrot' };
  await call(D, 'sync', {}); await call(E, 'sync', {});
  const wsUrl = B.replace(/^http/, 'ws') + '/api/friends/live';
  const bad = new WebSocket(wsUrl, { headers: { 'x-fct-id': D.id, 'x-fct-key': '0'.repeat(64) } });
  const badResult = await new Promise((r) => { bad.onopen = () => r('open'); bad.onerror = () => r('refused'); });
  ok(badResult === 'refused', 'live: wrong key refused');
  const ws = new WebSocket(wsUrl, { headers: { 'x-fct-id': D.id, 'x-fct-key': D.key } });
  await new Promise((r, j) => { ws.onopen = r; ws.onerror = j; });
  const got = [];
  ws.onmessage = (e) => got.push(String(e.data));
  ws.send('ping');
  await new Promise((r) => setTimeout(r, 300));
  ok(got.includes('pong'), 'live: ping answered');
  await call(E, 'friend/add', { target: D.id });
  await new Promise((r) => setTimeout(r, 300));
  ok(got.includes('changed'), 'live: rung on a friend request');
  got.length = 0;
  await call(D, 'sync', { playing: true });
  await new Promise((r) => setTimeout(r, 300));
  ok(!got.includes('changed'), 'live: not rung for its own sync');
  await call(D, 'friend/answer', { target: E.id, accept: true });
  got.length = 0;
  await call(E, 'sync', { playing: true });
  await new Promise((r) => setTimeout(r, 300));
  ok(got.includes('changed'), 'live: rung when a friend starts a match');
  got.length = 0;
  await call(E, 'sync', { playing: true });
  await new Promise((r) => setTimeout(r, 300));
  ok(!got.includes('changed'), 'live: not rung when nothing changed');
  ws.close();
  let busy = false;
  for (let i = 0; i < 35 && !busy; i++) busy = (await call(E, 'friend/add', { name: 'nobody' + i })).status === 429;
  ok(busy, 'limit: name lookups per player');
}

// ---- hardening: names, summaries, body size
{
  const G = { id: '9'.repeat(31) + 'a', key: 'a'.repeat(64), name: 'Evil‮gnirts\u0007  Name' };
  let r = await call(G, 'sync', { summary: { season: { chests: 5, matches: 2, extra: 'x' }, odds: { odds_pirate: 2, '<b>': 1 }, treasures: [{ n: '<img src=x onerror=alert(1)>', g: true, t: 1, z: 9 }], script: 'nope' } });
  const H = { id: '9'.repeat(31) + 'b', key: 'b'.repeat(64), name: 'Viewer' };
  await call(H, 'sync', {});
  await call(H, 'friend/add', { target: G.id });
  r = await call(G, 'friend/answer', { target: H.id, accept: true });
  r = await call(H, 'sync', {});
  const g = r.friends.find((f) => f.id === G.id);
  ok(g && g.name === 'Evilgnirts Name', 'name cleaned of direction/control characters');
  ok(g && g.summary.script === undefined && g.summary.season.extra === undefined, 'summary: unknown fields dropped');
  ok(g && g.summary.odds.odds_pirate === 1 && g.summary.odds['<b>'] === undefined, 'summary: odds clamped, odd keys dropped');
  ok(g && g.summary.treasures[0].z === undefined && typeof g.summary.treasures[0].n === 'string', 'summary: treasures rebuilt');
  const big = 'x'.repeat(70 * 1024);
  const r1 = await fetch(`${B}/api/friends/sync`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ id: H.id, key: H.key, pad: big }) });
  ok(r1.status === 413, 'body over 64 KB refused');
  const stream = new ReadableStream({ start(c) { for (let i = 0; i < 80; i++) c.enqueue(new TextEncoder().encode('y'.repeat(1024))); c.close(); } });
  const r2 = await fetch(`${B}/api/friends/sync`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: stream, duplex: 'half' }).catch(() => ({ status: 'error' }));
  ok(r2.status === 413, 'chunked body over 64 KB refused');
}
