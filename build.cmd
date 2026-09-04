@echo off
:: The entry point CI and developers share. See docs/plan/12 § Nuke.
::
:: No machine-wide lock here, unlike build.sh. The lock exists because several agents share one
:: developer laptop, that laptop is a Mac, and the mechanism is an fcntl advisory lock the kernel
:: releases when the holder dies. Reproducing that on Windows means a named mutex and a helper
:: process to hold it, which is worth writing when somebody is running parallel worktrees there and
:: not before -- a lock nobody needs is still a thing that can fail.
dotnet run --project "%~dp0build\_build.csproj" --no-launch-profile -- %*
