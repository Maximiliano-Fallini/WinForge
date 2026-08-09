# ============================================================
# Construye el instalador de WinForge (WinForge-<version>.msi)
# Requisitos: .NET SDK + WiX v7 local (.tools/wix)
# ============================================================
$ErrorActionPreference = 'Stop'

$root     = Split-Path $PSScriptRoot -Parent
$wix      = Join-Path $root '.tools\wix\wix.exe'
$version  = '1.0.0'
$out      = Join-Path $PSScriptRoot "WinForge-$version.msi"

if (-not (Test-Path $wix)) {
    throw "WiX no encontrado en $wix. Instalalo con: dotnet tool install wix --tool-path .tools/wix"
}

# 1) Publicar la app (Release, win-x64, WinAppSDK self-contained -> sin dependencias externas)
Write-Host "=== 1/4 Publicando WinForge (Release win-x64, WinAppSDK self-contained) ==="
Push-Location (Join-Path $root 'src\WHPO.UI')
try {
    dotnet publish -c Release -r win-x64 `
        -p:PublishReadyToRun=false `
        -p:WindowsAppSDKSelfContained=true `
        -o (Join-Path $PSScriptRoot 'publish') 2>&1 | Select-Object -Last 3
} finally { Pop-Location }

# 2) El publish de WinAppSDK NO copia los .xbf / Assets / logos / WinForge.pri
#    (quedan en el build output). Copiamos TODO el build output al publish:
#    agrega lo faltante y refresca lo desactualizado (el publish conserva el
#    runtime self-contained que el build output no tiene).
Write-Host "=== 2/4 Sincronizando build output -> publish ==="
$buildOut = Join-Path $root 'src\WHPO.UI\bin\Release\net9.0-windows10.0.19041.0\win-x64'
$publish  = Join-Path $PSScriptRoot 'publish'
$copied   = 0
Get-ChildItem -Path $buildOut -Recurse -File | ForEach-Object {
    $rel  = $_.FullName.Substring($buildOut.Length + 1)
    $dest = Join-Path $publish $rel
    if (-not (Test-Path $dest) -or $_.LastWriteTime -gt (Get-Item $dest).LastWriteTime) {
        New-Item -ItemType Directory -Force -Path (Split-Path $dest -Parent) | Out-Null
        Copy-Item $_.FullName $dest -Force
        $script:copied++
    }
}
Write-Host "Sincronizados $copied archivos"

# 3) Generar el fragmento de archivos (GUIDs determinísticos)
Write-Host "=== 3/4 Generando Files.wxs ==="
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'harvest.ps1')

# 4) Compilar el MSI (x64) y validarlo
Write-Host "=== 4/4 Compilando MSI ==="
Push-Location $PSScriptRoot
try {
    & $wix build -arch x64 Product.wxs Files.wxs -o $out
    if ($LASTEXITCODE -ne 0) { throw "wix build fallo con codigo $LASTEXITCODE" }
    & $wix msi validate $out -sice ICE03
    if ($LASTEXITCODE -ne 0) { Write-Warning "Validacion con errores (ver arriba); el MSI se genero igualmente" }
} finally { Pop-Location }

Write-Host "`nInstalador listo: $out ($([math]::Round((Get-Item $out).Length / 1MB, 1)) MB)"
