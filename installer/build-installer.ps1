# ============================================================
# Construye el instalador de WinForge (WinForge-<version>.msi)
# Requisitos: .NET SDK + WiX v7 local (.tools/wix)
# ============================================================
$ErrorActionPreference = 'Stop'

$root     = Split-Path $PSScriptRoot -Parent
$wix      = Join-Path $root '.tools\wix\wix.exe'
$version  = '0.1.0'
$out      = Join-Path $PSScriptRoot "WinForge-$version.msi"

if (-not (Test-Path $wix)) {
    throw "WiX no encontrado en $wix. Instalalo con: dotnet tool install wix --tool-path .tools/wix"
}

# 0) Coherencia de versión: Product.wxs debe declarar la misma versión que este
#    script, para no publicar un MSI con una versión distinta a la esperada.
$productWxs = Join-Path $PSScriptRoot 'Product.wxs'
$declaredVersion = [regex]::Match((Get-Content $productWxs -Raw), '<Package[^>]*?\bVersion="([^"]+)"').Groups[1].Value
if ($declaredVersion -ne $version) {
    throw "Version inconsistente: build-installer.ps1 dice $version pero Product.wxs declara $declaredVersion."
}

# 0b) Limpiar builds anteriores: solo queda el MSI/wixpdb que se va a generar.
Get-ChildItem $PSScriptRoot -Filter 'WinForge-*.msi'   | Remove-Item -Force -ErrorAction SilentlyContinue
Get-ChildItem $PSScriptRoot -Filter 'WinForge-*.wixpdb' | Remove-Item -Force -ErrorAction SilentlyContinue

# 0c) Cerrar WinForge si está corriendo: el exe queda bloqueado por el proceso
#     y el publish falla al copiarlo (el MSI hace lo mismo al instalar/desinstalar).
$running = Get-Process -Name WinForge -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "Cerrando WinForge en ejecucion (PID $($running.Id -join ', ')) para poder publicar..."
    try {
        $running | Stop-Process -Force -ErrorAction Stop
        Start-Sleep -Milliseconds 800
    }
    catch {
        throw "No se pudo cerrar WinForge (probablemente corre como administrador). Cerralo manualmente y volvé a ejecutar este script."
    }
}

# 0d) Publish determinista: borrar el contenido previo (evita archivos huérfanos
#     de builds viejos en el harvest) y recrear la carpeta.
$publish = Join-Path $PSScriptRoot 'publish'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

