/* Dependency-free browser smoke test: node viewer/verify.cjs
   Set CHROME_PATH if Chrome is not installed in a standard location. */
'use strict';
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const http = require('node:http');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const { spawn } = require('node:child_process');
const html = fs.readFileSync(path.join(__dirname, 'report.html'), 'utf8');
new vm.Script(html.match(/<script>([\s\S]*?)<\/script>/)[1]);
assert.equal(html.split('/*EMBER_DATA*/null').length - 1, 1);
assert(!/innerHTML|outerHTML|insertAdjacentHTML/.test(html));
assert(!/<(?:script|link)[^>]+(?:src|href)=/i.test(html));
const dist = (mean, p99 = mean * 1.5) => ({ count: 60, mean, p50: mean, p95: mean * 1.2, p99, max: p99 * 1.2 });
const hostile = '<img src=x onerror="window.injected=true"> & </script>';
const fixture = {
  schemaVersion: '1.0.0', producer: 'Ember smoke fixture', id: 'smoke', createdUtc: '2026-09-19T12:00:00Z', side: 'client', gameVersion: '1.22-test', reason: 'smoke', targetIntervalMs: 16.67, slowTickThresholdMs: 50,
  environment: { runtime: '.NET 8', processors: 8 }, settings: { shadows: 'high' }, modConfigs: {},
  mods: [{ id: 'sample', name: hostile, version: '1.0', side: 'client', sourceType: 'zip', fileName: 'sample.zip', systems: ['SampleSystem'], assemblies: ['Sample'], dependencies: { game: '1.22' }, sha256: 'abc123' }],
  assemblies: [{ name: 'Sample', version: '1.0', moduleId: 'module-id', mods: ['sample'] }],
  patches: [{ method: 'Game.Render', owners: ['sample'], prefixes: ['Sample.Before'], postfixes: [], transpilers: [], finalizers: [] }],
  world: { unixMs: 1789819200000, players: 1, entities: 23, chunks: null, entitiesExamined: 20, truncated: true, collectionMs: 0.7, entityTypes: { 'game:hare': 15, 'game:wolf': 5 }, entityDomains: { game: 20 }, pingMs: dist(25) },
  timeline: Array.from({ length: 12 }, (_, i) => ({ unixMs: 1789819200000 + i * 1000, monotonicMs: 5000 + i * 1000, windowSeconds: 1, loopRate: 60 - i, workMs: dist(6 + i / 2), intervalMs: dist(16.67 + i), slowTicks: i > 8 ? 1 : 0, runtime: { cpuOneCorePercent: 110 + i * 3, cpuMachinePercent: 13.75 + i, workingSetBytes: 1e9 + i * 1e7, privateBytes: 1.2e9, managedBytes: 4e8 + i * 1e6, heapBytes: 5e8, fragmentedBytes: 2e7, availableMemoryBytes: 16e9, allocationBytesPerSecond: 5e6 + i * 1e5, gcPauseMilliseconds: i % 3 ? 0 : 2, gen0: i % 3 ? 0 : 1, gen1: 0, gen2: 0, osThreads: 40, threadPoolThreads: 4, threadPoolPending: i, threadPoolCompleted: 100 }, players: 1, entities: 23, chunks: null, censusAgeSeconds: i, collectorMs: 0.2, renderStagesMs: { Opaque: dist(3 + i / 10), Shadow: dist(1) } })),
  ticks: Array.from({ length: 100 }, (_, i) => ({ endUnixMs: 1789819200000 + i * 17, endMonotonicMs: 5000 + i * 17, workMs: 5 + i / 100, intervalMs: 16 + i / 100 })),
  logs: [{ unixMs: 1789819200000, level: 'warning', message: hostile }], events: [{ unixMs: 1789819201000, kind: 'profile', message: 'Capture complete' }],
  findings: [{ severity: 'info', title: 'Synthetic fixture', evidence: 'Smoke-test data only.', nextStep: 'Verify viewer interactions.' }], limitations: ['Synthetic data; not a real game capture.'], metrics: { fps: { unit: 'frames/s', source: 'window', meaning: 'Completed frames per second' } },
  profile: { kind: 'EventPipe sampled thread stacks (not exact on-CPU time)', startedUtc: '2026-09-19T12:00:00Z', durationSeconds: 10, eventsLost: 2, samplesWithoutStack: 0, samplesDroppedByLimit: 0, traceBytes: 100000, sizeLimitReached: false, frames: [{ name: 'Main', module: 'Game', mods: [] }, { name: 'Render', module: 'Game', mods: [] }, { name: hostile, module: 'Sample', mods: ['sample'] }, { name: 'Wait', module: 'CoreLib', mods: [] }], samples: Array.from({ length: 30 }, (_, i) => ({ relativeMs: i * 10, threadId: i % 3 ? 1 : 2, managed: i % 3 !== 0, stack: i % 3 ? [0, 1, 2] : [0, 3] })), hotspots: [], allocations: [{ type: 'System.String', sampledBytes: 102400, events: 8 }], exceptionTypes: { 'System.Exception': 1 }, contentionEvents: 2, contentionTotalMs: 3.4, notes: ['Sample counts are not CPU time.'] }
};
// Deliberately different session, profile and first metric origins catch timestamp drift.
const start = Date.parse(fixture.createdUtc), iso = seconds => new Date(start + seconds * 1000).toISOString();
const sessionFixture = structuredClone(fixture);
Object.assign(sessionFixture, {
  schemaVersion: '1.1.0', id: 'session-smoke', settings: { shadows: 'low', graphics: { viewDistance: 512 } },
  session: { id: 'session-smoke', status: 'completed', stopReason: 'user stopped', startedUtc: iso(0), stoppedUtc: iso(12), recordedSeconds: 12, stackCoverageSeconds: 8, stackCoveragePercent: 66.7, framesObserved: 1000, framesOmitted: 900, droppedBatches: 1, incompleteJournalLines: 1, recovered: true,
    segments: [{ index: 0, startedUtc: iso(2), endedUtc: iso(6), status: 'completed', observedSamples: 2000, retainedSamples: 4 }, { index: 1, startedUtc: iso(6), endedUtc: iso(8), status: 'failed', observedSamples: 0, retainedSamples: 0, error: 'Synthetic trace gap' }, { index: 2, startedUtc: iso(8), endedUtc: iso(12), status: 'completed', observedSamples: 2000, retainedSamples: 3 }], notes: ['Global samples use a deliberate reservoir.'] },
  contextHistory: [{ unixMs: start, settings: { shadows: 'high', graphics: { viewDistance: 256 } }, environment: { cpu: 'Fixture CPU', physicalRamBytes: 32e9 } }, { unixMs: start + 6000, settings: { shadows: 'low', graphics: { viewDistance: 512 } }, environment: { cpu: 'Fixture CPU', physicalRamBytes: 32e9 } }]
});
sessionFixture.mods[0].version = '2.0';
sessionFixture.profile.startedUtc = iso(2);
sessionFixture.profile.durationSeconds = 10;
sessionFixture.profile.observedStackSamples = 4000;
sessionFixture.profile.samplesDroppedByLimit = 3993;
sessionFixture.profile.samples = [-1000, 1000, 2500, 3000, 3500, 5000, 6500].map(relativeMs => ({ relativeMs, threadId: 1, managed: true, stack: [0, 1] }));
sessionFixture.timeline.forEach((p, i) => { p.gpuElapsedMs = i === 3 ? null : dist(8 + i); });
sessionFixture.ticks = [{ endUnixMs: start + 7000, endMonotonicMs: 12000, workMs: 10, intervalMs: 200 }];
sessionFixture.incidents = [{ id: 'stutter-1', title: 'Chunk arrival stutter', startUnixMs: start + 5000, endUnixMs: start + 7000, contextStartUnixMs: start + 3000, contextEndUnixMs: start + 9000, peakFrameMs: 200, slowFrames: 4, confidence: 'medium', stackCoveragePercent: 50,
  windows: sessionFixture.timeline.slice(5, 8), samples: [1000, 2999, 3000, 3999, 5000, 6999, 7000].map((relativeMs, i) => ({ relativeMs, threadId: i === 3 ? 2 : 1, managed: i !== 3, stack: [0, 2] })), hotspots: [{ frame: 2, selfSamples: 7, inclusiveSamples: 7, managedSelfSamples: 6 }], evidence: ['Frame spike coincides with chunk arrival.', hostile], counterEvidence: ['GC pause did not increase.'], hypotheses: [{ severity: 'info', title: 'Meshing work', evidence: 'Ownership hint only.', nextStep: 'Repeat the route with shorter view distance.' }], nextExperiments: ['Repeat the same route after changing only view distance.'], notes: ['Dedicated incident context samples; do not merge with reservoir.'] }];

