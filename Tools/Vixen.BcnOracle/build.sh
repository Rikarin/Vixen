#!/bin/sh
# SPDX-FileCopyrightText: Copyright (c) Rikarin
# SPDX-License-Identifier: Apache-2.0
#
# Builds the BCn reference-decoder oracle that BcnReferenceDecoderTests checks Vixen against.
#
# It downloads bcdec.h into a cache OUTSIDE this repository and compiles it there. Nothing
# third-party is written into the tree — see README.md.

set -eu

VERSION=${BCDEC_VERSION:-93628fe5627102fe5187b7eeb99122dec6612c36}
CACHE=${VIXEN_BCN_ORACLE_CACHE:-$HOME/.cache/vixen/bcn-oracle}
SOURCE=$(cd "$(dirname "$0")" && pwd)
URL="https://raw.githubusercontent.com/iOrange/bcdec/$VERSION/bcdec.h"

mkdir -p "$CACHE"

if [ ! -f "$CACHE/bcdec.h" ]; then
    echo "Downloading bcdec.h ($VERSION) into $CACHE"
    curl -fsSL "$URL" -o "$CACHE/bcdec.h"
fi

CC=${CC:-cc}
echo "Compiling bcn-oracle with $CC"
"$CC" -O2 -std=c99 -Wall -Wextra -I "$CACHE" -o "$CACHE/bcn-oracle" "$SOURCE/bcn-oracle.c"

echo "$CACHE/bcn-oracle"
"$CACHE/bcn-oracle" 2>&1 | head -1 || true

cat <<EOF

Built. The tests find it at that path on their own. To run them:

    dotnet test Core/Vixen.Core.Imaging.Tests/Vixen.Core.Imaging.Tests.csproj \\
        --filter FullyQualifiedName~BcnReferenceDecoderTests

Set VIXEN_REQUIRE_EXTERNAL_TOOLS=1 to make a missing oracle fail instead of skip.
EOF