# 1) Publicar la app (Release, win-x64, WinAppSDK self-contained -> sin dependencias externas)
Write-Host "=== 1/4 Publicando WinForge (Release win-x64, WinAppSDK self-contained) ==="
Push-Location (Join-Path $root 'src\WHPO.UI')
try {
    dotnet publish -c Release -r win-x64 `
        -p:PublishReadyToRun=false `
        -p:WindowsAppSDKSelfContained=true `
        -o $publish 2>&1 | Select-Object -Last 3
} finally { Pop-Location }

# 2) El publish de WinAppSDK NO copia los .xbf / Assets / logos / WinForge.pri
#    (quedan en el build output). Copiamos TODO el build output al publish:
#    agrega lo faltante y refresca lo desactualizado (el publish conserva el
#    runtime self-contained que el build output no tiene).
Write-Host "=== 2/4 Sincronizando build output -> publish ==="
$buildOut = Join-Path $root 'src\WHPO.UI\bin\Release\net9.0-windows10.0.19041.0\win-x64'
$copied   = 0
Get-ChildItem -Path $buildOut -Recurse -File | ForEach-Object {
    $rel  = $_.FullName.Substring($buildOut.Length + 1)
    # No copiar un subdirectorio 'publish' anidado del build output: es un residuo de
    # un 'dotnet publish' manual y generaria recursividad (installer\publish\publish\...)
    # que rompe el harvest/Files.wxs con directorios duplicados.
    if ($rel -eq 'publish' -or $rel.StartsWith("publish$([IO.Path]::DirectorySeparatorChar)", [StringComparison]::OrdinalIgnoreCase)) { return }
    $dest = Join-Path $publish $rel
    if (-not (Test-Path $dest) -or $_.LastWriteTime -gt (Get-Item $dest).LastWriteTime) {
        New-Item -ItemType Directory -Force -Path (Split-Path $dest -Parent) | Out-Null
        Copy-Item $_.FullName $dest -Force
        $script:copied++
    }
}
Write-Host "Sincronizados $copied archivos"

# 2b) El script de exclusiones de Windows Defender tiene que viajar DENTRO del MSI:
#     el instalador lo ejecuta con una CustomAction (Product.wxs) para que el .exe
#     sin firmar no quede bloqueado o en cuarentena. Va a la raíz de la carpeta de
#     instalación, junto a WinForge.exe, antes del harvest para que quede incluido.
Write-Host "=== 2b/4 Copiando script de exclusiones de Defender ==="
$defenderScript = Join-Path $PSScriptRoot 'Add-DefenderExclusion.ps1'
if (-not (Test-Path $defenderScript)) { throw "No se encontro $defenderScript" }
Copy-Item $defenderScript (Join-Path $publish 'Add-DefenderExclusion.ps1') -Force

# 3) Generar el fragmento de archivos (GUIDs determinísticos)
Write-Host "=== 3/4 Generando Files.wxs ==="
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'harvest.ps1')

# 3b) Compilar el CustomAction de relanzamiento post-update (PostUpdateCA): el
#     auto-updater de la app instala el MSI en silencio y este CA reabre WinForge
#     al terminar su instalación.
Write-Host "=== 3b/4 Compilando CustomAction (PostUpdateCA) ==="
$caProj = Join-Path $PSScriptRoot 'PostUpdateCA\PostUpdateCA.csproj'
dotnet build $caProj -c Debug 2>&1 | Select-Object -Last 3
if ($LASTEXITCODE -ne 0) { throw "Falló la compilación del CustomAction" }

# 4) Compilar el MSI (x64) y validarlo
Write-Host "=== 4/4 Compilando MSI ==="
Push-Location $PSScriptRoot
try {
    $uiExt = Join-Path $root '.wix\extensions\WixToolset.UI.wixext\7.0.0\wixext7\WixToolset.UI.wixext.dll'
    if (-not (Test-Path $uiExt)) { throw "Extensión UI de WiX no encontrada en $uiExt. Instalala con: wix extension add WixToolset.UI.wixext" }
    $caDll = Get-ChildItem (Join-Path $PSScriptRoot 'PostUpdateCA\bin\Debug\net462\') -Filter 'PostUpdateCA.CA.dll' -Recurse | Select-Object -First 1
    if (-not $caDll) { 'No se encontró PostUpdateCA.CA.dll para incluir en el MSI' }
    # -culture es-ES: el instalador es en español y define sus propios textos, pero los
    # diálogos que aporta la extensión de WiX (el de error del motor) resuelven su texto
    # con !(loc.*) según esta cultura. Sin esto saldrían en inglés.
    & $wix build -arch x64 -ext $uiExt -culture es-ES -bindpath $caDll.DirectoryName Product.wxs Files.wxs UI.wxs -o $out
    if ($LASTEXITCODE -ne 0) { throw "wix build fallo con codigo $LASTEXITCODE" }
    & $wix msi validate $out -sice ICE03
    if ($LASTEXITCODE -ne 0) { Write "Validacion con errores (ver arriba); el MSI se genero igualmente" }
} finally { Pop-Location }

# 5) Resumen final: tamaño + checksum SHA-256 (para verificar descargas)
$hash = Get-FileHash $out -Algorithm SHA256
$size = [math]::Round((Get-Item $out).Length / 1MB, 1)
Write-Host ""
Write-Host "Instalador listo: $out ($size MB)"
Write-Host "SHA-256: $($hash.Hash)"
