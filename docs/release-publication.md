# Publishing existing builds to GitHub Releases

`Build Windows and Android` creates Actions artifacts. `Publish built Windows Android and extensions` now publishes a tagged **pre-release** after a successful manual main build whose owner-signed Android job succeeded. Unsigned/debug-only builds never produce a release. Manual publication accepts a successful source run ID and optional tag. Automatic tags are `v<Windows-version>-build<run-number>` (with an attempt suffix on reruns), avoiding overwrites.

The initial request publishes run 38266392698, source 9dc0b4741863591a488c6e618b81c4f5fc8cf51d, as v1.7.1. This follows the actual Windows/add-on internal version 1.7.1 rather than the older published v1.1.2. Android retains its own embedded version and version code; neither is changed by renaming an APK.

## Separate downloads
- Windows x64 application/core ZIP.
- Chrome ZIP with manifest at root.
- Firefox XPI with manifest at root (self-hosted, not claimed AMO-signed).
- Owner-signed ARM64 APK.
- Owner-signed x86_64 APK.
- SHA256SUMS, certificate fingerprint, install instructions and source provenance.

Legacy Windows builds also contained copies of the add-ons. The publisher recovers the originally built XPI and removes **all** bundled extension code/archives from the Windows ZIP, preserving every other payload byte. Only `extensions/README.txt` remains so the existing Open extension folder button provides installation guidance. Future `build.ps1` builds stage add-ons outside the application folder and verify the same package-separation invariant. Actions' artifact wrapper may contain multiple standalone packages; GitHub Releases exposes each package as an individual asset.

## Checks and permissions
The source must be a successful manual same-repository main run of the expected workflow, with all Windows/Android build/test/signing jobs successful and a commit contained in main. Archive digests are checked against GitHub metadata; unsafe paths, symlinks and oversized archives are rejected. APK signatures are checked again against `ANDROID_SIGNING_CERT_SHA256`, including package ID/version-code/ABI checks. Only the public certificate pin is needed; no private keystore is downloaded or reused by the publisher.

The publisher uses the existing `android-release` environment, `contents: write` and `actions: read`. Its built-in GitHub token creates the tag/release; no additional PAT is required. If environment review is configured, an authorized user must approve the job. Off-host artifact redirects do not forward the GitHub token.

Assets first upload to a draft, then their complete names, sizes and digests are verified before publication. Existing different assets/targets are never overwritten. A failed publication leaves its draft available for safe retry, not a partial public release.

## Test/release status
The user reports a successful functional test. Independent leakage/IPv6/lockdown/sustained-performance verification and the third-party libbox provenance/audit gate are not asserted complete, so snapshots remain pre-releases. Windows is not Authenticode signed; standard Firefox needs AMO signing for permanent installation.
