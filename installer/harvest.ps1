# ============================================================
# Genera installer\Files.wxs a partir de installer\publish
#
# Por que existe: el MSI tiene que declarar cada archivo que instala (MSI no sabe
# copiar una carpeta entera). Este script recorre el publish y escribe el fragmento.
#
# Decisiones:
#   * Un <Component> por archivo, con un <File> que es KeyPath: es el modelo
#     estandar del MSI y hace que Windows Installer repare/actualice archivo por
#     archivo.
#   * GUID determinista = MD5 de "WinForge\<ruta relativa>". No es aleatorio a
#     proposito: si los GUIDs cambiaran en cada compilacion, el MSI trataria a los
#     mismos archivos como componentes nuevos, instalaria copias duplicadas y las
#     actualizaciones dejarian basura.
#   * La estructura de carpetas del publish se conserva tal cual.
#   * El orden es por ruta, para que dos compilaciones del mismo publish generen
#     el mismo archivo byte a byte y se pueda comparar.
#
# INSTALLFOLDER vive aca (C:\Program Files\WinForge): Product.wxs lo referencia
# para los accesos directos, la CustomAction y la desinstalacion.
# ============================================================
$ErrorActionPreference = 'Stop'

$installer = $PSScriptRoot
$publish   = Join-Path $installer 'publish'
$out       = Join-Path $installer 'Files.wxs'

if (-not (Test-Path $publish)) {
    throw "No existe $publish. Ejecuta primero el publish de la app (build-installer.ps1 lo hace)."
}

# GUID estable a partir de una semilla de texto.
function Get-StableGuid([string] $Seed) {
    $md5 = [System.Security.Cryptography.MD5]::Create()
    try   { $bytes = $md5.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Seed)) }
    finally { $md5.Dispose() }
    $guid = New-Object System.Guid -ArgumentList (, $bytes)
    return $guid.ToString().ToUpper()
}

# Escapa lo minimo que puede romper el XML (el resto de caracteres validos de
# nombres de archivo no necesita escape).
function Escape-Xml([string] $Value) {
    return $Value.Replace('&', '&amp;').Replace('<', '&lt;').Replace('>', '&gt;').Replace('"', '&quot;')
}

$files = Get-ChildItem -Path $publish -Recurse -File |
    Where-Object { $_.FullName -notmatch '\\publish\\publish\\' } |
    # Los .pdb (símbolos de depuración) NO viajan al MSI: no hacen falta en runtime y
    # publicarlos en la instalación de cada usuario no aporta nada.
    Where-Object { $_.Extension -ne '.pdb' } |
    Sort-Object FullName

if ($files.Count -eq 0) { throw "El publish esta vacio ($publish)." }

# --- 1) Carpetas: una sola por ruta relativa, con Id estable por orden alfabetico ---
$relDirs = New-Object System.Collections.Generic.List[string]
$fileRows = New-Object System.Collections.Generic.List[object]

foreach ($f in $files) {
    $rel = $f.FullName.Substring($publish.Length + 1)
    $dir = Split-Path $rel -Parent
    if ([string]::IsNullOrEmpty($dir)) { $dir = '.' }      # raiz del publish
    if (-not $relDirs.Contains($dir)) { $relDirs.Add($dir) }
    $fileRows.Add([pscustomobject]@{ Rel = $rel; Dir = $dir })
}

$dirIds = @{}
$n = 0
foreach ($d in ($relDirs | Sort-Object)) {
    if ($d -eq '.') { $dirIds[$d] = 'INSTALLFOLDER' }
    else { $n++; $dirIds[$d] = ('dir_{0:D4}' -f $n) }
}

# --- 2) Escribir el fragmento ---
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('<?xml version="1.0" encoding="UTF-8"?>')
[void]$sb.AppendLine('<!-- Generado por harvest.ps1 a partir de installer\publish. NO EDITAR A MANO:')
[void]$sb.AppendLine('     se regenera en cada compilacion del instalador. -->')
[void]$sb.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
[void]$sb.AppendLine('  <Fragment>')
[void]$sb.AppendLine('    <StandardDirectory Id="ProgramFiles64Folder">')
[void]$sb.AppendLine('      <Directory Id="INSTALLFOLDER" Name="WinForge">')

# Arbol de carpetas: se ordena por ruta y se abre/cierra por profundidad, asi el
# anidado queda correcto sin recursividad.
$stack = New-Object System.Collections.Generic.List[string]
foreach ($d in ($relDirs | Where-Object { $_ -ne '.' } | Sort-Object)) {
    $parts = $d -split '\\'
    $depth = $parts.Count
    while ($stack.Count -ge $depth) { $stack.RemoveAt($stack.Count - 1); [void]$sb.AppendLine((('    ' * ($stack.Count + 3)) + '</Directory>')) }
    $indent = '    ' * ($depth + 2)
    [void]$sb.AppendLine("$indent<Directory Id=""$($dirIds[$d])"" Name=""$(Escape-Xml ($parts[-1]))"">")
    $stack.Add($d)
}
while ($stack.Count -gt 0) { $stack.RemoveAt($stack.Count - 1); [void]$sb.AppendLine((('    ' * ($stack.Count + 3)) + '</Directory>')) }

[void]$sb.AppendLine('      </Directory>')
[void]$sb.AppendLine('    </StandardDirectory>')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('    <ComponentGroup Id="AppFiles">')

$i = 0
foreach ($row in $fileRows) {
    $i++
    $id    = 'cmp_{0:D4}' -f $i
    $fileId= 'fil_{0:D4}' -f $i
    $dirId = $dirIds[$row.Dir]
    $guid  = Get-StableGuid ("WinForge\" + $row.Rel.ToUpperInvariant())
    $src   = 'publish\' + $row.Rel
    [void]$sb.AppendLine("      <Component Id=""$id"" Directory=""$dirId"" Guid=""{$guid}"">")
    [void]$sb.AppendLine("        <File Id=""$fileId"" Source=""$(Escape-Xml $src)"" KeyPath=""yes"" />")
    [void]$sb.AppendLine('      </Component>')
}

[void]$sb.AppendLine('    </ComponentGroup>')
[void]$sb.AppendLine('  </Fragment>')
[void]$sb.AppendLine('</Wix>')

# UTF-8 con BOM: WiX lo lee sin problemas y evita dudas con acentos de los nombres.
[System.IO.File]::WriteAllText($out, $sb.ToString(), (New-Object System.Text.UTF8Encoding($true)))

Write-Host "Files.wxs generado: $($fileRows.Count) archivos · $($relDirs.Count - 1) carpetas"
