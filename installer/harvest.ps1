# Genera installer/Files.wxs (fragmento WiX) recorriendo installer/publish
# Los GUIDs de componentes son determinísticos (MD5 de la ruta relativa) para que
# las actualizaciones (major upgrade) mantengan los mismos componentes.

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot\publish").Path
$out  = Join-Path $PSScriptRoot 'Files.wxs'

function Get-DeterministicGuid([string]$s) {
    $md5 = [System.Security.Cryptography.MD5]::Create()
    $hash = $md5.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($s))
    $b = [byte[]]($hash[0..15])
    # versión 5 (namespace) + variante RFC 4122 para GUIDs válidos
    $b[7]  = ($b[7] -band 0x0f) -bor 0x50
    $b[8]  = ($b[8] -band 0x3f) -bor 0x80
    return ([guid]::new($b)).ToString('B').ToUpper()
}

function Get-Sanitized([string]$s) {
    $chars = $s.ToCharArray() | ForEach-Object {
        if ($_ -match '[A-Za-z0-9]') { $_ } else { '_' }
    }
    return (-join $chars).Trim('_')
}

# Rutas relativas de TODOS los archivos (posix, ordenadas)
$files = Get-ChildItem -Path $root -Recurse -File | ForEach-Object {
    $_.FullName.Substring($root.Length + 1) -replace '\\', '/'
} | Sort-Object

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('<?xml version="1.0" encoding="UTF-8"?>')
[void]$sb.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
[void]$sb.AppendLine('  <Fragment>')
[void]$sb.AppendLine('    <StandardDirectory Id="ProgramFiles64Folder">')
[void]$sb.AppendLine('      <Directory Id="INSTALLFOLDER" Name="WinForge">')

$allComponents = New-Object System.Collections.Generic.List[string]
$stack = New-Object System.Collections.Generic.List[string]  # directorios abiertos (nombres)

function Close-To([int]$depth) {
    while ($script:stack.Count -gt $depth) {
        $script:stack.RemoveAt($script:stack.Count - 1)
        $indent = '        ' + ('  ' * $script:stack.Count)
        [void]$script:sb.AppendLine("$indent</Directory>")
    }
}

foreach ($posix in $files) {
    $dir   = Split-Path $posix -Parent
    $leaf  = Split-Path $posix -Leaf
    $segs = @()
    if ($dir) { $segs = @($dir -split '/') }

    # Abrir directorios que falten (prefijo común)
    $common = 0
    while ($common -lt $segs.Count -and $common -lt $stack.Count -and $stack[$common] -eq $segs[$common]) { $common++ }
    Close-To $common
    for ($i = $common; $i -lt $segs.Count; $i++) {
        $segPath = ($segs[0..$i] -join '/')
        $dirId = 'D_' + (Get-Sanitized $segPath)
        $indent = '        ' + ('  ' * ($stack.Count + 1))
        [void]$sb.AppendLine("$indent<Directory Id=`"$dirId`" Name=`"$($segs[$i])`">")
        $stack.Add($segs[$i])
    }

    # Componente con el archivo
    $cmpId = 'C_' + (Get-Sanitized $posix)
    $filId = 'F_' + (Get-Sanitized $posix)
    $guid  = Get-DeterministicGuid "file:$posix"
    $src   = [System.Security.SecurityElement]::Escape(($posix -replace '/', '\'))
    $indent = '        ' + ('  ' * ($stack.Count + 1))
    [void]$sb.AppendLine("$indent<Component Id=`"$cmpId`" Guid=`"$guid`">")
    [void]$sb.AppendLine("$indent  <File Id=`"$filId`" Source=`"publish\$src`" KeyPath=`"yes`" />")
    [void]$sb.AppendLine("$indent</Component>")
    $allComponents.Add($cmpId)
}

Close-To 0

[void]$sb.AppendLine('      </Directory>')
[void]$sb.AppendLine('    </StandardDirectory>')
[void]$sb.AppendLine('  </Fragment>')
[void]$sb.AppendLine('  <Fragment>')
[void]$sb.AppendLine('    <ComponentGroup Id="AppFiles">')
foreach ($c in $allComponents) {
    [void]$sb.AppendLine("      <ComponentRef Id=`"$c`" />")
}
[void]$sb.AppendLine('    </ComponentGroup>')
[void]$sb.AppendLine('  </Fragment>')
[void]$sb.AppendLine('</Wix>')

$sb.ToString() | Set-Content -Path $out -Encoding UTF8
Write-Host "Generado: $out ($($allComponents.Count) componentes)"
