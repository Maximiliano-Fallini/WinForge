# ============================================================
# Construye el instalador de WinForge (WinForge-<release>.msi)
# Requisitos: .NET SDK + WiX v7 local (.tools/wix)
# ============================================================
$ErrorActionPreference = 'Stop'

$root     = Split-Path $PSScriptRoot -Parent
$wix      = Join-Path $root '.tools\wix\wix.exe'
$version  = '0.7.5'
# Etiqueta de release (0.7.5-beta3): la fuente de verdad es InformationalVersion
# de WHPO.UI.csproj. El MSI conserva Version numérica (0.7.5) porque Windows
# Installer no acepta sufijos, pero el archivo y la UI muestran la beta.
$uiProject = Join-Path $root 'src\WHPO.UI\WHPO.UI.csproj'
$releaseLabel = [regex]::Match((Get-Content $uiProject -Raw), '<InformationalVersion>([^<]+)</InformationalVersion>').Groups[1].Value.Trim()
$releaseLabel = $releaseLabel.Split('+')[0].Trim()
if ([string]::IsNullOrWhiteSpace($releaseLabel)) {
    throw "No se pudo leer InformationalVersion de $uiProject"
}
if (-not $releaseLabel.StartsWith($version, [System.StringComparison]::Ordinal)) {
    throw "Release inconsistente: build-installer.ps1 usa $version pero InformationalVersion es $releaseLabel."
}
# Etiqueta del canal: vacía en una release final (UI.wxs omite el sufijo con <?ifdef?>).
$betaLabel = ''
$betaMatch = [regex]::Match($releaseLabel, '-beta(\d*)$', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
if ($betaMatch.Success) {
    $betaNumber = $betaMatch.Groups[1].Value
    $betaLabel = if ($betaNumber) { "Beta $betaNumber" } else { 'Beta' }
}
$out = Join-Path $PSScriptRoot "WinForge-$releaseLabel.msi"

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

# 0b) Limpiar únicamente los artefactos de esta versión; conservar los instaladores
#     anteriores para que una nueva compilación no borre releases locales previas.
if (Test-Path $out) { Remove-Item $out -Force }
$wixpdb = [IO.Path]::ChangeExtension($out, '.wixpdb')
if (Test-Path $wixpdb) { Remove-Item $wixpdb -Force }

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

# 1) Publicar la app (Release, win-x64, .NET y WinAppSDK self-contained -> sin dependencias externas)
Write-Host "=== 1/4 Publicando WinForge (Release win-x64, self-contained) ==="
Push-Location (Join-Path $root 'src\WHPO.UI')
try {
    # --self-contained true EXPLÍCITO: no se delega en el default del proyecto.
    # El MSI se anuncia como "no necesita .NET installed"; si esa propiedad se
    # pierde, el publish sale framework-dependent y el usuario ve el cartel
    # "You must install .NET" al abrir WinForge.exe.
    dotnet publish -c Release -r win-x64 --self-contained true `
        -p:PublishReadyToRun=false `
        -p:WindowsAppSDKSelfContained=true `
        -o $publish 2>&1 | Select-Object -Last 3
    # $ErrorActionPreference no cubre el código de salida de un proceso nativo:
    # sin esto, un publish fallido seguía de largo y el harvest encontraba un
    # publish vacío (o, peor, el de la corrida anterior). Importa especialmente en
    # CI, donde nadie mira la salida a ojo antes de publicar el MSI.
    if ($LASTEXITCODE -ne 0) { throw "Falló el publish de la app (código $LASTEXITCODE)" }

    # Guard de autosuficiencia: el MSI promete correr en una PC sin .NET ni
    # Windows App SDK. Estos archivos son la prueba de que el runtime viajó:
    # sin ellos el apphost pide instalar .NET antes de ejecutar una sola línea
    # de WinForge. Falla el build acá, no en la máquina del usuario.
    $requiredRuntimeFiles = @(
        'WinForge.exe',
        'WinForge.runtimeconfig.json',
        'hostfxr.dll',
        'hostpolicy.dll',
        'coreclr.dll',
        'System.Private.CoreLib.dll',
        'Microsoft.WindowsAppRuntime.dll'
    )
    $missingRuntimeFiles = @($requiredRuntimeFiles | Where-Object {
        -not (Test-Path (Join-Path $publish $_))
    })
    if ($missingRuntimeFiles.Count -gt 0) {
        throw "El publish no es self-contained: faltan $($missingRuntimeFiles -join ', '). El MSI instalaría una app que pide .NET."
    }
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
    $wixArgs = @('build', '-arch', 'x64', '-ext', $uiExt, '-culture', 'es-ES',
        '-bindpath', $caDll.DirectoryName, '-d', "ReleaseLabel=$releaseLabel")
    # Solo las betas definen BetaLabel: sin la variable, UI.wxs muestra únicamente
    # «Versión [ProductVersion]» (ver <?ifdef?> en el control Version).
    if ($betaLabel) { $wixArgs += @('-d', "BetaLabel=$betaLabel") }
    $wixArgs += @('Product.wxs', 'Files.wxs', 'UI.wxs', '-o', $out)
    & $wix @wixArgs
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
