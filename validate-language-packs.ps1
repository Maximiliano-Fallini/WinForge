# validate-language-packs.ps1 — Comprueba los packs y el manifest generados. Temporal.
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$manifestPath = Join-Path $root "languages.json"
$distDir = Join-Path $root "languages-dist"
$sourcePath = Join-Path $root "src\WHPO.UI\Translations.cs"

$rx = [regex]'^\s*\["(?<es>(?:[^"\\]|\\.)*)"\]\s*=\s*\("(?<en>(?:[^"\\]|\\.)*)",\s*"(?<pt>(?:[^"\\]|\\.)*)",\s*"(?<de>(?:[^"\\]|\\.)*)",\s*"(?<fr>(?:[^"\\]|\\.)*)"\),?\s*$'

function Unescape([string]$s) { return [regex]::Replace($s, '\\(.)', { param($m) switch ($m.Groups[1].Value) { '"' { '"' } '\' { '\' } 'n' { "`n" } 't' { "`t" } 'r' { "`r" } default { $m.Value } } }) }

# Los packs se leen con JavaScriptSerializer y NO con ConvertFrom-Json: este ultimo arma
# un diccionario case-insensitive y revienta si el pack trae dos claves que solo difieren
# en mayusculas (p.ej. "Prioridad de CPU" y "prioridad de CPU"), que son claves
# legitimas y distintas. La app usa System.Text.Json, que si distingue mayusculas.
Add-Type -AssemblyName System.Web.Extensions
$js = New-Object System.Web.Script.Serialization.JavaScriptSerializer

# OrderedDictionary con comparador Ordinal: la tabla de C# es un Dictionary<string,...>,
# que distingue mayusculas, asi que aca hay que contar exactamente lo mismo que la app.
$byKey = New-Object System.Collections.Specialized.OrderedDictionary -ArgumentList ([System.StringComparer]::Ordinal)
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
    $doc = $js.DeserializeObject([System.IO.File]::ReadAllText($file))
    $packKeys = @($doc["strings"].Keys)
    $packSet = New-Object System.Collections.Generic.HashSet[string]
    foreach ($k in $packKeys) { [void]$packSet.Add($k) }
    $missing = @($byKey.Keys | Where-Object { -not $packSet.Contains($_) })
    $extra = @($packKeys | Where-Object { -not $byKey.Contains($_) })
    # Cobertura real: las traducciones salen de languages-src/<code>.json (el flujo del
    # repo). Solo si no existe ese archivo se usa la columna embebida pt/de/fr de la tabla,
    # que no cubre idiomas como zh-CN o ru-RU (de ahi que antes marcaran 0%).
    $sourceFile = Join-Path $root ("languages-src\{0}.json" -f $l.code)
    $sourceStrings = $null
    $origin = "Translations.cs (columna " + $l.code.Substring(0, 2) + ")"
    if (Test-Path $sourceFile) {
        $sourceStrings = $js.DeserializeObject([System.IO.File]::ReadAllText($sourceFile))["strings"]
        $origin = "languages-src/$($l.code).json"
    }
    $translated = 0
    $emptyKeys = @()
    foreach ($k in $byKey.Keys) {
        if ($sourceStrings) { $v = [string]$sourceStrings[$k] } else { $v = $byKey[$k].($l.code.Substring(0, 2)) }
        if ([string]::IsNullOrWhiteSpace($v)) { $emptyKeys += $k } else { $translated++ }
    }
    $pct = [math]::Floor(100 * $translated / $byKey.Count)
    Write-Output ("   {0}: strings={1} faltantes={2} sobrantes={3} traducidas={4}/{5} ({6}% real vs {7}% del manifest) origen={8}" -f `
        $l.code, $packKeys.Count, $missing.Count, $extra.Count, $translated, $byKey.Count, $pct, $doc["completion"], $origin)
    foreach ($k in ($emptyKeys | Select-Object -First 5)) { Write-Output "      SIN TRADUCIR '$k'" }
    # Aviso: claves que solo difieren en mayusculas. La app las resuelve bien, pero son
    # una trampa para cualquier lector case-insensitive (el propio validador, antes).
    $collide = @($packKeys | Group-Object { $_.ToLowerInvariant() } | Where-Object { $_.Count -gt 1 })
    if ($collide.Count -gt 0) {
        Write-Output ("      AVISO: {0} grupo(s) de claves que solo difieren en mayusculas: {1}" -f `
            $collide.Count, (($collide | ForEach-Object { $_.Group -join ' / ' }) -join ' | '))
    }
    foreach ($k in ($missing | Select-Object -First 5)) {
        $e = $byKey[$k]
        Write-Output ("      FALTA '{0}'  [pt='{1}' de='{2}' fr='{3}']" -f $e.raw, $e.pt, $e.de, $e.fr)
    }
    foreach ($k in ($extra | Select-Object -First 5)) { Write-Output "      SOBRA '$k'" }
}

Write-Output ""
Write-Output "== Manifest"
foreach ($l in $manifest.languages) {
    $url = if ($l.url) { $l.url } else { "-" }
    Write-Output ("   {0,-6} builtin={1,-5} v{2,-6} completion={3,-4} bytes={4,-8} url={5}" -f $l.code, $l.builtin, $l.version, $l.completion, $l.sizeBytes, $url)
}
