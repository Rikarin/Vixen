# SPDX-FileCopyrightText: Copyright (c) Rikarin
# SPDX-License-Identifier: Apache-2.0

"""What `build/lock.py` has to be true for, checked against the real file and a real lock.

Run it directly: `python3 build/lock_test.py`. It needs nothing installed, touches no repository
state and never uses the developer's own lock — every case runs against its own file under a
temporary directory, via `VIXEN_BUILD_LOCK`.

⚠ The case that matters is `a_daemon_the_build_leaves_behind_does_not_keep_the_lock`. Everything
else here was already true of the version that shipped the defect: it serialised, it survived a
SIGKILL, it printed who it was waiting for. What it did was hand the lock to MSBuild's node-reuse
daemons, which outlive the build on purpose — so the lock outlived the build too, and the next run
queued behind nothing at all. A suite that only asserts "two runs do not overlap" is green on that.
"""

import os
import signal
import subprocess
import sys
import tempfile
import time

LOCK = os.path.join(os.path.dirname(os.path.abspath(__file__)), "lock.py")


def spawn(lock_file: str, *command: str) -> subprocess.Popen:
    """A `lock.py` run against the test's own lock file, with its output captured."""
    environment = dict(os.environ, VIXEN_BUILD_LOCK=lock_file)

    return subprocess.Popen(
        [sys.executable, LOCK, *command],
        env=environment,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
    )


def seconds_to_acquire(lock_file: str, timeout: float = 20.0) -> float:
    """How long a trivial run takes to get through the lock — the measurement every case makes.

    Expressed as the wait a *second* run observes rather than as a sleep in the test, because the
    property under test is ordering and the assertions below compare it against the holder's own
    duration rather than against a wall-clock budget.
    """
    started = time.monotonic()
    run = spawn(lock_file, sys.executable, "-c", "pass")

    try:
        run.wait(timeout=timeout)
    except subprocess.TimeoutExpired:
        run.kill()
        run.wait()

        return float("inf")

    return time.monotonic() - started


def serialises_two_runs(lock_file: str) -> None:
    """A second run waits for the first rather than competing with it."""
    holder = spawn(lock_file, sys.executable, "-c", "import time; time.sleep(4)")
    time.sleep(1.0)

    waited = seconds_to_acquire(lock_file)
    holder.wait()

    assert waited > 2.0, f"the second run did not wait for a holder still running ({waited:.1f} s)"


def a_daemon_the_build_leaves_behind_does_not_keep_the_lock(lock_file: str) -> None:
    """The regression. A process that outlives the run must not inherit the lock.

    This is MSBuild `/nodeReuse:true` and Roslyn's `VBCSCompiler` in miniature: the run starts
    something that deliberately survives it, then exits. Against the `os.execvp` +
    `os.set_inheritable` version this hangs for the daemon's full lifetime; the daemon here sleeps
    far longer than the timeout, so the failure is unambiguous rather than a slow pass.

    ⚠ The daemon is backgrounded by `sh`, and that detail is the whole case. Written the obvious
    way — a `subprocess.Popen` from Python — this test passes against the broken version, because
    Python closes descriptors above stderr in a child it starts and the miniature therefore stops
    reproducing the thing being tested. `sh` inherits whatever it was handed, which is what MSBuild
    does and what makes the lock leak.
    """
    spawn(lock_file, "/bin/sh", "-c", "sleep 120 &").wait()

    waited = seconds_to_acquire(lock_file, timeout=15.0)

    assert waited < 10.0, (
        "a process the run left behind is still holding the lock — the descriptor reached a "
        f"descendant, which is the defect this file exists for (waited {waited:.1f} s)"
    )


def the_kernel_releases_the_lock_when_the_holder_is_killed(lock_file: str) -> None:
    """No stale-lock case: SIGKILL the holder and the next run walks straight in."""
    holder = spawn(lock_file, sys.executable, "-c", "import time; time.sleep(120)")
    time.sleep(1.5)
    holder.send_signal(signal.SIGKILL)
    holder.wait()

    waited = seconds_to_acquire(lock_file, timeout=15.0)

    assert waited < 10.0, f"a killed holder left the lock behind ({waited:.1f} s)"


