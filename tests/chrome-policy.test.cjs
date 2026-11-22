const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

test('PAC routes only matching HTTP hosts including more specific overrides', () => {
  const observer = {addListener() {}};
  const context = vm.createContext({chrome:{
    runtime:{onMessage:observer,onStartup:observer}, alarms:{onAlarm:observer},
    storage:{local:{get:async()=>({enabled:false})}}
  }});
  vm.runInContext(fs.readFileSync(path.join(__dirname,'../chrome/worker.js'),'utf8'),context);
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
