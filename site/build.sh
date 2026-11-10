#!/usr/bin/env bash
# Assembles the website into <out> (default _site): the pages of site/ plus the 3D island, models and pictures
# of the app (one source for both), and the README pictures. The result is a static site meant for the root of
# https://fortnite-chest-tracker.com/ (Cloudflare Pages: build command "bash site/build.sh _site", output "_site";
# _headers and _redirects come from site/). All links are relative, so it also works from any local server.
# Needs bash and node 22; npx (to minify three.js) and the GitHub API (release number) are optional.
# Then: three.js is minified for the site only (the app keeps the readable files), and prerender.mjs writes one
# page per language (/, /fr/, /es/) with the texts, SEO tags and the sitemap.
set -euo pipefail
OUT="${1:-_site}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
W="$ROOT/src/FortniteChestTracker/Web/wwwroot"
rm -rf "$OUT"
mkdir -p "$OUT/docs"
cp -r "$ROOT/site/." "$OUT/"
rm -f "$OUT/build.sh" "$OUT/prerender.mjs"
cp "$W/scene.js" "$OUT/"
cp -r "$W/vendor" "$W/models" "$W/img" "$W/cursors" "$OUT/"
rm -f "$OUT/img/favicon.ico"                  # the site has its own small PNG icons (icons/)
cp "$W/fonts/OFL.txt" "$OUT/fonts/"           # the site uses a Latin subset of Anton in WOFF2 (site/fonts)
cp "$ROOT"/docs/*.png "$OUT/docs/"

# Minify three.js (same file names, so the imports between them still resolve). Falls back to the plain copy.
if command -v npx >/dev/null 2>&1; then
  for f in "$W"/vendor/three/*.js "$W/scene.js"; do
    rel="${f#"$W"/}"
    npx --yes esbuild@0.24 "$f" --minify --format=esm --log-level=warning --outfile="$OUT/$rel" \
      || { echo "esbuild failed on $rel, kept as is"; cp "$f" "$OUT/$rel"; }
  done
else
  echo "npx not found: three.js not minified"
fi

node "$ROOT/site/prerender.mjs" "$OUT"
echo "site assembled in $OUT"
