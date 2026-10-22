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
:: Windows shares that failure mode rather than escaping it -- CONFIRMED 2026-10-22: with the log
:: held by a .NET File.Open(ReadWrite, FileShare.Read), this file's own `build.cmd --help` exited 255
:: in 13 s with the one IO message.
::
:: The Python half of that work is done (#1324): build\lock.py now loads on Windows and takes both
:: locks there, because build.sh under Git Bash runs it. The lock is msvcrt.locking on one byte past
:: the contents, and the log probe is a plain write-open -- FileShare.Read refuses exactly that, so it
:: needs no ctypes, which is what this comment used to predict. What is still missing is only the
:: wiring here: finding a real Python from batch (not the Store's python3 alias) and calling lock.py.
::
:: ⚠ build/lock_test.py reads this file: whatever runs the build here must stay the command build.sh
:: runs, and the day this grows a lock it must name --checkout, because a checkout lock without one
:: is a machine lock wearing its name.
dotnet run --project "%~dp0build\_build.csproj" --no-launch-profile -- %*
