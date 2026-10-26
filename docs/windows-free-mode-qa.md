# Windows subscriptions and Free mode QA

## Subscription changes

- The subscription dialog is draggable only by its title, not the form's outer border.
  Save/Cancel/Remove and the text-editing area must receive their own mouse events.
- Save accepts HTTPS URLs without embedded userinfo. A trimmed duplicate URL reuses
  its subscription; different URLs create separate subscriptions.
- A new subscription is persisted before downloading. Empty/failed subscriptions
  remain visible with status, Refresh and Edit, including after an app restart.
- Refresh replaces only profiles owned by that subscription. Matching configuration,
  or a matching name within that owner, retains selection identity and browser pins.
- Settings writes are serialized so two async commands cannot race the same `.tmp` file.

## Free & connect

Use **Profiles & subscriptions → Free & connect** while disconnected, as administrator,
with the packaged sing-box core installed. This is opt-in on every search: public
server operators are unknown; latency is not an assurance of privacy or safety.
Never use these servers for sensitive traffic.

Fixed public sources:

- https://github.com/Epodonios/v2ray-configs (`main/All_Configs_Sub.txt`)
- https://github.com/mahdibland/V2RayAggregator (`master/sub/sub_merge.txt`)

The same HTTPS-only downloader and 5 MB/full-body deadline used for subscriptions
fetches each feed. Each response is parsed up to the subscription parser's 500-entry
limit; at most 50 unique valid links per feed (100 overall) are sampled for testing.
Raw runtime JSON/WireGuard documents, placeholders, literal private/local addresses,
and links disabling TLS verification are excluded. This is not a complete network
sandbox: domain names are resolved by the core and public feeds remain untrusted.

A temporary sing-box process measures a real HTTPS request, not just ICMP or a TCP
handshake. Nodes with missing/failed/negative measurements are not eligible. An invalid
node that prevents the probe core starting is isolated by splitting its batch. A
four-minute search deadline and **Stop testing** cancel the operation. A failed feed
does not discard another feed's valid candidates. The fastest successful measurement
is selected, saved in a separate free pool and connected using the normal tunnel path.
Existing private/manual subscriptions are not replaced. Only tested working nodes are
saved in the free pool; no failed/untested node is connected when none pass.

**Refresh** on the free pool runs the consent/test flow again. **Refresh all** updates
normal subscriptions but deliberately leaves the tested free pool unchanged. **Edit →
Remove subscription** deletes the free pool; its fixed source list cannot be edited
from the normal single-URL dialog. The best result means lowest measured latency among
the sampled candidates, not all servers on GitHub. A later tunnel startup/health failure
is reported by the existing connection UI; end-to-end availability cannot be guaranteed.

## Required Windows manual checks

1. On a clean install, paste a valid HTTPS subscription, click Save, and verify it is listed.
   Exercise text selection/copy/paste, Cancel, Enter-to-save and title dragging.
2. Add a second provider; confirm the first provider, its pins and selected node survive.
   Re-add a duplicate URL and confirm it updates instead of duplicating.
3. Add an unreachable/empty/unsupported subscription. It must remain visible with an error,
   support editing/retrying/removal, and still be present after restart.
4. Refresh provider A with a rotated server configuration. Provider B and manual entries must
   not change; matching A names retain identity and pins. An empty refresh must erase nothing.
5. Free: decline consent (no network changes), then accept. Verify the progress message,
   stop/cancel, administrator/core preflight, no-working-server outcome and lowest-latency selection.
6. Disconnect, refresh the free pool, remove it, restart, and confirm private providers remain.
7. Verify real Windows TUN, kill switch, per-app/browser routing and health status. Neither a Linux
   cross-build nor core configuration checks substitute for this device/network validation.

## Automated checks

`dotnet test tests/RouteShield.Core.Tests/RouteShield.Core.Tests.csproj -c Release`

Set `ROUTESHIELD_SINGBOX` to the packaged-version sing-box binary to include engine
validation. The existing Windows GitHub Actions job builds the WPF package and runs
these tests on pull requests. Native WPF input/TUN behavior still needs the checklist above.
