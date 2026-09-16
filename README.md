# RouteShield

A split-tunnel VPN controller for Windows 10 and 11, powered by the official
[sing-box](https://github.com/SagerNet/sing-box) core. You pick which applications go through
the tunnel; everything else keeps your own connection.

[فارسی](README.fa.md) · [Changelog](CHANGELOG.md) · [Security model](SECURITY.md)

## What it does

- **Three routing policies** — route only the applications you select, route everything *except*
  the ones you select, or tunnel the whole system.
- **Protocols** — VLESS (including Reality), VMess, Trojan, Shadowsocks, WireGuard, and any
  sing-box JSON configuration. Transports: TCP, WebSocket, gRPC, HTTP/2, HTTPUpgrade, QUIC.
- **Subscriptions** — HTTPS subscription URLs, plain or Base64, refreshed on demand and
  de-duplicated. URLs are sealed with DPAPI before they touch disk.
- **Application kill switch** — while the tunnel is up, the routed executables are blocked from
  reaching the internet over any physical adapter, so a core crash drops their traffic instead
  of leaking it.
- **DNS that cannot be poisoned** — names are answered instantly from a private range and the
  name itself travels to the proxy, so a blocked or lying local resolver cannot break a routed
  application. Direct traffic still resolves on your own connection.
- **Latency test** — one throwaway core times a request through every profile; sort the
  library by the result.
- **Browser extension** — a route per tab in Firefox, per site in Chrome: any pinned profile,
  or no VPN at all, while the rest of the browser follows the routing policy.
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
build.cmd -Version 1.2.0
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
Both are packaged with every release; see [extension/README.md](extension/README.md) for
installation.

## How the tunnel is put together

RouteShield builds one sing-box configuration per connection:

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
  port reports throughput. All ports are reserved at start, with a fresh API token each run.
- The **latency test** starts a second, inbound-less core with every profile as a node and asks
  its Clash API to time `https://www.gstatic.com/generate_204` through each one.

## Licence

MIT — see [LICENSE](LICENSE). Third-party components are listed in
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
