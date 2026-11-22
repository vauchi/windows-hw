#!/bin/sh
# SPDX-FileCopyrightText: 2026 Mattia Egloff <mattia.egloff@pm.me>
# SPDX-License-Identifier: GPL-3.0-or-later
#
# sign-helper.sh <helper.exe> <out dir>
#
# Copies the helper into <out dir> with its SHA-256 and a cosign bundle over
# that checksum, as Core ships its Windows DLL (core ci/package.yml), and
# verifies the bundle with the key's own public half before it returns. The
# key is COSIGN_KEY (a file path; GitLab may store the file base64-encoded)
# and its passphrase COSIGN_PASSWORD, which cosign reads itself.

set -eu

helper="${1:?usage: sign-helper.sh <helper.exe> <out dir>}"
out="${2:?usage: sign-helper.sh <helper.exe> <out dir>}"

if [ ! -s "$helper" ]; then
    echo "ERROR: no helper at $helper" >&2
    exit 1
fi
if [ -z "${COSIGN_KEY:-}" ] || [ ! -s "$COSIGN_KEY" ]; then
    echo "ERROR: COSIGN_KEY names no key file; the helper is not published unsigned" >&2
    exit 1
fi
command -v cosign >/dev/null 2>&1 || { echo "ERROR: cosign is not installed" >&2; exit 1; }

name=$(basename "$helper")
mkdir -p "$out"
cp "$helper" "$out/$name"

hash=$(sha256sum "$out/$name" | cut -d' ' -f1)
printf '%s' "$hash" > "$out/$name.sha256"
echo "SHA-256: $hash"

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
key="$COSIGN_KEY"
if ! head -1 "$COSIGN_KEY" | grep -q -- "-----BEGIN"; then
    key="$work/cosign.key"
    base64 -d < "$COSIGN_KEY" > "$key"
fi

cosign sign-blob --yes --key "$key" --bundle "$out/$name.sha256.bundle" "$out/$name.sha256" >/dev/null
cosign public-key --key "$key" > "$work/cosign.pub"
if ! cosign verify-blob --key "$work/cosign.pub" --bundle "$out/$name.sha256.bundle" "$out/$name.sha256" >/dev/null 2>&1; then
    echo "ERROR: the bundle does not verify against the key that signed it" >&2
    exit 1
fi
echo "signed and verified: $out/$name.sha256.bundle"
