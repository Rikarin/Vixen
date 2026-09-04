#!/usr/bin/env bash
# The entry point CI and developers share. See docs/plan/12 § Nuke.
set -euo pipefail

root="$(cd "$(dirname "$0")" && pwd)"

# The targets that take the whole machine rather than a share of it. Everything else — CheckStrings,
# CheckAttribution, AffectedProjects — reads files and finishes in milliseconds, and queueing those
# behind a test sweep would only teach people to bypass the lock.
#
# ⚠ The no-target case is in the list because it is the expensive one: `./build.sh` with no argument
# runs Nuke's default target, which is `Test` (build/Build.cs).
expensive=" compile test checkapi docs checkshaders checkformat affectedtests goldenimages pack "

needs_lock() {
    if [ $# -eq 0 ]; then
        return 0
    fi

    local argument lowered
    for argument in "$@"; do
        # Targets come first and parameters follow, so the first switch ends the target list. A
        # parameter *value* that happens to spell a target name is then not mistaken for one.
        case "${argument}" in
            -*) break ;;
        esac

        # ⚠ Not ${argument,,}: macOS still ships bash 3.2 as /bin/bash, where that expansion is a
        # syntax error — and a syntax error here takes out the entry point for every target, not
        # just the lock.
        lowered="$(printf '%s' "${argument}" | tr '[:upper:]' '[:lower:]')"

        case "${expensive}" in
            *" ${lowered} "*) return 0 ;;
        esac
    done

    return 1
}

# Off in CI, where a leg owns its runner and there is nothing for a lock to protect — the same
# distinction `NukeBuild.IsLocalBuild` makes inside the build. VIXEN_NO_BUILD_LOCK is the escape
# hatch for someone who knows what else is running, and a machine with no python3 falls through it
# rather than refusing to build.
#
# ⚠ `${1+"$@"}` rather than `"$@"`: with `set -u` and no arguments, bash 4.3 and earlier — which
# includes the 3.2 macOS ships — treat an empty `"$@"` as an unbound variable and abort. That is the
# bare `./build.sh` case, which is also the one this lock most wants to catch.
if [ -n "${CI:-}" ] || [ -n "${VIXEN_NO_BUILD_LOCK:-}" ] || ! command -v python3 > /dev/null 2>&1 \
    || ! needs_lock ${1+"$@"}; then
    exec dotnet run --project "${root}/build/_build.csproj" --no-launch-profile -- "$@"
fi

exec python3 "${root}/build/lock.py" \
    dotnet run --project "${root}/build/_build.csproj" --no-launch-profile -- "$@"
