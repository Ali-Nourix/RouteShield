# Security review — 2026-10-12

Scope: targeted source review of RouteShield at `34bd01a40bfae244223a3c8386ed27368e6415ea`,
with regression tests and Linux-hosted compilation. This is not an independent penetration
test, a complete audit, or certification that the application has no vulnerabilities.

## Addressed findings

| Area | Finding | Change / verification |
| --- | --- | --- |
| Diagnostics | JSON-quoted secret fields, AnyTLS links, Authorization headers and root-query/userinfo URLs could leak credentials. | Broader non-backtracking redaction. Seven of eleven initial regression cases failed before the fix; all eleven pass afterward. |
| Elevated firewall commands | A predictable script in user-writable LocalAppData was executed by elevated PowerShell. A process able to replace it could change the code executed. | No script file: UTF-16 encoded argument to the system PowerShell executable, with a 30-second process deadline. Unit test checks command construction; native Windows execution still needs testing. |
| Elevated task scheduling | `schtasks.exe` was resolved by name rather than its system location. | Resolve from the Windows system directory, as for PowerShell. |
| Subscriptions | `ResponseHeadersRead` left body reads outside HttpClient's timeout. | One 35-second deadline includes headers, decompression, redirects and capped body reads. Caller cancellation is preserved; internal timeout becomes a UI-handled TimeoutException. |
| Subscription redirects | The HTTPS policy was implicit in the handler. | Explicit HTTPS-only redirects (at most five), no embedded credentials or cookies; tests cover relative redirect, downgrade, loops and an oversized body with unknown length. |
| Chrome routes | An earlier site's bypass family could win over another site's explicit VPN assignment (and vice versa). | Explicit domain assignments take priority over learned/known family entries. PAC execution tested with both assignment orders. |
| Firefox routes | A service worker shared by differently assigned tabs could resolve to the open default route. | Ambiguous ownership is held on the dead port when any matching tab requires VPN. Equal assignments still use their selected VPN. |
| Extension ports | Unavailable profiles used cached ports, which another process might subsequently occupy; malformed bridge ports were not checked. | No cached-port fallback for unavailable profiles; validate live integer ports and route kinds, including the desktop's `active` route. |
| Runtime plaintext | A rejected preflight was not owned by a started process, so normal process shutdown could leave its input file behind. | Explicit teardown cleanup of runtime/WireSock inputs and mutex-owner cleanup of interrupted-session files on startup. Deletion failures are reported. |
| CI / release (separate patch, not applied) | Release input was interpolated into shell source; CI executed a downloaded core before checking its digest. | Prepared workflow patch passes version via an environment variable, verifies the official asset SHA-256, and adds read-only CI permissions/job limits. The connection rejected the all-files commit; workflow edits are excluded from this branch pending write permission. |

## Verification in this environment

- .NET 8.0 SDK, Release configuration.
- **415 .NET tests passed, zero failed, zero skipped**, including all real-core configuration
  checks with official sing-box 1.14.0. The Linux core was checked against its GitHub release
  asset digest before execution.
- **14 Node routing regression tests passed**, zero failed/skipped. These execute extension
  routing logic and generated PAC scripts in isolated contexts; they are not live-browser
  end-to-end traffic tests.
- Windows WPF cross-build succeeded with **zero warnings and zero errors**.
- Staged Firefox `web-ext@8 lint`: **zero errors, notices or warnings**. The tool installation
  reports deprecated dependencies; it is build tooling, not a shipped runtime dependency.
- NuGet vulnerability listing for the test project, including transitive dependencies,
  reported no vulnerable packages against the configured NuGet advisory source. This does
  not cover sing-box, WireSock, operating-system components or all possible vulnerabilities.
- The proposed release-workflow patch was tested locally with a valid version and command/quote injection inputs: invalid inputs
  rejected, test marker never executed.
- New generated PNG/ICO bytes checked against their embedded WPF resource bytes. SVG and
  raster logo visually inspected. Native WPF screens were not rendered on Windows here.

## Remaining release gates and known limits

- Live Windows 10/11 testing: elevated firewall apply/remove, startup task, initial-connect
  failure, crash/reconnect, DNS/IPv6/QUIC and adapter changes. Test with disposable profiles
  and owned endpoints; never upload real credentials or raw traffic.
- Native UI review at minimum/default window sizes and 100/125/150/200% DPI, in both themes.
- Firefox/Chrome end-to-end tests with real service workers and shared CDN requests. A newly
  learned Chrome dependency's first request and a Firefox request without attributable origin
  are not proven fail-closed by these unit tests. Chrome has per-site, not per-tab, routing.
- The application, bundled core and highest-privilege startup task must not be considered safe
  against replacement of binaries in an unprotected installation directory. Move installation
  to an administrator-protected location before relying on unattended privileged startup.
- Runtime inputs still contain plaintext secrets while engines need them; cleanup is not
  secure erasure and cannot guarantee removal after every OS/power failure.
- Same-user local processes can still access unauthenticated loopback bridge proxies. Bridge
  response validation is robustness, not authentication of another local process.
- The existing kill switch remains selected-application scoped, does not survive reboot, and
  is not armed for the WireSock engine. Secure tunnel behavior on a device is not proven by
  `sing-box check` accepting its configuration.
- Workflow hardening is a separate, unapplied patch: the GitHub connection rejected the
  all-files commit. This branch intentionally excludes `.github/workflows` changes. New .NET
  tests run through existing CI; run Node extension tests explicitly until its CI patch lands.
- Targeted GitHub secret scanning could not run: GitHub Advanced Security is not enabled for
  this repository. No successful GitHub secret-scan result is claimed.
- No code signing, merged production release, Supabase deployment or mobile binary is included.
