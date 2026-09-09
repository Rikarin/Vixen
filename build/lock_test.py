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

import fcntl
import os
import re
import signal
import subprocess
import sys
import tempfile
import time

LOCK = os.path.join(os.path.dirname(os.path.abspath(__file__)), "lock.py")


def spawn(lock_file: str, *command: str, checkout_lock: str = None) -> subprocess.Popen:
    """A `lock.py` run against the test's own lock files, with its output captured.

    ⚠ Both overrides are always set, never one. `VIXEN_CHECKOUT_LOCK` left unset would send a case
    that passes `--checkout` at the developer's own `~/.vixen`, and a suite that locks the machine
    it is testing on is a suite somebody disables.
    """
    environment = dict(
        os.environ,
        VIXEN_BUILD_LOCK=lock_file,
        VIXEN_CHECKOUT_LOCK=checkout_lock or (lock_file + ".checkout"),
    )

    return subprocess.Popen(
        [sys.executable, LOCK, *command],
        env=environment,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
    )


def seconds_to_acquire(
    lock_file: str, timeout: float = 20.0, options: tuple = (), checkout_lock: str = None
) -> float:
    """How long a trivial run takes to get through the lock — the measurement every case makes.

    Expressed as the wait a *second* run observes rather than as a sleep in the test, because the
    property under test is ordering and the assertions below compare it against the holder's own
    duration rather than against a wall-clock budget.
    """
    started = time.monotonic()
    run = spawn(lock_file, *options, sys.executable, "-c", "pass", checkout_lock=checkout_lock)

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


def checkout(lock_file: str, name: str = "checkout") -> str:
    """A directory that stands in for a checkout, with a Nuke log where Nuke would put one."""
    root = os.path.join(os.path.dirname(lock_file), name)
    os.makedirs(os.path.join(root, ".nuke", "temp"), exist_ok=True)
    open(os.path.join(root, ".nuke", "temp", "build.log"), "a").close()

    return root


def two_runs_in_one_checkout_do_not_overlap(lock_file: str) -> None:
    """#1057. Two `build.sh` runs in one checkout contend for one log file, cheap targets included.

    ⚠ The holder here asks for no machine lock at all — `--checkout` without `--machine` is what a
    `CheckStrings` or a `CheckArchitecture` now sends, and those three were the targets observed
    exiting 255 in 8 s having run nothing. Against the version that shipped the defect this case is
    the whole finding: both runs would sail straight through, because neither was locking anything.
    """
    root = checkout(lock_file)
    options = ("--checkout", root)

    holder = spawn(lock_file, *options, sys.executable, "-c", "import time; time.sleep(4)")
    time.sleep(1.0)

    waited = seconds_to_acquire(lock_file, options=options)
    holder.wait()

    assert waited > 2.0, f"a second run in the same checkout did not wait ({waited:.1f} s)"


def a_run_in_another_checkout_is_not_delayed_by_a_cheap_one(lock_file: str) -> None:
    """The other direction, and what keeps "gate from another worktree" working.

    A cheap target holds only its own checkout's lock. An agent in a different worktree — a
    different lock file — must walk straight past it, or the fix for #1057 has quietly serialised
    every parallel agent behind whoever typed `./build.sh CheckStrings` first.
    """
    mine, theirs = checkout(lock_file, "mine"), checkout(lock_file, "theirs")

    holder = spawn(
        lock_file,
        "--checkout",
        mine,
        sys.executable,
        "-c",
        "import time; time.sleep(6)",
        checkout_lock=lock_file + ".mine",
    )
    time.sleep(1.0)

    waited = seconds_to_acquire(
        lock_file,
        timeout=15.0,
        options=("--checkout", theirs),
        checkout_lock=lock_file + ".theirs",
    )
    holder.kill()
    holder.wait()

    assert waited < 4.0, f"a cheap target in one checkout blocked another checkout ({waited:.1f} s)"


def a_held_nuke_log_is_not_reported_as_a_gate_failure(lock_file: str) -> None:
    """⚠ The instrument question: what does the wrapper say on the day the build cannot start?

    Nuke opens `.nuke/temp/build.log` with `FileShare.Read`, which is an exclusive `flock` on Unix,
    and it does so before it reads the command line — measured 2026-09-09, `--help` exited **255**
    with that single IO message and no target run. 255 is also a failing gate, so passing it through
    is the wrapper reporting a broken tree for a build that never started. Both halves are asserted:
    a distinguishable code with the log held, and the command's own code with it free.
    """
    root = checkout(lock_file)
    handle = os.open(os.path.join(root, ".nuke", "temp", "build.log"), os.O_RDWR)
    fcntl.flock(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)

    try:
        run = spawn(lock_file, "--checkout", root, sys.executable, "-c", "raise SystemExit(42)")
        run.wait(timeout=20.0)
        notice = run.stderr.read()
    finally:
        os.close(handle)

    assert run.returncode == 75, f"expected 75 (EX_TEMPFAIL), got {run.returncode}"
    assert "not a gate failure" in notice, f"the refusal did not say what it was: {notice!r}"
    assert "build.log" in notice, f"the refusal did not name the file: {notice!r}"

    free = spawn(lock_file, "--checkout", root, sys.executable, "-c", "raise SystemExit(42)")
    free.wait(timeout=20.0)

    assert free.returncode == 42, (
        "with the log free the probe must be invisible and the command's own code must come "
        f"through, got {free.returncode}"
    )


