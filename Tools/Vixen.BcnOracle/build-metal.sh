#!/bin/sh
# SPDX-FileCopyrightText: Copyright (c) Rikarin
# SPDX-License-Identifier: Apache-2.0
#
# Builds the Metal oracle GpuBcnDecoderTests checks Vixen against. It downloads nothing: the only
# input is bcn-metal-oracle.swift beside this script, and the decoder it wraps is the GPU's.

set -eu

CACHE=${VIXEN_BCN_ORACLE_CACHE:-$HOME/.cache/vixen/bcn-oracle}
SOURCE=$(cd "$(dirname "$0")" && pwd)

mkdir -p "$CACHE"

echo "Compiling bcn-metal-oracle with swiftc"
swiftc -O -framework Metal -o "$CACHE/bcn-metal-oracle" "$SOURCE/bcn-metal-oracle.swift"

echo "$CACHE/bcn-metal-oracle"
"$CACHE/bcn-metal-oracle" --probe

cat <<DONE

Built. The tests find it at that path on their own. To run them:

    dotnet test Core/Vixen.Core.Imaging.Tests/Vixen.Core.Imaging.Tests.csproj \\
        --filter FullyQualifiedName~GpuBcnDecoderTests

Set VIXEN_REQUIRE_EXTERNAL_TOOLS=1 to make a missing oracle fail instead of skip.
DONE
