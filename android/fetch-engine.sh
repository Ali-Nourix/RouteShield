#!/usr/bin/env sh
set -eu
cd "$(dirname "$0")"
mkdir -p app/libs
url='https://github.com/singbox-android/libbox/releases/download/1.14.0/libbox.aar'
expected='13fbeabcb4622250762afa1b137daaba861a4dc1875643d18621339912f32330'
if [ ! -f app/libs/libbox.aar ]; then
  curl --fail --location --proto '=https' "$url" --output app/libs/libbox.aar.tmp
  printf '%s  %s\n' "$expected" app/libs/libbox.aar.tmp | sha256sum --check
  mv app/libs/libbox.aar.tmp app/libs/libbox.aar
fi
printf '%s  %s\n' "$expected" app/libs/libbox.aar | sha256sum --check
