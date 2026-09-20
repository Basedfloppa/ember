/* Build and inspect the production viewer using an existing real capture.
   node viewer/review.cjs <report.json>
   Writes a standalone preview and bounded desktop/mobile evidence. */
'use strict';
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict'),{spawn}=require('node:child_process'),{pathToFileURL}=require('node:url');
const source=process.argv[2];assert(source,'Pass the path to an Ember report.json');
const root=path.resolve(__dirname,'..'),out=path.join(root,'test-runs/viewer-workbench'),review=path.join(root,'.impeccable/review',process.env.EMBER_REVIEW_NAME||'workbench');
fs.mkdirSync(out,{recursive:true});fs.mkdirSync(review,{recursive:true});
const data=JSON.parse(fs.readFileSync(source,'utf8')),template=fs.readFileSync(path.join(__dirname,'report.html'),'utf8');
const preview=path.join(out,'report.html');fs.writeFileSync(preview,template.replace('/*EMBER_DATA*/null',()=>JSON.stringify(data).replace(/</g,'\\u003c').replace(/\u2028/g,'\\u2028').replace(/\u2029/g,'\\u2029')));
(async()=>{let browser,ws;const tmp=fs.mkdtempSync(path.join(process.env.LOCALAPPDATA,'Temp/opencode/ember-workbench-'));
 try{
  browser=spawn('C:/Program Files/Google/Chrome/Application/chrome.exe',['--headless=new','--disable-gpu','--no-first-run','--no-default-browser-check','--remote-debugging-port=0','--user-data-dir='+tmp,'about:blank'],{stdio:['ignore','ignore','pipe']});
  const endpoint=await new Promise((resolve,reject)=>{let s='';const timer=setTimeout(()=>reject(Error('Chrome startup timeout')),20000);browser.on('error',reject);browser.stderr.on('data',b=>{s+=b;const m=s.match(/DevTools listening on (ws:\/\/\S+)/);if(m){clearTimeout(timer);resolve(m[1]);}});});
  const targets=await(await fetch('http://127.0.0.1:'+new URL(endpoint).port+'/json')).json();ws=new WebSocket(targets.find(t=>t.type==='page').webSocketDebuggerUrl);await new Promise(r=>ws.addEventListener('open',r,{once:true}));let id=0;const pending=new Map(),errors=[];
  ws.addEventListener('message',e=>{const m=JSON.parse(e.data);if(m.id&&pending.has(m.id)){const p=pending.get(m.id);pending.delete(m.id);m.error?p.reject(m.error):p.resolve(m.result);}if(m.method==='Runtime.exceptionThrown')errors.push(m.params.exceptionDetails);if(m.method==='Runtime.consoleAPICalled'&&m.params.type==='error')errors.push(m.params.args);});
  const call=(method,params={})=>new Promise((resolve,reject)=>{const n=++id,timer=setTimeout(()=>reject(Error('CDP timeout '+method)),30000);pending.set(n,{resolve:v=>{clearTimeout(timer);resolve(v)},reject:e=>{clearTimeout(timer);reject(e)}});ws.send(JSON.stringify({id:n,method,params}));});
  const evaluate=async expression=>{const r=await call('Runtime.evaluate',{expression,returnByValue:true,awaitPromise:true});assert(!r.exceptionDetails,JSON.stringify(r.exceptionDetails));return r.result.value;};
  const wait=expression=>evaluate(`new Promise((resolve,reject)=>{let n=0;const t=setInterval(()=>{if(${expression}){clearInterval(t);resolve(true)}else if(++n>400){clearInterval(t);reject(Error('Wait failed'))}},25)})`);
  await call('Page.enable');await call('Runtime.enable');await call('Emulation.setLocaleOverride',{locale:'en-US'});
  const results=[];
  for(const [width,height,name] of [[1440,1050,'desktop'],[1920,1080,'wide'],[390,844,'mobile']]){
   await call('Emulation.setDeviceMetricsOverride',{width,height,deviceScaleFactor:1,mobile:false});await call('Page.navigate',{url:pathToFileURL(preview).href});
   await wait("document.readyState==='complete' && document.querySelector('.stack-workbench') && !document.querySelector('.processing')");
   assert(await evaluate('document.documentElement.scrollWidth<=innerWidth+1'),name+' overflow');
   const selected=await evaluate("(()=>{const rail=document.querySelector('.incident-queue').getBoundingClientRect(),s=document.querySelector('.incident-choice[aria-pressed=true]').getBoundingClientRect();return {visible:s.left>=rail.left&&s.right<=rail.right&&s.top>=rail.top&&s.bottom<=rail.bottom,text:document.querySelector('.incident-choice[aria-pressed=true]').textContent}})()");assert(selected.visible,name+' selected incident hidden');
   if(width>=1440)assert(await evaluate("document.querySelector('.flame-scroll').getBoundingClientRect().top<750"),'Flame graph should be usable in the first desktop viewport');
   for(const full of [false,true]){const shot=await call('Page.captureScreenshot',{format:'png',captureBeyondViewport:full,...(full?{clip:{x:0,y:0,width,height:Math.min(7000,await evaluate('document.documentElement.scrollHeight')),scale:1}}:{})});fs.writeFileSync(path.join(review,name+(full?'-full':'')+'.png'),Buffer.from(shot.data,'base64'));}
   results.push({width,height,name,selected});
  }
  assert(await evaluate("(()=>{const f=document.querySelector('.flame-scroll').getBoundingClientRect(),i=document.querySelector('.stack-inspector').getBoundingClientRect(),t=document.querySelector('.stack-results').getBoundingClientRect();return i.top>=f.bottom&&i.bottom<=t.top})()"),'Mobile inspector must follow flame and precede results');
  await evaluate("document.querySelector('.sample-details').open=true");
  await wait("document.querySelector('.sample-details').textContent.includes('Start inclusive, end exclusive')");
  assert(await evaluate("document.querySelector('.sample-details').textContent.includes('not CPU utilization') && document.querySelector('.sample-details').textContent.includes('export uses the time')"));
  await evaluate("{const method=document.querySelectorAll('.method-button')[1];method.focus();method.click();}");
  assert(await evaluate("document.activeElement.matches('.method-button[aria-pressed=true]') && !document.activeElement.parentElement.querySelector('.method-jump').hidden"),'Selecting a method retains focus and exposes its detail link');
  await evaluate("document.activeElement.parentElement.querySelector('.method-jump').click()");assert.equal(await evaluate('document.activeElement.id'),'selected-method');
  for(let page=0;page<12;page++){
   const hasNext=await evaluate("!!document.querySelector('.stack-results button[data-page=next]:not(:disabled)')");if(!hasNext)break;
   await evaluate("{const next=document.querySelector('.stack-results button[data-page=next]');next.focus();next.click();}");
   assert(await evaluate("document.activeElement.matches('.stack-results button[data-page]:not(:disabled)')"),'Next-page focus including boundary');
  }
  assert.equal(await evaluate('document.activeElement.dataset.page'),'previous');
  for(let page=0;page<12;page++){
   const hasPrevious=await evaluate("!!document.querySelector('.stack-results button[data-page=previous]:not(:disabled)')");if(!hasPrevious)break;
   await evaluate("{const prev=document.querySelector('.stack-results button[data-page=previous]');prev.focus();prev.click();}");
   assert(await evaluate("document.activeElement.matches('.stack-results button[data-page]:not(:disabled)')"),'Previous-page focus including boundary');
  }
  assert.equal(await evaluate('document.activeElement.dataset.page'),'next');
  await evaluate("{const g=document.getElementById('timeline-incident-1');g.focus();g.dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',bubbles:true}));}");
  await wait("document.querySelector('.stack-workbench') && !document.querySelector('.processing')");assert.equal(await evaluate('document.activeElement.id'),'timeline-incident-1');
  await call('Emulation.setEmulatedMedia',{features:[{name:'prefers-reduced-motion',value:'reduce'}]});assert.equal(await evaluate("getComputedStyle(document.getElementById('load')).transitionDuration"),'0s');
  assert.deepEqual(errors,[]);fs.writeFileSync(path.join(review,'verification.json'),JSON.stringify({passed:true,source:path.resolve(source),reportId:data.id,preview,results,checks:['offline file load','desktop/wide/mobile no overflow','active incident visible','timeline marker keyboard focus','mobile inspector before results','method selection retains focus with explicit detail link','repeated next/previous pagination focus including boundaries','reduced motion','zero console errors']},null,2));
  console.log('PASS real offline report: desktop/wide/mobile, selected incident, timeline markers, reduced motion; zero errors. Preview: '+preview);
 }finally{ws?.close();if(browser){const exited=new Promise(r=>browser.once('exit',r));browser.kill();await Promise.race([exited,new Promise(r=>setTimeout(r,3000))]);}try{fs.rmSync(tmp,{recursive:true,force:true,maxRetries:5,retryDelay:100})}catch{}}
})().catch(e=>{console.error(e);process.exitCode=1});