def a_wait_names_what_it_is_waiting_for(lock_file: str) -> None:
    """A silent wait is indistinguishable from a hang, and gets killed like one."""
    holder = spawn(lock_file, sys.executable, "-c", "import time; time.sleep(4)")
    time.sleep(1.0)

    waiter = spawn(lock_file, sys.executable, "-c", "pass")
    waiter.wait(timeout=20.0)
    holder.wait()

    notice = waiter.stderr.read()

    assert "waiting for the build lock" in notice, f"the wait said nothing: {notice!r}"
    assert str(holder.pid) in notice, f"the wait did not name the holder: {notice!r}"


def the_exit_code_is_the_builds_own(lock_file: str) -> None:
    """`build.sh` is a gate's exit code and nothing else; the wrapper must not launder it."""
    run = spawn(lock_file, sys.executable, "-c", "raise SystemExit(42)")
    run.wait(timeout=20.0)

    assert run.returncode == 42, f"expected 42, got {run.returncode}"


def a_signalled_build_reports_the_shell_convention(lock_file: str) -> None:
    """A build killed by a signal is 128 + N, not Python's negative."""
    run = spawn(lock_file, sys.executable, "-c", "import os, signal; os.kill(os.getpid(), signal.SIGKILL)")
    run.wait(timeout=20.0)

    assert run.returncode == 128 + signal.SIGKILL, f"expected {128 + signal.SIGKILL}, got {run.returncode}"


ENTRY_POINT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(LOCK))), "build.sh")
SCOPE_OPEN = "# --- lock scope"
SCOPE_CLOSE = "# --- end lock scope ---"


def decision(*arguments: str) -> str:
    """What the shipped `build.sh` decides about a command line, in bash, without building anything.

    The fragment between the two markers is sourced rather than paraphrased, because a copy of the
    rule in this file would be a test of the copy. ⚠ It is also read in `bash` specifically: the
    reasons the rule is written the way it is — no `${x,,}`, `${1+"$@"}` — are all bash 3.2, which
    is what macOS still ships as /bin/bash and what nobody's `zsh` would have caught.
    """
    script = open(ENTRY_POINT, encoding="utf-8").read()
    fragment = script[script.index(SCOPE_OPEN) : script.index(SCOPE_CLOSE)]

    assert "needs_lock()" in fragment, "the marked range no longer contains the rule it names"
    assert "expensive=" in fragment, "the marked range no longer contains the target list"

    probe = f'{fragment}\nif needs_lock "$@"; then echo lock; else echo free; fi\n'
    run = subprocess.run(
        ["bash", "-c", probe, "build.sh", *arguments], capture_output=True, text=True
    )

    assert run.returncode == 0, f"the fragment did not run: {run.stderr}"

    return run.stdout.strip()


def the_expensive_targets_are_the_ones_that_queue(_lock_file: str) -> None:
    """The scope, in both directions — and the case the first version walked past.

    ⚠ `--target` is Nuke's own switch for the target list, so `./build.sh --target Test` is a full
    test sweep. The original rule stopped scanning at the first switch and let it through unlocked.
    """
    expected = {
        (): "lock",  # Nuke's default target is Test.
        ("Test",): "lock",
        ("test",): "lock",
        ("CheckStrings",): "free",
        ("AffectedProjects", "--since", "master"): "free",
        ("Restore", "Compile", "Pack", "--configuration", "Release", "--skip", "Test"): "lock",
        ("CheckStrings", "--skip", "Test"): "free",  # A switch's value is not a target.
        ("--target", "Test"): "lock",
        ("--configuration", "Release", "--target", "CheckApi"): "lock",
        ("--target", "CheckStrings"): "free",
    }

    for arguments, want in expected.items():
        got = decision(*arguments)

        assert got == want, f"./build.sh {' '.join(arguments)} → {got}, expected {want}"


CASES = [
    the_expensive_targets_are_the_ones_that_queue,
    serialises_two_runs,
    a_daemon_the_build_leaves_behind_does_not_keep_the_lock,
    the_kernel_releases_the_lock_when_the_holder_is_killed,
    a_wait_names_what_it_is_waiting_for,
    the_exit_code_is_the_builds_own,
    a_signalled_build_reports_the_shell_convention,
]


def main() -> int:
    failures = 0

    for case in CASES:
        with tempfile.TemporaryDirectory() as directory:
            lock_file = os.path.join(directory, "build.lock")

            try:
                case(lock_file)
                print(f"  ok   {case.__name__}")
            except AssertionError as failure:
                failures += 1
                print(f"  FAIL {case.__name__}: {failure}")

    print(f"{len(CASES) - failures}/{len(CASES)} passed")

    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
