# Builds the mod for every server platform and assembles a ready-to-copy package in dist\LlmAgents.
#   .\tools\package.ps1
#   .\tools\package.ps1 -LocalMods "C:\Program Files (x86)\Steam\steamapps\common\Barotrauma\LocalMods"
#       also copies it to <LocalMods>\LlmAgents (an existing llm_config.json there is kept)
param([string]$LocalMods = "")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "dist\LlmAgents"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force $out | Out-Null
Copy-Item (Join-Path $root "Content\*") $out
foreach ($p in "Windows", "Linux", "OSX") {
    dotnet build (Join-Path $root "src\LlmAgents.Server\LlmAgents.Server.csproj") -c Release "-p:GamePlatform=$p" -v q -nologo
    if ($LASTEXITCODE -ne 0) { throw "build failed for $p" }
    $bin = Join-Path $out "bin\Server\$p"
    New-Item -ItemType Directory -Force $bin | Out-Null
    Copy-Item (Join-Path $root "src\LlmAgents.Server\bin\Release\$p\LlmAgents.dll") $bin
}
Write-Host "Package: $out"
if ($LocalMods) {
    $dest = Join-Path $LocalMods "LlmAgents"
    $keep = Join-Path $dest "llm_config.json"
    $saved = $null
    if (Test-Path $keep) { $saved = Get-Content -Raw $keep }
    New-Item -ItemType Directory -Force $dest | Out-Null
    Copy-Item -Recurse -Force (Join-Path $out "*") $dest
    if ($saved) { Set-Content -NoNewline -Path $keep -Value $saved }
    Write-Host "Deployed to $dest"
}
