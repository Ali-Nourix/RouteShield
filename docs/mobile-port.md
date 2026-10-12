# Mobile port — platform decision required

Status: preparation/architecture only. No APK, AAB, IPA, native mobile UI or working mobile
VPN is implemented or claimed. Choose Android, iOS or both before implementation.

## Reuse and replacement

The cross-platform Core library already owns parsing and subscription/configuration logic.
WPF views, process-path selectors, Windows Firewall, WireSock, DPAPI and elevated startup
cannot simply be copied to mobile. In particular, the Windows TUN runtime configuration and
process regex routing are not a drop-in mobile engine contract.

Separate mobile tunnel lifecycle/storage from the shared parsing layer; keep desktop behavior
unchanged. Persist keys only in the device's secure storage, never in Supabase. The device
may retain encrypted profiles locally; traffic payloads need no remote database writes.

## Android track

- Native `VpnService` consent and foreground-service lifecycle, with accurate connection,
  reconnecting, failed and disconnected states. A notification must remain visible while active.
- Select a supported sing-box Android library/binding or WireGuard engine and verify its
  license, pinned version and ABI support. Do not launch the Windows executable or hand a
  TUN descriptor to an unverified binding.
- Per-app routing via installed application package IDs and VpnService allow/disallow lists,
  with narrowly scoped package visibility. Rebuild the service when the policy changes.
- Android Keystore-backed encryption for profiles; no sensitive logs, analytics or cloud sync
  by default. Disable backup/export of unencrypted secrets.
- Test VPN permission refusal/revocation, always-on/lockdown integration, Wi-Fi↔cellular
  changes, foreground restrictions, battery saver, screen-off, process death and IPv6/DNS.

## iOS track

- Native Network Extension / Packet Tunnel Provider, including its entitlements, extension
  lifecycle and secure app-group/keychain sharing where necessary.
- Requires an Apple signing/developer setup, Xcode/macOS and physical-device testing. None
  was provisioned in this Linux environment.
- Do not promise Windows-style arbitrary per-application routing for a normal consumer
  app. Apple per-app VPN capabilities depend on platform/managed-device constraints.

## Acceptance gates

1. Signed, installable artifact for the explicitly selected platform.
2. Engine configurations validated against that platform's actual engine version.
3. Real tunnel traffic to owned test endpoints; verify exit IP, DNS and IPv6 behavior.
4. Airplane mode, endpoint loss and app/engine termination without silent direct fallback.
5. Secret storage, redacted diagnostics, import limits and no remote payload writes verified.
6. Small-screen UI, accessibility, touch targets, empty/error/permission states reviewed on device.
7. Publish device/OS/ABI test matrix and actual results, including blocked or skipped cases.

A responsive mockup, successful desktop cross-build or Linux `sing-box check` result is not
mobile VPN acceptance evidence.
