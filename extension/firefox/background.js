/*
 * Firefox lets an extension decide the proxy for every request, and tells it which tab the
 * request came from. That is what makes a per-tab VPN possible here: each tab remembers a
 * choice, and every request from it is pointed at the matching RouteShield proxy.
 *
 * A tab with no choice returns nothing from the listener, so Firefox applies its own proxy
 * settings — the extension stays out of the way until it is asked.
 */
const assignments = new Map();
let state = { reachable: false, connected: false, active: null, routes: [], version: null };

async function refreshState() {
  state = await RouteShieldBridge.fetchState();
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

browser.proxy.onRequest.addListener(
  (details) => {
    const assignment = assignments.get(details.tabId);
    if (!assignment || assignment.kind === "default") {
      return undefined;
    }

    const route = RouteShieldBridge.resolveRoute(state, assignment);
    if (!route) {
      return undefined;
    }

    // SOCKS with proxyDNS: the name goes to RouteShield unresolved, exactly as it does for
    // the applications in the routing policy.
    return { type: "socks", host: "127.0.0.1", port: route.port, proxyDNS: true };
  },
  { urls: ["<all_urls>"] }
);

browser.tabs.onRemoved.addListener((tabId) => assignments.delete(tabId));

browser.runtime.onMessage.addListener(async (message) => {
  switch (message?.type) {
    case "context":
      return context(message.tabId);

    case "assign":
      if (message.assignment?.kind === "default") {
        assignments.delete(message.tabId);
      } else {
        assignments.set(message.tabId, message.assignment);
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
    state
  };
}

refreshState();
setInterval(refreshState, 5000);
