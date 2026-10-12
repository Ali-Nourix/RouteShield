# GitHub Actions builds and Android signing

Workflow: `.github/workflows/build-apps.yml` — Build Windows and Android.
Pushes to main/fix branches and PRs build Windows portable ZIP, Android debug/diagnostic APKs
and unsigned release APKs; QA reports/captures are artifacts. No secrets required for previews.
Actions are SHA pinned; tokens read-only; no release publication or merge is automatic.
Existing CI/release workflows also receive the pending checksum/input hardening fixes.

After reviewed PR code is merged to main, GitHub Actions → Build Windows and Android → Run
workflow → branch main. Leave sign_android false for preview, true for owner-signed release.
Signed job runs only on main/manual after both preview jobs pass, in environment android-release.
Missing secrets/cert pin fail, never fall back to the development key. Keys are restored only
in private runner temp and deleted in an always step; artifacts only include APKs/public checksums.
Signed release versionCode is 1000 + workflow run number. Preserve this workflow's monotonically
increasing version-code policy; increase it explicitly if an existing published version is higher.
Artifacts retained 14 days; download and archive them yourself if needed longer.

## Owner creates the key on their own computer

Install JDK 17 (Android Studio's bundled JDK also provides keytool). In PowerShell:

```powershell
keytool -genkeypair -v -keystore "$env:USERPROFILE\RouteShield-signing.jks" -storetype JKS -alias routeshield -keyalg RSA -keysize 3072 -validity 10000
```

Enter passwords interactively, not as command arguments or chat messages. The key password can
be the same as the store password (Enter at the prompt). Certificate identity fields are public
in APK signing metadata; use your own appropriate values. Store this file OUTSIDE the repository,
not in a cloud-synced folder. Keep encrypted offline backups of file/password/alias. Losing a
self-managed APK signing key usually prevents updating existing installations with the same identity.
Do not generate a new key on every build. If an app was already published, use its existing key.

Copy encoded keystore (not encryption!) to clipboard for the secret field:

```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes("$env:USERPROFILE\RouteShield-signing.jks")) | Set-Clipboard
```

Inspect the public certificate fingerprint; password is prompted interactively:

```powershell
keytool -list -v -keystore "$env:USERPROFILE\RouteShield-signing.jks" -alias routeshield
```

Copy the `SHA256:` fingerprint without the label; colon-separated or plain hex accepted.
Clear the clipboard after storing the secret. Never commit .jks/.keystore/base64 private keys.

## Five exact GitHub secrets

Repository → Settings → Secrets and variables → Actions → New repository secret:

| Name | Value |
|---|---|
| ANDROID_KEYSTORE_BASE64 | Entire Base64 clipboard value, no quotes |
| ANDROID_KEYSTORE_PASSWORD | Keystore password |
| ANDROID_KEY_ALIAS | routeshield, or your existing alias |
| ANDROID_KEY_PASSWORD | Key password |
| ANDROID_SIGNING_CERT_SHA256 | Expected public SHA-256 certificate fingerprint |

Recommended: Settings → Environments → New environment `android-release`. Restrict deployment
branches to main, add required reviewers when your GitHub plan supports it. For better isolation,
put the five secrets in that environment instead of repository-wide secrets; the workflow supports
both. Do not put them under Actions Variables. Never expose signing secrets to fork/PR code or
use pull_request_target to build untrusted changes. Do not enable debug logs/build scans for signing.

## Scope and trust

Windows ZIP is not Authenticode signed; Android keys cannot sign a Windows EXE. Windows signing
is separate and requires your certificate/provider; not necessary to compile a portable build,
though unsigned downloads may show SmartScreen warnings. Workflow does not claim live device QA.
Android engine provenance/device gates still apply even to a correctly signed APK.

For Google Play, prefer an upload key plus Play App Signing and an AAB workflow; this workflow
currently produces directly signed APKs, not a Play-ready listing. If Play uses a different app
signing key, those APKs and sideloaded APKs are not interchangeable updates. Review signing identity
and Play target-SDK/distribution requirements before publishing.

References:
- https://developer.android.com/studio/publish/app-signing
- https://docs.github.com/en/actions/security-for-github-actions/security-guides/using-secrets-in-github-actions
