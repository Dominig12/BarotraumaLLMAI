# Downloads the publicized Barotrauma/LuaCs reference DLLs into Refs\ (needed to build the mod).
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$url = if ($env:LUACS_REFS_URL) { $env:LUACS_REFS_URL } else { "https://github.com/evilfactory/LuaCsForBarotrauma/releases/download/latest/luacsforbarotrauma_refs.zip" }
$zip = Join-Path ([IO.Path]::GetTempPath()) "luacs_refs.zip"
Write-Host "Downloading $url"
Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
Expand-Archive -Path $zip -DestinationPath (Join-Path $root "Refs") -Force
Remove-Item $zip
if (Test-Path (Join-Path $root "Refs\Windows\DedicatedServer.dll")) { Write-Host "Refs ready in $root\Refs" } else { throw "Refs\Windows\DedicatedServer.dll not found after extraction" }
