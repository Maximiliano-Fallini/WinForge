# build-components.ps1 — Publica un componente de WinForge para el Workshop.
#
# Uso:
#   ./build-components.ps1 -Component demo                       # compila + arma zip + actualiza components.json
#   ./build-components.ps1 -Component demo -Version 1.0.1        # con otra versión
#   ./build-components.ps1 -Component demo -Upload               # además sube el asset a la Release con gh
#
# Flujo que automatiza:
#   1. Compila el proyecto del componente (src/Components/WinForge.Component.<Nombre>).
#   2. Arma <id>-<version>.zip con la DLL del componente (las dependencias pesadas
#      — WHPO.Core, WinAppSDK, runtime — NO viajan: las resuelve la app en runtime).
#   3. Calcula el SHA-256 del zip.
#   4. Agrega/actualiza la entrada en components.json (catálogo que la app lee
#      desde raw.githubusercontent.com/<repo>/main/components.json).
#   5. Con -Upload: sube el zip como asset a la Release "components" con gh CLI
#      (requiere GitHub CLI instalado y logueado: gh auth login).
#
# Después de subir el asset y hacer commit del components.json, todos los
# usuarios ven el componente nuevo/actualizado en el Workshop sin publicar
# una versión nueva de la app.

param(
    [Parameter(Mandatory = $true)][string]$Component,
    [string]$Version = "",
    [string]$Repo = "Maximiliano-Fallini/WinForge",
    [string]$ReleaseTag = "components",
    [string]$Category = "Sistema",
    [string]$Icon = "\uE7C3",
    [string]$MinAppVersion = "",
    [switch]$Upload
)

$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$projectDir = Join-Path $root "src\Components\WinForge.Component.$Component"
$projectFile = Join-Path $projectDir "WinForge.Component.$Component.csproj"
if (-not (Test-Path $projectFile)) {
    throw "No existe el proyecto del componente: $projectFile"
}

# Versión: la del csproj si no se pasa por parámetro.
if (-not $Version) {
    $csprojXml = [xml](Get-Content $projectFile -Raw)
    $Version = $csprojXml.Project.PropertyGroup.Version | Select-Object -First 1
    if (-not $Version) { $Version = "0.1.0" }
}

Write-Output "== Compilando $Component v$Version =="
dotnet build $projectFile -c Release -p:Platform=x64 --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet build falló ($LASTEXITCODE)" }

# Salidas posibles según plataforma/TFM: con -p:Platform=x64 la salida cae en
# bin\x64\Release\<tfm>\win-x64; sin plataforma explícita, en bin\Release\<tfm>.
$tfm = "net9.0-windows10.0.19041.0"
$binCandidates = @(
    (Join-Path $projectDir "bin\x64\Release\$tfm\win-x64"),
    (Join-Path $projectDir "bin\Release\$tfm\win-x64"),
    (Join-Path $projectDir "bin\x64\Release\$tfm"),
    (Join-Path $projectDir "bin\Release\$tfm")
)
$binDir = $binCandidates | Where-Object { Test-Path (Join-Path $_ "WinForge.Component.$Component.dll") } | Select-Object -First 1
if (-not $binDir) { $binDir = $binCandidates[0] }
$mainDll = Join-Path $binDir "WinForge.Component.$Component.dll"
if (-not (Test-Path $mainDll)) { throw "No se encontró la DLL compilada: $mainDll" }

# DLLs que viajan en el zip: solo las propias del componente. Todo lo demás
# (WHPO.Core, WinAppSDK, BCL) lo resuelve el ALC desde la app instalada.
$runtimePrefixes = @("WHPO.", "Microsoft.", "System.", "WindowsBase", "WinRT.Runtime", "D2DMAP", "MrAdvice", "Newtonsoft.")
$files = Get-ChildItem $binDir -Filter *.dll | Where-Object {
    $n = $_.Name
    -not ($runtimePrefixes | Where-Object { $n.StartsWith($_) })
}
if (-not ($files | Where-Object { $_.Name -eq "WinForge.Component.$Component.dll" })) {
    Write-Warning "La DLL principal no pasó el filtro de runtime: se fuerza su inclusión."
    $files = @($files) + @(Get-Item $mainDll)
}

$distDir = Join-Path $root "components-dist"
New-Item -ItemType Directory -Force -Path $distDir | Out-Null
$zipName = "$Component-$Version.zip"
$zipPath = Join-Path $distDir $zipName
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

Write-Output "== Armando $zipName =="
$staging = Join-Path $env:TEMP "wf-comp-$Component-$([guid]::NewGuid().ToString('N').Substring(0,8))"
New-Item -ItemType Directory -Force -Path $staging | Out-Null
foreach ($f in $files) { Copy-Item $f.FullName -Destination $staging -Force }
Compress-Archive -Path (Join-Path $staging "*") -DestinationPath $zipPath -Force
Remove-Item $staging -Recurse -Force

$hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item $zipPath).Length
Write-Output "Zip:     $zipPath"
Write-Output "SHA-256: $hash"
Write-Output "Tamaño:  $size bytes"

# Actualizar components.json (merge de la entrada por id).
$catalogPath = Join-Path $root "components.json"
$catalog = if (Test-Path $catalogPath) { Get-Content $catalogPath -Raw | ConvertFrom-Json } else { [pscustomobject]@{ catalogVersion = 1; components = @() } }
$entry = [pscustomobject]@{
    id             = $Component
    name           = $Component
    description    = "Componente $Component de WinForge."
    icon           = $Icon
    category       = $Category
    version        = $Version
    minAppVersion  = $MinAppVersion
    url            = "https://github.com/$Repo/releases/download/$ReleaseTag/$zipName"
    sha256         = $hash
    sizeBytes      = $size
}
$components = @($catalog.components | Where-Object { $_.id -ne $Component }) + @($entry)
$catalog | Add-Member -NotePropertyName components -NotePropertyValue $components -Force
$catalog | ConvertTo-Json -Depth 6 | Set-Content $catalogPath -Encoding UTF8
Write-Output "== components.json actualizado (entrada '$Component' v$Version) =="

if ($Upload) {
    Write-Output "== Subiendo asset a la Release '$ReleaseTag' con gh CLI =="
    gh release view $ReleaseTag -R $Repo 2>$null
    if ($LASTEXITCODE -ne 0) {
        gh release create $ReleaseTag -R $Repo --title "Componentes del Workshop" --notes "Assets del catálogo de componentes del Workshop." $zipPath
    } else {
        gh release upload $ReleaseTag $zipPath -R $Repo --clobber
    }
    if ($LASTEXITCODE -ne 0) { throw "gh falló: revisá 'gh auth login'." }
    Write-Output "Asset publicado: la app lo descarga desde el manifest."
}

Write-Output ""
Write-Output "Listo. Para que los usuarios lo vean: subí el asset a la Release (-Upload) y hacé commit del components.json."
