# Android runtime follow-up

## Explicit Disconnect
Android may keep a started-and-bound VPN service alive after `stopService`: native teardown must not depend solely on `onDestroy`. UI, notification STOP, revoke and native stop now converge on idempotent explicit shutdown. The borrowed TUN descriptor closes immediately; registration of a late descriptor is atomic with cancellation. Native resources then close on the serialized lifecycle worker. Stopping is not shown as disconnected until teardown finishes. A bound-service host regression exercises early descriptor closure without destroying the service. This does not prove JNI shutdown or traffic behavior on a phone.

## Live server selection
All validated nodes retain stable `node-N` tags in the native config. `proxy` is a selector between the `auto` URL-test group and individual nodes. A manual selection calls the actual native `selectOutbound`, then persists the selection; it does not merely change a radio button. Existing connections can reconnect on explicit selection. Selecting Automatic triggers native HTTPS measurements and periodic one-minute remeasurement, with a default 150ms stability tolerance to avoid oscillation (strict 0ms in speed-priority mode). This optimizes measured response latency, not a guarantee of maximum download throughput. Automatic background changes preserve healthy existing sessions. Switching subscription/profile groups still requires Disconnect.

## Sustained stability
The stability setting defaults on. A previously healthy route needs two consecutive failed health rounds before outage-triggered remeasurement; failed startup can recover immediately. A second independent HTTPS 204 endpoint is tried if the first fails, reducing provider-specific false outages. A single failed round is shown as Checking connection stability, not falsely Online. Success resets the failure counter. The existing monotonic capped backoff limits repeated whole-group tests. This does not diagnose congestion, bandwidth throttling or a poor remote server; no sustained throughput improvement is claimed without real endpoint/device tests.

## System Kill Switch
Android Always-on VPN + Block connections without VPN is the kill switch that survives application/engine death. The app provides the system settings entry and displays the actual `VpnService.isLockdownEnabled` result when connected (API 29+), otherwise unknown. No app-only preference is presented as OS enforcement. On an engine health failure the TUN remains up. Lockdown may block excluded apps, and intentional Disconnect can leave all traffic blocked until the user turns off the OS policy. Non-device-owner apps cannot silently enable these OS policies for the user.

## Domestic catalog
Expanded from eight to twenty-nine exact package IDs; optional and disabled by default. No prefix/name/nationality heuristics, background app inventory upload, or assurance that an app needs no VPN. Manual VPN-on overrides automatic exceptions. The UI reports installed matches and total catalog size. Unknown apps remain through VPN.

Added public listing sources:
- https://cafebazaar.ir/app/com.aparat.filimo
- https://cafebazaar.ir/app/com.mydigipay.app.android
- https://cafebazaar.ir/app/com.pintapin.pintapin
- https://cafebazaar.ir/app/taxi.tap30.passenger.play
- https://cafebazaar.ir/app/taxi.tap30.driver
- https://cafebazaar.ir/app/ir.balad
- https://cafebazaar.ir/app/ir.tgbs.peccharge
- https://cafebazaar.ir/app/com.adpdigital.mbs.ayande
- https://ble.ir/s/update_bale (ir.nasim)

Additional exact catalog entries:
- https://cafebazaar.ir/app/ir.torob
- https://cafebazaar.ir/app/o.com.torob.panel
- https://cafebazaar.ir/app/com.sibche.aspardproject.app
- https://cafebazaar.ir/app/com.tosan.dara.sepah
- https://cafebazaar.ir/app/com.pmb.mobile
- https://cafebazaar.ir/app/com.samanpr.blu
- https://cafebazaar.ir/app/ir.bmi.bam.nativeweb
- https://cafebazaar.ir/app/ir.omidbank
- https://cafebazaar.ir/app/com.bmelat.mobile
- https://cafebazaar.ir/app/com.bpm.sekeh
- https://cafebazaar.ir/app/com.isc.bsinew

- https://cafebazaar.ir/app/org.rajman.neshan.traffic.tehran.navigator

## Release gates
Phone-owned endpoint tests must cover Disconnect from UI and notification, stop during startup, reconnect, manual/automatic node selection, DNS/IPv6 leakage, Wi-Fi/mobile changes, excluded-app routing and OS lockdown. Host screenshots are not device traffic evidence. Existing third-party libbox provenance and owner signing gates remain.

## Workflow diagnosis
The failed main build stopped at `packageDebug`, after passing tests/lint. Its original annotation omitted the nested packaging cause, so a specific root cause is not asserted. A fresh branch run with full stack traces and Ubuntu 24.04 passed. Packaging now runs in a fresh Gradle process after tests/lint, with a 2GiB CI heap, runner image pinned and nested exceptions surfaced as annotations. Local heap remains 1GiB. The owner-signed job still requires a manual main dispatch and protected secrets; no signing fallback is introduced.
