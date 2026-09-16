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
- **DNS that follows the routing policy** — routed applications resolve over DNS-over-HTTPS
  inside the tunnel; everything else keeps the system resolver.
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

## How the tunnel is put together

RouteShield builds one sing-box configuration per connection:

- A **TUN inbound** with `auto_route` and `strict_route` captures traffic, and route rules decide
  per process whether it leaves through `proxy` or `direct`. Executables are matched with a
  case-insensitive regex, because Windows hands back the same program with different casing
  depending on where the path came from.
- **`route.default_domain_resolver`** always names `dns-local`, a resolver that answers on the
  physical adapter. sing-box 1.14 refuses to start without it, and the resolver that reaches the
  proxy server has to live outside the tunnel or the tunnel can never come up.
- With secure DNS on, a second resolver (`dns-tunnel`) carries queries over DNS-over-HTTPS with
  `detour: proxy`, and a DNS rule sends each query to whichever resolver matches how its process
  is routed.
- A loopback **mixed inbound** carries the latency and exit-address probe, and the **Clash API**
  on a second loopback port reports throughput. Both use ports reserved at start, with a fresh
  API token each run.

## Licence

MIT — see [LICENSE](LICENSE). Third-party components are listed in
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
