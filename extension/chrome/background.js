/*
 * Chrome has no per-tab proxy: chrome.proxy applies one setting to the whole browser, and
 * the only thing a PAC script can see is the URL. So on Chrome the choice is per site.
 * Every assignment becomes a line in a PAC script that sends that site — and its
 * subdomains — to the matching RouteShield proxy; everything else is left to Chrome.
 *
 * When nothing is assigned the proxy setting is cleared entirely, so another proxy
 * extension or the system setting is not overridden for no reason.
 */
importScripts("shared/bridge.js");

const REFRESH_ALARM = "routeshield-refresh";

async function loadAssignments() {
  const { assignments = {} } = await chrome.storage.local.get("assignments");
  return assignments;
}

async function saveAssignments(assignments) {
  await chrome.storage.local.set({ assignments });
}

/** The key a URL is assigned under: its host without a leading www., so the rule covers the whole site. */
function siteOf(url) {
  try {
    const host = new URL(url).hostname.toLowerCase();
    if (!host || host === "localhost" || /^[\d.]+$/.test(host)) {
      return null;
    }

    return host.replace(/^www\./, "");
  } catch {
    return null;
  }
}

function pacFor(rules) {
  const table = rules
    .map(([site, port]) => `["${site.replace(/[^a-z0-9.-]/g, "")}", ${Number(port)}]`)
    .join(",\n    ");

  return `function FindProxyForURL(url, host) {
  host = host.toLowerCase();
  var rules = [
    ${table}
  ];
  for (var i = 0; i < rules.length; i++) {
    var site = rules[i][0];
    if (host === site || dnsDomainIs(host, "." + site)) {
      return "SOCKS5 127.0.0.1:" + rules[i][1];
    }
  }
  return "DIRECT";
}`;
}

async function applyProxy() {
  const [assignments, state] = await Promise.all([loadAssignments(), RouteShieldBridge.fetchState()]);

  const rules = Object.entries(assignments)
    .map(([site, assignment]) => [site, RouteShieldBridge.resolveRoute(state, assignment)])
    .filter(([, route]) => route)
    .map(([site, route]) => [site, route.port]);

  if (rules.length === 0) {
    await chrome.proxy.settings.clear({ scope: "regular" });
  } else {
    await chrome.proxy.settings.set({
      value: { mode: "pac_script", pacScript: { data: pacFor(rules) } },
      scope: "regular"
    });
  }

  await refreshBadges(assignments, state);
  return state;
}

async function refreshBadges(assignments, state) {
  const tabs = await chrome.tabs.query({});
  for (const tab of tabs) {
    const site = siteOf(tab.url ?? "");
    const assignment = site ? assignments[site] : undefined;
    const badge = RouteShieldBridge.badgeFor(assignment, RouteShieldBridge.resolveRoute(state, assignment));

    chrome.action.setBadgeText({ tabId: tab.id, text: badge.text }).catch(() => {});
    chrome.action.setBadgeBackgroundColor({ tabId: tab.id, color: badge.color }).catch(() => {});
  }
}

async function context(url) {
  const site = siteOf(url);
  const [assignments, state] = await Promise.all([loadAssignments(), RouteShieldBridge.fetchState()]);

  return {
    mode: "site",
    subject: site ?? "This page",
    assignable: Boolean(site),
    assignment: (site && assignments[site]) || { kind: "default" },
    state
  };
}

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  (async () => {
    switch (message?.type) {
      case "context":
        return context(message.url);

      case "assign": {
        const site = siteOf(message.url);
        if (site) {
          const assignments = await loadAssignments();
          if (message.assignment?.kind === "default") {
            delete assignments[site];
          } else {
            assignments[site] = message.assignment;
          }

          await saveAssignments(assignments);
          await applyProxy();
        }

        return context(message.url);
      }

      case "refresh":
        await applyProxy();
        return context(message.url);

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

  const site = siteOf(tab.url ?? "");
  const assignments = await loadAssignments();
  const assignment = site ? assignments[site] : undefined;
  const state = await RouteShieldBridge.fetchState();
  const badge = RouteShieldBridge.badgeFor(assignment, RouteShieldBridge.resolveRoute(state, assignment));

  chrome.action.setBadgeText({ tabId, text: badge.text }).catch(() => {});
  chrome.action.setBadgeBackgroundColor({ tabId, color: badge.color }).catch(() => {});
});

chrome.alarms.onAlarm.addListener((alarm) => {
  if (alarm.name === REFRESH_ALARM) {
    applyProxy();
  }
});

chrome.runtime.onInstalled.addListener(() => {
  chrome.alarms.create(REFRESH_ALARM, { periodInMinutes: 0.5 });
  applyProxy();
});

chrome.runtime.onStartup.addListener(() => {
  chrome.alarms.create(REFRESH_ALARM, { periodInMinutes: 0.5 });
  applyProxy();
});
