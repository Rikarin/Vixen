@echo off
:: The entry point CI and developers share. See docs/plan/12 § Nuke.
::
:: There are two locks in build.sh and this refuses only one of them, which is not what this comment
:: used to say. #1143.
::
:: STILL REFUSED -- the MACHINE lock. It exists because several agents share one developer laptop,
:: that laptop is a Mac, and the mechanism is an fcntl advisory lock the kernel releases when the
:: holder dies. Reproducing that on Windows means a named mutex and a helper process to hold it,
:: which is worth writing when somebody is running parallel worktrees there and not before -- a lock
:: nobody needs is still a thing that can fail.
::
:: NOT REFUSED, MERELY MISSING -- the CHECKOUT lock. That one is not about sharing a laptop between
:: agents: it is two runs in ONE checkout, which is one developer with two terminals. Nuke opens
:: <checkout>\.nuke\temp\build.log for writing with FileShare.Read, and it does so BEFORE it reads
:: the command line -- so the second run dies whatever target it was given, down to --help, having
:: run nothing, and reports Nuke's 255, which is also what a genuine gate failure reports (#1057).
:: Windows shares that failure mode rather than escaping it: FileShare.Read is a Win32 share mode
:: natively, so the second CreateFile is refused with ERROR_SHARING_VIOLATION. Measured on macOS on
:: 2026-09-09; NOT confirmed on Windows, which is what should price the work.
::
:: And the cheapest-looking version of that work is cheaper in one half and dearer in the other, so
:: whoever prices it should know before starting: build\lock.py is fcntl-only, and its two jobs do
:: NOT map onto one Windows primitive. The advisory lock it holds is a byte-range lock and becomes
:: msvcrt.locking. Its log_is_held probe is not: a Python flock sees .NET's FileShare on Unix only
:: because .NET implements FileShare there WITH flock, and on Win32 it is the share mode passed to
:: CreateFile, which no byte-range lock can observe. Probing it there means ctypes and CreateFileW
:: with dwShareMode = 0, not msvcrt.
::
:: ⚠ build/lock_test.py reads this file: whatever runs the build here must stay the command build.sh
:: runs, and the day this grows a lock it must name --checkout, because a checkout lock without one
:: is a machine lock wearing its name.
dotnet run --project "%~dp0build\_build.csproj" --no-launch-profile -- %*
