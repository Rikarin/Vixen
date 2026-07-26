#!/usr/bin/env bash
# The entry point CI and developers share. See docs/plan/12 § Nuke.
set -euo pipefail
dotnet run --project "$(dirname "$0")/build/_build.csproj" --no-launch-profile -- "$@"
