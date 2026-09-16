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
request came from, so each tab can carry its own choice. Requests a site's service worker
makes carry no tab; they are matched to the assigned tab by the site they come from.

The release ships `RouteShield-Extension-firefox-<version>.xpi`, and the same file sits in the
app's `extensions` folder as `RouteShield-firefox.xpi`. It is packaged the way
[Mozilla's guide](https://extensionworkshop.com/documentation/publish/package-your-extension/)
describes — the add-on's files at the root of the archive, `manifest.json` among them — and
CI runs Mozilla's `web-ext lint` over it.

- **Firefox Developer Edition or Nightly:** set `xpinstall.signatures.required` to `false` in
  `about:config`, then open the `.xpi` (drag it onto a window, or *File → Open File*). It stays
  installed.
- **Release Firefox** accepts only add-ons signed by Mozilla. Until this one is, load it for
  the session: `about:debugging` → *This Firefox* → *Load Temporary Add-on* → the `.xpi`, or
  `manifest.json` inside the `firefox` folder.

## Chrome — per site, plus what the site loads from

Chrome has no per-tab proxy. `chrome.proxy` applies one setting to the whole browser, and the
only thing its PAC script can see is the URL. On Chrome the choice is therefore **per site**:
the assignment covers every tab on that site and its subdomains.

A site is more than its own domain. YouTube's page comes from `youtube.com` and its video from
`googlevideo.com`; Instagram's pictures from `cdninstagram.com`. Sending only the first domain
through the VPN loads the page and leaves the player spinning. So each assigned site carries a
built-in list of the domains it is known to load from (`shared/bridge.js`, `SITE_FAMILIES`),
and the background watches the requests the assigned tab makes and adds any other domain it
sees to that site's rule. The learned set is kept, so the second visit is covered from the
first request. Choosing *Default* for a site forgets what was learned for it.

When nothing is assigned the extension clears its proxy setting so other extensions and the
system setting are untouched.

Install: `chrome://extensions` → *Developer mode* → *Load unpacked* → pick the `chrome`
folder (or the extracted `RouteShield-Extension-chrome-<version>.zip`).

## Fail closed

A tab or site assigned to a **profile** is a promise that its traffic goes through a VPN.
While the tunnel is down, or RouteShield is not running, those requests are sent to a loopback
port nothing listens on — they fail, and the badge reads **HELD** — rather than leaving on the
open connection. *No VPN* is not held: the open connection is what it means. Both extensions
re-read the state the moment a proxy connection fails, and the app keeps a route on the same
port across reconnects whenever that port is still free.

## Permissions

`proxy` to route, `tabs` to know which tab or site a request belongs to, `webRequest` and
`<all_urls>` to see which domains an assigned site loads from and to notice a dead proxy.
Request bodies are never read, and nothing leaves the browser except the traffic you routed.
The Firefox manifest declares `data_collection_permissions: none`.

## Layout

| Path | What it is |
| --- | --- |
| `shared/bridge.js` | The client for the app's loopback API; resolves a choice to a live port. |
| `shared/popup.*` | The toolbar popup, shared by both browsers. |
| `firefox/` | Manifest V2 background with `proxy.onRequest`, per tab, fail-closed. |
| `chrome/` | Manifest V3 service worker generating a PAC script per site and the domains it loads from. |
