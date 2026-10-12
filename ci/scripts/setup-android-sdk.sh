#!/usr/bin/env bash
set -euo pipefail
: "${ANDROID_HOME:?Android SDK is required on the runner}"
manager="${ANDROID_HOME}/cmdline-tools/latest/bin/sdkmanager"
if command -v sdkmanager >/dev/null 2>&1; then manager="$(command -v sdkmanager)"; fi
python3 - "$manager" <<'PY'
import subprocess, sys
subprocess.run([sys.argv[1], "--licenses"], input="y\n" * 100, text=True, check=True, stdout=subprocess.DEVNULL)
PY
"$manager" 'platforms;android-35' 'build-tools;35.0.0' 'platform-tools'
