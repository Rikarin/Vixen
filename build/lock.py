# SPDX-FileCopyrightText: Copyright (c) Rikarin
# SPDX-License-Identifier: Apache-2.0

"""Runs one command at a time across every checkout on this machine.

`build.sh` calls this for the targets that take the whole box — a solution compile, a test sweep,
the analyzer-heavy gates. Five agents in five worktrees are five of those at once, and capping the
node count inside one run (build/Build.cs, `Workers`) achieves nothing when five capped runs start
together.

The lock is `fcntl.flock` on a file, held by *this* process, which stays alive as the build's parent
rather than being replaced by it. That is the whole reason it is this and not a `mkdir` with a pid
file:

  ⚠ **A lock left behind by a killed run is worse than no lock at all**, and runs here have been
    SIGTERM'd under load. A flock is a property of an open file description, so the kernel drops it
    when the holder dies for any reason — including SIGKILL, a panic or a pulled plug. There is no
    stale-lock case to detect, and therefore no stale-lock heuristic to get wrong.

  ⚠ **But that guarantee is about the file description, not about the process that opened it**, and
    the first version of this file gave the description away. It `exec`'d the build over itself and
    marked the descriptor inheritable so the lock would survive the `exec` — which also handed it to
    every *descendant*, and .NET's build spawns two kinds of process that deliberately outlive the
    build: MSBuild's `/nodeReuse:true` nodes and Roslyn's `VBCSCompiler`. Measured on 2026-09-05:
    three orphaned MSBuild nodes (ppid 1) held this lock seven minutes after the `dotnet run` that
    started them had exited, with two later gate runs queued behind a build that had finished.
    `lsof` named them holding the same descriptor. So the fix is not another flag — `FD_CLOEXEC` is
    all-or-nothing across `exec` and cannot say "into me but not into my children" — it is to stop
    `exec`ing: run the build as a child with the descriptor closed in it, and hold the lock here.

The file's *contents* are advisory reporting only — who holds it, since when — so that a wait is
distinguishable from a hang. Nothing reads them to decide anything. They are now also true for the
whole wait, because the pid written there is this process and this process lives as long as the lock
does; before, it named a `dotnet run` that had usually already exited.
"""

import fcntl
import os
import signal
import subprocess
import sys
import time

WAIT_NOTICE_SECONDS = 30


def lock_path() -> str:
    """Where the lock lives: one file per user, shared by every checkout they have."""
    # ⚠ Not under the repository and not under TMPDIR. A repository-relative path gives each
    # worktree its own lock, which is exactly the thing being fixed; TMPDIR is per-session in some
    # agent harnesses, which fails the same way and fails invisibly.
    #
    # And deliberately not narrowed to one checkout either, though a second, unrelated one on this
    # machine now queues behind this one. The resource being rationed is the machine — ten cores and
    # its memory — and it does not care which clone asked. A per-repository lock would be an
    # honestly-named lock that protects nothing: two full solution compiles on one laptop is the
    # measurement #552 opens with, whether or not they share a `.git`. VIXEN_NO_BUILD_LOCK is the
    # answer for someone who knows what else is running; VIXEN_BUILD_LOCK below is the answer for
    # someone who genuinely wants two pools.
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


def run(argv: list) -> int:
    """Runs the build as a child and reports its exit status as our own.

    ⚠ Deliberately not `os.execvp`. The lock has to outlive nothing but this process, and a child
    started by `subprocess` gets `close_fds=True` — Python closes every descriptor above stderr in
    it — so the lock reaches neither the build nor the daemons the build leaves behind. That is the
    defect this replaced: see the second warning in the module docstring.
    """
    try:
        child = subprocess.Popen(argv)
    except OSError as error:
        print(f"vixen: could not run {argv[0]}: {error}", file=sys.stderr)

        return 127

    def forward(number, _frame) -> None:
        """Passes on the signals a person or an orchestrator actually sends.

        Interposing a process between the terminal and the build would otherwise swallow them; a
        Ctrl-C that stops the wrapper and leaves a solution compile running is the failure this
        avoids. The wait below then reaps the child normally, so the lock is released after the
        build is gone rather than before.
        """
        try:
            child.send_signal(number)
        except (OSError, ValueError):
            pass

    for number in (signal.SIGINT, signal.SIGTERM, signal.SIGHUP):
        signal.signal(number, forward)

    while True:
        try:
            status = child.wait()

            break
        except KeyboardInterrupt:
            # Already forwarded by the handler above; keep waiting for the build to finish dying.
            continue

    # Popen reports a signalled child as -N. The shell convention every caller of build.sh reads —
    # CI, and CLAUDE.md's warning about pipelines swallowing exit codes — is 128 + N.
    return status if status >= 0 else 128 - status


def main() -> int:
    argv = sys.argv[1:]

    if not argv:
        print("usage: lock.py <command> [args...]", file=sys.stderr)

        return 2

    handle = os.open(lock_path(), os.O_RDWR | os.O_CREAT, 0o644)
    acquire(handle, " ".join(argv))

    return run(argv)


if __name__ == "__main__":
    sys.exit(main())
