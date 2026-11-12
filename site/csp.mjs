// Content-Security-Policy of every page of the built site: run by build.sh after prerender.mjs (node site/csp.mjs <out>).
// Each page gets a <meta> policy that only lets run the site's own files and the inline scripts of that very page
// (allowed by their SHA-256), so an injected script could not run. The rest of the policy (framing, base, plugins)
// is in _headers, sent for every path.
import { readdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { join } from 'node:path';

const OUT = process.argv[2];
if (!OUT) throw new Error('usage: node csp.mjs <out>');

function htmlFiles(dir) {
  return readdirSync(dir).flatMap((name) => {
    const p = join(dir, name);
    return statSync(p).isDirectory() ? htmlFiles(p) : name.endsWith('.html') ? [p] : [];
  });
}

let pages = 0;
for (const file of htmlFiles(OUT)) {
  let html = readFileSync(file, 'utf8');
  if (html.includes('http-equiv="Content-Security-Policy"')) continue;
  const hashes = [];
  // inline scripts that run (JSON data blocks such as application/ld+json are not scripts for the browser)
  for (const m of html.matchAll(/<script(\s[^>]*)?>([\s\S]*?)<\/script>/g)) {
    const attrs = m[1] || '';
    if (/\ssrc=/.test(attrs) || /type="application\/(ld\+)?json"/.test(attrs)) continue;
    hashes.push(`'sha256-${createHash('sha256').update(m[2], 'utf8').digest('base64')}'`);
  }
  const policy = [
    "default-src 'self'",
    `script-src 'self' ${hashes.join(' ')}`.trim(),
    "style-src 'self' 'unsafe-inline'",
    "img-src 'self' data: blob:",
    "font-src 'self'",
    "connect-src 'self' blob: https://api.github.com", // blob: the textures of the 3D island
    "media-src 'self'",
    "worker-src 'self' blob:",
    "manifest-src 'self'",
    "form-action 'self'",
  ].join('; ');
  const meta = `<meta http-equiv="Content-Security-Policy" content="${policy}">`;
  const at = html.search(/<meta charset[^>]*>/i);
  if (at < 0) throw new Error(`${file}: no <meta charset> to put the policy after`);
  const end = html.indexOf('>', at) + 1;
  html = html.slice(0, end) + '\n' + meta + html.slice(end);
  writeFileSync(file, html);
  pages++;
}
console.log(`content security policy added to ${pages} pages`);
