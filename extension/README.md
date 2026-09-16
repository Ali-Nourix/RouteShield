# RouteShield browser extension

Point a tab (Firefox) or a site (Chrome) at a different RouteShield profile — or at no VPN at
all — while the rest of the browser follows RouteShield's routing policy.

The extension talks only to the RouteShield app on this machine, over loopback, and only
while the app is running. It never opens anything to the network itself.

## How it works

While the tunnel is up, RouteShield runs one loopback SOCKS proxy per route:

- the profile the tunnel is connected to,
- every profile you **pinned** for the browser in RouteShield → Profiles,
- and a **bypass** that leaves through your own connection.

The extension reads that list from `http://127.0.0.1:47831/v1/state` and sends the traffic of
each tab or site to the proxy you chose. Names are passed to RouteShield unresolved, exactly
as they are for the applications in the routing policy, so a poisoned local resolver cannot
break a routed tab.

## Firefox — per tab

Firefox lets an extension decide the proxy for every request and tells it which tab the
request came from, so each tab can carry its own choice.

Install for the session: `about:debugging` → *This Firefox* → *Load Temporary Add-on* →
pick `manifest.json` inside the `firefox` folder. Firefox Developer Edition and Nightly can
keep an unsigned extension installed permanently after setting
`xpinstall.signatures.required` to `false` in `about:config`.

## Chrome — per site

Chrome has no per-tab proxy. `chrome.proxy` applies one setting to the whole browser, and the
only thing its PAC script can see is the URL. On Chrome the choice is therefore **per site**:
the assignment covers every tab on that site and its subdomains. When nothing is assigned the
extension clears its proxy setting so other extensions and the system setting are untouched.

Install: `chrome://extensions` → *Developer mode* → *Load unpacked* → pick the `chrome`
folder.

## Layout

| Path | What it is |
| --- | --- |
| `shared/bridge.js` | The client for the app's loopback API; resolves a choice to a live port. |
| `shared/popup.*` | The toolbar popup, shared by both browsers. |
| `firefox/` | Manifest V2 background with `proxy.onRequest`, per tab. |
| `chrome/` | Manifest V3 service worker generating a PAC script, per site. |
