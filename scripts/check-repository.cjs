/* Check tracked documentation links and release metadata without game files.
   Run from any directory: node scripts/check-repository.cjs */
'use strict';
const fs = require('node:fs');
const path = require('node:path');
const { execFileSync } = require('node:child_process');
const root = path.resolve(__dirname, '..');
const files = execFileSync('git', ['ls-files', '-z'], { cwd: root, encoding: 'utf8' }).split('\0').filter(Boolean);
if (!files.length) throw new Error('No tracked files: stage the initial repository before checking it.');
const tracked = new Set(files);
const errors = [];
let links = 0;
for (const file of files.filter(f => f.endsWith('.md'))) {
  const content = fs.readFileSync(path.join(root, file), 'utf8').replace(/^```[^\n]*\n[\s\S]*?^```/gm, '');
  for (const match of content.matchAll(/\[[^\]\n]*\]\(([^)\s]+)(?:\s+"[^"]*")?\)/g)) {
    const href = match[1];
    if (/^(?:[a-z][a-z\d+.-]*:|#|\/\/)/i.test(href)) continue;
    const target = decodeURIComponent(href.split(/[?#]/)[0]);
    if (!target) continue;
    links++;
    const resolved = path.resolve(root, path.dirname(file), target);
    const relative = path.relative(root, resolved).split(path.sep).join('/');
    if (relative.startsWith('../') || path.isAbsolute(relative)) {
      errors.push(`${file}: link escapes repository: ${href}`);
    } else if (!tracked.has(relative) && !files.some(f => f.startsWith(relative + '/'))) {
      errors.push(`${file}: untracked or missing target: ${href}`);
    }
  }
}
const metadata = JSON.parse(fs.readFileSync(path.join(root, 'src/Ember.Mod/modinfo.json'), 'utf8'));
const props = fs.readFileSync(path.join(root, 'Directory.Build.props'), 'utf8');
if (metadata.version !== props.match(/<Version>([^<]+)<\/Version>/)?.[1]) errors.push('Mod and build versions differ.');
if (!/^[a-z0-9]+$/.test(metadata.modid)) errors.push('Invalid mod identifier.');
if (metadata.side !== 'Client' || metadata.requiredOnServer !== false) errors.push('Unexpected client/server metadata.');
for (const file of files) {
  if (/(?:^|\/)(?:bin|obj|test-runs|artifacts|\.research|\.tools)\//.test(file) || /\.(?:dll|exe|nettrace|etl|etlx|dmp|gcdump|hprof|zip)$/i.test(file)) {
    errors.push(`Generated, proprietary or diagnostic binary tracked: ${file}`);
  }
}
if (errors.length) {
  console.error(errors.join('\n'));
  process.exitCode = 1;
} else {
  console.log(`PASS ${links} local Markdown links, version ${metadata.version}, client metadata and tracked-file hygiene (${files.length} files).`);
}
