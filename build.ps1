$ErrorActionPreference = 'Stop'
$proj = Join-Path $PSScriptRoot 'src\WHPO.UI\WHPO.UI.csproj'
Write-Output "Publishing $proj"
dotnet publish $proj -c Debug -p:PublishProfile=win-x64 -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
Write-Output "Done"
