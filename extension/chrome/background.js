/*
 * Chrome has no per-tab proxy: chrome.proxy applies one setting to the whole browser, and
 * the only thing a PAC script can see is the URL of each request. So on Chrome the choice
 * is per site — and a site is more than its own domain. YouTube's page comes from
 * youtube.com, its video from googlevideo.com; sending only the first through the VPN loads
 * the page and leaves the player spinning.
 *
 * Two things close that gap. Every site starts with the domains it is known to load from,
 * and the background watches the requests each assigned tab makes and adds the domains it
 * sees to that site's rule. The learned set is kept, so the second visit is covered from the
 * first request.
 *
 * When the tunnel is down, a site assigned to a profile is pointed at a port nothing listens
 * on: its requests fail rather than leave unprotected. When nothing is assigned the proxy
 * setting is cleared entirely, so another extension or the system setting is not overridden
 * for no reason.
 */
importScripts("shared/bridge.js");

const REFRESH_ALARM = "routeshield-refresh";
const REAPPLY_DELAY_MS = 150;
const ERROR_REFRESH_COOLDOWN_MS = 3000;

let reapplyTimer = null;
let lastErrorRefresh = 0;

// ── Storage ──
//
// Both stores are read through a small cache: the request observer below runs for every
// request the browser makes, and must not hit storage each time. The cache is dropped
// whenever storage changes, including from another instance of this worker.

let localCache = null;
let sessionCache = null;

async function loadLocal() {
  if (!localCache) {
    const { assignments = {}, learned = {} } = await chrome.storage.local.get(["assignments", "learned"]);
    localCache = { assignments, learned };
  }

  return localCache;
}

async function saveLocal(changes) {
  localCache = null;
  await chrome.storage.local.set(changes);
}

/** Session storage survives the service worker being put to sleep, and is gone when Chrome closes. */
async function loadSession() {
  if (!sessionCache) {
    const { tabSites = {}, lastPorts = {}, lastPac = null } = await chrome.storage.session.get(["tabSites", "lastPorts", "lastPac"]);
    sessionCache = { tabSites, lastPorts, lastPac };
  }

  return sessionCache;
}

async function saveSession(changes) {
  sessionCache = null;
  await chrome.storage.session.set(changes);
}

chrome.storage.onChanged.addListener((_changes, area) => {
  if (area === "local") {
    localCache = null;
  } else if (area === "session") {
    sessionCache = null;
  }
});

// ── Rules ──

function routeKey(assignment) {
  return assignment.kind === "bypass" ? "bypass" : `profile:${assignment.id}`;
}

/** The domains one assigned site sends through its route: itself, its known family, and what was learned. */
function domainsFor(site, learned) {
  return [...new Set([site, ...RouteShieldBridge.familyOf(site), ...(learned[site] ?? [])])];
}

/**
 * Builds the rule table for the PAC script. A profile assignment with no live route gets the
 * dead port so it fails closed; a bypass with no live route is simply left to Chrome, since
 * the user's own connection is what "No VPN" means anyway.
 */
function rulesFor(assignments, learned, state, lastPorts) {
  const rules = [];
  const claimed = new Set();

  for (const [site, assignment] of Object.entries(assignments)) {
    const route = RouteShieldBridge.resolveRoute(state, assignment);
    let port;

    if (route) {
      port = route.port;
    } else if (RouteShieldBridge.failsClosed(assignment)) {
      port = lastPorts[routeKey(assignment)] ?? RouteShieldBridge.DEAD_PORT;
    } else {
      continue;
    }

    // A domain two assigned sites share follows the first site assigned; a site's own domain always wins.
    const domains = domainsFor(site, learned).filter((domain) => domain === site || !claimed.has(domain));
    domains.forEach((domain) => claimed.add(domain));
    rules.push({ domains, port });
  }

  return rules;
}

function pacFor(rules) {
  const lines = rules.map(({ domains, port }) => {
    const list = domains.map((domain) => JSON.stringify(domain.replace(/[^a-z0-9.-]/g, ""))).join(", ");
    return `    [[${list}], ${Number(port)}]`;
  });

  return `function FindProxyForURL(url, host) {
  host = host.toLowerCase();
  var rules = [
${lines.join(",\n")}
  ];
  for (var i = 0; i < rules.length; i++) {
    var domains = rules[i][0];
    for (var j = 0; j < domains.length; j++) {
      if (host === domains[j] || dnsDomainIs(host, "." + domains[j])) {
        return "SOCKS5 127.0.0.1:" + rules[i][1];
      }
    }
  }
  return "DIRECT";
}`;
}

async function applyProxy() {
  const [{ assignments, learned }, session, state] = await Promise.all([
    loadLocal(),
    loadSession(),
    RouteShieldBridge.fetchState()
  ]);

  const lastPorts = { ...session.lastPorts };
  if (state.connected) {
    for (const route of state.routes) {
      lastPorts[route.kind === "bypass" ? "bypass" : `profile:${route.id}`] = route.port;
    }
  }

  const rules = rulesFor(assignments, learned, state, lastPorts);
  const pac = rules.length === 0 ? null : pacFor(rules);

  if (pac !== session.lastPac) {
    if (pac === null) {
      await chrome.proxy.settings.clear({ scope: "regular" });
    } else {
      await chrome.proxy.settings.set({
        value: { mode: "pac_script", pacScript: { data: pac } },
        scope: "regular"
      });
    }
  }

  await saveSession({ lastPorts, lastPac: pac });
  await refreshBadges(assignments, state, session.tabSites);
  return state;
}

