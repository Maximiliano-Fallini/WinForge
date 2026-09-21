# validate-language-packs.ps1 — Comprueba los packs y el manifest generados. Temporal.
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$manifestPath = Join-Path $root "languages.json"
$distDir = Join-Path $root "languages-dist"
$sourcePath = Join-Path $root "src\WHPO.UI\Translations.cs"

$rx = [regex]'^\s*\["(?<es>(?:[^"\\]|\\.)*)"\]\s*=\s*\("(?<en>(?:[^"\\]|\\.)*)",\s*"(?<pt>(?:[^"\\]|\\.)*)",\s*"(?<de>(?:[^"\\]|\\.)*)",\s*"(?<fr>(?:[^"\\]|\\.)*)"\),?\s*$'

function Unescape([string]$s) { return [regex]::Replace($s, '\\(.)', { param($m) switch ($m.Groups[1].Value) { '"' { '"' } '\' { '\' } 'n' { "`n" } 't' { "`t" } 'r' { "`r" } default { $m.Value } } }) }

$byKey = [ordered]@{}
$dupeCount = 0
foreach ($line in [System.IO.File]::ReadAllLines($sourcePath)) {
    $m = $rx.Match($line)
    if (-not $m.Success) { continue }
    $es = Unescape $m.Groups["es"].Value
    if ($byKey.Contains($es)) { $dupeCount++ }
    $byKey[$es] = [pscustomobject]@{
        raw = $m.Groups["es"].Value
        es = $es
        pt = Unescape $m.Groups["pt"].Value
        de = Unescape $m.Groups["de"].Value
        fr = Unescape $m.Groups["fr"].Value
    }
}
Write-Output ("== Fuente: {0} lineas -> {1} claves unicas | repetidas (gana la ultima): {2}" -f ($dupeCount + $byKey.Count), $byKey.Count, $dupeCount)

Write-Output ""
Write-Output "== Packs vs fuente"
$manifest = [System.IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
foreach ($l in $manifest.languages) {
    if ($l.builtin) { continue }
    $file = Join-Path $distDir ("{0}-{1}.json" -f $l.code, $l.version)
    $doc = [System.IO.File]::ReadAllText($file) | ConvertFrom-Json
    $packKeys = @($doc.strings.PSObject.Properties.Name)
    $packSet = New-Object System.Collections.Generic.HashSet[string]
    foreach ($k in $packKeys) { [void]$packSet.Add($k) }
    $missing = @($byKey.Keys | Where-Object { -not $packSet.Contains($_) })
    $extra = @($packKeys | Where-Object { -not $byKey.Contains($_) })
    $translated = 0
    foreach ($k in $byKey.Keys) {
        $v = $byKey[$k].($l.code.Substring(0, 2))
        if (-not [string]::IsNullOrWhiteSpace($v)) { $translated++ }
    }
    $pct = [math]::Floor(100 * $translated / $byKey.Count)
    Write-Output ("   {0}: strings={1} faltantes={2} sobrantes={3} traducidas={4}/{5} ({6}% real vs {7}% del manifest)" -f `
        $l.code, $packKeys.Count, $missing.Count, $extra.Count, $translated, $byKey.Count, $pct, $doc.completion)
    foreach ($k in ($missing | Select-Object -First 5)) {
        $e = $byKey[$k]
        Write-Output ("      FALTA '{0}'  [pt='{1}' de='{2}' fr='{3}']" -f $e.raw, $e.pt, $e.de, $e.fr)
    }
    foreach ($k in ($extra | Select-Object -First 5)) { Write-Output "      SOBRA '$k'" }
}

Write-Output ""
Write-Output "== Manifest"
foreach ($l in $manifest.languages) {
    Write-Output ("   {0,-6} builtin={1,-5} v{2,-6} completion={3,-4} bytes={4,-8} url={5}" -f $l.code, $l.builtin, $l.version, $l.completion, $l.sizeBytes, (if ($l.url) { $l.url } else { "-" }))
}
