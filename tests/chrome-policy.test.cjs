const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const {webcrypto} = require('node:crypto');

function worker(proxy = {}, extra = {}) {
  const observer = {addListener() {}};
  const context = vm.createContext({AbortController, AbortSignal, URL, crypto:webcrypto,
    chrome:{
      proxy:{settings:proxy},
      runtime:{id:'test-extension',onMessage:observer,onStartup:observer,onInstalled:observer},
      alarms:{onAlarm:observer},
      storage:{local:{get:async()=>({enabled:false})}}
    }, ...extra
  });
  vm.runInContext(fs.readFileSync(path.join(__dirname,'../chrome/worker.js'),'utf8'),context);
  return context;
}

function pac(rules, defaultMode) {
  const context = worker();
  vm.runInContext(context.pacFor(rules, defaultMode),context);
  return host => context.FindProxyForURL('https://' + host + '/',host);
}

test('legacy PAC routes only matching hosts with the longest matching rule first', () => {
  const resolve = pac([
    {host:'example.net',sub:true,route:true},
    {host:'local.example.net',sub:true,route:false},
    {host:'example.org',sub:false,route:true}
  ]);
  for(const host of ['a.example.net','example.net','EXAMPLE.ORG.'])
    assert.equal(resolve(host),'PROXY 127.0.0.1:17831');
  for(const host of ['n.example.org','x.local.example.net','falseexample.net','other.com'])
    assert.equal(resolve(host),'DIRECT');
});

test('Direct default forwards unmatched public hosts to the adapter-bound proxy', () => {
  const resolve = pac([{host:'selected.example',sub:false,mode:2,route:true}],1);
  for(const host of ['selected.example','other.example','a.selected.example'])
    assert.equal(resolve(host),'PROXY 127.0.0.1:17831');
});

test('VPN default and Direct exceptions never advertise a DIRECT fallback', () => {
  const ctx = worker();
  const rules = [{host:'domestic.example',sub:true,mode:1,route:true}];
  const resolve = pac(rules,2);
  for(const host of ['domestic.example','www.domestic.example','elsewhere.example'])
    assert.equal(resolve(host),'PROXY 127.0.0.1:17831');
  assert.doesNotMatch(ctx.pacFor(rules,2), /PROXY 127\.0\.0\.1:17831\s*;\s*DIRECT/);
  assert.notEqual(ctx.pacFor(rules,1),ctx.pacFor(rules,2));
});

test('explicit System exception overrides either enforced default without breaking label boundaries', () => {
  for(const defaultMode of [1,2]) {
    const resolve = pac([{host:'system.example',sub:true,mode:0,route:false}],defaultMode);
    assert.equal(resolve('system.example'),'DIRECT');
    assert.equal(resolve('a.system.example'),'DIRECT');
    assert.equal(resolve('fakesystem.example'),'PROXY 127.0.0.1:17831');
    assert.equal(resolve('system.example.evil.test'),'PROXY 127.0.0.1:17831');
  }
});

test('loopback remains reachable in every mode, including control API and local developer hosts', () => {
  for(const defaultMode of [0,1,2]) {
    const resolve = pac([{host:'localhost',sub:true,mode:2}],defaultMode);
    for(const host of ['localhost','LOCALHOST.','app.localhost','127.0.0.1','127.42.0.7','::1','[::1]','0:0:0:0:0:0:0:1'])
      assert.equal(resolve(host),'DIRECT',host);
    assert.equal(resolve('localhost.evil.test'),defaultMode ? 'PROXY 127.0.0.1:17831' : 'DIRECT');
  }
});

test('site matching reports exact, inherited, and default modes consistently', () => {
  const ctx = worker();
  const policy = {defaultMode:1,rules:[
    {host:'example.net',sub:true,mode:2},
    {host:'direct.example.net',sub:false,mode:1}
  ]};
  assert.equal(ctx.matchSite(policy,'example.net').mode,2);
  assert.equal(ctx.matchSite(policy,'a.example.net').inherited,true);
  assert.equal(ctx.matchSite(policy,'direct.example.net').exact,true);
  assert.equal(ctx.matchSite(policy,'other.test').source,'default');
  assert.equal(ctx.matchSite(policy,'other.test').mode,1);
  assert.equal(ctx.matchSite({defaultMode:2,rules:[{host:'old.test',route:false}]},'old.test').mode,0);
  assert.equal(ctx.matchSite({rules:[{host:'old.test',route:true}]},'old.test').mode,null);
});

