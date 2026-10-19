# RouteShield Android — preview, not a production release

Native Kotlin/Compose Android client, Android 8+ (API 26), ARM64 physical phones and
x86_64 emulator builds. No Supabase, remote database, analytics or profile cloud sync.
An existing working proxy/VPN endpoint is required; the app does not conjure a free server.

## WireGuard and profile files

Profiles → Import .conf or profile file uses Android's document picker (no broad storage permission).
Standard WireGuard configs can also be pasted. UTF-8 input is bounded to 5 MB; private/public/PSK
keys, literal CIDRs, endpoints, MTU and keepalive are validated before encrypted local persistence.
Commented/BOM configs, repeated address/AllowedIPs lines, IPv6 endpoints and multiple peers work.
AllowedIPs and keepalive are preserved. DNS and per-app routing remain controlled by RouteShield
settings, as explained before import; this is not an implementation of wg-quick host scripts.
Unsupported directives, including PostUp/PreDown and AmneziaWG extensions, fail closed.

The flower remains neutral while starting/verifying and turns semantic green with a check only
on verified connectivity. Its outline never morphs. Slow IP metadata and native group tests run
outside the health scheduler; handover invalidates stale results without deliberately dropping
TUN capture. These are scheduling fixes, not a measured throughput/success-rate promise.

See ../docs/android-wireguard-polish.md for references and mandatory real-device release gates.

## Reproducible build

Install JDK 17, Android SDK platform/build tools 35; set ANDROID_HOME or local.properties.
On Linux/macOS: `sh fetch-engine.sh`, `sh gradlew :app:assembleDebug :app:testDebugUnitTest :app:lintDebug`.
On Windows: download the AAR URL in fetch-engine.sh, verify its exact SHA-256 with
Get-FileHash, place it at app/libs/libbox.aar, then run `gradlew.bat` with the same tasks.
The text-encoded standard Gradle wrapper JAR is rehydrated by the launchers; the Gradle
8.13 distribution is SHA-256 pinned. Never commit local.properties, keystores or AARs.

Variants (two APKs per variant, ARM64 and x86_64):
- debug: `.dev` application ID; debug signed, development build.
- diagnostic: `.diagnostic` ID; R8/resource shrinking like release, debug signed so it can
  be installed separately; redacted diagnostic export, no verbose native traffic logs.
- release: production ID, optimized, **unsigned by default**. Complete owner-provided signing environment
  enables release signing, with no partial-config/debug fallback. Supply your own production signing
  outside source control before distribution. A debug-key build is not a production release.

Build optimized variants: `sh gradlew :app:assembleDiagnostic :app:assembleRelease`.
APK output: app/build/outputs/apk/{variant}. UI captures are host-side Robolectric renders,
not Android device screenshots or successful VPN evidence.

## Genuine Material 3 Expressive

Compose Material3 1.5.0-alpha08 explicitly pinned outside the BOM, MaterialExpressiveTheme,
MotionScheme.expressive, ButtonDefaults.shapes morphing, overflow-aware ButtonGroup,
LinearWavyProgressIndicator; MaterialKolor TonalSpot seed scheme; semantic wrappers in
`designsystem/ShieldDesign.kt`. Light/dark/system, Persian RTL/English, adaptive navigation
rail at 840dp, capped 680dp reading column, 20dp gutters and 48–56dp controls.
No state transition depends on an animation finishing. Platform motion scaling applies.

Sekke's token/semantic/component layering was inspected as a reference; no private Sekke
source/assets were copied. This uses genuine Expressive **alpha08**, not Sekke's current
alpha28: alpha28 requires compileSdk 37/AGP 9.1+ unavailable in this build environment.
That dependency upgrade needs its own compatibility/regression pass.

## Engine provenance and release gate

libbox 1.14.0 is SHA-256 pinned by fetch-engine.sh. The binary comes from the third-party
`singbox-android/libbox` distribution, not an authenticated official SagerNet Android release.
Checksum pinning detects changes but is not an independent audit. Before a production
release, rebuild libbox from audited matching source/toolchain, verify the binding/version,
provide corresponding source and all applicable third-party notices. GPL-3.0 licenses and
source links are packaged in assets. Engine source: https://github.com/SagerNet/sing-box.

## Privacy and routing

AES-GCM Android Keystore profiles with atomic writes; Android cloud/device-transfer backup
excluded; no public listening proxy or remote administration. Subscription fetching only via
HTTPS, manual bounded redirects, 35-second deadline, 5 MB/500-node limits, insecure TLS
rejected. Export excludes profiles, subscription URLs, credentials, destinations and payloads.
Import dialog is secure against screen capture; imported labels remain visible in profile lists.

Full IPv4/IPv6 capture for participating apps. Per-app include/exclude uses package IDs;
only launchable apps are listed, not all system processes. The engine protects its sockets
from VPN recursion. Health checks use HTTPS gstatic 204; exit-IP checks use api.ipify.org,
no more frequently than once per minute; these services receive normal request metadata.
URL-test probes also contact the health service. DNS bootstrap resolves server hostnames
on the physical network; encrypted application DNS defaults to Cloudflare HTTPS through
VPN. "Secure DNS off" and domestic bypass are explicit policy relaxations, not performance
optimizations. Imported server plaintext protocols are not turned into encrypted protocols.

While native TUN is running, outages retain it and automatic native node retests back off
30s to 5m; do not interrupt healthy sessions on every URL test. Android OS Always-on VPN
and "Block connections without VPN" must be enabled for process-death/engine-stop lockdown.
This app does not promise a kernel kill switch without OS lockdown. Policy changes require
explicit disconnect/reconnect. Auto-mode node metrics originate in the native engine.

See ../docs/mobile-port.md for implemented features, desktop-only differences and device gates.

Actions and owner-key setup: ../docs/github-actions-and-android-signing.md.
Android UI/app-policy follow-up: ../docs/android-followup.md and ../docs/android-m3-expressive-review.md.
