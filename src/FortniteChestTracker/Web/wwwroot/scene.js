// Fortnite - Chest Tracker — the 3D scene of the home page (three.js, MIT, embedded in the exe).
// "Keep digging": a floating chunk of land seen in cross-section. Up top, an old tunnel stops one block short of a
// wall of crystals, and the one who dug it sits there, worn out — he gave up just before the treasure. Below, the
// player's miner digs towards the same wall, where the pirate token shines: the tunnel's length follows the chests
// opened since the hunt started, against the average number of chests per token. Chests of a counted match fly out
// of the tunnel and land on a pile up top. Characters, tools, chests, trees and rocks are Kenney models (CC0,
// /models/LICENSE-Kenney.txt); the land and the crystals are built here.
// The view turns with the mouse position; wheel to zoom, double-click to reset. The land is as wide as the window
// allows (more columns on a wide screen). Driven by the page through "fct-scene" events.
// Smooth on purpose: 60 fps in every case; under load the resolution drops instead of the frame rate. Shadows are
// refreshed every other frame.
import * as THREE from './vendor/three/three.module.js';
import { GLTFLoader } from './vendor/three/GLTFLoader.js';

const root = document.getElementById('scene');
const reduced = matchMedia('(prefers-reduced-motion: reduce)').matches;

// Box with rounded edges and smooth normals (a subdivided box whose outer bands are bent onto quarter circles).
function roundedBox(w, h, d, r, s = 3) {
  r = Math.min(r, w / 2 - 1e-3, h / 2 - 1e-3, d / 2 - 1e-3);
  const n = 2 * s + 1;
  const g = new THREE.BoxGeometry(1, 1, 1, n, n, n);
  const pos = g.attributes.position;
  const nor = g.attributes.normal;
  const half = [w / 2, h / 2, d / 2];
  const map = (c, hf) => {
    const i = Math.round((c + 0.5) * n);
    const core = hf - r;
    return i <= s ? -core - r * (1 - i / s) : core + r * ((i - s - 1) / s);
  };
  const p = new THREE.Vector3();
  const inner = new THREE.Vector3();
  const dir = new THREE.Vector3();
  for (let i = 0; i < pos.count; i++) {
    p.set(map(pos.getX(i), half[0]), map(pos.getY(i), half[1]), map(pos.getZ(i), half[2]));
    inner.set(
      THREE.MathUtils.clamp(p.x, -half[0] + r, half[0] - r),
      THREE.MathUtils.clamp(p.y, -half[1] + r, half[1] - r),
      THREE.MathUtils.clamp(p.z, -half[2] + r, half[2] - r));
    dir.subVectors(p, inner);
    if (dir.lengthSq() > 1e-12) dir.normalize(); else dir.set(0, 1, 0);
    p.copy(inner).addScaledVector(dir, r);
    pos.setXYZ(i, p.x, p.y, p.z);
    nor.setXYZ(i, dir.x, dir.y, dir.z);
  }
  return g;
}

