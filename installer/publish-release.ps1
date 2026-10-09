$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

dotnet publish "$root\QuickNotes.App\QuickNotes.App.csproj" `
  -c Release `
  -r win-x64 `
  --self-contained false `
  -p:PublishSingleFile=false `
  -p:IncludeNativeLibrariesForSelfExtract=false

Write-Host "Сборка publish готова. Пользовательские данные в %LOCALAPPDATA%\QuickNotes установщик и удаление не трогают."
Write-Host "Соберите установщик через Inno Setup: installer\QuickNotes.iss"
