$ErrorActionPreference = 'Stop'
$proj = Join-Path $PSScriptRoot 'src\WHPO.UI\WHPO.UI.csproj'

# Guard: no compilar/copiar con la app corriendo (DLLs bloqueadas o un proceso
# con ensamblados mezclados en memoria).
$proc = Get-Process WinForge -ErrorAction SilentlyContinue
if ($proc) {
    throw "WinForge esta corriendo (PID $($proc.Id)). Cerrala antes de compilar."
}

# Build de la app con plataforma x64. La salida de build (a diferencia del
# publish) es la completa para una app WinUI unpackaged: incluye los .xbf de
# cada pagina y el resources.pri. Ademas dispara el target post-build del
# csproj que copia toda la salida a src\bin\Debug\WHPO.Debug.x64.
# NOTA: no usar `dotnet publish` aqui — su salida no trae XBFs y el destino del
# acceso directo quedaria sin ellos (XamlParseException en todas las paginas).
Write-Output "Building $proj (x64)"
dotnet build $proj -c Debug -p:Platform=x64 -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }
Write-Output "Done"
