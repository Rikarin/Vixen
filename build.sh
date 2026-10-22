#!/usr/bin/env bash
# The entry point CI and developers share. See docs/plan/12 § Nuke.
set -euo pipefail

root="$(cd "$(dirname "$0")" && pwd)"

# --- lock scope: build/lock_test.py sources exactly this range and calls needs_lock ---
# The targets that take the whole machine rather than a share of it. Everything else — CheckStrings,
# CheckAttribution, AffectedProjects — reads files and finishes in milliseconds, and queueing those
# behind a test sweep would only teach people to bypass the lock.
#
# ⚠ The no-target case is in the list because it is the expensive one: `./build.sh` with no argument
# runs Nuke's default target, which is `Test` (build/Build.cs).
expensive=" compile test checkapi docs checkshaders checkformat affectedtests goldenimages pack "

# ⚠ Not ${argument,,}: macOS still ships bash 3.2 as /bin/bash, where that expansion is a syntax
# error — and a syntax error here takes out the entry point for every target, not just the lock.
lower() {
    printf '%s' "$1" | tr '[:upper:]' '[:lower:]'
}

needs_lock() {
    if [ $# -eq 0 ]; then
        return 0
    fi

    # Which arguments are *targets*. Leading positional ones are, which is how everybody writes it;
    # a switch ends that run, so `--skip Test` — ci.yml's pack leg writes exactly that — does not
    # read as a request to run Test.
    #
    # ⚠ Except `--target`, which is Nuke's own switch for the same list and takes it as values, so
    # `./build.sh --target Test` is a whole test sweep. The first version of this function walked
    # straight past it: it stopped at the first switch and never looked again, so the one spelling
    # that names a target *after* a switch was the one spelling that never queued. Confirmed against
    # `--help`, where `--target` sits beside `--skip` and `--continue`.
    local argument lowered targeting
    targeting=1

    for argument in "$@"; do
        case "${argument}" in
            -*)
                if [ "$(lower "${argument}")" = "--target" ]; then
                    targeting=1
                else
                    targeting=0
                fi

                continue
                ;;
        esac

        if [ "${targeting}" -eq 0 ]; then
            continue
        fi

        lowered="$(lower "${argument}")"

        case "${expensive}" in
            *" ${lowered} "*) return 0 ;;
        esac
    done

    return 1
}
# --- end lock scope ---

# The interpreter that will run lock.py, found by *running* each candidate rather than by name.
#
# ⚠ `command -v python3` is not a test for Python. On a default Windows install it finds
# `%LOCALAPPDATA%\Microsoft\WindowsApps\python3.exe`, the Microsoft Store's App Execution Alias — a
# real executable on PATH that prints an advertisement and exits 49. The old guard took that for a
# Python, handed it lock.py, and `./build.sh <anything>` under Git Bash ran no target and exited 49,
# which reads as a red gate (#1324). `python` is what a Windows install or conda puts on PATH, and
# `py -3` is the launcher the alias does not shadow; both come after `python3` so nothing changes on
# a machine where `python3` is real.
find_python() {
    local candidate

    for candidate in "python3" "python" "py -3"; do
        # ⚠ Unquoted on purpose: `py -3` is a command and an argument.
        if ${candidate} -c 'import sys; sys.exit(sys.version_info < (3, 6))' > /dev/null 2>&1; then
            printf '%s' "${candidate}"

            return 0
        fi
    done

    return 1
}

# Off in CI, where a leg owns its runner and there is nothing for a lock to protect — the same
# distinction `NukeBuild.IsLocalBuild` makes inside the build. VIXEN_NO_BUILD_LOCK is the escape
# hatch for someone who knows what else is running, and a machine with no working Python falls
# through it rather than refusing to build — but says so, since it is then running unlocked.
if [ -n "${CI:-}" ] || [ -n "${VIXEN_NO_BUILD_LOCK:-}" ]; then
    exec dotnet run --project "${root}/build/_build.csproj" --no-launch-profile -- "$@"
fi

if ! python="$(find_python)"; then
    echo "vixen: no working Python 3 (tried python3, python, py -3), so this run takes no build lock." >&2
    exec dotnet run --project "${root}/build/_build.csproj" --no-launch-profile -- "$@"
fi

# ⚠ The machine lock is now the *second* of two, and `needs_lock` decides only that one. Every run
# also takes a lock on this checkout, cheap target or not, because every run — down to `--help` —
# opens `.nuke/temp/build.log`, and Nuke opens it with `FileShare.Read`, which on Unix is an
# exclusive `flock`. Two runs in one checkout therefore never contended for the machine: they
# contended for one log file, and the loser exited 255 in under a second having run nothing, which
# is indistinguishable from a red gate (#1057). A cheap target now waits for a sweep in its own
# checkout, which is worse than it was only if you believe the old behaviour was running.
#
# ⚠ `${1+"$@"}` rather than `"$@"`: with `set -u` and no arguments, bash 4.3 and earlier — which
# includes the 3.2 macOS ships — treat an empty `"$@"` as an unbound variable and abort. That is the
# bare `./build.sh` case, which is also the one the machine lock most wants to catch.
machine=""

if needs_lock ${1+"$@"}; then
    machine="--machine"
fi

# ⚠ `${machine}` unquoted on purpose: quoted, an empty value is an empty *argument*, and lock.py
# would read it as the command to run. `${python}` unquoted for the same `py -3` reason as above.
exec ${python} "${root}/build/lock.py" --checkout "${root}" ${machine} \
    dotnet run --project "${root}/build/_build.csproj" --no-launch-profile -- "$@"
