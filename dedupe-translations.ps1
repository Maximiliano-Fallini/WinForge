# dedupe-translations.ps1 — Quita de Translations.cs la copia MUERTA de cada clave repetida.
#
# En C# el diccionario se llena con el indexer (["clave"] = valor), así que cuando una
# clave aparece dos veces gana la ÚLTIMA y la primera nunca se usa. Este script borra
# esas primeras copias: el runtime queda idéntico y desaparecen las entradas muertas.
#
# OJO: las claves se comparan con StringComparer.Ordinal (case-SENSITIVE), igual que el
# Dictionary de C#. Con un hashtable de PowerShell, "Prioridad de CPU" y "prioridad de
# CPU" —que son dos claves reales y en uso— se pisarían y el script borraría una.
#
# Es idempotente: si ya no hay duplicadas, no toca el archivo.
$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$path = Join-Path $root "src\WHPO.UI\Translations.cs"
$raw = [System.IO.File]::ReadAllText($path)
$bytes = [System.IO.File]::ReadAllBytes($path)
$hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
$hadTrailingNewline = $raw.EndsWith("`n")
$lines = [System.IO.File]::ReadAllLines($path)

$rx = [regex]'^\s*\["(?<es>(?:[^"\\]|\\.)*)"\]\s*=\s*\('

$indexes = New-Object 'System.Collections.Generic.Dictionary[string,System.Collections.Generic.List[int]]' ([System.StringComparer]::Ordinal)
for ($i = 0; $i -lt $lines.Length; $i++) {
    $m = $rx.Match($lines[$i])
    if (-not $m.Success) { continue }
    $key = $m.Groups["es"].Value
    if (-not $indexes.ContainsKey($key)) { $indexes[$key] = New-Object System.Collections.Generic.List[int] }
    $indexes[$key].Add($i)
}

$toRemove = New-Object System.Collections.Generic.HashSet[int]
$report = New-Object System.Collections.Generic.List[string]
foreach ($kv in $indexes.GetEnumerator()) {
    $key = $kv.Key
    $idx = $kv.Value
    if ($idx.Count -le 1) { continue }
    # Se conserva la ÚLTIMA (la que hoy usa la app) y se marcan las anteriores.
    for ($j = 0; $j -lt $idx.Count - 1; $j++) { [void]$toRemove.Add($idx[$j]) }
    $kept = $lines[$idx[$idx.Count - 1]]
    $differs = $false
    foreach ($j in 0..($idx.Count - 2)) { if ($lines[$idx[$j]] -ne $kept) { $differs = $true } }
    $borradas = @($idx[0..($idx.Count - 2)] | ForEach-Object { $_ + 1 })
    $report.Add(("  {0} -> borra linea(s) {1}; conserva {2}{3}" -f $key, ($borradas -join ", "), ($idx[$idx.Count - 1] + 1), $(if ($differs) { "  [LAS COPIAS DIFERIAN]" } else { "" })))
    if ($differs) {
        foreach ($j in 0..($idx.Count - 2)) { $report.Add("      borrada: " + $lines[$idx[$j]].Trim()) }
        $report.Add("      conserva: " + $kept.Trim())
    }
}

if ($toRemove.Count -eq 0) {
    Write-Output "Sin claves duplicadas: no se toca $path"
    return
}

Write-Output "== Duplicadas encontradas"
$report | ForEach-Object { Write-Output $_ }

$keptLines = New-Object System.Collections.Generic.List[string]
for ($i = 0; $i -lt $lines.Length; $i++) { if (-not $toRemove.Contains($i)) { $keptLines.Add($lines[$i]) } }

$out = ($keptLines -join "`r`n")
if ($hadTrailingNewline) { $out += "`r`n" }
[System.IO.File]::WriteAllText($path, $out, (New-Object System.Text.UTF8Encoding($hasBom)))
Write-Output ""
Write-Output ("== Listo: {0} lineas -> {1} (borradas {2}) | BOM={3}" -f $lines.Length, $keptLines.Count, $toRemove.Count, $hasBom)

Remove-Item $PSCommandPath -Force
