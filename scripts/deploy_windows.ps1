#!/usr/bin/env pwsh
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Set-Location (Join-Path $PSScriptRoot '..')
$version = dotnet msbuild BassNotes.csproj -nologo -getProperty:Version
if ($LASTEXITCODE -ne 0) { throw 'Could not read the app version.' }

dotnet publish BassNotes.csproj -c Release -r win-x64 --self-contained true -p:DebugType=None
if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed.' }

New-Item -ItemType Directory -Force -Path dist | Out-Null
$archive = "dist/BassNotes-$version-Windows-x64.zip"
Compress-Archive -Path 'bin/Release/net10.0/win-x64/publish/*' -DestinationPath $archive -Force
Write-Host "Created $archive"
