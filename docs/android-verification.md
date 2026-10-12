# Verification — Android preview / desktop follow-up

Results recorded on Linux:
- .NET Core: 421 passed, 0 failed/skipped, with official sing-box 1.14.0.
- Windows Release cross-build: 0 errors/warnings. Not a Windows runtime test.
- Extension routing: 14 Node tests passed, 0 failed/skipped.
- Android: 21 JUnit tests passed, 0 failures/errors/skipped, including 3 actual Compose host UI tests.
- Android lint: 0 errors, 14 warnings (5 newer-dependency notices, 9 unused resources).
  Compiler also reports two deprecated allNetworks calls.
- Real Linux sing-box accepted 11 generated Android config fixtures, including eight share-link
  protocols, automatic group, standard WireGuard and optional routing/TLS settings.
- Nine actual Compose Robolectric/Roborazzi captures: English four tabs, Persian dark four tabs
  with synthetic non-live profile, tablet rail. Reviewed connection, profiles, routing, settings,
  RTL and tablet spacing. Discovered label/row/gutter issues corrected and renders repeated.
- Debug, diagnostic, release assembled for arm64-v8a and x86_64: six APKs.
- Debug/diagnostic are development-key signed; release intentionally unsigned.
- Corrected logo reviewed at 16px and 1024px; PNG/ICO regenerated from SVG.
- Standard Gradle wrapper encoded-JAR round-trip verified; distribution SHA-256 pinned.

See android-build-checksums.txt for hashes and ../android/README.md for reproduction/provenance.

## Not established

No passing physical-device VPN session, DNS/IPv6 leak test, protocol interoperability,
Wi-Fi/cellular failover, OS lockdown or OEM battery/foreground-policy test is claimed.
Software-only Android emulator attempt did not become usable (no KVM, constrained memory).
Host UI/config tests are not a substitute for mobile JNI, Keystore and live networking tests.
Windows runtime/admin behavior and UI were not tested on Windows. AAR is third-party
 distributed, checksum pinned but not independently audited/rebuilt from matching source.
No full desktop parity, security certification or universal bandwidth improvement is claimed.
PR stays draft until device/signing/provenance gates in mobile-port.md are addressed.
No Supabase, remote database or cloud profile sync is used. Existing servers are required.
