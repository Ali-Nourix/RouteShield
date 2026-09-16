# Changelog

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
