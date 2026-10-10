/*
 * Firefox lets an extension decide the proxy for every request, and tells it which tab the
 * request came from. That is what makes a per-tab VPN possible here: each tab remembers a
 * choice, and every request from it is pointed at the matching RouteShield proxy.
 *
 * Two details keep a tab whole. Requests a site's service worker makes carry no tab, so they
 * are matched to an assigned tab by the site they come from. And a tab assigned to a profile
 * is never allowed to fall back to the open connection: while the tunnel is down its requests
 * are sent to a port nothing listens on, and fail, instead of leaking.
 *
 * A tab with no choice returns nothing from the listener, so Firefox applies its own proxy
 * settings — the extension stays out of the way until it is asked.
 */
const assignments = new Map();
const tabSites = new Map();
const lastPorts = new Map();
const ERROR_REFRESH_COOLDOWN_MS = 3000;

let state = { reachable: false, connected: false, active: null, routes: [], version: null };
let lastErrorRefresh = 0;

async function refreshState() {
  state = await RouteShieldBridge.fetchState();

  if (state.connected) {
    for (const route of state.routes) {
      lastPorts.set(route.kind === "bypass" ? "bypass" : `profile:${route.id}`, route.port);
    }
  }

  for (const tabId of assignments.keys()) {
    updateBadge(tabId);
  }
}

function updateBadge(tabId) {
  const assignment = assignments.get(tabId);
  const badge = RouteShieldBridge.badgeFor(assignment, RouteShieldBridge.resolveRoute(state, assignment));

  browser.browserAction.setBadgeText({ tabId, text: badge.text }).catch(() => {});
  browser.browserAction.setBadgeBackgroundColor({ tabId, color: badge.color }).catch(() => {});
  browser.browserAction.setBadgeTextColor?.({ tabId, color: "#F3F2F2" }).catch(() => {});
}

/** SOCKS with proxyDNS: the name goes to RouteShield unresolved, exactly as it does for the applications in the routing policy. */
function socks(port) {
  return { type: "socks", host: "127.0.0.1", port, proxyDNS: true };
}

/**
 * The proxy for an assignment, as a one-element list: Firefox tries the entries in order and
 * fails the request after the last one, so a list with no "direct" at the end never falls back.
 */
function proxyFor(assignment) {
  const route = RouteShieldBridge.resolveRoute(state, assignment);
  if (route) {
    return [socks(route.port)];
  }

  if (RouteShieldBridge.failsClosed(assignment)) {
    const key = `profile:${assignment.id}`;
    return [socks(lastPorts.get(key) ?? RouteShieldBridge.DEAD_PORT)];
  }

  return undefined;
}

/** The assignment for a request that carries no tab: the one shared by every assigned tab showing that site. */
function assignmentByOrigin(details) {
  const site = RouteShieldBridge.siteOf(details.originUrl ?? details.documentUrl ?? "");
  if (!site) {
    return undefined;
  }

  let found;
  for (const [tabId, assignment] of assignments) {
    if (tabSites.get(tabId) !== site) {
      continue;
    }

    if (found && (found.kind !== assignment.kind || found.id !== assignment.id)) {
      return undefined;
    }

    found = assignment;
  }

  return found;
}

browser.proxy.onRequest.addListener(
  (details) => {
    if (details.tabId >= 0 && details.type === "main_frame") {
      const site = RouteShieldBridge.siteOf(details.url);
      if (site) {
        tabSites.set(details.tabId, site);
      } else {
        tabSites.delete(details.tabId);
      }
    }

    const assignment = details.tabId >= 0 ? assignments.get(details.tabId) : assignmentByOrigin(details);
    if (!assignment || assignment.kind === "default") {
      return undefined;
    }

    return proxyFor(assignment);
  },
  { urls: ["<all_urls>"] }
);

// A proxy that stopped answering usually means the tunnel restarted on new ports: look again now.
function onProxyTrouble() {
  const now = Date.now();
  if (now - lastErrorRefresh < ERROR_REFRESH_COOLDOWN_MS) {
    return;
  }

  lastErrorRefresh = now;
  refreshState();
}

browser.proxy.onError.addListener(onProxyTrouble);
browser.webRequest.onErrorOccurred.addListener(
  (details) => {
    if (/proxy|socks/i.test(details.error ?? "")) {
      onProxyTrouble();
    }
  },
  { urls: ["<all_urls>"] }
);

browser.tabs.onRemoved.addListener((tabId) => {
  assignments.delete(tabId);
  tabSites.delete(tabId);
});

browser.runtime.onMessage.addListener(async (message) => {
  switch (message?.type) {
    case "context":
      return context(message.tabId);

    case "assign":
      if (message.assignment?.kind === "default") {
        assignments.delete(message.tabId);
      } else {
        assignments.set(message.tabId, message.assignment);
        const site = RouteShieldBridge.siteOf(message.url ?? "");
        if (site) {
          tabSites.set(message.tabId, site);
        }
      }

      updateBadge(message.tabId);
      return context(message.tabId);

    case "refresh":
      await refreshState();
      return context(message.tabId);

    default:
      return undefined;
  }
});

function context(tabId) {
  return {
    mode: "tab",
    subject: "This tab",
    assignment: assignments.get(tabId) ?? { kind: "default" },
    related: 0,
    state
  };
}

refreshState();
setInterval(refreshState, 5000);