test('active site parser handles IDN and excludes unsupported, IP, and loopback pages', () => {
  const ctx = worker();
  assert.equal(ctx.siteHost('https://EXAMPLE.org./path?q=1'),'example.org');
  assert.equal(ctx.siteHost('https://bücher.example/'),'xn--bcher-kva.example');
  for(const url of ['chrome://extensions','file:///C:/file.txt','about:blank','https://127.0.0.1/','http://localhost:17831/',
    'https://app.localhost/','https://[::1]/','http://192.168.1.1/','not a url']) assert.equal(ctx.siteHost(url),'');
});

test('repair actual proxy after an external change even when revision is unchanged', async () => {
  let value = {mode:'direct'}, writes = 0;
  const ctx = worker({
    get:async () => ({levelOfControl:'controlled_by_this_extension', value}),
    set:async args => { writes++; value = args.value; }
  });
  const policy = {revision:'same', defaultMode:1,rules:[{host:'example.net',sub:true,mode:2}]};
  await ctx.ensurePolicy(policy);
  await ctx.ensurePolicy(policy);
  assert.equal(writes,1);
  value = {mode:'pac_script',pacScript:{mandatory:true,data:'obsolete script'}};
  await ctx.ensurePolicy(policy);
  assert.equal(writes,2);
});

test('no success when Chrome refuses ownership or acknowledges a different setting', async () => {
  const policy = {rules:[]};
  const blocked = worker({get:async () => ({levelOfControl:'controlled_by_other_extensions'})});
  await assert.rejects(blocked.ensurePolicy(policy), /پراکسی/);
  const ignored = worker({get:async () => ({levelOfControl:'controllable_by_this_extension'}), set:async () => {}});
  await assert.rejects(ignored.ensurePolicy(policy), /تأیید/);
});

function client() {
  const state = {enabled:false, profile:'d84f849e-d6df-46c3-a532-e1162b97b9cc'};
  const observation = {posts:[],clears:0};
  let value = {mode:'direct'}, level = 'controllable_by_this_extension';
  let policy = {revision:'revision-1',defaultMode:1,rules:[],token:'test-nonce'};
  let tab = {id:7,url:'https://current.example/path'};
  let listener;
  const observer = {addListener(){}};
  const chrome = {
    proxy:{settings:{get:async()=>({value,levelOfControl:level}),
      set:async options=>{value=options.value;level='controlled_by_this_extension';},
      clear:async()=>{observation.clears++;value={mode:'direct'};level='controllable_by_this_extension';}}},
    storage:{local:{get:async()=>({...state}),set:async patch=>Object.assign(state,patch)}},
    runtime:{id:'test-extension',onMessage:{addListener(fn){listener=fn;}},onStartup:observer,onInstalled:observer},
    alarms:{get:async()=>({}),create:async()=>{},clear:async()=>{},onAlarm:observer},
    tabs:{get:async id=>{assert.equal(id,7);return tab;}}
  };
  const fetch = async (url,options) => {
    if(options.method === 'POST') {
      observation.posts.push({url,options});
      const body = JSON.parse(options.body);
      const modes={direct:1,vpn:2,system:0};
      policy={...policy,revision:'revision-2',rules:body.mode === 'default' ? [] :
        [{host:body.host,mode:modes[body.mode],sub:body.subdomains,route:body.mode!=='system'}]};
      return {ok:true,json:async()=>policy};
    }
    if(url.endsWith('/chrome-rules/')) return {ok:true,json:async()=>policy};
    return new Promise((resolve,reject)=> {
      if(options.signal.aborted) {reject(Object.assign(new Error(),{name:'AbortError'}));return;}
      options.signal.addEventListener('abort',()=>reject(Object.assign(new Error(),{name:'AbortError'})),{once:true});
    });
  };
  const ctx = worker({}, {chrome,fetch});
  return {ctx,state,observation,get policy(){return policy;},setPolicy(p){policy=p;},setTab(t){tab=t;},
    dispatch:(msg,sender,reply)=>listener(msg,sender,reply)};
}

