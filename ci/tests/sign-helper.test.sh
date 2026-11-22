#!/bin/sh
# SPDX-FileCopyrightText: 2026 Mattia Egloff <mattia.egloff@pm.me>
# SPDX-License-Identifier: GPL-3.0-or-later
#
# sign-helper.sh with a throwaway key: it signs and its bundle verifies,
# the bundle refuses a changed checksum, a base64-stored key works, and it
# refuses to run without a key or without a helper. Needs cosign on PATH.

set -u

here=$(cd "$(dirname "$0")" && pwd)
script="$here/../sign-helper.sh"
pass=0
fail=0
check() {
    if [ "$2" = "$3" ]; then
        pass=$((pass + 1))
    else
        fail=$((fail + 1))
        printf '  FAIL %s\n    want: %s\n    got:  %s\n' "$1" "$2" "$3"
    fi
}

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
cd "$work" || { echo "ERROR: cannot enter $work"; exit 1; }
export COSIGN_PASSWORD="test-only"
cosign generate-key-pair >/dev/null 2>&1 || { echo "ERROR: cosign could not make a test key"; exit 1; }
printf 'MZ not a real helper\n' > helper.exe

COSIGN_KEY="$work/cosign.key" sh "$script" helper.exe out >run.log 2>&1
check "signs with a PEM key" "0" "$?"
check "writes the file's SHA-256" "$(sha256sum helper.exe | cut -d' ' -f1)" "$(cat out/helper.exe.sha256)"
# $(cat) drops a trailing newline; the byte count does not, and verifiers hash the bytes.
check "and nothing after it" "64" "$(wc -c < out/helper.exe.sha256 | tr -d ' ')"
cosign verify-blob --key cosign.pub --bundle out/helper.exe.sha256.bundle out/helper.exe.sha256 >/dev/null 2>&1
check "the bundle verifies against the public key" "0" "$?"

printf '%s' "0000000000000000000000000000000000000000000000000000000000000000" > tampered.sha256
cosign verify-blob --key cosign.pub --bundle out/helper.exe.sha256.bundle tampered.sha256 >/dev/null 2>&1
check "the bundle refuses a changed checksum" "1" "$([ $? -ne 0 ] && echo 1 || echo 0)"

base64 < cosign.key > cosign.key.b64
COSIGN_KEY="$work/cosign.key.b64" sh "$script" helper.exe out-b64 >run.log 2>&1
check "signs with a base64-stored key" "0" "$?"

COSIGN_KEY="" sh "$script" helper.exe out-none >run.log 2>&1
check "refuses without a key" "1" "$?"
check "and leaves nothing to publish" "absent" "$([ -e out-none ] && echo present || echo absent)"

COSIGN_KEY="$work/cosign.key" sh "$script" missing.exe out-missing >run.log 2>&1
check "refuses without a helper" "1" "$?"

echo "sign-helper.test: $pass passed, $fail failed"
[ "$fail" -eq 0 ]
