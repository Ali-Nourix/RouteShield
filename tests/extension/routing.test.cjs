const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const root = path.resolve(__dirname, '../..');

function load(browser) {
  const event = () => ({ addListener() {} });
  const done = () => Promise.resolve();
  const context = vm.createContext({
    URL, AbortController, setTimeout, clearTimeout, setInterval() {},
    fetch: async () => ({ ok: false }),
    importScripts() {},
    browser: { proxy: { onRequest: event(), onError: event() }, webRequest: { onErrorOccurred: event() },
      tabs: { onRemoved: event() }, runtime: { onMessage: event() },
      browserAction: { setBadgeText: done, setBadgeBackgroundColor: done } },
    chrome: { storage: { onChanged: event() },
      runtime: { onMessage: event(), onInstalled: event(), onStartup: event() },
      tabs: { onUpdated: event(), onRemoved: event() }, alarms: { onAlarm: event() },
      webRequest: { onBeforeRequest: event(), onErrorOccurred: event() } }
  });
  vm.runInContext(fs.readFileSync(path.join(root, 'extension/shared/bridge.js'), 'utf8'), context);
  vm.runInContext(fs.readFileSync(path.join(root, `extension/${browser}/background.js`), 'utf8'), context);
  return context;
}

function pac(context, assignments, state, cached = {}) {
  context.input = { assignments, state, cached };
  const source = vm.runInContext('pacFor(rulesFor(input.assignments, {}, input.state, input.cached))', context);
  const pacContext = vm.createContext({ dnsDomainIs: (host, suffix) => host.endsWith(suffix) });
  vm.runInContext(source, pacContext);
  return host => vm.runInContext(`FindProxyForURL('https://${host}/', '${host}')`, pacContext);
}

const profile = { kind: 'profile', id: 'p1' };
const bypass = { kind: 'bypass' };
const live = { connected: true, routes: [
  { kind: 'profile', id: 'p1', port: 41001 }, { kind: 'bypass', port: 41002 }
] };

test('Chrome: an explicitly protected site beats a previously assigned bypass family', () => {
  const route = pac(load('chrome'), { 'youtube.com': bypass, 'google.com': profile }, live);
  assert.equal(route('google.com'), 'SOCKS5 127.0.0.1:41001');
  assert.equal(route('youtube.com'), 'SOCKS5 127.0.0.1:41002');
});

test('Chrome: explicit bypass also beats a previously assigned profile family', () => {
  const route = pac(load('chrome'), { 'youtube.com': profile, 'google.com': bypass }, live);
  assert.equal(route('google.com'), 'SOCKS5 127.0.0.1:41002');
});

test('Chrome: an unavailable profile uses the dead port, never a possibly reused cached port', () => {
  const route = pac(load('chrome'), { 'example.com': profile }, { connected: false, routes: [] }, { 'profile:p1': 41099 });
  assert.equal(route('example.com'), 'SOCKS5 127.0.0.1:1');
  assert.equal(route('unassigned.example'), 'DIRECT');
});

test('Firefox: ambiguous service-worker ownership fails closed when either tab is protected', () => {
  const ctx = load('firefox');
  vm.runInContext(`assignments.set(1, {kind:'bypass'}); assignments.set(2, {kind:'profile',id:'p1'});
    tabSites.set(1, 'example.com'); tabSites.set(2, 'example.com');`, ctx);
  const port = vm.runInContext(`proxyFor(assignmentByOrigin({originUrl:'https://example.com/sw.js'}))[0].port`, ctx);
  assert.equal(port, 1);
});

test('Firefox: identical assignments keep the selected VPN', () => {
  const ctx = load('firefox');
  ctx.live = live;
  vm.runInContext(`state = live; assignments.set(1, {kind:'profile',id:'p1'}); assignments.set(2, {kind:'profile',id:'p1'});
    tabSites.set(1, 'example.com'); tabSites.set(2, 'example.com');`, ctx);
  assert.equal(vm.runInContext(`proxyFor(assignmentByOrigin({documentUrl:'https://example.com/'}))[0].port`, ctx), 41001);
});

test('Firefox: disconnected assignments never reuse previous ports', () => {
  const ctx = load('firefox');
  vm.runInContext(`lastPorts.set('profile:p1', 41099)`, ctx);
  assert.equal(vm.runInContext(`proxyFor({kind:'profile',id:'p1'})[0].port`, ctx), 1);
});

for (const port of [0, -1, 65536, 12.5, '41001', null]) {
  test(`Bridge: invalid port ${JSON.stringify(port)} is not a live route`, () => {
    const ctx = load('chrome');
    ctx.port = port;
    assert.equal(vm.runInContext(`RouteShieldBridge.resolveRoute({connected:true,routes:[{kind:'profile',id:'p1',port}]},{kind:'profile',id:'p1'})`, ctx), null);
  });
}

test('Bridge: valid IPv4/IPv6 URLs and unassignable pages stay distinct', () => {
  const ctx = load('chrome');
  assert.equal(vm.runInContext(`RouteShieldBridge.siteOf('https://www.example.co.uk/')`, ctx), 'example.co.uk');
  assert.equal(vm.runInContext(`RouteShieldBridge.siteOf('https://[::1]/')`, ctx), null);
  assert.equal(vm.runInContext(`RouteShieldBridge.siteOf('file:///home/user')`, ctx), null);
});

test('Bridge: the active profile from the desktop API remains assignable', () => {
  const ctx = load('chrome');
  assert.equal(vm.runInContext(`RouteShieldBridge.resolveRoute({connected:true,routes:[{kind:'active',id:'p1',port:41001}]},{kind:'profile',id:'p1'}).port`, ctx), 41001);
});
