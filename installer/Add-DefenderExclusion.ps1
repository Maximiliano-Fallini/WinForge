<#
.SYNOPSIS
    Agrega (o quita) las exclusiones de Windows Defender que WinForge necesita para
    poder instalarse y arrancar sin que el antivirus lo bloquee o lo ponga en cuarentena.

.DESCRIPTION
    Por que existe: WinForge es una app sin firmar digitalmente que modifica ajustes del
    sistema, escribe en el registro, instala servicios y trae un filtro de driver de
    kernel para el overclock de USB. Ese conjunto de comportamientos es justo el perfil
    que el analisis heuristico de Defender marca como sospechoso, asi que el ejecutable
    termina bloqueado o borrado: la app no abre y el usuario no entiende por que.

    Lo llama el instalador con una CustomAction diferida, pero tambien se puede correr a
    mano:

        powershell -ExecutionPolicy Bypass -File Add-DefenderExclusion.ps1 `
            -Paths "C:\Program Files\WinForge" -IncludeDataFolder

        powershell -ExecutionPolicy Bypass -File Add-DefenderExclusion.ps1 `
            -Paths "C:\Program Files\WinForge" -IncludeDataFolder -Remove

    NOTA: el instalador aplica las mismas exclusiones ANTES de copiar los archivos, con las
    CustomActions administradas de DefenderExclusionCA.cs (llamada WMI con tope de tiempo).
    Ese es el paso que evita el forcejeo con el antivirus durante la copia; este script es la
    pasada de verificacion posterior (y la que deja el log en %TEMP%), asi que lo normal es
    que lo encuentre todo ya aplicado.

    Reglas del script, para que no pueda mentir:
      * Verifica contra el REGISTRO de Defender (HKLM\...\Windows Defender\Exclusions),
        que es donde Defender guarda el estado real. No usa Get-MpPreference para
        verificar: esa lectura queda desactualizada dentro de la misma sesion (y el
        servicio aplica el cambio con un pequeño retraso), asi que daba "no quedo"
        aunque el cambio si estuviera hecho. El registro no tiene ese problema.
      * Espera a que el cambio se asiente: reintenta la lectura hasta ~10 s. Si despues
        de eso no quedo, devuelve error. Ese es el caso tipico de la proteccion contra
        alteraciones (Tamper Protection) activada: Windows acepta el comando y lo
        descarta en silencio, sin error.
      * Si el antivirus activo no es Defender (AMRunningMode distinto de "Normal"),
        lo dice y sale con 2: en ese equipo estas exclusiones no cambian nada, y
        callarse seria vender una solucion que no aplica.
      * Excluye solamente la carpeta de instalacion de la app, su carpeta de datos y
        el nombre del ejecutable. Nunca C:\, nunca una carpeta de usuario completa,
        nunca todo el sistema: una exclusion amplia deja sin escanear cualquier cosa
        que se copie ahi, y eso es un agujero que no nos corresponde abrir.
      * Deja un log propio (%TEMP%\WinForge-DefenderExclusion.log). El motor del MSI no
        captura la salida de una CustomAction EXE, asi que sin este archivo, cuando algo
        falla, el log del instalador no dice por que.
      * -Remove deshace exactamente lo mismo, para no dejar la maquina con una
        carpeta excluida para siempre despues de desinstalar.

    Codigos de salida:
      0 = aplicado y verificado (o no habia nada que hacer)
      1 = error inesperado
      2 = Defender no es el antivirus activo, o no esta disponible
      3 = no se pudo aplicar (tipicamente Tamper Protection)
      4 = no se pudo quitar
#>
[CmdletBinding()]
param(
    # Carpetas a excluir. Si no se pasa ninguna, se usa la carpeta del propio script
    # (que es la carpeta de instalacion cuando lo llama el instalador).
    [string[]] $Paths = @(),

    # Nombres de ejecutable a excluir (Defender compara solo el nombre del archivo).
    [string[]] $Processes = @('WinForge.exe'),

    # Excluir tambien la carpeta de datos de la app (%LOCALAPPDATA%\WHPO), que es
    # donde se descomprimen los componentes y de donde salen los instaladores que
    # la app baja despues de instalada.
    [switch] $IncludeDataFolder,

    # Carpeta de datos explicita (por defecto %LOCALAPPDATA%\WHPO).
    [string] $DataFolder,

    # Quitar las exclusiones en vez de agregarlas (desinstalacion).
    [switch] $Remove,

    # Segundos maximos a esperar a que Defender asiente el cambio antes de darlo por fallido.
    [int] $WaitSeconds = 10,

    # Archivo de log. Por defecto %TEMP%\WinForge-DefenderExclusion.log.
    [string] $LogFile,

    # No imprimir el detalle (igual queda en el log).
    [switch] $Quiet
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $LogFile) { $LogFile = Join-Path $env:TEMP 'WinForge-DefenderExclusion.log' }