ENTRY_POINT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(LOCK))), "build.sh")
WINDOWS_ENTRY_POINT = os.path.join(
    os.path.dirname(os.path.dirname(os.path.abspath(LOCK))), "build.cmd"
)
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

    ⚠ Since #1057 this answers about the *machine* lock alone. "free" no longer means "runs
    unlocked": every run takes its checkout's lock as well, which is the one that keeps two runs off
    one `.nuke/temp/build.log`. The case below is what holds that distinction in place.
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


def every_run_the_entry_point_starts_names_its_checkout(_lock_file: str) -> None:
    """⚠ The regression is a *missing argument*, and nothing else in the tree would see it.

    `build.sh` reaches `lock.py` on exactly one line. Drop `--checkout` from it and every case above
    still passes — they call `lock.py` themselves — while the entry point quietly goes back to
    letting two runs into one checkout. So the line is read here: one invocation, carrying the
    checkout, and `needs_lock` reduced to choosing `--machine` rather than choosing whether to lock
    at all. That last clause is the shape of the original defect: `! needs_lock` in the bypass.
    """
    script = open(ENTRY_POINT, encoding="utf-8").read()
    invocations = [line for line in script.splitlines() if "lock.py" in line and "exec" in line]

    assert len(invocations) == 1, f"expected one lock.py invocation, found {len(invocations)}"
    assert "--checkout" in invocations[0], (
        f"the entry point does not lock its own checkout: {invocations[0].strip()!r}"
    )

    bypass = [line for line in script.splitlines() if "needs_lock" in line and "if " in line]

    for line in bypass:
        assert "!" not in line, (
            "needs_lock decides the machine lock only; a `! needs_lock` bypass sends a cheap target "
            f"round every lock again: {line.strip()!r}"
        )


def batch_commands(script: str) -> str:
    """The lines of a `.cmd` that actually run, with `::`/`rem` comments and `@echo off` dropped.

    ⚠ A batch comment is executable-looking text, and reading the file whole is how a check about
    what a script *does* becomes a check about what it *says*.
    """
    lines = []

    for line in script.splitlines():
        text = line.strip()

        if not text or text.startswith("::") or text.lower().startswith("rem ") or text == "@echo off":
            continue

        lines.append(text)

    return "\n".join(lines)


def the_two_entry_points_run_the_same_build(_lock_file: str) -> None:
    """`build.cmd` and `build.sh` hand Nuke the same command line. #1143.

    ⚠ Nothing else in this repository compares them, and they are one line each in two languages
    nobody edits together. A `--configuration` added to one, a `--no-launch-profile` dropped from the
    other, and Windows quietly builds something else — which reads as a platform difference in the
    build rather than as a diff of two lines.

    ⚠ The second assertion is about a file this suite cannot otherwise reach. `build.cmd` has no
    checkout lock and its comment now says which half of `build.sh`'s locking is refused (the machine
    lock) and which is merely missing (the checkout lock). The day somebody adds it, it has to name
    `--checkout`: `lock.py` with neither switch means "machine lock only", so a wrapper that forgot
    it would take the lock this file explicitly refuses and not the one it was added for.
    """
    windows = batch_commands(open(WINDOWS_ENTRY_POINT, encoding="utf-8").read())
    posix = open(ENTRY_POINT, encoding="utf-8").read()

    def invocation(script: str, arguments: str) -> str:
        start = script.rindex("dotnet run")
        line = " ".join(script[start:].split())
        line = re.sub(r'"[^"]*_build\.csproj"', "<project>", line)

        return line.replace(arguments, "<arguments>")

    assert invocation(windows, "%*") == invocation(posix, '"$@"'), (
        "build.cmd and build.sh no longer run the same build:\n"
        f"  build.cmd: {invocation(windows, '%*')}\n"
        f"  build.sh:  {invocation(posix, '\"$@\"')}"
    )

    # ⚠ Against the *commands*, never the file. Written against the file this passed vacuously the
    # moment #1143 rewrote the comment, because that comment names both `lock.py` and `--checkout`
    # in prose — a rule satisfied by exactly the text it was reading for, which is why the sabotage
    # is the point and not the ceremony.
    if "lock.py" in windows:
        assert "--checkout" in windows, (
            "build.cmd wraps lock.py without --checkout, which is the machine lock its own comment "
            "refuses rather than the checkout lock #1143 is about"
        )


CASES = [
    the_expensive_targets_are_the_ones_that_queue,
    every_run_the_entry_point_starts_names_its_checkout,
    the_two_entry_points_run_the_same_build,
    two_runs_in_one_checkout_do_not_overlap,
    a_run_in_another_checkout_is_not_delayed_by_a_cheap_one,
    a_held_nuke_log_is_not_reported_as_a_gate_failure,
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
