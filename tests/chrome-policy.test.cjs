const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

function worker(proxy = {}) {
  const observer = {addListener() {}};
  const context = vm.createContext({AbortController,chrome:{
    proxy:{settings:proxy}, runtime:{onMessage:observer,onStartup:observer,onInstalled:observer}, alarms:{onAlarm:observer},
    storage:{local:{get:async()=>({enabled:false})}}
  }});
  vm.runInContext(fs.readFileSync(path.join(__dirname,'../chrome/worker.js'),'utf8'),context);
  return context;
}

test('PAC routes only matching HTTP hosts including more specific overrides', () => {
  const context = worker();
  vm.runInContext(context.pacFor([
    {host:'local.example.net',sub:true,route:false},
    {host:'example.net',sub:true,route:true},
    {host:'example.org',sub:false,route:true}
  ]),context);
  for(const host of ['a.example.net','example.net','EXAMPLE.ORG.'])
    assert.equal(context.FindProxyForURL('',host),'PROXY 127.0.0.1:17831');
  for(const host of ['n.example.org','x.local.example.net','falseexample.net','other.com'])
    assert.equal(context.FindProxyForURL('',host),'DIRECT');
});

test('repair the actual proxy even when the revision has not changed', async () => {
  let value = {mode:'direct'}, writes = 0;
  const ctx = worker({
    get:async () => ({levelOfControl:'controlled_by_this_extension', value}),
    set:async args => { writes++; value = args.value; }
  });
  const policy = {revision:'same', rules:[{host:'example.net',sub:true,route:true}]};
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
