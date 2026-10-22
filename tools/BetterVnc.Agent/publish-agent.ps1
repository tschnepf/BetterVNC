# Publishes the BetterVNC guest agent for Windows and Linux and wraps each build in an archive that contains the
# single-file binary, an installer script and a short INSTALL.txt.
#
#   powershell -ExecutionPolicy Bypass -File tools/BetterVnc.Agent/publish-agent.ps1 [-Rids win-x64,linux-x64] [-Out publish]
#
# The macOS agent is a native app built on a Mac: sh tools/BetterVnc.Agent.Mac/build.sh.
#
# Output: publish/bettervnc-agent-<version>-<rid>.zip (Windows) or .tar.gz (Linux).
param(
    [string[]]$Rids = @("win-x64", "linux-x64", "linux-arm64"),
    [string]$Out = "publish",
    [string]$Configuration = "Release"
)
$ErrorActionPreference = "Stop"
$Rids = @($Rids | ForEach-Object { $_ -split "," } | Where-Object { $_ })  # "-Rids a,b" arrives as one string when run with -File
$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$project = Join-Path $root "tools\BetterVnc.Agent\BetterVnc.Agent.csproj"
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$outDir = Join-Path $root $Out
New-Item -ItemType Directory -Force $outDir | Out-Null

$installSh = @'
#!/bin/sh
# Installs the BetterVNC guest agent for the current user: copies it into place, creates the token,
# registers it to start at login (systemd user service or desktop autostart) and starts it.
set -e
cd "$(dirname "$0")"
chmod +x ./bettervnc-agent
exec ./bettervnc-agent install "$@"
'@

foreach ($rid in $Rids) {
    $tfm = if ($rid -like "win-*") { "net10.0-windows" } else { "net10.0" }
    $stage = Join-Path $outDir "agent-$rid"
    Write-Host "== $rid ($tfm) -> $stage"
    if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
    & dotnet publish $project -c $Configuration -f $tfm -r $rid -o $stage -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $rid" }
    Get-ChildItem $stage -Filter *.pdb | Remove-Item -Force

    $exe = if ($rid -like "win-*") { "bettervnc-agent.exe" } else { "bettervnc-agent" }
    $install = if ($rid -like "win-*") { "Open a command prompt in this folder and run:  bettervnc-agent.exe install" } else { "Open Terminal in this folder and run:  sh install.sh" }
    @"
BetterVNC guest agent $version ($rid)

$install

That copies the agent into a per-user location, creates its config with a random token, registers it
to start at login and starts it. The token it prints goes into the BetterVNC connection (Guest agent
section). Remove it again with '$exe uninstall'.

Manual use instead of install: run '$exe' in this folder; it creates bettervnc-agent.json next to
itself and prints the token. Options: --port, --listen, --config. See the README's "Guest agent" section.
"@ | Set-Content -Path (Join-Path $stage "INSTALL.txt") -Encoding ascii

    $name = "bettervnc-agent-$version-$rid"
    if ($rid -like "win-*") {
        $zip = Join-Path $outDir "$name.zip"
        if (Test-Path $zip) { Remove-Item $zip }
        Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip
        Write-Host "   $zip"
    }
    else {
        # LF line endings for the shell script; tar from Git for Windows can store the executable bit.
        [IO.File]::WriteAllText((Join-Path $stage "install.sh"), $installSh.Replace("`r`n", "`n"), [Text.UTF8Encoding]::new($false))
        $tgz = Join-Path $outDir "$name.tar.gz"
        if (Test-Path $tgz) { Remove-Item $tgz }
        $gitTar = "C:\Program Files\Git\usr\bin\tar.exe"
        Push-Location $outDir
        try {
            if (Test-Path $gitTar) {
                & $gitTar --mode=755 --owner=0 --group=0 --transform "s,^agent-$rid,$name," -czf "$name.tar.gz" "agent-$rid"
            }
            else {
                & tar -czf "$name.tar.gz" -s "/^agent-$rid/$name/" "agent-$rid"
            }
            if ($LASTEXITCODE -ne 0) { throw "tar failed for $rid" }
        }
        finally { Pop-Location }
        Write-Host "   $tgz"
    }
}
Write-Host "Done. Archives are in $outDir"
