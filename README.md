# RouteShield

A split-tunnel VPN controller for Windows 10 and 11, powered by the official
[sing-box](https://github.com/SagerNet/sing-box) core. You pick which applications go through
the tunnel; everything else keeps your own connection.

[فارسی](README.fa.md) · [Changelog](CHANGELOG.md) · [Security model](SECURITY.md)

## What it does

- **Three routing policies** — route only the applications you select, route everything *except*
  the ones you select, or tunnel the whole system.
- **Protocols** — VLESS (including Reality), VMess, Trojan, Shadowsocks, Hysteria2, TUIC,
  AnyTLS, WireGuard, and any sing-box JSON configuration. Transports: TCP, WebSocket, gRPC,
  HTTP/2, HTTPUpgrade, QUIC. Links that name no fingerprint imitate Chrome's TLS client hello.
- **Automatic selection** — every subscription gets a *Fastest of …* entry. The core measures
  all of its nodes, carries traffic over the fastest, re-tests every three minutes and fails
  over on its own when the chosen node dies, without dropping the tunnel. Every node is also
  tested the moment the tunnel is up, so the group is on one that answers within a second or two,
  and the dashboard says plainly when none does and what is most likely in the way.
- **Anti-DPI handshakes** — optionally fragment every TLS handshake with the proxy server
  across several packets and TLS records, for networks that read the server name from the
  first packet.
- **QUIC refused for routed applications** — UDP through a proxy is what makes a tunnelled
  browser load a page and then stall on the video; refusing it puts the browser back on TCP.
- **Domestic traffic stays home** — `.ir` and the large Iranian services go out on your own
  connection instead of abroad and back.
- **Immune to your other VPN** — the tunnel names the adapter it leaves on, so a corporate
  client taking over the default route cannot drag RouteShield's own traffic through it.
- **Two engines** — sing-box for every protocol, and WireSock (the engine TunnlTo uses) for
  WireGuard and AmneziaWG profiles. WireSock works underneath the routing table, so it keeps
  per-application tunnelling working even while a corporate VPN in full-tunnel mode is connected.
- **Subscriptions** — HTTPS subscription URLs, plain or Base64, refreshed on demand and
  de-duplicated. The traffic left and the expiry the provider reports are shown beside each one,
  and the entries a panel adds for them ("7.75 GB left") are kept as notes, never as nodes. URLs
  are sealed with DPAPI before they touch disk.
- **Application kill switch** — while the tunnel is up, the routed executables are blocked from
  reaching the internet over any physical adapter, so a core crash drops their traffic instead
  of leaking it.
- **DNS that cannot be poisoned** — names are answered instantly from a private range and the
  name itself travels to the proxy, so a blocked or lying local resolver cannot break a routed
  application. Direct traffic still resolves on your own connection.
- **Latency test** — one throwaway core times a request through every profile; sort the
  library by the result.
- **Browser extension** — a route per tab in Firefox, per site in Chrome: any pinned profile,
  or no VPN at all, while the rest of the browser follows the routing policy. A site's video,
  images and scripts follow it, and a tab assigned to a profile never falls back to the open
  connection.
- **Light and dark** — the whole interface follows the Windows app colour, or is pinned to
  either palette, and switches in place.
- **Live telemetry** — uptime, HTTP latency measured through the tunnel, throughput read from the
  core, and the exit address the outside world sees.
- **Diagnostics** — a filterable event log and an export bundle that never carries a secret.

## Install

Download the latest `RouteShield-<version>-win-x64.zip` from
[Releases](https://github.com/Ali-Nourix/RouteShield/releases), unzip it anywhere, and run
`RouteShield.exe` **as administrator** — the TUN adapter and the firewall rules both need an
elevated token. Verify the download against the `.sha256` file published beside it.

## Build from source

On Windows, from the folder you unzipped into:

```cmd
build.cmd
```

That is the whole prerequisite list. If the machine has no .NET 8 SDK, the build installs a
private copy under `.tools` and uses it only for this build, leaving the rest of the machine
alone. It then downloads the pinned sing-box release, verifies it against the digest GitHub
publishes for the asset, publishes a self-contained single-file executable, and writes
`artifacts\RouteShield-<version>-win-x64.zip` with its checksum.

The first build takes a few minutes because of the downloads; later ones reuse both caches.
`clean.cmd` removes every build output, including them.

Options pass straight through:

```cmd
build.cmd -Runtime win-arm64
build.cmd -Version 1.7.1
```

## Layout

| Path | What lives there |
| --- | --- |
| `src/RouteShield.Core` | Link parsing, sing-box configuration building, settings, subscriptions. Cross-platform, so it can be tested anywhere. |
| `src/RouteShield` | The WPF application: shell, pages, services, tray integration. |
| `tests/RouteShield.Core.Tests` | Unit tests, plus validation of every generated configuration against a real sing-box binary. |
| `scripts/build.ps1` | The packaging script `build.cmd` calls. |
| `extension/` | The Firefox and Chrome add-ons and the loopback bridge client they share. |
| `docs/design-system.md` | The Modernist design system the interface is built on. |

## Tests

```bash
dotnet test
```

The configuration tests run each generated policy through `sing-box check`. Point
`ROUTESHIELD_SINGBOX` at a sing-box executable to enable them; without it they report as
skipped rather than passing silently.

```bash
export ROUTESHIELD_SINGBOX=/path/to/sing-box
dotnet test
```

## Browser extension

While the tunnel is up, RouteShield runs one loopback SOCKS proxy per route — the active
profile, every profile you pinned in *Profiles*, and a bypass that leaves through your own
connection — and describes them on `http://127.0.0.1:47831/v1/state`. The add-ons in
`extension/` read that list and point a tab (Firefox, via `proxy.onRequest`) or a site
(Chrome, via a generated PAC script — Chrome has no per-tab proxy) at the proxy you choose.

On Chrome an assigned site carries the domains it loads its content from — YouTube's video
comes from `googlevideo.com`, not `youtube.com` — starting from a built-in list and learning
the rest by watching the tab. On either browser a tab or site assigned to a profile fails
closed: while the tunnel is down its requests are held rather than sent unprotected.

Both add-ons are packaged with every release — Firefox as an `.xpi`, Chrome as a `.zip` — and
also sit in the app's `extensions` folder; see [extension/README.md](extension/README.md) for
installation.

## Engines

RouteShield carries a tunnel with one of two programs.

- **sing-box** (bundled) speaks every protocol, and provides the browser bridge, secure DNS and
  the kill switch. It captures traffic with a virtual adapter and the routing table, which means it
  competes with any other VPN for that table.
- **WireSock** (installed separately; TunnlTo installs it too) carries WireGuard and AmneziaWG
  profiles. It takes the chosen applications' packets at the network-driver level and sends them on
  the network card directly, underneath the routing table, so a corporate VPN in full-tunnel mode
  cannot pull them into its own tunnel. RouteShield writes the routing policy into the profile as
  WireSock's `#@ws:` directives and runs `wiresock-client run` in transparent mode.

With the engine on *Automatic*, a WireGuard profile runs on WireSock when another VPN holds the
default route or the profile is AmneziaWG, and everything else runs on sing-box.

Only WireGuard can get underneath a VPN like that. VLESS, VMess, Trojan and the other stream
protocols have to go through it, so they reach the internet only where the network behind that VPN
lets them. When it refuses every node, the dashboard says so after the first test rather than
showing a tunnel that looks connected.

## How the tunnel is put together

RouteShield builds one sing-box configuration per connection:

- The **proxy** is one node, or — for a *Fastest of …* entry — a `urltest` group holding every
  node of the subscription. The group tests each member against `generate_204` every three
  minutes, uses the fastest, and switches when the chosen one fails or a member is faster by a
  clear margin; existing connections are left alone. Until the core's first test is in, and for
  as long as no test passes, the group carries traffic over its first member — so members are
  ordered by the last latency test, and an entry pointing at no server (a provider's quota line
  on 1.1.1.1, a loopback address, port 0 or 1) is never a member at all. Between the core's own
  rounds, RouteShield tests the member in use every 15 seconds: a node that dies is left within
  seconds, and connections left hanging on it are closed so applications reconnect.
- The core logs **warnings and errors only**. At "info" it writes three lines for every
  connection, and a core whose output is not read in time stops accepting connections.
- The core is told **which adapter to dial on** rather than asked to detect it. Detection
  follows the default route, which is exactly what another VPN takes over. The adapter is
  checked first — a VPN in full-tunnel mode leaves no route behind it, and a socket bound to a
  routeless adapter fails on every dial — and a network change moves the tunnel to the adapter
  that is up now. Names are always answered by the system resolver: an ISP's own server refuses
  the names the tunnel exists to reach.
- A node that cannot carry IPv6, or that wraps each packet in a datagram, shapes the tunnel:
  no IPv6 addresses on the interface for the first, and no frame larger than the peer's MTU
  for the second.
- Rules are decided in order: sniff, DNS hijack, the probe and bridge inbounds, private
  addresses and local names, domestic names, the QUIC refusal, then the process policy. Each
  one that matches ends the decision, so a domestic site is never denied QUIC it could have
  used, and an excluded application keeps QUIC on its own connection.
- With *Fragment TLS handshakes* on, every TCP-based node gets `tls.fragment` and
  `tls.record_fragment`; QUIC-based nodes (Hysteria2, TUIC) are left alone. Nodes without a
  declared fingerprint get uTLS with Chrome's profile on TCP, WebSocket and HTTPUpgrade.
- A **TUN inbound** with `auto_route` and `strict_route` captures traffic, and route rules decide
  per process whether it leaves through `proxy` or `direct`. Executables are matched with a
  case-insensitive regex, because Windows hands back the same program with different casing
  depending on where the path came from.
- **`route.default_domain_resolver`** always names `dns-local`, a resolver that answers on the
  physical adapter. sing-box 1.14 refuses to start without it, and the resolver that reaches the
  proxy server has to live outside the tunnel or the tunnel can never come up.
- With secure DNS on, every A/AAAA query is hijacked and answered from the **FakeIP** range
  `198.18.0.0/15`. Windows sends all DNS from the DNS Client service, so a query can never be
  attributed to the program that asked; fake answers make attribution unnecessary. The
  application connects to the fake address, the core maps it back to the name, routed traffic
  carries the name to the proxy, and direct traffic resolves it at connect time on the physical
  adapter. The mapping is persisted in `cache.db` so addresses an application cached survive a
  restart.
- A loopback **mixed inbound** carries the latency and exit-address probe, the **bridge
  inbounds** carry the browser extension's routes, and the **Clash API** on another loopback
  port reports throughput and which node a group is using. All ports are reserved at start,
  with a fresh API token each run; bridge ports are asked for again on a reconnect so a
  browser's resolved route stays valid.
- The **latency test** starts a second, inbound-less core with every profile as a node and asks
  its Clash API to time `https://www.gstatic.com/generate_204` through each one.

## Licence

MIT — see [LICENSE](LICENSE). Third-party components are listed in
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
