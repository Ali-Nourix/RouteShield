"""Restore user-provided signing key to runner temp only; never print its contents."""
import base64
import os
import re
from pathlib import Path

required = ["ANDROID_KEYSTORE_BASE64", "ANDROID_KEYSTORE_PASSWORD", "ANDROID_KEY_ALIAS", "ANDROID_KEY_PASSWORD", "ANDROID_SIGNING_CERT_SHA256"]
if any(not os.environ.get(name, "").strip() for name in required):
    raise SystemExit("Required Android signing secrets are missing; no debug-key fallback allowed.")
expected=os.environ["ANDROID_SIGNING_CERT_SHA256"].replace(":", "").strip().lower()
if not re.fullmatch(r"[0-9a-f]{64}", expected):
    raise SystemExit("Invalid certificate fingerprint; refuse signing setup.")
encoded = os.environ["ANDROID_KEYSTORE_BASE64"]
if len(encoded) > 48000:
    raise SystemExit("Keystore secret is too large.")
try:
    data = base64.b64decode("".join(encoded.split()), validate=True)
except ValueError:
    raise SystemExit("Invalid keystore encoding.") from None
if not 100 <= len(data) <= 36000:
    raise SystemExit("Invalid keystore size.")
path = Path(os.environ["ANDROID_KEYSTORE_PATH"])
root = Path(os.environ["RUNNER_TEMP"]).resolve()
if path.resolve().parent != root:
    raise SystemExit("Keystore must be directly inside the private runner temporary directory.")
fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
with os.fdopen(fd, "wb") as stream:
    stream.write(data)
