// Deep diff of two JSON files (desync reports): prints differing paths with both values.
// Usage: node tools/jsondiff.js host.json peer.json [maxLines]
const fs = require('fs');
const [a, b, max = 40] = process.argv.slice(2);
const left = JSON.parse(fs.readFileSync(a, 'utf8'));
const right = JSON.parse(fs.readFileSync(b, 'utf8'));
let lines = 0;
const show = v => { const s = JSON.stringify(v); return s === undefined ? 'undefined' : s.length > 120 ? s.slice(0, 120) + '…' : s; };
function diff(x, y, path) {
  if (lines >= max) return;
  if (typeof x === 'object' && x !== null && typeof y === 'object' && y !== null && Array.isArray(x) === Array.isArray(y)) {
    if (Array.isArray(x) && x.length !== y.length) { console.log(`${path}: length ${x.length} vs ${y.length}`); lines++; }
    for (const k of new Set([...Object.keys(x), ...Object.keys(y)])) diff(x[k], y[k], `${path}.${k}`);
    return;
  }
  if (JSON.stringify(x) !== JSON.stringify(y)) { console.log(`${path}: ${show(x)} vs ${show(y)}`); lines++; }
}
diff(left, right, '$');
if (lines === 0) console.log('identical');