# Arranca el log de nuevo en cada corrida: interesa la ultima, no el historial.
try {
    $stamp = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    Set-Content -Path $LogFile -Value "=== WinForge: exclusiones de Defender ($stamp) ===" -Encoding UTF8
} catch {
    # Si no se puede escribir el log, el script sigue: el log no es la funcion principal.
    $LogFile = $null
}

function Say([string] $Text) {
    if (-not $Quiet) { Write-Host $Text }
    if ($LogFile) {
        try { Add-Content -Path $LogFile -Value $Text -Encoding UTF8 } catch { }
    }
}

function Say-Log([string] $Text) {
    if ($LogFile) {
        try { Add-Content -Path $LogFile -Value $Text -Encoding UTF8 } catch { }
    }
}

# Normaliza una ruta para compararla: sin espacios al borde y sin barra final.
function Normalize([string] $Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) { return '' }
    return $Value.Trim().TrimEnd('\')
}

# Estado real de las exclusiones, leido del registro (fuente de verdad de Defender).
function Read-RegistryExclusions {
    $paths = New-Object System.Collections.Generic.List[string]
    $procs = New-Object System.Collections.Generic.List[string]

    $baseKey = 'HKLM:\SOFTWARE\Microsoft\Windows Defender\Exclusions'
    foreach ($sub in @('Paths', 'Processes')) {
        $key = Join-Path $baseKey $sub
        if (-not (Test-Path $key)) { continue }
        $names = (Get-Item -Path $key -ErrorAction Stop).GetValueNames()
        foreach ($name in $names) {
            $n = if ($sub -eq 'Paths') { Normalize $name } else { ('' + $name).Trim() }
            if ($n) {
                if ($sub -eq 'Paths') { if (-not $paths.Contains($n)) { $paths.Add($n) } }
                else { if (-not $procs.Contains($n)) { $procs.Add($n) } }
            }
        }
    }

    return [pscustomobject]@{ Paths = @($paths); Processes = @($procs) }
}

# Compara lo pedido contra el registro. Devuelve las que faltan (o las que sobran, al quitar).
function Compare-Exclusions {
    param([string[]] $Paths, [string[]] $Processes, [switch] $ExpectPresent)

    $state = Read-RegistryExclusions
    $faltan = New-Object System.Collections.Generic.List[string]

    foreach ($t in $Paths) {
        $present = $state.Paths -contains $t
        if ($ExpectPresent -and -not $present) { $faltan.Add($t) }
        if (-not $ExpectPresent -and $present) { $faltan.Add($t) }
    }
    foreach ($p in $Processes) {
        $present = $state.Processes -contains $p
        if ($ExpectPresent -and -not $present) { $faltan.Add($p) }
        if (-not $ExpectPresent -and $present) { $faltan.Add($p) }
    }

    return [pscustomobject]@{ Faltan = @($faltan); State = $state }
}

Say ''
Say '=== WinForge: exclusiones de Windows Defender ==='

# ---- 1) Defender tiene que ser el antivirus que esta actuando ----------------
# Si el estado no se puede leer, NO se abandona: se intenta igual y se informa el
# resultado real (puede fallar la consulta y aun asi aceptarse el cambio).
$status = $null
try { $status = Get-MpComputerStatus -ErrorAction Stop } catch { $status = $null }

$runningMode = ''
if ($status) {
    $runningMode = [string] $status.AMRunningMode
    if ($runningMode -and $runningMode -ne 'Normal') {
        Say "El antivirus activo no es Windows Defender (modo: $runningMode)."
        Say 'Estas exclusiones no cambian nada en ese caso: hace falta agregar la carpeta de WinForge'
        Say 'en las exclusiones del antivirus que este instalado.'
        exit 2
    }
}
else {
    Say 'No se pudo leer el estado de Windows Defender (puede pasar cuando el script corre como'
    Say 'CustomAction del instalador). Se intenta aplicar igual y se verifica contra el registro.'
}

