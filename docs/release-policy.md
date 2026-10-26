# Product version policy

Every published Windows update must increase the visible product version, not only
GitHub Actions' run number, file build counter or artifact suffix. This update is
**1.7.2**, from **1.7.1**. The Windows product metadata, executable manifest and
network user agents are kept in sync.

Before the next release:

```sh
python3 scripts/bump-version.py --check
python3 scripts/bump-version.py --dry-run
python3 scripts/bump-version.py             # next patch version
# --part minor or --part major for larger compatibility changes
```

Then update CHANGELOG, build/test, and publish the corresponding versioned package.
`--check` and the Python regression test catch mismatched product/manifest/user-agent
versions. They do not prove that a new release has been published or enforce remote
release tags; the maintainer still reviews that the new version exceeds the last release.
Custom `build.ps1 -Version` overrides remain an advanced packaging option, not a
replacement for updating the committed product version.

Android has its own preview version stream. This change contains Windows features
only, so it does not fabricate an Android release. When Android is updated, increment
its visible `versionName` as well as the store-required monotonic `versionCode`.