(async () => {
  const chrome = [process.env.CHROME_PATH, 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe', 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe', '/usr/bin/google-chrome', '/usr/bin/chromium'].filter(Boolean).find(p => fs.existsSync(p));
  assert(chrome, 'Set CHROME_PATH to a local Chromium browser');
  const tempBase = process.env.LOCALAPPDATA ? path.join(process.env.LOCALAPPDATA, 'Temp', 'opencode') : os.tmpdir();
  fs.mkdirSync(tempBase, { recursive: true });
  const temp = fs.mkdtempSync(path.join(tempBase, 'ember-viewer-'));
  let browser, ws;
    const embedded = html.replace('/*EMBER_DATA*/null', JSON.stringify(fixture).replace(/</g, '\\u003c'));
    const sessionEmbedded = html.replace('/*EMBER_DATA*/null', JSON.stringify(sessionFixture).replace(/</g, '\\u003c'));
    const server = http.createServer((req, res) => { res.setHeader('Content-Type', 'text/html; charset=utf-8'); res.end(req.url === '/embedded' ? embedded : req.url === '/session' ? sessionEmbedded : html); });
  try {
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    browser = spawn(chrome, ['--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check', '--remote-debugging-port=0', '--user-data-dir=' + temp, 'about:blank'], { stdio: ['ignore', 'ignore', 'pipe'] });
    const endpoint = await new Promise((resolve, reject) => {
      let buffer = ''; const timer = setTimeout(() => reject(new Error('Chrome startup timed out')), 20000);
      browser.on('error', reject);
      browser.stderr.on('data', chunk => { buffer += chunk; const m = buffer.match(/DevTools listening on (ws:\/\/[^\s]+)/); if (m) { clearTimeout(timer); resolve(m[1]); } });
    });
    const port = new URL(endpoint).port;
    const targets = await (await fetch('http://127.0.0.1:' + port + '/json')).json();
    ws = new WebSocket(targets.find(t => t.type === 'page').webSocketDebuggerUrl);
    await new Promise((resolve, reject) => { ws.addEventListener('open', resolve, { once: true }); ws.addEventListener('error', reject, { once: true }); });
    let id = 0; const pending = new Map(), errors = [];
    ws.addEventListener('message', event => {
      const m = JSON.parse(event.data);
      if (m.id && pending.has(m.id)) { const p = pending.get(m.id); pending.delete(m.id); m.error ? p.reject(m.error) : p.resolve(m.result); }
      if (m.method === 'Runtime.exceptionThrown') errors.push(m.params.exceptionDetails.text);
      if (m.method === 'Runtime.consoleAPICalled' && m.params.type === 'error') errors.push(JSON.stringify(m.params.args));
    });
    const call = (method, params = {}) => new Promise((resolve, reject) => { const key = ++id; pending.set(key, { resolve, reject }); ws.send(JSON.stringify({ id: key, method, params })); });
    const evaluate = async expression => { const r = await call('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true }); assert(!r.exceptionDetails, JSON.stringify(r.exceptionDetails)); return r.result.value; };
    const wait = expression => evaluate(`new Promise((resolve,reject)=>{let n=0;const t=setInterval(()=>{if(${expression}){clearInterval(t);resolve(true)}else if(++n>200){clearInterval(t);reject(new Error('condition timed out'))}},20)})`);
    const click = id => evaluate(`document.getElementById(${JSON.stringify(id)}).click()`);
    const open = async suffix => { const url='http://127.0.0.1:' + server.address().port + suffix;await call('Page.navigate', { url }); await wait(`location.href===${JSON.stringify(url)} && document.readyState==='complete' && document.getElementById('tab-overview')`); };
    await call('Runtime.enable'); await call('Page.enable');await call('Emulation.setLocaleOverride',{locale:'en-US'});
    await open('/');
    assert(await evaluate("!document.getElementById('welcome').hidden"));
    console.log('PASS empty standalone viewer');
    await open('/embedded');
    assert(await evaluate("document.getElementById('tab-investigate').getAttribute('aria-selected')==='true' && !!document.querySelector('.stack-workbench')"));
    await click('tab-overview');
    assert(await evaluate("document.getElementById('welcome').hidden && document.querySelectorAll('.chart').length === 6"));
    for (const tab of ['overview','frames','session','mods','render','world','settings','logs','profile']) {
      await click('tab-' + tab);
      assert(await evaluate("!document.getElementById('content').textContent.includes('could not be rendered')"), tab);
      console.log('PASS section: ' + tab);
    }
    assert(await evaluate("document.querySelectorAll('.flame [role=button]').length > 2"), await evaluate("document.getElementById('content').textContent"));
    await evaluate("document.querySelectorAll('.flame [role=button]')[1].dispatchEvent(new MouseEvent('click',{bubbles:true}))");
    assert(await evaluate("!Array.from(document.querySelectorAll('button')).find(b=>b.textContent==='Reset zoom').disabled"));
    await evaluate("document.querySelector('.tree details').open=true");
    await wait("document.querySelectorAll('.tree details').length > 1");
    await evaluate("const t=document.querySelector('select[aria-label=\"Sampled thread\"]');t.value='2';t.dispatchEvent(new Event('change'));const m=document.querySelector('input[type=checkbox]');m.checked=true;m.dispatchEvent(new Event('change'));");
    assert(await evaluate("document.getElementById('content').textContent.includes('No matching stack observations')"));
    console.log('PASS flame zoom, lazy call tree, thread and managed filters');
    await click('tab-mods');
    assert(await evaluate(`document.getElementById('content').textContent.includes(${JSON.stringify(hostile)}) && !window.injected && !document.querySelector('#content img')`));
    await evaluate("const s=document.querySelector('input[type=search]');s.value='no-such-mod';s.dispatchEvent(new Event('input'));");
    assert(await evaluate("document.getElementById('content').textContent.includes('No records match')"));
    console.log('PASS text-only hostile payload rendering and inventory search');
    const importFile = async (input, data, name) => {
      await evaluate(`(()=>{const dt=new DataTransfer();dt.items.add(new File([${JSON.stringify(JSON.stringify(data))}],${JSON.stringify(name)},{type:'application/json'}));const f=document.getElementById(${JSON.stringify(input)});f.files=dt.files;f.dispatchEvent(new Event('change'));})()`);
      await wait("!document.getElementById('status').textContent.startsWith('Reading')");
    };
    await importFile('baseline-file', fixture, 'baseline.json');
    await click('tab-overview');
    assert(await evaluate("document.getElementById('content').textContent.includes('Baseline comparison') && document.querySelectorAll('path[stroke-dasharray=\"5 5\"]').length > 0"));
    console.log('PASS baseline file input, comparison table and chart overlays');
    await open('/session');
    const clickText = label => evaluate(`Array.from(document.querySelectorAll('button')).find(b=>b.textContent===${JSON.stringify(label)}).click()`);
    const content = () => evaluate("document.getElementById('content').textContent");
    assert(await evaluate("document.querySelector('.incident-queue .incident-choice').getAttribute('aria-pressed')==='true'"));
    assert((await content()).includes('6 matching observations of 6'),'initial incident context excludes its end boundary');
    assert(await evaluate("!!document.querySelector('.stack-canvas .flame') && !!document.querySelector('.stack-inspector')"));
    await evaluate("{const g=document.querySelectorAll('.flame g[role=button]')[1];g.focus();g.dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',bubbles:true}));}");
    assert(await evaluate("document.activeElement.matches('.flame g') && document.querySelector('.method-signature').textContent==='Main'"));
    await evaluate("document.activeElement.dispatchEvent(new KeyboardEvent('keydown',{key:' ',bubbles:true}))");
    assert(await evaluate("document.activeElement.matches('.flame g')"),'repeated flame activation retains focus');
    await evaluate("{const b=document.querySelector('.method-button');b.focus();b.click();}");
    assert(await evaluate("document.activeElement.matches('.method-button[aria-pressed=true]')"));
    await click('investigation-phase-during');
    assert((await content()).includes('2 matching observations of 2'));
    await evaluate("{const b=document.getElementById('investigation-phase-before');b.focus();b.click();}");
    assert.equal(await evaluate('document.activeElement.id'),'investigation-phase-before');
    assert((await content()).includes('2 matching observations of 2'));
    await click('investigation-phase-context');
    await evaluate("{const s=document.getElementById('investigation-search');s.focus();s.value='no-match';s.dispatchEvent(new Event('input'));}");
    await wait("document.getElementById('content').textContent.includes('No matching stack observations')");
    assert.equal(await evaluate('document.activeElement.id'),'investigation-search');
    await evaluate("{const s=document.getElementById('investigation-search');s.value='';s.dispatchEvent(new Event('input'));}");
    await wait("!!document.querySelector('.stack-workbench')");
    await evaluate("{const d=Array.from(document.querySelectorAll('.evidence-section details')).find(d=>d.textContent.includes('Counter-evidence'));d.open=true;}");
    await wait("document.getElementById('content').textContent.includes('GC pause did not increase.')");
    await evaluate("window.selectionBlob=null;const createSelectionUrl=URL.createObjectURL;URL.createObjectURL=b=>{window.selectionBlob=b;return createSelectionUrl(b)};HTMLAnchorElement.prototype.click=function(){window.selectionName=this.download;}");
    await clickText('Export selection');
    const selectedExport=await evaluate('(async()=>JSON.parse(await window.selectionBlob.text()))()');
    assert.equal(selectedExport.samples.length,6);assert.deepEqual(selectedExport.rangeMilliseconds,[1000,7000]);assert.equal(selectedExport.source,'dedicated incident context');assert.equal(selectedExport.profileOriginUtc,iso(2));
    await evaluate("document.getElementById('investigation-timeline').focus();document.activeElement.dispatchEvent(new KeyboardEvent('keydown',{key:'ArrowRight',bubbles:true}));document.activeElement.dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',bubbles:true}));");
    assert(await evaluate("document.getElementById('tab-investigate').getAttribute('aria-selected')==='true' && document.getElementById('content').textContent.includes('Custom interval')"));
    assert.equal(await evaluate('document.activeElement.id'),'investigation-timeline');
    console.log('PASS integrated incident queue, context/phase scope, keyboard continuity, method inspector, evidence, selection export and in-place timeline selection');
    await click('tab-overview');
    assert((await content()).includes('66.7% stack coverage'),await content());
    assert((await content()).includes('GPU elapsed time'));
    assert((await content()).includes('Not GPU utilization or execution busy time'));
    await evaluate("document.querySelector('.incident-choice').click()");
    assert((await content()).includes('GC pause did not increase.'));
    assert((await content()).includes('Repeat the same route after changing only view distance.'));
    assert(await evaluate("!window.injected && !document.querySelector('#content img')"));
    await clickText('Explore selected stacks');
    const bounds = () => evaluate("Array.from(document.querySelectorAll('.range-controls input')).map(i=>Number(i.value))");
    assert.deepEqual(await bounds(), [3, 5], 'incident unix time must align to profile start, not session or first window');
    assert((await content()).includes('2 matching observations of 2'));
    assert((await content()).includes('1 retained observations/s over 2 s'));
    assert((await content()).includes('Using only the incident’s dedicated context samples'));
    assert((await content()).includes('7 retained') === false, 'full source is not a filtered matching count');
    await clickText('Before');
    assert.deepEqual(await bounds(), [1, 3]);
    assert((await content()).includes('2 matching observations of 2'), 'end-exclusive range avoids boundary double counting');
    await clickText('After');
    assert.deepEqual(await bounds(), [5, 7]);
    assert((await content()).includes('2 matching observations of 2'));
    await clickText('Comparable normal');
    assert.deepEqual(await bounds(), [1, 3]);
    assert((await content()).includes('Using the global retained sample set'));
    assert((await content()).includes('2 matching observations of 2'));
    await clickText('During');
    await evaluate("{const thread=document.querySelector('select[aria-label=\"Sampled thread\"]');thread.value='2';thread.dispatchEvent(new Event('change'));const managed=document.querySelector('input[type=checkbox]');managed.checked=true;managed.dispatchEvent(new Event('change'));}");
    assert((await content()).includes('No matching stack observations'));
    await clickText('Before');
    assert(await evaluate("document.querySelector('select[aria-label=\"Sampled thread\"]').value==='2' && document.querySelector('input[type=checkbox]').checked"));
    assert((await content()).includes('No matching stack observations'));
    await evaluate("{const thread=document.querySelector('select[aria-label=\"Sampled thread\"]');thread.value='all';thread.dispatchEvent(new Event('change'));const managed=document.querySelector('input[type=checkbox]');managed.checked=false;managed.dispatchEvent(new Event('change'));}");
    await click('tab-frames');
    await clickText('Select frame range');
    assert.deepEqual(await bounds(), [4.8, 5], 'slow frame interval maps to profile-origin bounds');
    await click('tab-frames');
    await evaluate("{const p=document.querySelector('.chart');p.focus();p.dispatchEvent(new KeyboardEvent('keydown',{key:'ArrowRight',bubbles:true}));p.dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',bubbles:true}));}");
    assert.deepEqual(await bounds(), [-2, -1], 'chart window selection shares profile-origin seconds');
    await click('tab-frames');
    await evaluate("{const p=document.querySelector('.chart');p.scrollIntoView({block:'center'});const r=p.getBoundingClientRect();const x=t=>r.left+(53+(t+2)/11*532)/600*r.width;window.dragCoordinates={a:x(2),b:x(4),y:r.top+r.height/2};}");
    const coords = await evaluate('window.dragCoordinates');
    await call('Input.dispatchMouseEvent', { type: 'mousePressed', x: coords.a, y: coords.y, button: 'left', clickCount: 1 });
    await call('Input.dispatchMouseEvent', { type: 'mouseMoved', x: coords.b, y: coords.y, button: 'left', buttons: 1 });
    await call('Input.dispatchMouseEvent', { type: 'mouseReleased', x: coords.b, y: coords.y, button: 'left', clickCount: 1 });
    assert((await bounds()).every((n,i)=>Math.abs(n-[2,4][i])<.02), 'drag selection bounds');
    await evaluate("const inputs=document.querySelectorAll('.range-controls input');inputs[0].value='3';inputs[1].value='5';");
    await clickText('Apply time range');
    assert((await content()).includes('2 matching observations of 2'));
    assert((await content()).includes('Using the global retained sample set'));
    console.log('PASS 1.1 incident evidence, dense samples, shared time origin, before/during/after/normal, slow-frame and chart selections');
    await click('tab-session');
    assert((await content()).includes('Coverage gap · No segment'));
    assert((await content()).includes('Coverage gap · failed'));
    assert((await content()).includes('graphics.viewDistance'));
    assert((await content()).includes('shadows'));
    assert(await evaluate("document.querySelectorAll('.coverage .gap').length === 2"));
    await evaluate("Array.from(document.querySelectorAll('details')).find(d=>d.querySelector('summary').textContent.startsWith('Machine / RAM & settings')).open=true");
    await wait("Array.from(document.querySelectorAll('summary')).some(s=>s.textContent==='Machine / RAM')");
    await evaluate("Array.from(document.querySelectorAll('details')).find(d=>d.querySelector('summary').textContent==='Machine / RAM').open=true");
    await wait("document.getElementById('content').textContent.includes('physicalRamBytes')");
    await importFile('baseline-file', fixture, 'baseline-1.0.json');
    await click('tab-overview');
    assert((await content()).includes('Version changed'));
    assert((await content()).includes('Settings differences'));
    assert((await content()).includes('shadows'));
    await evaluate("window.savedBlob=null;window.savedName=null;const create=URL.createObjectURL;URL.createObjectURL=b=>{window.savedBlob=b;return create(b);};HTMLAnchorElement.prototype.click=function(){window.savedName=this.download;};");
    await click('summary');
    const brief = await evaluate('window.savedBlob.text()');
    assert.equal(await evaluate('window.savedName'), 'GPT-REPORT.md');
    for (const evidence of ['Chunk arrival stutter','GC pause did not increase.','Repeat the same route','Stack coverage gaps','Baseline mod differences','physicalRamBytes','4000','shadows','not trace corruption','Incident metric windows']) assert(brief.includes(evidence), 'brief: ' + evidence);
    console.log('PASS coverage gaps, lazy machine/RAM timeline, nested settings changes, baseline mod/settings differences and one-file GPT brief');
    await importFile('report-file', { wrong: true }, 'invalid.json');
    assert(await evaluate("document.getElementById('status').textContent.includes('Could not open')"));
    await importFile('report-file', { schemaVersion:'1.0.0',producer:'Ember',timeline:[],mods:[],ticks:[],side:'client',gameVersion:'test' }, 'empty.json');
    for (const tab of ['overview','frames','session','mods','render','world','settings','logs','profile']) {
      await click('tab-' + tab);
      assert(await evaluate("!document.getElementById('content').textContent.includes('could not be rendered')"), 'empty ' + tab);
    }
    console.log('PASS rejected invalid JSON shape and empty report sections');
    await importFile('report-file', {schemaVersion:'1.1.0',producer:'Ember',timeline:[],ticks:[],incidents:[],contextHistory:[],session:{status:'recovered',recordedSeconds:0,segments:[]},profile:{frames:[],samples:[],hotspots:[],durationSeconds:0}}, 'empty-session.json');
    for(const tab of ['overview','session','frames','profile']){await click('tab-'+tab);assert(!(await content()).includes('could not be rendered'),'empty 1.1 '+tab);}
    console.log('PASS empty 1.1 session and stack profile');
    await open('/session');
    await click('tab-overview');
    await evaluate(`(()=>{const data=structuredClone(INITIAL);data.incidents=[];data.profile.samples=Array.from({length:400000},(_,i)=>({relativeMs:i/40,threadId:i%4,managed:!!(i%2),stack:[0,1,i%2?2:3]}));data.ticks=Array.from({length:200000},(_,i)=>({endUnixMs:${start}+i/20,endMonotonicMs:i/20,intervalMs:16+i%11,workMs:5}));const dt=new DataTransfer();dt.items.add(new File([JSON.stringify(data)],'large.json',{type:'application/json'}));const input=document.getElementById('report-file');input.files=dt.files;input.dispatchEvent(new Event('change'));})()`);
    await wait("document.getElementById('status').textContent.includes('Report loaded locally: large.json')");
    assert(!await evaluate("!!document.querySelector('.flame')"), 'profile work stays lazy on overview');
    await evaluate("window.profileYielded=false;document.getElementById('tab-profile').click();setTimeout(()=>{window.profileYielded=!!document.querySelector('.processing');document.getElementById('tab-mods').click();},0)");
    await wait("document.getElementById('tab-mods').getAttribute('aria-selected')==='true'");
    assert(await evaluate('window.profileYielded'), 'large profile aggregation yields to navigation');
    await click('tab-profile');
    await wait("!!document.querySelector('.flame') && !document.querySelector('.processing')");
    assert((await content()).includes('400,000 matching observations of 400,000'));
    await evaluate("const input=document.querySelector('input[aria-label=\"Search stack frames\"]');input.value='no matching frame';input.dispatchEvent(new Event('input'));input.value='Render';input.dispatchEvent(new Event('input'));");
    await wait("!!document.querySelector('.processing')");
    await wait("!!document.querySelector('.flame') && !document.querySelector('.processing') && document.getElementById('content').textContent.includes('400,000 matching observations')");
    await evaluate("input.value='no matching frame';input.dispatchEvent(new Event('input'));");
    await wait("document.getElementById('content').textContent.includes('No matching stack observations') && !document.querySelector('.processing')");
    assert((await content()).includes('0 matching observations of 400,000'));
    console.log('PASS 400,000-sample / 200,000-frame capture, lazy profile aggregation, yielding/cancellation and debounced search');
    await open('/embedded');
    await call('Emulation.setDeviceMetricsOverride', { width: 390, height: 844, deviceScaleFactor: 1, mobile: true });
    assert(await evaluate('document.documentElement.scrollWidth <= 390'), 'mobile horizontal overflow');
    await open('/session');
    await evaluate("document.querySelector('.incident-choice').click()");
    for(const tab of ['overview','profile','session']){await click('tab-'+tab);assert(await evaluate('document.documentElement.scrollWidth <= 390'),'mobile session '+tab);}
    await click('tab-overview');
    const screenshot = await call('Page.captureScreenshot', { format: 'png' });
    const screenshotPath = path.join(tempBase, 'ember-viewer-mobile.png');
    fs.writeFileSync(screenshotPath, Buffer.from(screenshot.data, 'base64'));
    console.log('PASS mobile layout at 390px; screenshot: ' + screenshotPath);
    assert.deepEqual(errors, [], 'Browser console/runtime errors');
    console.log('PASS zero browser console/runtime errors; all viewer checks passed');
  } finally {
    if (ws) ws.close();
    server.close();
    if (browser) { const exited=new Promise(resolve => browser.once('exit', resolve));browser.kill();await Promise.race([exited,new Promise(resolve=>setTimeout(resolve,3000))]); }
    try { fs.rmSync(temp, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 }); } catch { /* Chrome may briefly retain profile handles. */ }
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
