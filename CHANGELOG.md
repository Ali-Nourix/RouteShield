# Changelog

## 1.2.0

### Fixed

- **Browsers in *Selected applications* mode could not load anything, while *Full system
  tunnel* worked.** On Windows every DNS query is sent by the DNS Client service, never by the
  application, so no per-process rule can carry a routed application's DNS through the tunnel;
  a blocked or poisoned local resolver then broke the application. Secure DNS now answers every
  name from a private range (FakeIP) and the name itself travels to the proxy. Direct traffic is
  resolved at connect time on the physical adapter, exactly as it would be without RouteShield.
  Verified end to end against the sing-box 1.14 core.
- **The DISCONNECT button showed no label.** The implicit text style pinned ink on ink inside
  the dark card; the label now follows the button's own colour, including on hover.
- **The exit-IP and latency probe gave up after one attempt.** It now retries three times over
  the first seconds, then refreshes the latency figure every minute while connected.

### Added

- **Latency test.** A throwaway core carries every profile at once and the Clash API times a
  request through each; results appear per row and the library can sort by them.
- **Browser bridge and extensions.** While the tunnel is up, the active profile, every pinned
  profile and a "No VPN" bypass are exposed as loopback proxies, discovered through a read-only
  loopback API. The Firefox add-on assigns a route per tab; the Chrome add-on per site, because
  Chrome has no per-tab proxy. Both are packaged with every release.
- **Local names stay local.** With local network access on, `.local`, `.lan`, `.home`,
  `.internal`, `.home.arpa` and `.localdomain` go direct alongside private addresses.
- VLESS carries UDP as `xudp`, the encoding Xray servers expect, so QUIC, DNS and games inside
  the tunnel no longer fall back to one connection per destination.

### Changed

- The Profiles page groups the library by subscription, with each subscription's status,
  refresh and edit actions on its own heading; subscriptions are added and edited in a dialog.
- Pills and dots gave way to rounded rectangles throughout: state tags, badges, status marks.
- Without IPv6 in the tunnel, AAAA queries get an empty successful answer instead of a real
  address that would have travelled around the tunnel.


## 1.1.0

### Fixed

- **The tunnel would not start on sing-box 1.12 and later.** The generated configuration named
  no resolver for dialing, so the core refused it with `missing route.default_domain_resolver or
  domain_resolver in dial fields`, fatal as of 1.14. Every configuration now sets
  `route.default_domain_resolver` to a resolver that answers outside the tunnel, and the proxy
  node carries the same resolver explicitly.
- **Reality profiles could not connect.** sing-box rejects a Reality client without a uTLS
  profile; a link that asked for Reality without `fp=` now gets the Chrome fingerprint instead
  of a failed start.
- **The bootstrap resolver could be a dangling reference.** With secure DNS switched off, the
  proxy pointed at a DNS tag that was never declared.
- **Executables were matched by exact path,** so the same program dropped out of the routing
  policy when Windows reported its path with different casing. Matching is now case-insensitive.
- **The event log stayed empty.** The core was told to log to a file, which silences its
  standard output — the only thing the app was reading. It now logs to standard output, and the
  app mirrors it to disk itself.

### Added

- Throughput read from the core's Clash API, and latency and exit address measured through a
  loopback inbound the core owns.
- DNS rules that follow the routing policy: routed applications resolve inside the tunnel,
  everything else on the system resolver.
- `RouteShield.Core`, a cross-platform library holding link parsing and configuration building,
  with a test suite that runs every generated configuration through a real sing-box binary.
- GitHub Actions: CI on every push, and a tag-triggered workflow that publishes the packaged
  build as the release for that tag.

### Changed

- The interface was rebuilt on the Modernist design system — see `docs/design-system.md`.
- Loopback ports for the probe and the control API are reserved at start instead of fixed, and
  the control API gets a fresh token on every run.
- Log redaction keeps URL hosts (so a failure stays diagnosable) while dropping the path and
  query, which is where subscription tokens live.

## 1.0.0

- First release.
