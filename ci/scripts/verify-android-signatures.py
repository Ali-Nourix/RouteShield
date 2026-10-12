"""Fail if any release APK is unsigned or uses a certificate other than the owner's pin."""
import hashlib
import os
import re
import subprocess
from pathlib import Path

expected = os.environ.get("ANDROID_SIGNING_CERT_SHA256", "").replace(":", "").strip().lower()
if not re.fullmatch(r"[0-9a-f]{64}", expected):
    raise SystemExit("Expected signing certificate SHA-256 is missing or invalid.")
apksigner = Path(os.environ["ANDROID_HOME"]) / "build-tools/35.0.0/apksigner"
artifacts = sorted(Path("android/app/build/outputs/apk/release").glob("*.apk"))
if len(artifacts) != 2 or any("unsigned" in apk.name for apk in artifacts):
    raise SystemExit("Expected two signed release APKs, not unsigned/debug fallback artifacts.")
checksums = []
for apk in artifacts:
    result = subprocess.run([str(apksigner), "verify", "--print-certs", str(apk)], capture_output=True, text=True)
    if result.returncode:
        raise SystemExit("APK signature verification failed.")
    actual = re.findall(r"certificate SHA-256 digest:\s*([0-9a-fA-F]{64})", result.stdout)
    if len(actual) != 1 or actual[0].lower() != expected:
        raise SystemExit("Unexpected APK signing certificate.")
    checksums.append(f"{hashlib.sha256(apk.read_bytes()).hexdigest()}  {apk.name}")
Path("android/app/build/outputs/apk/release/SHA256SUMS.txt").write_text("\n".join(checksums) + "\n")
Path("android/app/build/outputs/apk/release/signing-certificate-sha256.txt").write_text(expected + "\n")
print("Both release APKs verified against the expected signing certificate.")
