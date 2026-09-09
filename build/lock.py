# SPDX-FileCopyrightText: Copyright (c) Rikarin
# SPDX-License-Identifier: Apache-2.0

"""Runs one command at a time across every checkout on this machine, and one at a time inside each.

`build.sh` calls this for the targets that take the whole box — a solution compile, a test sweep,
the analyzer-heavy gates. Five agents in five worktrees are five of those at once, and capping the
node count inside one run (build/Build.cs, `Workers`) achieves nothing when five capped runs start
together.

⚠ **There are two locks, because there are two shared resources and only one of them is the
machine.** The other is `<checkout>/.nuke/temp/build.log`, which Nuke opens for writing with
`FileShare.Read` — on Unix that is an exclusive `flock`, so the *second* `dotnet run` in one
checkout dies on it. Measured 2026-09-09 in an agent worktree, with the log held by a foreign
`flock` and `--help` as the argument, so no target could have run:

    exit=255, in under a second, and the entire output is
    "The process cannot access the file '…/.nuke/temp/build.log' because it is being used
     by another process."

⚠ **That is worse than a slow gate, and it is why the checkout lock covers the cheap targets too.**
Nuke configures its logging before it reads the command line, so this kills `--help` as readily as
a test sweep: nothing runs, and the target-status table is red for targets that were never started.
A sweep reading exit codes cannot tell that from a real failure, which is the "verify the instrument
first" shape — *ask what a gate prints on the day it does not run*, and until now the answer was
"the same thing as a defect". The machine lock could not help: it is deliberately skipped by
`CheckStrings`, `CheckArchitecture` and `CheckDocComments`, and those are exactly the three that
were observed exiting 255 having run nothing (#1057).

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
import hashlib
import os
import signal
import subprocess
import sys
import time

WAIT_NOTICE_SECONDS = 30

# What we exit with when the checkout's Nuke log is held by a run this process did not start and
# cannot wait for — a `dotnet run` somebody launched by hand, or a `build.sh` with the lock turned
# off. ⚠ It has to be *distinguishable*: Nuke's own answer is 255, which is also what it exits with
# for a genuine gate failure, and a wrapper that passed this through would be reporting "the tree is
# broken" for "the gate never started". 75 is EX_TEMPFAIL — try again, nothing was learned.
LOG_HELD_EXIT = 75


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


def checkout_lock_path(checkout: str) -> str:
    """Where one checkout's lock lives: one file per checkout root, named by that root.

    ⚠ Still not under the repository, even though what it guards is. `git clean -xdf` and Nuke's own
    `.nuke/temp` housekeeping both delete files in there, and a lock file that is unlinked while it
    is held keeps its holder happy and hands the *next* run a brand-new inode to lock — which is the
    silent no-lock case this whole file exists to avoid. A path outside the tree cannot be swept.
    """
    override = os.environ.get("VIXEN_CHECKOUT_LOCK")

    if override:
        return override

    directory = os.path.join(os.path.expanduser("~"), ".vixen")
    os.makedirs(directory, exist_ok=True)

    # The real path, so a symlinked checkout and its target are one lock rather than two.
    digest = hashlib.sha256(os.path.realpath(checkout).encode("utf-8")).hexdigest()[:16]

    return os.path.join(directory, f"checkout-{digest}.lock")


def nuke_log_path(checkout: str) -> str:
    """The one file every run in a checkout writes, whatever target it was given."""
    return os.path.join(checkout, ".nuke", "temp", "build.log")


def log_is_held(checkout: str) -> bool:
    """Whether somebody else already has the checkout's Nuke log open for writing.

    ⚠ `flock` is the whole mechanism on both sides: .NET implements `FileShare` on Unix with it, so
    a probe from Python sees exactly what the build's own open would see. Confirmed by holding this
    file from a Python process and running the build with `--help`, which exited 255 with that one
    IO message and nothing else.

    The probe takes the lock and drops it immediately, which leaves a window of a few microseconds
    in which a foreign run could lose a race it would otherwise have won. That is accepted: the
    checkout lock above already keeps every `build.sh` out of this window, so the only caller that
    can reach it is one that opted out of locking altogether.
    """
    path = nuke_log_path(checkout)

    try:
        handle = os.open(path, os.O_RDWR)
    except OSError:
        # No log yet, or nothing we may open — either way there is nobody to collide with.
        return False

    try:
        fcntl.flock(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
        fcntl.flock(handle, fcntl.LOCK_UN)

        return False
    except BlockingIOError:
        return True
    finally:
        os.close(handle)


def holder(handle) -> str:
    """What the current holder wrote about itself, or a shrug."""
    try:
        os.lseek(handle, 0, os.SEEK_SET)
        text = os.read(handle, 4096).decode("utf-8", "replace").strip()

        return text or "another run (which did not say who it is)"
    except OSError:
        return "another run"


def acquire(handle, command: str, what: str = "the build lock") -> None:
    """Blocks until the lock is ours, saying what is being waited on rather than going quiet."""
    waited = 0.0

    while True:
        try:
            fcntl.flock(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)

            break
        except BlockingIOError:
            if waited == 0.0 or waited % WAIT_NOTICE_SECONDS < 1.0:
                print(
                    f"vixen: waiting for {what} held by {holder(handle)} "
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


USAGE = "usage: lock.py [--checkout PATH] [--machine] <command> [args...]"


def split_options(argv: list):
    """Our own switches, which come first and stop at the command.

    Deliberately hand-rolled rather than `argparse`: the command that follows carries switches of
    its own (`dotnet run --project … -- --configuration Release`) and every parser worth the name
    would try to own them.
    """
    checkout = None
    machine = False

    while argv and argv[0].startswith("--"):
        option = argv[0]

        if option == "--checkout":
            if len(argv) < 2:
                raise ValueError("--checkout wants a path")

            checkout, argv = argv[1], argv[2:]
        elif option == "--machine":
            machine, argv = True, argv[1:]
        else:
            raise ValueError(f"unknown option {option}")

    return checkout, machine, argv


def main() -> int:
    try:
        checkout, machine, argv = split_options(sys.argv[1:])
    except ValueError as error:
        print(f"lock.py: {error}\n{USAGE}", file=sys.stderr)

        return 2

    if not argv:
        print(USAGE, file=sys.stderr)

        return 2

    # ⚠ Neither switch means "machine lock only", which is what every caller predating the checkout
    # lock asked for. Keeping that the default is what lets `build/lock_test.py`'s original cases go
    # on testing the thing they were written for.
    if checkout is None:
        machine = True

    command = " ".join(argv)

    # ⚠ Checkout first, then machine, always — never the other way and never only one order in one
    # branch. Two locks taken in two orders is the textbook deadlock, and this ordering additionally
    # means nobody ever sits on the machine lock while waiting for something smaller.
    if checkout is not None:
        acquire(
            os.open(checkout_lock_path(checkout), os.O_RDWR | os.O_CREAT, 0o644),
            command,
            what=f"another ./build.sh in this same checkout ({checkout})",
        )

    if machine:
        acquire(os.open(lock_path(), os.O_RDWR | os.O_CREAT, 0o644), command)

    # Last thing before the build, so the answer is as fresh as it can be. Only reachable from a run
    # that opted out of the locks above, since they are what keeps two `build.sh` runs apart.
    if checkout is not None and log_is_held(checkout):
        print(
            f"vixen: {nuke_log_path(checkout)} is held by another run of this checkout, so Nuke "
            "cannot start and no target ran. ⚠ This is not a gate failure and says nothing about "
            f"the tree — exit {LOG_HELD_EXIT}, not Nuke's 255. Wait for that run to finish, or "
            "gate from another worktree.",
            file=sys.stderr,
            flush=True,
        )

        return LOG_HELD_EXIT

    return run(argv)


if __name__ == "__main__":
    sys.exit(main())