function init() {
  let renderer;
  try {
    renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true, powerPreference: 'low-power' });
  } catch {
    root.remove(); // no WebGL: the page keeps its plain background
    document.documentElement.classList.add('no-gpu');
    return;
  }
  // Without a graphics card (remote desktop, virtual machine, blocked driver) WebGL is drawn by the processor: the scene
  // would take all of it and the page would answer in seconds. There: no shadows, fewer pixels, fewer frames.
  const gl = renderer.getContext();
  const dbg = gl.getExtension('WEBGL_debug_renderer_info');
  const gpu = `${gl.getParameter(gl.RENDERER)} ${dbg ? gl.getParameter(dbg.UNMASKED_RENDERER_WEBGL) : ''}`;
  const software = /swiftshader|basic render|llvmpipe|softpipe|software/i.test(gpu);
  if (software) document.documentElement.classList.add('no-gpu'); // (app.css then drops the blur and the endless decorations)
  renderer.setPixelRatio(software ? Math.min(devicePixelRatio, 1) * 0.75 : Math.min(devicePixelRatio, 1.5));
  renderer.shadowMap.enabled = !software;
  renderer.shadowMap.autoUpdate = false; // refreshed every other frame by the loop (half the shadow cost)
  renderer.shadowMap.type = THREE.PCFShadowMap; // (PCFSoftShadowMap is gone from three.js: it fell back to this with a warning)
  renderer.toneMapping = THREE.NeutralToneMapping;
  root.appendChild(renderer.domElement);

  const scene = new THREE.Scene();
  const camera = new THREE.OrthographicCamera(-10, 10, 10, -10, 0.1, 200);

  // ---- Light: violet sky, warm key light, cool rim ----
  scene.add(new THREE.HemisphereLight(0xd9ccff, 0x4a2a6c, 1.7));
  const sun = new THREE.DirectionalLight(0xfff0dc, 2.3);
  sun.position.set(-6, 12, 10);
  sun.castShadow = true;
  sun.shadow.mapSize.set(2048, 2048);
  sun.shadow.bias = -0.0004;
  sun.shadow.normalBias = 0.02;
  // (shadow camera bounds are set once the width of the land is known, below)
  scene.add(sun);
  const rim = new THREE.DirectionalLight(0xa58bff, 1.1);
  rim.position.set(8, 4, -6);
  scene.add(rim);

  const mats = new Map();
  const mat = (color, extra = {}) => {
    const key = color + JSON.stringify(extra);
    if (!mats.has(key)) mats.set(key, new THREE.MeshStandardMaterial({ color, roughness: 0.75, metalness: 0, ...extra }));
    return mats.get(key);
  };
  const mesh = (geo, material, { x = 0, y = 0, z = 0, cast = true, receive = true } = {}) => {
    const m = new THREE.Mesh(geo, material);
    m.position.set(x, y, z);
    m.castShadow = cast;
    m.receiveShadow = receive;
    return m;
  };
  const rand = (a, b) => a + Math.random() * (b - a);
  let seed = 7;
  const srand = () => { seed = (seed * 16807) % 2147483647; return (seed - 1) / 2147483646; }; // same land every time

  const CELL = 0.5;
  // ---- The land: a grid of blocks (columns × rows × 3 layers deep), tunnels removed from the front two layers ----
  // As many columns as the free area of the home page holds at the window's shape (the height stays the same).
  const COLS = (() => {
    const stageW = Math.max(600, innerWidth - 48);
    const stageH = Math.max(300, innerHeight - 380);
    const ppu = (stageH * 0.98) / 9.4;
    return Math.max(26, Math.min(64, Math.round((stageW * 0.95) / ppu / CELL) - 1));
  })();
  const K = COLS / 26;              // decoration positions were laid out for 26 columns
  const ROWS = 12;
  const LAYERS = 3;
  const GEM0 = COLS - 6;            // first column of the crystal wall
  const UPPER = [2, 3, 4];          // rows of the abandoned tunnel
  const LOWER = [6, 7, 8];          // rows of the player's tunnel
  const UPPER_END = GEM0 - 2;       // the old tunnel stops one block before the crystals
  const MAX_DIG = GEM0 - 1;         // the player's tunnel can go up to the crystals
  const cx = (c) => (c - (COLS - 1) / 2) * CELL;
  const halfW = (COLS * CELL) / 2 + 1;
  Object.assign(sun.shadow.camera, { left: -halfW, right: halfW, top: 6, bottom: -9, near: 1, far: 60 });
  sun.shadow.mapSize.set(2048, 1024);
  const cy = (r) => -(r + 0.5) * CELL;
  const cz = (l) => (1 - l) * CELL;
  const inset = (r) => (r === ROWS - 3 ? 1 : r === ROWS - 2 ? 3 : r === ROWS - 1 ? 5 : 0);
  const inShape = (c, r) => c >= inset(r) && c < COLS - inset(r);
  const isGem = (c, r) => c >= GEM0 && c <= COLS - 2 && r >= 1 && r <= ROWS - 3;
  const inTunnel = (r) => UPPER.includes(r) || LOWER.includes(r);
  const floorOf = (rows) => cy(rows[rows.length - 1]) - CELL / 2;

  const TOP = new THREE.Color(0xe0905a);
  const DEEP = new THREE.Color(0x8c4a3c);
  const dirtColor = (r) => TOP.clone().lerp(DEEP, r / (ROWS - 1)); // darker with depth, smoothly
  const cells = [];
  for (let l = 0; l < LAYERS; l++) for (let r = 0; r < ROWS; r++) for (let c = 0; c < COLS; c++) if (inShape(c, r)) cells.push({ c, r, l });
  const land = new THREE.InstancedMesh(new THREE.BoxGeometry(CELL, CELL, CELL), new THREE.MeshStandardMaterial({ roughness: 0.9 }), cells.length);
  land.castShadow = true;
  land.receiveShadow = true;
  const index = new Map();          // "c,r,l" -> instance
  const dummy = new THREE.Object3D();
  const color = new THREE.Color();
  cells.forEach(({ c, r, l }, i) => {
    index.set(`${c},${r},${l}`, i);
    dummy.position.set(cx(c), cy(r), cz(l));
    dummy.updateMatrix();
    land.setMatrixAt(i, dummy.matrix);
    if (isGem(c, r)) color.set(0x3b2a78);                     // dark violet rock around the crystals
    else if (l === 2 && inTunnel(r)) color.set(0xf6dcbc);     // light sand inside the tunnels
    else color.copy(dirtColor(r));
    land.setColorAt(i, color);
  });
  scene.add(land);
  const hide = (c, r, l) => {
    const i = index.get(`${c},${r},${l}`);
    if (i == null) return;
    dummy.position.set(0, -999, 0);
    dummy.scale.setScalar(0.001);
    dummy.updateMatrix();
    dummy.scale.setScalar(1);
    land.setMatrixAt(i, dummy.matrix);
    land.instanceMatrix.needsUpdate = true;
  };
  for (const r of UPPER) for (let c = 0; c <= UPPER_END; c++) { hide(c, r, 0); hide(c, r, 1); }
  for (let c = GEM0; c <= COLS - 2; c++) for (let r = 1; r <= ROWS - 3; r++) hide(c, r, 0); // the crystals take the front
  // Small pebbles in the front face, like in the cartoon (none where a tunnel is or will be dug)
  const frontSpots = [];
  for (let r = 0; r < ROWS; r++) for (let c = 0; c < COLS; c++) {
    if (inShape(c, r) && !isGem(c, r) && !inTunnel(r) && !(inset(r) && (c <= inset(r) || c >= COLS - 1 - inset(r)))) frontSpots.push({ c, r });
  }
  const pebbleGeo = new THREE.SphereGeometry(0.5, 16, 10);
  const pebbleMats = [mat(0xf0b07a), mat(0x9a5340), mat(0xf6c69a)];
  for (let i = 0; i < 90; i++) {
    const { c, r } = frontSpots[Math.floor(srand() * frontSpots.length)];
    const p = mesh(pebbleGeo, pebbleMats[i % 3], { x: cx(c) + (srand() - 0.5) * 0.3, y: cy(r) + (srand() - 0.5) * 0.3, z: cz(0) + CELL * 0.5, cast: false });
    p.scale.set(0.1 + srand() * 0.16, 0.07 + srand() * 0.1, 0.05);
    p.rotation.z = srand() * 3;
    scene.add(p);
  }

  // ---- Grass ----
  const grassTop = 0.22;
  for (let c = 0; c < COLS; c++) {
    const h = 0.2 + srand() * 0.12;
    scene.add(mesh(roundedBox(CELL, h, CELL * LAYERS, 0.06, 2), mat(c % 2 ? 0x62d38c : 0x57c882), { x: cx(c), y: h / 2 - 0.04, z: 0 }));
  }

  // Little floating rocks under the land (it floats in the sky)
  const floaters = [];
  for (const [x, y, s] of [[-5 * K, -6.9, 0.8], [4.4 * K, -7.2, 0.6], [-1.2 * K, -7.6, 0.45], [8.5 * K, -6.6, 0.5]]) {
    const g = new THREE.Group();
    g.add(mesh(roundedBox(s, s * 0.7, s, 0.12, 2), mat(0xa55a42)));
    g.add(mesh(roundedBox(s * 1.02, 0.12, s * 1.02, 0.05, 2), mat(0x57c882), { y: s * 0.35 }));
    g.position.set(x, y, 0);
    g.userData = { y, ph: rand(0, 6) };
    floaters.push(g);
    scene.add(g);
  }

  // ---- Wooden mine frames and lamps (lit in the player's tunnel, dead in the old one) ----
  const wood = mat(0xb4683f);
  function frame(c, rows, lit) {
    const top = cy(rows[0]) + CELL / 2;
    const bottom = floorOf(rows);
    const h = top - bottom;
    scene.add(mesh(roundedBox(0.12, h, 0.12, 0.03, 1), wood, { x: cx(c), y: bottom + h / 2, z: -0.18 }));
    scene.add(mesh(roundedBox(0.14, 0.12, CELL * 1.8, 0.03, 1), wood, { x: cx(c), y: top - 0.06, z: 0.25 }));
    scene.add(mesh(new THREE.SphereGeometry(0.07, 12, 10), lit ? mat(0xffc35a, { emissive: 0xffa02a, emissiveIntensity: 2 }) : mat(0x5a4d6c), { x: cx(c) + 0.12, y: top - 0.22, z: -0.1, cast: false }));
  }
  for (let c = 3; c <= UPPER_END - 1; c += 5) frame(c, UPPER, false);
  for (let c = 2; c <= MAX_DIG; c += 5) frame(c, LOWER, true);

  // ---- Crystal wall and the pirate token ----
  const crystals = new THREE.Group();
  const gemGeo = new THREE.OctahedronGeometry(0.2, 0);
  const gemMats = [
    mat(0x45e8ff, { emissive: 0x1aa6c4, emissiveIntensity: 0.6, roughness: 0.2 }),
    mat(0xb28cff, { emissive: 0x6a3ad0, emissiveIntensity: 0.6, roughness: 0.2 }),
    mat(0xe9fbff, { emissive: 0x7fdfff, emissiveIntensity: 0.4, roughness: 0.2 }),
  ];
  for (let c = GEM0; c <= COLS - 2; c++) {
    for (let r = 1; r <= ROWS - 3; r++) {
      for (let k = 0; k < 2; k++) {
        const g = mesh(gemGeo, gemMats[Math.floor(srand() * 3)], { x: cx(c) + (srand() - 0.5) * 0.3, y: cy(r) + (srand() - 0.5) * 0.3, z: cz(0) + (srand() - 0.3) * 0.2, cast: false });
        g.scale.set(0.8 + srand() * 0.6, 1.2 + srand() * 1.2, 0.8 + srand() * 0.6);
        g.rotation.set((srand() - 0.5) * 1.2, srand() * 3, (srand() - 0.5) * 1.6);
        crystals.add(g);
      }
    }
  }
  scene.add(crystals);
  const gemLight = new THREE.PointLight(0x5feaff, 3, 7, 1.6);
  gemLight.position.set(cx(GEM0 + 2), cy(6), 1.6);
  scene.add(gemLight);

  const token = new THREE.Group();
  const tokenTex = new THREE.TextureLoader().load('img/token.webp?r=1');
  tokenTex.colorSpace = THREE.SRGBColorSpace;
  const goldMat = new THREE.MeshStandardMaterial({ map: tokenTex, emissiveMap: tokenTex, emissive: 0xffffff, emissiveIntensity: 0.5, transparent: true, alphaTest: 0.35, side: THREE.DoubleSide, roughness: 0.4 });
  const face = new THREE.Mesh(new THREE.PlaneGeometry(1.15, 1.15), goldMat);
  token.add(face);
  const disc = mesh(new THREE.CylinderGeometry(0.42, 0.42, 0.12, 48), goldMat);
  disc.visible = false; // (kept for the shape of the group; the picture is the token)
  disc.rotation.x = Math.PI / 2;
  token.add(disc);
  token.position.set((cx(GEM0) + cx(COLS - 2)) / 2, (cy(1) + cy(ROWS - 3)) / 2, cz(0) + 0.35); // right in the middle of the crystals
  token.scale.setScalar(1.6);
  scene.add(token);
  const tokenLight = new THREE.PointLight(0xffc04a, 1, 4, 1.6);
  tokenLight.position.copy(token.position).add(new THREE.Vector3(0, 0, 0.9));
  scene.add(tokenLight);

  // ---- Particles ----
  const bits = [];
  const bitGeo = roundedBox(0.08, 0.08, 0.08, 0.02, 1);
  function burst(pos, colors, n = 10, up = 2.4) {
    for (let i = 0; i < n; i++) {
      const m = mesh(bitGeo, mat(colors[i % colors.length]), { x: pos.x, y: pos.y, z: pos.z, cast: false, receive: false });
      m.userData = { v: new THREE.Vector3(rand(-1.6, -0.2), rand(0.6, up), rand(-0.5, 1.2)), life: rand(0.5, 0.9) };
      scene.add(m);
      bits.push(m);
    }
  }

  // ---- Digging state ----
  let dug = 1;                      // columns removed in the player's tunnel (0..MAX_DIG)
  let digTarget = 1;
  let firstState = true;
  const lowerFloor = floorOf(LOWER);
  const upperFloor = floorOf(UPPER);
  function digColumn(c) { for (const r of LOWER) { hide(c, r, 0); hide(c, r, 1); } }
  // Fill a column back (next milestone: a new tunnel starts from the entrance)
  function fillColumn(c) {
    for (const r of LOWER) for (const l of [0, 1]) {
      const i = index.get(`${c},${r},${l}`);
      if (i == null) continue;
      dummy.position.set(cx(c), cy(r), cz(l));
      dummy.updateMatrix();
      land.setMatrixAt(i, dummy.matrix);
    }
    land.instanceMatrix.needsUpdate = true;
  }
  for (let c = 0; c <= dug; c++) digColumn(c);
  const minerX = () => Math.max(cx(0) + 0.1, cx(dug) + CELL / 2 - 0.95); // never outside the land

  // ---- Imported models (Kenney, CC0) ----
  const CHAR_SCALE = 1.55;          // the mini characters are 0.82 high: about 1.27 here, the tunnels are 1.5
  const models = {};
  const loader = new GLTFLoader();
  const names = ['miner', 'quitter', 'ship', 'crate', 'captain', 'lookout', 'parrot', 'crab', 'monkey', 'dog', 'cannon', 'cannon-ball', 'palm-detailed-bend', 'crate-bottles', 'tool-pickaxe', 'chest', 'tree', 'tree-tall', 'rock-a', 'rock-b', 'rock-c', 'signpost', 'barrel', 'bucket', 'box', 'flag-pirate'];
  const prepare = (obj) => {
    obj.traverse((o) => { if (o.isMesh) { o.castShadow = true; o.receiveShadow = true; } });
    return obj;
  };
  const closedChest = () => prepare(models.chest.scene.clone());
  const place = (name, x, y, z, s = 1, ry = 0) => {
    const o = prepare(models[name].scene.clone());
    o.position.set(x, y, z);
    o.scale.setScalar(s);
    o.rotation.y = ry;
    scene.add(o);
    return o;
  };
  const mixers = [];
  let miner = null;
  let ship = null;
  let quitter = null;
  let minerActions = null;
  let quitterHead = null;
  let quitterTorso = null;
  const pile = new THREE.Group();
  pile.position.set(cx(3.5), grassTop, 0.05);
  scene.add(pile);
  let pileCount = -1;
  let pileTotal = 0;

  function setPile(total) {
    pileTotal = total;
    if (!models.chest) return;
    const n = Math.max(0, Math.min(12, Math.round(Math.log2(total + 1))));
    if (n === pileCount) return;
    pileCount = n;
    pile.clear();
    let placed = 0;
    for (let layer = 0; placed < n; layer++) {
      const cols = Math.max(1, 4 - layer);
      for (let i = 0; i < cols && placed < n; i++, placed++) {
        const c = closedChest();
        c.scale.setScalar(0.36);
        c.position.set((i - (cols - 1) / 2) * 0.5, layer * 0.27, (layer % 2) * 0.04);
        c.rotation.y = (srand() - 0.5) * 0.4;
        pile.add(c);
      }
    }
  }

  // ---- Life: crew, animals and pirate gear (Kenney, CC0). Everything here can be clicked. ----
  const clickables = [];                  // { root, onClick(hitObject) }
  const actors = [];                      // things that move every frame
  const bubbles = [];                     // speech bubbles (HTML) following an object
  let bubbleTexts = {};
  let tokenSpin = 0;
  let tokenTurn = 0;
  let cannon = null;
  let parrot = null;
  const balls = [];
  const puffs = [];

  // An animated model with its clips; play() blends to a looping clip, once() plays a clip then goes back.
  function actor(name, scale, clone = false) {
    const root = prepare(clone ? models[name].scene.clone() : models[name].scene);
    root.scale.setScalar(scale);
    const mixer = new THREE.AnimationMixer(root);
    mixers.push(mixer);
    const actions = {};
    for (const c of models[name].animations) actions[c.name] = mixer.clipAction(c);
    const a = { root, actions, current: null, base: 'idle', until: 0 };
    a.play = (n, fade = 0.2) => {
      const next = actions[n];
      if (!next || next === a.current) return;
      next.reset().fadeIn(fade).play();
      if (a.current) a.current.fadeOut(fade);
      a.current = next;
    };
    a.once = (n) => {
      const act = actions[n];
      if (!act) return;
      a.current = null;
      a.play(n, 0.15);
      a.until = elapsed + act.getClip().duration;
    };
    a.tick = () => { if (a.until && elapsed > a.until) { a.until = 0; a.current = null; a.play(a.base); } };
    return a;
  }

  const lastLine = {};
  function say(obj, key, height = 1.5, style = '') {
    let text = bubbleTexts[key];
    if (Array.isArray(text)) {
      const pool = text.length > 1 ? text.filter((x) => x !== lastLine[key]) : text;
      text = pool[Math.floor(Math.random() * pool.length)];
      lastLine[key] = text;
    }
    if (!text || !active) return;
    for (const b of bubbles) if (b.obj === obj) { b.el.remove(); b.done = true; }
    const el = document.createElement('div');
    el.className = 'scene-bubble' + (style ? ' ' + style : '');
    el.textContent = text;
    document.body.appendChild(el);
    bubbles.push({ el, obj, height, until: elapsed + 2.6 });
  }

  const clickable = (root, onClick) => clickables.push({ root, onClick });

  function fireCannon() {
    if (!cannon || !models['cannon-ball']) return;
    const muzzle = cannon.localToWorld(new THREE.Vector3(0, 0.62, 1.5));
    const ball = prepare(models['cannon-ball'].scene.clone());
    ball.scale.setScalar(0.18);
    ball.position.copy(muzzle);
    scene.add(ball);
    balls.push({ m: ball, v: new THREE.Vector3(5.5, 3.6, rand(0.4, 1.2)), life: 3 });
    for (let i = 0; i < 9; i++) {
      const p = mesh(new THREE.SphereGeometry(0.12, 10, 8), new THREE.MeshStandardMaterial({ color: 0xf2f2f2, transparent: true, opacity: 0.9, roughness: 1 }), { x: muzzle.x, y: muzzle.y, z: muzzle.z, cast: false, receive: false });
      p.userData = { v: new THREE.Vector3(rand(0.2, 1.2), rand(0.2, 0.9), rand(-0.4, 0.4)), life: rand(0.7, 1.2) };
      scene.add(p);
      puffs.push(p);
    }
    flash.position.copy(muzzle);
    flash.intensity = 25;
    cannon.userData.recoil = 0.25;
    say(cannon, 'cannon', 1.0);
  }
  const flash = new THREE.PointLight(0xffb347, 0, 5, 2);
  scene.add(flash);

  // The parrot leaves the flag for a loop over the land, then comes back
  function flyParrot() {
    if (!parrot || parrot.flight) return;
    parrot.flight = { t: 0, from: parrot.root.position.clone() };
    parrot.play('run');
    say(parrot.root, 'parrot', 0.8);
  }

  function addLife() {
    const surface = 0.26;             // top of the grass
    // Pirate gear: palms, a cannon pointing off the island, bottles
    place('palm-detailed-bend', cx(1.4), grassTop, -0.35, 0.36, 2.6);
    place('palm-detailed-bend', cx(GEM0 - 7), grassTop, -0.45, 0.32, 0.4);
    place('crate-bottles', cx(GEM0 - 2.4), grassTop, -0.25, 0.32, 0.5);
    cannon = place('cannon', cx(GEM0 - 1), grassTop, 0.35, 0.36, Math.PI / 2 - 0.25);
    cannon.userData.x = cannon.position.x;
    clickable(cannon, fireCannon);

    // The captain walks along the island, her dog behind her
    const captain = actor('captain', CHAR_SCALE * 0.85);
    captain.root.position.set(cx(8), surface, 0.58);
    captain.walk = { from: cx(6), to: cx(GEM0 - 4), target: cx(GEM0 - 4), wait: 0 };
    captain.facing = 1;
    captain.play('walk');
    captain.base = 'walk';
    scene.add(captain.root);
    actors.push((dt) => {
      const w = captain.walk;
      const r = captain.root;
      if (w.wait > 0) {
        w.wait -= dt;
        if (w.wait <= 0) { w.target = w.target === w.to ? w.from : w.to; captain.base = 'walk'; captain.play('walk'); }
        return;
      }
      if (captain.until) return;
      const d = w.target - r.position.x;
      r.rotation.y = d > 0 ? Math.PI / 2 : -Math.PI / 2;
      captain.facing = Math.sign(d) || captain.facing;
      r.position.x += Math.sign(d) * Math.min(Math.abs(d), dt * 0.9);
      if (Math.abs(d) < 0.02) {
        w.wait = rand(1.5, 3.5);
        r.rotation.y = 0;               // stops and looks at us
        captain.base = 'idle';
        captain.play('idle');
      }
    });
    clickable(captain.root, () => { captain.root.rotation.y = 0; captain.once('emote-yes'); say(captain.root, 'captain'); });
    actors.push(() => captain.tick());

    const dog = actor('dog', 0.26);
    dog.root.position.set(cx(7), surface, 0.62);
    scene.add(dog.root);
    dog.play('idle');
    actors.push((dt) => {
      // Follows smoothly: speed grows with the gap (no stop-and-go), walk/idle switch with a margin,
      // and the head turns gradually (towards where it goes, or towards us once settled)
      const behind = captain.root.position.x - (captain.facing || 1) * 0.75;
      const d = behind - dog.root.position.x;
      const v = THREE.MathUtils.clamp(d * 2.5, -1.3, 1.3);
      dog.root.position.x += v * dt;
      if (!dog.until) {
        if (dog.moving && Math.abs(v) < 0.08) dog.moving = false;
        else if (!dog.moving && Math.abs(v) > 0.3) dog.moving = true;
        dog.play(dog.moving ? 'walk' : 'idle');
      }
      const face = dog.moving ? (v > 0 ? Math.PI / 2 : -Math.PI / 2) : 0;
      dog.root.rotation.y += (face - dog.root.rotation.y) * Math.min(1, dt * 6);
      dog.tick();
    });
    clickable(dog.root, () => { dog.once('gesture-positive'); say(dog.root, 'dog', 0.7); });

    // Two crabs scuttling sideways on the grass
    for (const [c0, range] of [[5.5, 1.2], [GEM0 - 2.8, 0.9]]) {
      const crab = actor('crab', 0.2, true);
      crab.root.position.set(cx(c0), surface, 0.62);
      scene.add(crab.root);
      crab.home = cx(c0);
      crab.dir = 1;
      crab.pause = rand(0, 2);
      crab.play('idle');
      actors.push((dt) => {
        crab.tick();
        if (crab.until) return;
        if (crab.pause > 0) { crab.pause -= dt; if (crab.pause <= 0) crab.play('walk'); return; }
        crab.root.position.x += crab.dir * dt * 0.5;
        if (Math.abs(crab.root.position.x - crab.home) > range) {
          crab.dir *= -1;
          crab.pause = rand(1, 3);
          crab.play('idle');
        }
      });
      clickable(crab.root, () => { crab.once('dance'); say(crab.root, 'crab', 0.6); });
    }

    // A monkey on the crates, eating
    const monkey = actor('monkey', 0.24);
    monkey.root.position.set(cx(2.45), grassTop + 0.57, 0.3);
    monkey.base = 'eat';
    monkey.play('eat');
    scene.add(monkey.root);
    actors.push(() => monkey.tick());
    clickable(monkey.root, () => { monkey.once('gesture-positive'); say(monkey.root, 'monkey', 0.7); });

    // The parrot on top of the pirate flag
    parrot = actor('parrot', 0.22);
    parrot.perch = new THREE.Vector3(cx(GEM0 + 2) - 0.02, grassTop + 1.6, -0.05);
    parrot.root.position.copy(parrot.perch);
    parrot.play('idle');
    parrot.next = 15;
    scene.add(parrot.root);
    actors.push((dt) => {
      parrot.tick();
      if (!parrot.flight) {
        parrot.next -= dt;
        if (parrot.next <= 0 && !live) { parrot.next = rand(20, 35); flyParrot(); }
        return;
      }
      const f = parrot.flight;
      f.t += dt / 6;
      const th = f.t * Math.PI * 2;
      const c = parrot.perch;
      const ease = Math.min(1, f.t * 6, (1 - f.t) * 6);
      const prev = parrot.root.position.clone();
      parrot.root.position.set(
        c.x - Math.sin(th) * 5 * ease,
        c.y + (Math.sin(th * 2) * 0.35 + 0.2) * ease,   // stays low: the panels are above
        c.z + (1 - Math.cos(th)) * 0.9 * ease);
      const v = parrot.root.position.clone().sub(prev);
      if (v.lengthSq() > 1e-8) parrot.root.rotation.y = Math.atan2(v.x, v.z);
      if (f.t >= 1) { parrot.flight = null; parrot.root.position.copy(c); parrot.root.rotation.y = 0; parrot.play('idle'); }
    });
    clickable(parrot.root, flyParrot);

    // A lookout on the deck of the flying ship, waving now and then
    if (ship) {
      const lookout = actor('lookout', (CHAR_SCALE * 0.8) / 0.2);
      lookout.root.position.set(0, 1.55, -1.2);
      lookout.root.rotation.y = -Math.PI / 2 - 0.35;
      ship.add(lookout.root);
      lookout.play('idle');
      lookout.next = 8;
      actors.push((dt) => {
        lookout.tick();
        lookout.next -= dt;
        if (lookout.next <= 0) { lookout.next = rand(10, 18); lookout.once('emote-yes'); }
      });
      clickable(ship, () => { lookout.once('emote-yes'); say(lookout.root, 'lookout', 1.4); });
    }

    // The ones already there
    clickable(miner, () => { say(miner, 'minerLines', 1.6); burst(miner.position.clone().add(new THREE.Vector3(0.4, 0.8, 0.3)), [0xffd93d, 0xc6ff3d, 0xffffff], 10, 2.4); });
    clickable(quitter, () => { sigh = -0.001; say(quitter, 'quitterLines', 1.3); });

    // They talk on their own, in turn: first who they are ("you" / "the other players"), then their lines.
    // The tired one lifts his head when he speaks. Quiet during a match.
    let chatter = 2.5;
    let turn = 0;
    actors.push((dt) => {
      if (live) return;
      chatter -= dt;
      if (chatter > 0) return;
      const intro = turn < 2;
      if (turn % 2 === 0) say(miner, intro ? 'you' : 'minerLines', 1.6, intro ? 'tag-you' : '');
      else { sigh = -0.001; say(quitter, intro ? 'others' : 'quitterLines', 1.3, intro ? 'tag-others' : ''); }
      turn++;
      chatter = intro ? 1.8 : rand(6, 10);
      if (turn % 8 === 0) turn = 0;    // now and then, the labels again
    });
    clickable(token, () => { tokenSpin = 14; say(token, 'token', 1.2); burst(token.position.clone(), [0xffd93d, 0xfff4c2], 18, 3); });
    clickable(pile, (hit) => {
      let c = hit;
      while (c && c.parent !== pile) c = c.parent;
      if (!c) return;
      const clip = THREE.AnimationClip.findByName(models.chest.animations, 'open-close');
      if (clip) {
        const m = new THREE.AnimationMixer(c);
        const act = m.clipAction(clip);
        act.setLoop(THREE.LoopOnce, 1);
        act.play();
        mixers.push(m);
        setTimeout(() => mixers.splice(mixers.indexOf(m), 1), clip.duration * 1000 + 200);
      }
      burst(c.getWorldPosition(new THREE.Vector3()).add(new THREE.Vector3(0, 0.3, 0)), [0xffd93d, 0xffbf3a, 0xfff4c2], 14, 3);
      say(c, 'chest', 0.6);
    });
  }

  function updateLife(dt) {
    for (const f of actors) f(dt);
    tokenSpin *= Math.pow(0.05, dt);
    if (cannon && cannon.userData.recoil) {
      cannon.userData.recoil = Math.max(0, cannon.userData.recoil - dt * 0.8);
      cannon.position.x = cannon.userData.x - cannon.userData.recoil * 0.5;
    }
    flash.intensity *= Math.pow(0.0005, dt);
    for (let i = balls.length - 1; i >= 0; i--) {
      const b = balls[i];
      b.life -= dt;
      b.v.y -= 6 * dt;
      b.m.position.addScaledVector(b.v, dt);
      if (b.life <= 0) { scene.remove(b.m); balls.splice(i, 1); }
    }
    for (let i = puffs.length - 1; i >= 0; i--) {
      const p = puffs[i];
      p.userData.life -= dt;
      p.position.addScaledVector(p.userData.v, dt);
      p.scale.multiplyScalar(1 + dt * 1.6);
      p.material.opacity = Math.max(0, p.userData.life);
      if (p.userData.life <= 0) { scene.remove(p); p.material.dispose(); p.geometry.dispose(); puffs.splice(i, 1); }
    }
    // Speech bubbles follow their object
    const rect = renderer.domElement.getBoundingClientRect();
    for (let i = bubbles.length - 1; i >= 0; i--) {
      const b = bubbles[i];
      if (b.done || elapsed > b.until || !active) { b.el.remove(); bubbles.splice(i, 1); continue; }
      const p = b.obj.getWorldPosition(new THREE.Vector3()).add(new THREE.Vector3(0, b.height, 0)).project(camera);
      b.el.style.left = `${rect.left + ((p.x + 1) / 2) * rect.width}px`;
      b.el.style.top = `${rect.top + ((1 - p.y) / 2) * rect.height}px`;
    }
  }

  // Clicks and hover on the scene (the free area of the home page)
  const raycaster = new THREE.Raycaster();
  function pick(e) {
    const r = renderer.domElement.getBoundingClientRect();
    raycaster.setFromCamera(new THREE.Vector2(((e.clientX - r.left) / r.width) * 2 - 1, -((e.clientY - r.top) / r.height) * 2 + 1), camera);
    const hits = raycaster.intersectObjects(clickables.map((c) => c.root), true);
    for (const h of hits) {
      for (let o = h.object; o; o = o.parent) {
        const c = clickables.find((x) => x.root === o);
        if (c) return { c, object: h.object };
      }
    }
    return null;
  }
  document.addEventListener('click', (e) => {
    if (!onStage(e)) return;
    const hit = pick(e);
    if (hit) hit.c.onClick(hit.object);
  });
  let hoverAt = 0;
  window.addEventListener('pointermove', (e) => {
    if (!active || performance.now() - hoverAt < 80) return;
    hoverAt = performance.now();
    const stage = onStage(e);
    if (stage) stage.style.cursor = pick(e) ? 'url("cursors/pickaxe.png") 21 2, pointer' : '';
  });

  Promise.all(names.map((n) => loader.loadAsync(`models/${n}.glb?r=4`).then((g) => { models[n] = g; })))
    .then(() => {
      // Surface: trees, rocks, a signpost at the tunnels' entrance, the pirate flag over the crystals
      for (let c = 8, i = 0; c < GEM0 - 1; c += 4 + Math.floor(srand() * 4), i++) {
        place(i % 3 === 1 ? 'tree' : 'tree-tall', cx(c), grassTop, -0.3 - srand() * 0.2, 1.05 + srand() * 0.3, srand() * 6);
        if (i % 2 === 0) place(i % 4 === 0 ? 'rock-b' : 'rock-c', cx(c + 2), grassTop, 0.3, 0.7 + srand() * 0.3, srand() * 6);
      }
      place('signpost', cx(0.6), grassTop, 0.45, 1.6, 0.5);
      place('flag-pirate', cx(GEM0 + 2), grassTop, -0.1, 0.75);
      // Rocks set in the front face
      for (let i = 0; i < Math.round(COLS / 4); i++) {
        const { c, r } = frontSpots[Math.floor(srand() * frontSpots.length)];
        const o = place(['rock-a', 'rock-b', 'rock-c'][i % 3], cx(c), cy(r) - 0.12, cz(0) + 0.12, 0.5 + srand() * 0.25, srand() * 6);
        o.rotation.x = -0.3;
      }
      // A pirate ship floating in the sky behind the land, and crates by the entrance
      ship = place('ship', cx(COLS * 0.58), -7.7, 0.5, 0.2, Math.PI / 2 + 0.35); // under the land, sailing in the sky
      ship.userData.y = ship.position.y;
      place('crate', cx(2.2), grassTop, 0.35, 0.42, 0.3);
      place('crate', cx(2.6), grassTop + 0.32, 0.3, 0.32, -0.2);
      // The player's tunnel: barrel and bucket at the entrance
      place('barrel', cx(0.1), lowerFloor, 0.45, 1.4);
      place('bucket', cx(0.7), lowerFloor, 0.6, 1.4, 0.4);
      setPile(pileTotal);

      // The miner, pickaxe in hand
      miner = prepare(models.miner.scene);
      miner.scale.setScalar(CHAR_SCALE);
      miner.rotation.y = Math.PI / 2;      // faces the crystals (+x)
      miner.position.set(minerX(), lowerFloor, 0.3);
      const hand = miner.getObjectByName('arm-right');
      const pick = prepare(models['tool-pickaxe'].scene.clone());
      pick.scale.setScalar(2.2);
      pick.position.set(0, -0.2, 0);
      pick.rotation.set(Math.PI / 2, 0, 0);
      (hand || miner).add(pick);
      scene.add(miner);
      const mm = new THREE.AnimationMixer(miner);
      const clip = (n) => THREE.AnimationClip.findByName(models.miner.animations, n);
      minerActions = {
        dig: mm.clipAction(clip('attack-melee-right')),
        walk: mm.clipAction(clip('walk')),
        idle: mm.clipAction(clip('idle')),
      };
      minerActions.dig.play();
      mixers.push(mm);

      // The one who gave up: sitting on a box at the end of the old tunnel, head down, pickaxe dropped
      place('box', cx(UPPER_END - 1.6), upperFloor, 0.2, 1.3, 0.3);
      quitter = prepare(models.quitter.scene);
      quitter.scale.setScalar(CHAR_SCALE);
      quitter.position.set(cx(UPPER_END - 1.6), upperFloor + 0.26, 0.3);
      quitter.rotation.y = 0.35;           // turned towards us, his back to the crystals he never reached
      scene.add(quitter);
      quitterHead = quitter.getObjectByName('head');
      quitterTorso = quitter.getObjectByName('torso');
      const qm = new THREE.AnimationMixer(quitter);
      qm.clipAction(THREE.AnimationClip.findByName(models.quitter.animations, 'sit')).play();
      mixers.push(qm);
      const dropped = place('tool-pickaxe', cx(UPPER_END) - 0.1, upperFloor + 0.03, 0.45, 1.9);
      dropped.rotation.set(Math.PI / 2, 0, 0.6);
      addLife();
      warmUp(); // the models' shaders, before they are first drawn
    })
    .catch((e) => console.warn('scene models', e));

  // ---- Chests flying from the tunnel to the pile, and the floating labels ----
  const flying = [];
  const queue = [];
  function launch() {
    if (!models.chest || !miner) return;
    const c = closedChest();
    c.position.set(miner.position.x + 0.4, lowerFloor + 0.4, 0.6);
    c.scale.setScalar(0.01);
    scene.add(c);
    flying.push({ c, t: 0, from: c.position.clone(), to: pile.position.clone().add(new THREE.Vector3(rand(-0.8, 0.8), 1.2, 0.3)) });
  }
  function popLabel(text, gold) {
    const el = document.createElement('div');
    el.className = 'scene-pop' + (gold ? ' gold' : '');
    el.textContent = text;
    const at = miner ? miner.position.clone() : new THREE.Vector3(minerX(), lowerFloor, 0.3);
    const p = at.add(new THREE.Vector3(0, 1.8, 0)).project(camera);
    const r = renderer.domElement.getBoundingClientRect();
    el.style.left = `${r.left + ((p.x + 1) / 2) * r.width}px`;
    el.style.top = `${r.top + ((1 - p.y) / 2) * r.height - 40}px`;
    document.body.appendChild(el);
    setTimeout(() => el.remove(), 2600);
  }

  // ---- Camera: straight front view, framed on the empty area of the home page;
  //      it turns gently towards the mouse, zooms with the wheel, double-click resets the zoom ----
  const look = new THREE.Vector3(0, -2.7, 0);
  const view = { cx: innerWidth / 2, cy: innerHeight / 2, ppu: 50 };
  const DEFAULT_ZOOM = 1 / 1.1;   // one wheel notch out by default
  const orbit = { yaw: 0, pitch: 0, zoom: DEFAULT_ZOOM, aimYaw: 0, aimPitch: 0 };
  // Sizes are taken relative to the scene's own box (the whole window in the app, the top section on the website).
  function measure() {
    const w = root.clientWidth || innerWidth;
    const h = root.clientHeight || innerHeight;
    const stage = document.querySelector('.home-page .stage');
    let r = { left: 0, top: h * 0.3, width: w, height: h * 0.65 };
    if (stage) {
      const b = stage.getBoundingClientRect();
      const o = root.getBoundingClientRect();
      if (b.width > 60 && b.height > 60) r = { left: b.left - o.left, top: b.top - o.top, width: b.width, height: b.height };
    }
    view.cx = r.left + r.width / 2;
    view.cy = r.top + r.height / 2;
    view.ppu = Math.max(14, Math.min((r.width * 0.98) / (COLS * CELL * 0.96 + 0.8), (r.height * 0.98) / 9.4));
  }
  function frameCamera() {
    const w = root.clientWidth || innerWidth;
    const h = root.clientHeight || innerHeight;
    const ppu = view.ppu * orbit.zoom;
    camera.left = -view.cx / ppu;
    camera.right = (w - view.cx) / ppu;
    camera.top = view.cy / ppu;
    camera.bottom = -(h - view.cy) / ppu;
    camera.updateProjectionMatrix();
  }
  const onStage = (e) => active && e.target instanceof Element && e.target.closest('.home-page .stage');
  // The view leans towards the mouse: left/right turns it, up/down tilts it (kept facing the cut)
  window.addEventListener('pointermove', (e) => {
    if (!active || e.pointerType === 'touch') return;
    orbit.aimYaw = (0.5 - (e.clientX / innerWidth)) * 0.9;   // inverted: the scene turns away from the mouse
    orbit.aimPitch = (1 - (e.clientY / innerHeight)) * 0.3;   // straight on with the mouse at the bottom middle
  });
  document.addEventListener('pointerleave', () => { orbit.aimYaw = orbit.aimPitch = 0; });
  document.documentElement.addEventListener('mouseleave', () => { orbit.aimYaw = orbit.aimPitch = 0; });
  document.addEventListener('wheel', (e) => {
    if (!onStage(e) || root.dataset.zoom === 'off') return; // the website keeps the wheel for scrolling
    e.preventDefault();
    orbit.zoom = THREE.MathUtils.clamp(orbit.zoom * (e.deltaY < 0 ? 1.1 : 1 / 1.1), 0.7, 2.5);
    frameCamera();
  }, { passive: false });
  document.addEventListener('dblclick', (e) => {
    if (!onStage(e)) return;
    orbit.zoom = DEFAULT_ZOOM;
    frameCamera();
  });

  let elapsed = 0;
  let live = false;
  let active = true;
  let reached = false;
  let walkFrom = null;
  let lastDigTime = 0;
  let sigh = 6;
  let minerState = 'dig';
  const headOffset = { x: 0, y: 0, t: 0 };
  function step(dt) {
    elapsed += dt;
    // Camera: eases towards the mouse
    const ease = reduced ? 1 : Math.min(1, dt * 2.5);
    orbit.yaw += (orbit.aimYaw - orbit.yaw) * ease;
    orbit.pitch += (orbit.aimPitch - orbit.pitch) * ease;
    // Straight in front when the mouse is in the middle of the screen (no base angle, no sway)
    const yaw = orbit.yaw;
    const pitch = orbit.pitch;
    camera.position.set(look.x + Math.sin(yaw) * Math.cos(pitch) * 40, look.y + Math.sin(pitch) * 40, look.z + Math.cos(yaw) * Math.cos(pitch) * 40);
    camera.lookAt(look);

    for (const f of floaters) f.position.y = f.userData.y + Math.sin(elapsed * 0.8 + f.userData.ph) * 0.12;
    if (ship) { ship.position.y = ship.userData.y + Math.sin(elapsed * 0.6) * 0.2; ship.rotation.z = Math.sin(elapsed * 0.5) * 0.04; }
    tokenTurn += tokenSpin * dt;
    token.rotation.y = Math.sin(elapsed * 1.2) * 0.5 + tokenTurn;
    gemMats[0].emissiveIntensity = 0.5 + Math.sin(elapsed * 2) * 0.15;
    gemMats[1].emissiveIntensity = 0.5 + Math.sin(elapsed * 2 + 2) * 0.15;

    // (the head offset is taken off before the animation runs, whether or not the clip moves the head)
    if (quitterHead && quitterTorso) { quitterHead.rotation.x -= headOffset.x; quitterHead.rotation.y -= headOffset.y; quitterTorso.rotation.x -= headOffset.t; }
    for (const m of mixers) m.update(dt);

    // The tired one: head down, now and then he lifts it towards the crystals, then lets it fall again
    if (quitterHead && quitterTorso) {
      sigh -= dt;
      if (sigh < -2.2) sigh = rand(6, 11);
      const up = sigh < 0 ? Math.sin((-sigh / 2.2) * Math.PI) : 0;
      const breath = Math.sin(elapsed * 1.6) * 0.03;
      headOffset.x = 0.6 - up * 0.7;
      headOffset.y = up * 0.45;
      headOffset.t = 0.35 - up * 0.2 + breath;   // slumped forward, breathing slowly
      quitterHead.rotation.x += headOffset.x;
      quitterHead.rotation.y += headOffset.y;
      quitterTorso.rotation.x += headOffset.t;
    }

    updateLife(dt);

    // The miner: digs at the tunnel face, walks one block forward after each column, waits during a match
    if (miner && minerActions) {
      let walking = false;
      if (walkFrom) {
        walkFrom.t += dt / 0.45;
        miner.position.x = THREE.MathUtils.lerp(walkFrom.x, minerX(), Math.min(1, walkFrom.t));
        walking = walkFrom.t < 1;
        if (!walking) walkFrom = null;
      }
      const want = live ? 'idle' : walking ? 'walk' : 'dig';
      if (want !== minerState) {
        minerActions[minerState].fadeOut(0.2);
        minerActions[want].reset().fadeIn(0.2).play();
        minerState = want;
      }
      const dig = minerActions.dig;
      dig.timeScale = queue.length || dug < digTarget ? 1.5 : 1;
      miner.rotation.y = live ? THREE.MathUtils.lerp(miner.rotation.y, 0.3, dt * 3) : Math.PI / 2;
      // The pickaxe hits the rock around the middle of the swing
      const d = dig.getClip().duration;
      const t = dig.time % d;
      if (want === 'dig' && t >= d * 0.45 && lastDigTime < d * 0.45) {
        const face = new THREE.Vector3(cx(dug + 1) - CELL / 2, lowerFloor + 0.55, 0.4);
        if (dug < digTarget) {
          dug += 1;
          digColumn(dug);
          burst(face, [0xe0905a, 0xb0603f, 0xf6c69a], 16, 3);
          walkFrom = { x: miner.position.x, t: 0 };
        } else if (reached) {
          burst(face, [0x45e8ff, 0xb28cff, 0xffffff], 10, 2.6);
        } else {
          burst(face, [0xe0905a, 0xb0603f], 5, 1.6);
        }
        if (queue.length) { queue.shift(); launch(); }
      }
      lastDigTime = t;
    }

    // Particles
    for (let i = bits.length - 1; i >= 0; i--) {
      const b = bits[i];
      b.userData.life -= dt;
      b.userData.v.y -= 9 * dt;
      b.position.addScaledVector(b.userData.v, dt);
      b.rotation.x += dt * 6;
      if (b.userData.life <= 0) { scene.remove(b); bits.splice(i, 1); }
    }
    // Chests flying to the pile
    for (let i = flying.length - 1; i >= 0; i--) {
      const f = flying[i];
      f.t += dt / 1.1;
      const k = THREE.MathUtils.smoothstep(f.t, 0, 1);
      f.c.position.lerpVectors(f.from, f.to, k);
      f.c.position.z += Math.sin(k * Math.PI) * 2.2;   // arcs out in front of the land
      f.c.position.y += Math.sin(k * Math.PI) * 1.5;
      f.c.rotation.y += dt * 6;
      f.c.scale.setScalar(0.36 * Math.min(1, f.t * 5) * (1 - Math.max(0, k - 0.85) * 4));
      if (f.t >= 1) { scene.remove(f.c); flying.splice(i, 1); }
    }
  }

  // ---- Size & loop ----
  function resize() {
    renderer.setSize(root.clientWidth || 1, root.clientHeight || 1, false);
    measure();
    frameCamera();
  }
  new ResizeObserver(resize).observe(root);
  resize();

  // Frames also wait at least twice what the last ones cost (time from a frame to the next callback): a slow machine
  // gets fewer frames instead of a page that stops answering (clicks, requests) while the scene is drawn.
  let lastFrame = 0;
  let cost = 0;            // smoothed cost of a frame, ms
  let justDrawn = false;
  // Shaders are compiled in the background (parallel compile where the browser has it) before a frame needs them:
  // otherwise the first frame with new materials blocks the page for a few hundred ms. Meanwhile the canvas keeps
  // its last picture.
  let compiling = false;
  function warmUp() {
    if (!renderer.compileAsync) return;
    compiling = true;
    renderer.compileAsync(scene, camera).catch(() => {}).finally(() => { compiling = false; });
  }
  warmUp();
  // Smoothness first: 60 frames a second whether the window has the focus or not, a match running or not (another
  // game beside it included). When frames come late (busy graphics card), the scene draws fewer pixels instead of
  // fewer frames, and goes back up when there is room again.
  const baseRatio = renderer.getPixelRatio();
  const minRatio = Math.max(0.5, baseRatio * 0.5);
  let ratio = baseRatio;
  let late = 0;
  let onTime = 0;
  let frameNo = 0;
  renderer.setAnimationLoop((now) => {
    if (!active || document.hidden || compiling) { lastFrame = now; justDrawn = false; return; }
    const gap = now - lastFrame;
    if (justDrawn) { const c = Math.min(60, gap); cost = cost ? cost * 0.8 + c * 0.2 : c; justDrawn = false; }
    const fps = reduced ? 1 : software ? 5 : 60;
    const wait = software ? Math.max(1000 / fps - 0.5, cost > 25 ? cost * 2 : 0) : 1000 / fps - 2;
    if (gap < wait) return;
    if (!software && !reduced && lastFrame > 0) {
      if (gap > 24) { late++; onTime = 0; } else if (gap < 19) { onTime++; late = Math.max(0, late - 1); }
      if (late >= 12 && ratio > minRatio) { ratio = Math.max(minRatio, ratio - 0.15); renderer.setPixelRatio(ratio); late = 0; }
      else if (onTime >= 240 && ratio < baseRatio) { ratio = Math.min(baseRatio, ratio + 0.1); renderer.setPixelRatio(ratio); onTime = 0; }
    }
    const dt = Math.min(0.1, gap / 1000);
    lastFrame = now;
    step(dt);
    if (frameNo++ % 2 === 0) renderer.shadowMap.needsUpdate = true;
    renderer.render(scene, camera);
    justDrawn = true;
  });

  // ---- State from the page ----
  let lastTotal = null;
  let lastTreasures = null;
  window.addEventListener('fct-scene', (e) => {
    const s = e.detail;
    active = s.visible;
    if (!active) {
      // Leaving the home page: speech bubbles and "+23" labels go away at once (the loop that removes them is paused)
      for (const b of bubbles) b.el.remove();
      bubbles.length = 0;
      document.querySelectorAll('.scene-pop, .scene-bubble').forEach((el) => el.remove());
    }
    root.classList.toggle('on', s.visible);
    live = s.live;
    if (s.bubbles) bubbleTexts = s.bubbles;
    measure();
    frameCamera();
    setPile(s.total);
    digTarget = Math.max(1, Math.min(MAX_DIG, 1 + Math.round((s.dug || 0) * (MAX_DIG - 1))));
    reached = digTarget >= MAX_DIG;
    if (firstState || s.instant) { // first state, or another goal chosen: the tunnel is set at once, no animation
      while (dug < digTarget) digColumn(++dug);
      while (dug > digTarget) fillColumn(dug--);
      if (miner) miner.position.x = minerX();
      walkFrom = null;
      firstState = false;
    } else if (digTarget < dug) { // milestone reached: the next treasure is farther, a new tunnel starts
      while (dug > digTarget) fillColumn(dug--);
      if (miner) miner.position.x = minerX();
      walkFrom = null;
    }
    // The token shines brighter as the chance of having found it rises (0..1).
    tokenLight.intensity = 0.6 + s.chance * 5;
    goldMat.emissiveIntensity = 0.5 + s.chance * 1.2;
    if (lastTotal != null && s.total > lastTotal) {
      const added = s.total - lastTotal;
      for (let i = 0; i < Math.min(6, added); i++) queue.push(1);
      popLabel(`+${added}`, false);
      fireCannon();
      flyParrot();
    }
    if (lastTreasures != null && s.treasures > lastTreasures) {
      queue.unshift(1);
      popLabel(s.treasureLabel || '★', true);
    }
    lastTotal = s.total;
    lastTreasures = s.treasures;
  });
  // Picture for the share card: the whole land seen slightly from the right and above, rendered once at the asked size
  // (the canvas is resized for that frame only and read back at once, then the view is restored)
  window.addEventListener('fct-snapshot', (e) => {
    const { width = 1600, height = 600, resolve } = e.detail || {};
    if (typeof resolve !== 'function') return;
    const keepDug = dug;
    const keepX = miner ? miner.position.x : 0;
    const setDug = (target) => {
      while (dug < target) digColumn(++dug);
      while (dug > target) fillColumn(dug--);
      if (miner) miner.position.x = minerX();
    };
    if (typeof e.detail.dug === 'number') setDug(Math.max(1, Math.min(MAX_DIG, 1 + Math.round(e.detail.dug * (MAX_DIG - 1)))));
    try {
      const prevSize = renderer.getSize(new THREE.Vector2());
      const prevRatio = renderer.getPixelRatio();
      const worldW = COLS * CELL + 1.6;
      const worldH = worldW * (height / width);
      const shot = new THREE.OrthographicCamera(-worldW / 2, worldW / 2, worldH / 2, -worldH / 2, 0.1, 200);
      const center = new THREE.Vector3(0, -3.0, 0);
      const yaw = 0.18;
      const pitch = 0.14;
      shot.position.set(center.x + Math.sin(yaw) * Math.cos(pitch) * 40, center.y + Math.sin(pitch) * 40, center.z + Math.cos(yaw) * Math.cos(pitch) * 40);
      shot.lookAt(center);
      shot.updateProjectionMatrix();
      renderer.setPixelRatio(1);
      renderer.setSize(width, height, false);
      renderer.shadowMap.needsUpdate = true;
      renderer.render(scene, shot);
      const url = renderer.domElement.toDataURL('image/png');
      renderer.setPixelRatio(prevRatio);
      renderer.setSize(prevSize.x, prevSize.y, false);
      setDug(keepDug);
      if (miner) miner.position.x = keepX;
      frameCamera();
      renderer.render(scene, camera);
      resolve(url);
    } catch {
      setDug(keepDug);
      resolve(null);
    }
  });
  window.dispatchEvent(new Event('fct-scene-ready'));
}

init();