function scheduleReapply() {
  clearTimeout(reapplyTimer);
  reapplyTimer = setTimeout(() => applyProxy().catch(() => {}), REAPPLY_DELAY_MS);
}

// ── Badges ──

function setBadge(tabId, assignment, state) {
  const badge = RouteShieldBridge.badgeFor(assignment, RouteShieldBridge.resolveRoute(state, assignment));
  chrome.action.setBadgeText({ tabId, text: badge.text }).catch(() => {});
  chrome.action.setBadgeBackgroundColor({ tabId, color: badge.color }).catch(() => {});
}

async function refreshBadges(assignments, state, tabSites) {
  const tabs = await chrome.tabs.query({});
  for (const tab of tabs) {
    const site = RouteShieldBridge.siteOf(tab.url ?? "") ?? tabSites[tab.id];
    setBadge(tab.id, site ? assignments[site] : undefined, state);
  }
}

// ── Learning ──

/** Remembers which site a tab shows, so requests the tab makes later can be attributed to it. */
async function rememberTabSite(tabId, url) {
  const site = RouteShieldBridge.siteOf(url);
  const { tabSites } = await loadSession();
  if ((tabSites[tabId] ?? null) === site) {
    return;
  }

  const next = { ...tabSites };
  if (site) {
    next[tabId] = site;
  } else {
    delete next[tabId];
  }

  await saveSession({ tabSites: next });
}

async function learn(tabId, requestUrl) {
  const domain = RouteShieldBridge.siteOf(requestUrl);
  if (!domain) {
    return;
  }

  const { tabSites } = await loadSession();
  const site = tabSites[tabId];
  if (!site || domain === site) {
    return;
  }

  const { assignments, learned } = await loadLocal();
  const assignment = assignments[site];
  if (!assignment || assignment.kind === "default") {
    return;
  }

  if (RouteShieldBridge.familyOf(site).includes(domain) || (learned[site] ?? []).includes(domain)) {
    return;
  }

  await saveLocal({ learned: { ...learned, [site]: [...(learned[site] ?? []), domain] } });
  scheduleReapply();
}

chrome.webRequest.onBeforeRequest.addListener(
  (details) => {
    if (details.tabId < 0) {
      return;
    }

    if (details.type === "main_frame") {
      rememberTabSite(details.tabId, details.url).catch(() => {});
      return;
    }

    learn(details.tabId, details.url).catch(() => {});
  },
  { urls: ["<all_urls>"] }
);

// A proxy that stopped answering usually means the tunnel restarted on new ports: look again now.
chrome.webRequest.onErrorOccurred.addListener(
  (details) => {
    if (!/PROXY|SOCKS|TUNNEL/i.test(details.error ?? "")) {
      return;
    }

    const now = Date.now();
    if (now - lastErrorRefresh < ERROR_REFRESH_COOLDOWN_MS) {
      return;
    }

    lastErrorRefresh = now;
    applyProxy().catch(() => {});
  },
  { urls: ["<all_urls>"] }
);

// ── Popup ──

async function context(url, tabId) {
  const site = RouteShieldBridge.siteOf(url);
  const [{ assignments, learned }, state] = await Promise.all([loadLocal(), RouteShieldBridge.fetchState()]);
  const assignment = (site && assignments[site]) || { kind: "default" };

  return {
    mode: "site",
    subject: site ?? "This page",
    assignable: Boolean(site),
    assignment,
    related: site && assignment.kind !== "default" ? domainsFor(site, learned).length - 1 : 0,
    state
  };
}

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  (async () => {
    switch (message?.type) {
      case "context":
        return context(message.url, message.tabId);

      case "assign": {
        const site = RouteShieldBridge.siteOf(message.url);
        if (site) {
          const { assignments, learned } = await loadLocal();
          if (message.assignment?.kind === "default") {
            delete assignments[site];
            delete learned[site];
          } else {
            assignments[site] = message.assignment;
          }

          await saveLocal({ assignments, learned });
          if (message.tabId >= 0) {
            await rememberTabSite(message.tabId, message.url);
          }

          await applyProxy();
        }

        return context(message.url, message.tabId);
      }

      case "refresh":
        await applyProxy();
        return context(message.url, message.tabId);

      default:
        return undefined;
    }
  })().then(sendResponse);

  return true;
});

chrome.tabs.onUpdated.addListener(async (tabId, change, tab) => {
  if (change.status !== "loading" && !change.url) {
    return;
  }

  await rememberTabSite(tabId, tab.url ?? "");
  const site = RouteShieldBridge.siteOf(tab.url ?? "");
  const [{ assignments }, state] = await Promise.all([loadLocal(), RouteShieldBridge.fetchState()]);
  setBadge(tabId, site ? assignments[site] : undefined, state);
});

chrome.tabs.onRemoved.addListener(async (tabId) => {
  const { tabSites } = await loadSession();
  if (tabId in tabSites) {
    const next = { ...tabSites };
    delete next[tabId];
    await saveSession({ tabSites: next });
  }
});

chrome.alarms.onAlarm.addListener((alarm) => {
  if (alarm.name === REFRESH_ALARM) {
    applyProxy().catch(() => {});
  }
});

function start() {
  chrome.alarms.create(REFRESH_ALARM, { periodInMinutes: 0.5 });
  applyProxy().catch(() => {});
}

chrome.runtime.onInstalled.addListener(start);
chrome.runtime.onStartup.addListener(start);
