# Android port — implemented preview and remaining acceptance gates

The user's chosen platform is Android; no Supabase. Actual source is in `android/`,
not a responsive desktop mockup. Native VpnService + pinned libbox engine; Compose
Material 3 Expressive UI and independent debug/diagnostic/release variants.

## Desktop-to-mobile capability matrix

| Capability | Android preview | Difference / limitation |
|---|---|---|
| Profile library, multiple groups, active node | Implemented, encrypted local library | Settings changes require explicit reconnect |
| Share-link/base64/JSON subscriptions, refresh | Implemented with HTTPS deadlines and limits | No remote profile sync or scheduled background refresh |
| VLESS/Reality, VMess, Trojan, SS, HY2, TUIC, AnyTLS | Implemented import + native config | Live interoperability must be checked with owned endpoints |
| Standard WireGuard | Userspace native endpoint and INI import | No WireSock driver; AmneziaWG rejected, not silently treated as WireGuard |
| Automatic selection and latency | Native urltest, stable tolerance, outage backoff | Measurements available while connected in auto group |
| Traffic rate/totals, exit IP, connectivity | Native traffic callbacks + HTTPS probes | No fabricated zero/success before a measurement; no historical analytics |
| Per-application split tunnel | All/include/exclude Android packages | Launchable apps only; not Windows process regex/rule editor |
| Encrypted DNS, QUIC preference, TLS fragmentation | Implemented explicit policies | Fragmentation off by default; no TLS validation downgrade |
| Domestic direct bypass | Explicit off-by-default .ir/service rules | Not a comprehensive maintained Iran-IP database |
| Light/dark, Persian RTL/English, adaptive UI | Genuine Expressive components/token wrappers | alpha08, not Sekke's newer alpha28; reference, not copied private code |
| Redacted diagnostics export | Implemented user-selected destination | No packet bodies/configs/URLs/keys exported |
| Windows browser route extensions, bridges, process ownership | Not ported | Mobile browsers do not provide desktop extension/IPC parity |
| Startup, tray, firewall elevation, WireSock, core-file picker | Platform-specific replacement or absent | Android consent + foreground notification + system VPN settings |
| Process-death kill switch | Android system Always-on/lockdown | Enable OS "Block connections without VPN"; app cannot promise it alone |

"Desktop-like capabilities" does not mean full desktop feature parity. Missing desktop-only
capabilities above are explicit; do not advertise them as implemented. Android retains encrypted
profiles locally, not traffic payloads in Supabase. Working remote servers are still required.

## Verification boundaries

Automated unit/config/render tests and successful APK assembly are necessary, not sufficient.
Robolectric captures render the actual Compose screens on the host, not a real Android device.
Linux engine checks parse the Android configuration, not mobile JNI/TUN behavior. Production
release remains unsigned; preview APKs are debug signed, with separate application IDs.

Device tests still required before removing preview/draft status:
1. Consent denial/revocation, Android 8/14/15 foreground restrictions and notification behavior.
2. Real tunnel traffic to authorized endpoints for each protocol, DNS/IPv6 and exit-IP checks.
3. All/include/exclude packages, invalid/uninstalled package handling, app/engine termination.
4. Wi-Fi↔cellular, airplane mode, endpoint loss, battery saver and screen-off.
5. Always-on/OS-lockdown with process death; verify no unexpected direct fallback.
6. Small/large screens, RTL, dark mode, 200% text, accessibility, IME and modal behavior.
7. Rebuilt/audited libbox source provenance, production signing and distribution compliance.

No production device or owned test-server credential was supplied. Do not infer a passing
mobile VPN test from APK compilation or mock HTTP tests. iOS is outside this request.