$tamper = $false
try { $tamper = [bool] $status.IsTamperProtected } catch { $tamper = $false }
if ($status) {
    Say ("Proteccion contra alteraciones: " + $(if ($tamper) { 'activada' } else { 'desactivada' }))
}

# ---- 2) Armar la lista de objetivos -----------------------------------------
if ($Paths.Count -eq 0) { $Paths = @($PSScriptRoot) }

$targetPaths = New-Object System.Collections.Generic.List[string]
foreach ($p in $Paths) {
    $n = Normalize $p
    if ($n -and -not $targetPaths.Contains($n)) { $targetPaths.Add($n) }
}

if ($IncludeDataFolder) {
    $folder = if ($DataFolder) { $DataFolder } else { Join-Path $env:LOCALAPPDATA 'WHPO' }
    $n = Normalize $folder
    if ($n -and -not $targetPaths.Contains($n)) { $targetPaths.Add($n) }
}

$targetProcesses = @()
foreach ($p in $Processes) {
    $n = ('' + $p).Trim()
    if ($n -and ($targetProcesses -notcontains $n)) { $targetProcesses += $n }
}

Say ('Carpetas: ' + ($(if ($targetPaths.Count) { $targetPaths -join ' | ' } else { '(ninguna)' })))
Say ('Ejecutables: ' + ($(if ($targetProcesses.Count) { $targetProcesses -join ' | ' } else { '(ninguno)' })))

# ---- 3) Escribir ------------------------------------------------------------
$action = if ($Remove) { 'quitando' } else { 'agregando' }
Say "Defender: $action las exclusiones..."

try {
    foreach ($t in $targetPaths) {
        if ($Remove) { Remove-MpPreference -ExclusionPath $t -ErrorAction Stop }
        else         { Add-MpPreference    -ExclusionPath $t -ErrorAction Stop }
    }
    foreach ($p in $targetProcesses) {
        if ($Remove) { Remove-MpPreference -ExclusionProcess $p -ErrorAction Stop }
        else         { Add-MpPreference    -ExclusionProcess $p -ErrorAction Stop }
    }
}
catch {
    # Un fallo aca es informativo, no definitivo: el mensaje tipico es que la exclusion
    # ya existia (al agregar) o no existia (al quitar), y en ambos casos el estado final
    # puede ser el correcto. La verificacion de abajo decide.
    Say "Aviso durante la escritura: $($_.Exception.Message)"
}

# ---- 4) Verificar leyendo el registro, esperando a que el cambio se asiente ----
# Defender aplica el cambio con un pequeño retraso, asi que una lectura inmediata puede
# no verlo. Se reintenta hasta WaitSeconds antes de declarar un fallo.
$pendientes = @()
$state = $null
$elapsed = 0
while ($elapsed -le $WaitSeconds) {
    $check = Compare-Exclusions -Paths $targetPaths.ToArray() -Processes $targetProcesses -ExpectPresent:(-not $Remove)
    $pendientes = @($check.Faltan)
    $state = $check.State
    if ($pendientes.Count -eq 0) { break }
    Start-Sleep -Milliseconds 500
    $elapsed += 0.5
}

Say-Log ('Registro tras la escritura - rutas: ' + ($(if ($state.Paths.Count) { $state.Paths -join ' | ' } else { '(ninguna)' })))
Say-Log ('Registro tras la escritura - procesos: ' + ($(if ($state.Processes.Count) { $state.Processes -join ' | ' } else { '(ninguno)' })))

if ($pendientes.Count -eq 0) {
    if ($Remove) { Say 'Listo: las exclusiones se quitaron y quedaron verificadas.' }
    else         { Say 'Listo: las exclusiones quedaron aplicadas y verificadas.' }
    exit 0
}

if ($Remove) {
    Say 'No se pudieron quitar estas exclusiones:'
    foreach ($q in $pendientes) { Say "  - $q" }
    exit 4
}

Say 'Defender no aplico estas exclusiones:'
foreach ($f in $pendientes) { Say "  - $f" }
if ($tamper) {
    Say 'La proteccion contra alteraciones esta activada y es la causa mas probable:'
    Say 'Windows descarta en silencio los cambios de configuracion de Defender hechos por una app.'
}
Say 'Se pueden agregar a mano, y queda aplicado igual:'
Say '  Seguridad de Windows > Proteccion contra virus y amenazas > Administrar la configuracion >'
Say '  Exclusiones > Agregar o quitar exclusiones > Agregar una carpeta'
exit 3
