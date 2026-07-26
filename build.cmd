@echo off
:: The entry point CI and developers share. See docs/plan/12 § Nuke.
dotnet run --project "%~dp0build\_build.csproj" --no-launch-profile -- %*
