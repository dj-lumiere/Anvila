# Kills any lingering Revare or Mielia resident compile-daemon before a build.
#
# A daemon (spawned by a non-jit `build`/`run`) runs the OLD Revare.dll and holds an open
# handle to it, so a subsequent `dotnet build` cannot overwrite bin\...\Revare.dll (MSB3021 "being
# used by another process"). The daemon is stale after any C# change anyway — it would be shut down as
# stale on the next client ping — so killing it here is free; the next run cold-spawns a fresh warm daemon.
#
# Invoked from Anvila.csproj's StopResidentDaemonBeforeBuild target. Always exits 0 (best-effort).

$ErrorActionPreference = 'SilentlyContinue'
# Match ONLY the daemon invocation: the `daemon` verb as the token right after Revare/Mielia .exe or
# .dll (e.g. `Revare.exe daemon` or `dotnet "…\Mielia.dll" daemon`). A loose
# `Revare.*daemon` would also match THIS script's own launcher command line
# (…\Revare\build-support\stop-resident-daemon.ps1) and make it kill itself. Also skip our own PID.
try {
    Get-CimInstance Win32_Process |
        Where-Object { $_.ProcessId -ne $PID -and $_.CommandLine -match '(Revare|Mielia)\.(exe|dll)"?\s+daemon(\s|$)' } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
} catch { }
exit 0
