# SPDX-FileCopyrightText: Copyright (c) Rikarin
# SPDX-License-Identifier: Apache-2.0

"""Runs one command at a time across every checkout on this machine.

`build.sh` calls this for the targets that take the whole box — a solution compile, a test sweep,
the analyzer-heavy gates. Five agents in five worktrees are five of those at once, and capping the
node count inside one run (build/Build.cs, `Workers`) achieves nothing when five capped runs start
together.

The lock is `fcntl.flock` on a file, held by *this* process and inherited across the `exec` below,
which is the whole reason it is this and not a `mkdir` with a pid file:

  ⚠ **A lock left behind by a killed run is worse than no lock at all**, and runs here have been
    SIGTERM'd under load. A flock is a property of an open file description, so the kernel drops it
    when the holder dies for any reason — including SIGKILL, a panic or a pulled plug. There is no
    stale-lock case to detect, and therefore no stale-lock heuristic to get wrong.

The file's *contents* are advisory reporting only — who holds it, since when — so that a wait is
distinguishable from a hang. Nothing reads them to decide anything.
"""

import fcntl
import os
import sys
import time

WAIT_NOTICE_SECONDS = 30


def lock_path() -> str:
    """Where the lock lives: one file per user, shared by every checkout they have."""
    # ⚠ Not under the repository and not under TMPDIR. A repository-relative path gives each
    # worktree its own lock, which is exactly the thing being fixed; TMPDIR is per-session in some
    # agent harnesses, which fails the same way and fails invisibly.
    override = os.environ.get("VIXEN_BUILD_LOCK")

    if override:
        return override

    directory = os.path.join(os.path.expanduser("~"), ".vixen")
    os.makedirs(directory, exist_ok=True)

    return os.path.join(directory, "build.lock")


def holder(handle) -> str:
    """What the current holder wrote about itself, or a shrug."""
    try:
        os.lseek(handle, 0, os.SEEK_SET)
        text = os.read(handle, 4096).decode("utf-8", "replace").strip()

        return text or "another run (which did not say who it is)"
    except OSError:
        return "another run"


def acquire(handle, command: str) -> None:
    """Blocks until the lock is ours, saying what is being waited on rather than going quiet."""
    waited = 0.0

    while True:
        try:
            fcntl.flock(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)

            break
        except BlockingIOError:
            if waited == 0.0 or waited % WAIT_NOTICE_SECONDS < 1.0:
                print(
                    f"vixen: waiting for the build lock held by {holder(handle)} "
                    f"({int(waited)}s so far). Set VIXEN_NO_BUILD_LOCK=1 to run anyway.",
                    file=sys.stderr,
                    flush=True,
                )

            time.sleep(1.0)
            waited += 1.0

    os.ftruncate(handle, 0)
    os.lseek(handle, 0, os.SEEK_SET)
    os.write(handle, f"pid {os.getpid()} in {os.getcwd()}: {command}".encode())
    os.fsync(handle)


def main() -> int:
    argv = sys.argv[1:]

    if not argv:
        print("usage: lock.py <command> [args...]", file=sys.stderr)

        return 2

    handle = os.open(lock_path(), os.O_RDWR | os.O_CREAT, 0o644)
    acquire(handle, " ".join(argv))

    # ⚠ The lock has to survive the exec, and a file descriptor opened by os.open is close-on-exec
    # in Python by default — leaving it set would drop the lock the instant the build started, and
    # every run would appear to work while serialising nothing.
    os.set_inheritable(handle, True)

    try:
        os.execvp(argv[0], argv)
    except OSError as error:
        print(f"vixen: could not run {argv[0]}: {error}", file=sys.stderr)

        return 127

    return 0


if __name__ == "__main__":
    sys.exit(main())
