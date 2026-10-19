# Android WireGuard import and connection polish

## Scope

- Android document picker for standard `.conf`/profile text; bounded actual bytes, strict UTF-8,
  no URI permission persistence, and encrypted ProfileStore persistence after validation.
- WireGuard endpoint schema used by the pinned sing-box/libbox 1.14.0 engine; no new VPN engine.
- Explicit app-owned DNS/app routing. Imported file DNS is not silently adopted as a direct resolver.
- Neutral disconnected flower and semantic success green/check only after HTTPS success; static
  outer geometry, short icon crossfade and expandable detail motion honor Compose duration scale.
- Separate coalesced background metadata and native group retests from health. Network-generation
  guards reject stale HTTPS/exit-IP results after Wi-Fi/cellular changes. No direct fallback added.

## Open-source references (patterns, not copied implementation)

- [Official WireGuard Android config parser](https://github.com/WireGuard/wireguard-android/blob/master/tunnel/src/main/java/com/wireguard/config/Config.java):
  section/comment parsing and explicit invalid-config handling. Our client importer deliberately
  supports a narrower directive set and rejects hooks rather than invoking wg-quick.
- [sing-box Android default network monitor](https://github.com/SagerNet/sing-box-for-android/blob/dev/app/src/main/java/io/nekohasekai/sfa/bg/DefaultNetworkMonitor.kt):
  physical network/interface lifecycle handling, distinct from VPN availability.
- [sing-box WireGuard endpoint schema](https://sing-box.sagernet.org/configuration/endpoint/wireguard/):
  userspace endpoint, peers, allowed IPs and keepalive. No 1.15-only fields introduced.

No third-party assets or source files are vendored by this change; existing license notices remain.

## Validation and release gates

Automated tests cover file limits/decoding, parser acceptance/rejection, generated endpoint policy,
background coalescing/concurrency, stale health generations and rendered flower geometry/color.
Robolectric renders are host tests, not a working WireGuard handshake or real VPN evidence.

Before calling this production-ready, the owner must test on a physical supported Android phone:

1. Import a real provider `.conf`, connect on Wi-Fi and mobile data, verify DNS and both IP families.
2. Test PSK, IPv6-only endpoint, NAT/keepalive and a multi-peer/private-prefix config as applicable.
3. Handover Wi-Fi ↔ cellular, airplane-mode recovery, long idle/Doze, network outage and engine death.
4. Confirm manual selection stays manual; automatic selection does not churn healthy sessions.
5. Test system Always-on/Block connections without VPN and participating/excluded apps for leaks.
6. Verify neutral/verifying/green states, TalkBack, Persian RTL, dark mode and 200% text size.
7. Run owner-signed main build with the existing certificate; verify update over the prior APK.

Existing engine provenance/source-compliance and physical-device gates in android/README.md
remain open; signing and successful CI do not close them. No release/tag publication is requested.
