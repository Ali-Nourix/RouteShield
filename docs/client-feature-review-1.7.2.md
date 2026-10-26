# Windows client review and RouteShield 1.7.2

## Scope and evidence

Reviewed the public README/source material for all nine requested repositories. This is a
feature/source review, **not** a benchmark, security audit, or a claim that their Windows
binaries were installed and exercised. Happ's repository contains README/release material
but no application source. PattN's current README presents the v2rayN codebase; it is not
assumed to have an independently documented feature set.

Implementation is original C#/WPF using RouteShield's existing sing-box integration. No
upstream application source, logos, CSS, private feeds, scripts or binary components were
copied into RouteShield. GPL notices for our existing components remain unchanged.

## Three selected ideas from each requested client (27 review slots)

**Added** means implemented in this Windows update, **Existing** means retained instead of
being misleadingly counted as a new feature. **Adapted/shared** means a narrower or shared
implementation, not another independent port. **Deferred** is explicitly not implemented.

| Client and evidence | Choice 1 | Choice 2 | Choice 3 |
| --- | --- | --- | --- |
| [Throne README](https://github.com/throneproj/Throne/blob/main/README.md), [profile actions](https://github.com/throneproj/Throne/blob/main/src/ui/mainWindow/mainwindow_profiles.cpp) | **Added:** explicit clipboard batch import of links/Base64 bodies, with confirmation | **Added:** ordinary SOCKS5 and HTTP/HTTPS proxy URI support, including encoded credentials/IPv6 | **Existing:** ask the bundled core to validate a configuration before connection |
| [Clash Verge Rev](https://github.com/clash-verge-rev/clash-verge-rev/blob/main/README.md) | **Adapted/added:** editable direct-domain suffix rules | **Adapted/added:** editable always-proxy domains | **Adapted/added:** editable blocked domains, with explicit precedence and validation; no Merge/Script/CSS execution |
| [FlClash](https://github.com/chen08209/FlClash/blob/main/README.md) | **Added:** portable password-encrypted library export | **Added:** authenticated, validated restore that merges instead of deleting current profiles | **Adapted/added:** window-scoped keyboard shortcuts, not OS-global hotkeys or background clipboard monitoring |
| [Happ Desktop](https://github.com/Happ-proxy/happ-desktop/blob/main/README.md) | **Added/shared:** SOCKS5 links (same implementation as the protocol work above) | **Added/shared:** flexible domain routing (same policy editor as above) | **Existing:** local encrypted storage/no new telemetry; Happ's privacy assertion is not treated as an independently verified audit |
| [Karing](https://github.com/KaringX/karing/blob/main/README.md) | **Added:** fastest-of-all-saved-profiles automatic group across provider/manual boundaries | **Adapted/added:** novice Quick connect: test matching saved nodes, then select the fastest successful HTTPS measurement | **Existing:** local file import, retained alongside the new clipboard workflow; no claim of full Clash YAML/V2Ray format parity |
| [Hiddify](https://github.com/hiddify/hiddify-app/blob/main/README.md) | **Added:** opt-in refresh of private subscriptions at startup | **Added:** opt-in scheduled refresh, interval control, failure backoff and shutdown cancellation | **Enhanced:** provider-reported usage/expiry now has explicit expired, near-expiry and low-quota warnings; no invented quota data |
| [PattN profile actions](https://github.com/patterniha/PattN/blob/master/v2rayN/ServiceLib/ViewModels/ProfilesViewModel.cs), [README](https://github.com/patterniha/PattN/blob/master/README.md) | **Added:** owner-scoped duplicate cleanup, retaining pins/favorites | **Added:** test only the selected node through the existing HTTPS probe | **Adapted/added:** copy selected configuration or export its original configuration text, with credential warnings; not a full multi-core client-config exporter |
| [Clash Mi](https://github.com/KaringX/clashmi/blob/main/README.md), [embedded zashboard documentation](https://github.com/Zephyruso/zashboard/blob/main/README.md) | **Existing/redesigned:** RouteShield's own local status/telemetry dashboard instead of embedding a remote panel | **Existing/enhanced:** node/group latency testing, with a new selected-node action; no core replacement | **Deferred:** a live connection-table/control panel. Safely exposing authenticated connection-management actions needs a separate design/test pass; no unauthenticated web panel was added |
| [WhiteVPN Desktop](https://github.com/WhiteDNS/WhiteVPN-Desktop/blob/main/README.md) | **Added:** protocol filter and explicit no-match state; Quick connect does not substitute an unrelated node | **Adapted/added:** country filter from flag labels only, explicitly described as *claimed*, not measured geography | **Existing/partial:** exit IP is measured through the tunnel. Country-level exit geolocation/mismatch reporting remains deferred; no extra geolocation service was silently introduced |

These are **27 reviewed choices, not 27 distinct new features**. Shared features are implemented
once. Existing equivalents were not duplicated, and deferred work is not advertised as shipped.
Extra quality-of-life additions include name search, favorites independent of browser pins,
working-only filters, clear filters, result counts, a consistent original icon set and accessible
action labels.

## Where to find the new functions

- **Profiles:** search/protocol/claimed-country/favorite/working filters, Quick connect, Paste configs.
- **Selected node & library tools:** test, copy, export, deduplicate, favorite selected.
- **Connect target picker:** Fastest of all saved profiles, alongside each provider's own group.
- **Security & behaviour:** subscription update controls, three domain lists, encrypted backup/restore.
- Shortcuts while RouteShield is focused: `Ctrl+Shift+V` paste configs, `Ctrl+Shift+T` test selected,
  `Ctrl+Shift+K` Quick connect, `Ctrl+Shift+L` open Profiles.

## Safety and compatibility boundaries

- Clipboard reads happen only on an explicit action; contents are not logged. Plaintext copy/export
  needs confirmation because clipboard sync/managers and files can expose server credentials.
- Backup uses AES-256-GCM with a fresh salt/nonce and PBKDF2-HMAC-SHA256 (210,000 iterations).
  Passwords must have 12+ characters for new exports and are not recoverable. The password is not
  written into settings or the backup. Plain payload byte buffers/derived keys are cleared after use;
  .NET/WPF string copies cannot be promised to be zeroized.
- Backups omit the Free pool, app executable paths, autostart, firewall and connection-security
  settings. All backup content is validated before merge. Restored domain lists enter the editor
  only and require review/explicit save, never silent activation.
- Domain policy is `block > proxy > direct > built-in policy` after protected probe/browser-inbound
  routes. Direct bypass is an explicit privacy choice. Rules apply on the next connection. WireSock
  refuses custom domain rules instead of silently ignoring them; choose sing-box or remove the lists.
- Scheduled refresh is off by default, excludes the Free pool, serializes with manual refresh,
  retains old profiles on errors and does not silently restart an active connection.
- Automatic groups, like existing provider groups, can use their first usable member while the core
  tests. **Quick connect** is the stricter workflow: it connects only after a successful measurement,
  honors cancellation and refuses a filter change during testing.
- No extra engine, subscription-selling catalogue, arbitrary script runner, background update binary
  installer, external dashboard or GeoIP service was added.
- This is Windows work. Android source/version is intentionally unchanged; no Android parity is claimed.

## Validation and release

Product version is **1.7.2**, not just a larger build number. See [release policy](release-policy.md).
Core tests include parser, rules, scheduler, filtering, duplicate ownership, encryption/authentication,
restore merge and real sing-box 1.14.0 configuration validation. WPF is cross-built on Linux.

HTML layout references were rendered/inspected for light/dark Profiles, expanded-tools/no-match at
1120×720, settings and the backup dialog. They are **not native WPF screenshots** and are not used
as proof of runtime/DPI/input behavior. Native Windows input, 100/150/200% DPI, keyboard focus,
TUN, firewall and real user-network testing still require [the Windows checklist](windows-free-mode-qa.md).