test('site update authenticates with live token, checks current tab, and verifies returned policy', async () => {
  const c=client();
  await Promise.resolve();
  c.state.enabled=true;
  await c.ctx.ensurePolicy(c.policy);
  const result=await c.ctx.change(()=>c.ctx.setSite({tabId:7,host:'current.example',mode:'vpn',subdomains:false}));
  c.ctx.stopFollowing();
  assert.equal(result.site.mode,2);
  const [{url,options}]=c.observation.posts;
  assert.equal(url,'http://127.0.0.1:17831/chrome-site/');
  assert.equal(options.headers['X-Netcontroll-Token'],'test-nonce');
  assert.equal(options.headers['X-Netcontroll-Client'],'chrome-v1');
  assert.deepEqual(JSON.parse(options.body),{host:'current.example',mode:'vpn',subdomains:false});
  const status=await c.ctx.status('current.example');
  assert.equal(status.applied,true);
  assert.equal(status.canEdit,true);
  assert.equal('token' in status,false);
});

test('site update refuses disconnected profiles, unsupported mode, and changed active page', async () => {
  const c=client();
  await Promise.resolve();
  const message={tabId:7,host:'current.example',mode:'vpn'};
  await assert.rejects(c.ctx.setSite(message),/متصل/);
  c.state.enabled=true;
  await assert.rejects(c.ctx.setSite({...message,mode:'unknown'}),/معتبر/);
  c.setTab({id:7,url:'https://changed.example/'});
  await assert.rejects(c.ctx.setSite(message),/تغییر کرده/);
  assert.equal(c.observation.posts.length,0);
});

test('reset deletes only the exact host rule and returns the current default', async () => {
  const c=client();
  await Promise.resolve();
  c.state.enabled=true;
  c.setPolicy({...c.policy,rules:[{host:'current.example',mode:2,sub:false,route:true}]});
  await c.ctx.ensurePolicy(c.policy);
  const result=await c.ctx.change(()=>c.ctx.setSite({tabId:7,host:'current.example',mode:'default'}));
  c.ctx.stopFollowing();
  assert.equal(result.site.source,'default');
  assert.equal(result.site.mode,1);
  assert.equal(JSON.parse(c.observation.posts[0].options.body).mode,'default');
});

test('disconnect is serialized after site update and does not leave proxy control enabled', async () => {
  const c=client();
  await Promise.resolve();
  c.state.enabled=true;
  await c.ctx.ensurePolicy(c.policy);
  const update=c.ctx.change(()=>c.ctx.setSite({tabId:7,host:'current.example',mode:'vpn'}));
  const disconnect=c.ctx.change(()=>c.ctx.toggle(false));
  await Promise.all([update,disconnect]);
  await Promise.resolve();
  assert.equal(c.state.enabled,false);
  assert.equal(c.observation.clears,1);
  const status=await c.ctx.status('current.example');
  assert.equal(status.applied,false);
  assert.equal(status.canDisconnect,false);
});

test('message listener rejects non-extension callers and unknown actions', () => {
  const c=client();
  const reply=()=>assert.fail('unexpected reply');
  assert.equal(c.dispatch({action:'site'}, {id:'other-extension'},reply),false);
  assert.equal(c.dispatch(null,{id:'test-extension'},reply),false);
  assert.equal(c.dispatch({action:'unknown'},{id:'test-extension'},reply),false);
});

test('manifest requests only active-tab inspection, not browsing-history permissions', () => {
  const manifest=JSON.parse(fs.readFileSync(path.join(__dirname,'../chrome/manifest.json'),'utf8'));
  assert.ok(manifest.permissions.includes('activeTab'));
  assert.ok(!manifest.permissions.includes('tabs'));
  assert.deepEqual(manifest.host_permissions,['http://127.0.0.1/*']);
});
