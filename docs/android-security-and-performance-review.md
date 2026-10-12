# Android/security/performance follow-up

Extends security-review-2026-10-12.md; original findings/test counts there are historical.

## Desktop changes
- Correct conventional checkmark geometry across SVG, WPF, generated PNG/ICO and Android.
- Authenticated bounded loopback /version readiness replaces fixed 700ms sleep; no redirects
  or system proxy. It measures actual core readiness, not a claim of 700ms saved on all machines.
- Automatic node selection refreshes UI after the first successful candidate; watcher cadence
  5s instead of 15s. No universal bandwidth multiplier or latency guarantee is claimed.

## Android controls
- No Supabase or analytics SDK; encrypted AES-GCM atomic profile storage via Android Keystore;
  cloud backup/device transfer excluded, no profile/key/config diagnostic export.
- HTTPS-only subscriptions, 35s complete-fetch deadline, redirect/5MB/500-node caps.
- Native logs and remote debug/shell/SSH/bridge management disabled; imported route/inbound/API
  objects are not executed. Invalid selection is rejected instead of becoming direct routing.
- Provider expiry/traffic metadata is range checked; impossible dates and integer-overflow quotas
  cannot crash the profile UI or create fabricated remaining quota.
- TLS verification cannot be disabled by imported profiles. TLS fragmentation stays opt-in.
- VPN-consent check, foreground notification, immutable action intents, private BIND_VPN_SERVICE;
  dual-stack TUN routes, socket protection, platform physical-network monitoring.
- Health work uses a separate scheduler from serialized native lifecycle, so a slow IP/health
  request does not block shutdown. Active probes disconnect on teardown; callbacks check closing.
- Outages retain the active TUN and trigger bounded automatic retests; existing healthy sessions
  are not forcibly interrupted at every latency refresh. OS lockdown needed for process death.
- Health/exit-IP/DNS providers receive request metadata. "No Supabase" is not "no third-party
  metadata anywhere". Server hostname bootstrap DNS may go to the physical network resolver.

## Unresolved release risks / test limitations
- AAR is third-party distributed; pinned hash but not independently audited/matching-source built.
- Material Expressive alpha08 is an experimental API dependency; review updates before release.
- Full mobile/desktop parity absent for process/browser routing, WireSock and AmneziaWG.
- Physical mobile networking, OEM battery policy and Windows runtime/UI were not tested here.
- GitHub Advanced Security scanning is unavailable; the connection cannot modify workflow files.
  Workflow-hardening patch remains separate; no security workflow activation is claimed.
- This review is scoped remediation, not a penetration-test certification or guarantee of no bugs.

## Follow-up: Actions access and Android feedback

The owner subsequently granted workflow access. The previously separate CI/release checksum and
input-hardening patch is now applied, along with a Windows/Android artifact workflow and main-only
manual signing. Windows artifact job has passed on a real Windows GitHub runner. Android feedback
adds explicit stopping/TUN-before-native teardown, all-on app controls, optional local known-package
exclusions with manual override, seed colors and compact profile rows. See android-followup.md,
android-m3-expressive-review.md and github-actions-and-android-signing.md. Device VPN gates remain.
