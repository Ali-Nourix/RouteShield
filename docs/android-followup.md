# Android follow-up: disconnect, app policy, color and profile structure

- Disconnect uses the framework stopService API, with an explicit stopping state and stale-state
  recovery. TUN closes before native shutdown/telemetry waits, following the pinned engine's
  Android service lifecycle contract; startup cancellation closes late descriptors and avoids
  publishing ready after shutdown. Regression checks cover ordering, cleanup failures and UI stop.
  These are host/unit checks; confirm on the user's physical phone with a live configuration.
- App switches now mean "uses VPN". First launch all ON, zero per-app exclusions. Turning one
  OFF excludes only that launcher package; turning it ON restores it. Old explicit include policies
  remain preserved, with an explicit reset to all apps. Changes require reconnect, not silent restarts.
- Optional autoDomesticApps defaults OFF. Exact offline catalog of eight package IDs; no Persian-
  name/prefix heuristic, no uploaded installed-app inventory, no background country classification.
  Shows automatic vs manual direct route; manual ON overrides automatic exclusion. Reset clears
  rules and turns all apps ON. OS lockdown may block excluded apps instead of routing directly.
  This excludes only matched apps, not the whole VPN. "Iranian" is not proof of no VPN requirement,
  connectivity quality, publisher authenticity or safety. Packages outside the catalog remain VPN.
- Eight local MaterialKolor seed colors, persisted encrypted and usable during an active tunnel.
- Profiles: compact horizontally scrollable group tabs, one active-group summary, compact radio
  rows with protocol/real delay, an automatic-selection row, clear refresh/delete controls, node
  batches of 20. No giant repetitive card/button for every node; absent measurements remain unknown.

## Offline package catalog evidence

Marketplace listings verified for identifiers, not for runtime reachability:
- com.digikala: https://cafebazaar.ir/app/com.digikala
- cab.snapp.passenger: https://cafebazaar.ir/app/cab.snapp.passenger
- cab.snapp.passenger.play: https://cafebazaar.ir/app/cab.snapp.passenger.play
- com.zoodfood.android: https://cafebazaar.ir/app/com.zoodfood.android
- com.sheypoor.mobile: https://cafebazaar.ir/app/com.sheypoor.mobile
- com.snapp_box.android: https://cafebazaar.ir/app/com.snapp_box.android
- ir.divar: https://cafebazaar.ir/app/ir.divar
- com.aparat: https://cafebazaar.ir/app/com.aparat

The catalog is intentionally small and local. Updating package identities requires review;
newly installed known apps are matched at the next explicit VPN connection, not during a tunnel.
