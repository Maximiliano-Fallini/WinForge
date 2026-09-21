# build-language-packs.ps1 — Publica los paquetes de idioma de WinForge.
#
# Uso:
#   ./build-language-packs.ps1                                  # arma TODOS los packs publicables (v1.0.0)
#   ./build-language-packs.ps1 -Language pt-BR -Version 1.0.1   # uno solo, con otra versión
#   ./build-language-packs.ps1 -Verify                          # solo audita: fuente vs manifest vs packs
#   ./build-language-packs.ps1 -ExportSources                   # escribe languages-src/<code>.json (para traducir)
#   ./build-language-packs.ps1 -Upload                          # sube los packs a la Release "languages"
#
# Flujo que automatiza:
#   1. Lee las traducciones y arma <code>-<version>.json en languages-dist/ (carpeta ignorada por git).
#   2. Calcula SHA-256, tamaño y completitud de cada pack.
#   3. Actualiza languages.json (manifest que la app lee desde raw.githubusercontent.com).
#   4. Con -Upload: sube los packs como assets de la Release "languages" con gh CLI
#      (requiere GitHub CLI logueado: gh auth login).
#
# De dónde sale cada idioma (en este orden):
#   1. languages-src/<code>.json  → fuente de traducción editable. Es el flujo pensado
#      para agregar un idioma NUEVO sin tocar C#: se genera la plantilla con
#      -ExportSources, se completa y se manda por PR.
#   2. La tabla de src/WHPO.UI/Translations.cs: tiene UN solo idioma embebido,
#      el inglés (el español no es una columna: sus claves SON el texto fuente).
#      Cualquier otro idioma (pt-BR, de-DE, fr-FR y los que vengan) ya no vive en
#      C#: se construye desde languages-src/<code>.json.
#
# Formatos (los dos JSON son UTF-8 sin BOM):
#   Pack:     { schema, code, version, sourceHash, keyCount, completion, strings{ "<texto ES>": "<traducción>" } }
#   Manifest: { catalogVersion, sourceHash, generatedAt, languages[ { code, endonym, englishName,
#               builtin, version, url, sha256, sizeBytes, keyCount, completion, minAppVersion } ] }
#
# CONTRATO DEL sourceHash (lo recalcula la app con su propia tabla): SHA-256 en UTF-8
# de las claves fuente en español ordenadas con StringComparer.Ordinal (NO por cultura)
# y unidas con "\n", sin salto final. Sirve para detectar un pack desactualizado (claves
# que cambiaron de texto en la app): si no coincide, la UI avisa "traducción desactualizada".

param(
    [string[]]$Language = @(),
    [string]$Version = "1.0.0",
    [string]$Repo = "Maximiliano-Fallini/WinForge",
    # OJO: tiene que coincidir con ProjectEndpoints (Owner/Name/Branch y la release de
    # contenido) del código: ahí vive la verdad para la app; acá, para la publicación.
    [string]$ReleaseTag = "languages",
    [string]$Endonym = "",
    [string]$EnglishName = "",
    [string]$MinAppVersion = "",
    [string]$Source = "",
    [string]$OutDir = "",
    [string]$Manifest = "",
    [switch]$Compact,
    [switch]$ExportSources,
    [switch]$Verify,
    [switch]$Upload
)

$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
if (-not $Source) { $Source = Join-Path $root "src\WHPO.UI\Translations.cs" }
if (-not $OutDir) { $OutDir = Join-Path $root "languages-dist" }
if (-not $Manifest) { $Manifest = Join-Path $root "languages.json" }
$sourceDir = Join-Path $root "languages-src"

# ---------------------------------------------------------------------------
# Idiomas conocidos: endónimo (como lo ve el usuario), nombre en inglés y si la app
# lo trae embebido (builtin: no se descarga).
#
# 'column' solo la tienen los idiomas que viven en la tabla embebida: es-AR (identidad:
# sus claves son el texto fuente) y en-US (la única columna traducida). pt-BR/de-DE/
# fr-FR se arman desde languages-src/<code>.json; si ese archivo falta, el script
# avisa en vez de publicar un pack vacío.
#
# Un idioma que no esté acá se puede agregar con -Endonym (idioma nuevo por pack).
# ---------------------------------------------------------------------------
$Known = [ordered]@{
    "es-AR" = @{ endonym = "Español (Argentina)"; englishName = "Spanish (Argentina)"; column = "es"; builtin = $true }
    "en-US" = @{ endonym = "English (US)";        englishName = "English (US)";        column = "en"; builtin = $true }
    "pt-BR" = @{ endonym = "Português (Brasil)";  englishName = "Portuguese (Brazil)"; builtin = $false }
    "de-DE" = @{ endonym = "Deutsch";             englishName = "German";              builtin = $false }
    "fr-FR" = @{ endonym = "Français";            englishName = "French";              builtin = $false }
    "zh-CN" = @{ endonym = "简体中文";           englishName = "Chinese (Simplified)"; builtin = $false }
    "ru-RU" = @{ endonym = "Русский";             englishName = "Russian";             builtin = $false }
}

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

# Deshace los escapes que escribe ConvertTo-JsonText (\" \\ \n \r \t y \uXXXX).
function ConvertFrom-JsonText([string]$text) {
    $block = {
        param($m)
        if ($m.Groups[2].Success) {
            switch ($m.Groups[2].Value) {
                '"' { return '"' }
                '\' { return '\' }
                'n' { return "`n" }
                't' { return "`t" }
                'r' { return "`r" }
                'b' { return "`b" }
                'f' { return "`f" }
                default { return $m.Value }
            }
        }
        return [string][char][int][Convert]::ToInt32($m.Groups[1].Value, 16)
    }
    return [regex]::Replace($text, '\\u([0-9a-fA-F]{4})|\\(.)', $block)
}

# Lee languages-src/<code>.json (el formato que escribe -ExportSources) y devuelve
# el diccionario clave->traduccion. NO usa ConvertFrom-Json a proposito: el parser de
# PowerShell 5.1 rechaza un objeto con dos claves que difieren solo en mayusculas
# ("Prioridad de CPU" y "prioridad de CPU" conviven en la tabla), mientras que
# System.Text.Json (la app) y este lector linea por linea las tratan como distintas.
# Si algun dia cambia el formato del archivo, esto hay que ajustarlo junto con
# -ExportSources (que es quien lo escribe).
function Read-TranslationSource([string]$path) {
    $lookup = New-Object 'System.Collections.Generic.Dictionary[string,string]' ([System.StringComparer]::Ordinal)
    $inStrings = $false
    $rx = [regex]'^\s{4}"((?:[^"\\]|\\.)*)"\s*:\s*"((?:[^"\\]|\\.)*)"\s*,?\s*$'
    foreach ($line in [System.IO.File]::ReadAllLines($path)) {
        if (-not $inStrings) {
            if ($line -match '^\s*"strings"\s*:') { $inStrings = $true }
            continue
        }
        if ($line -match '^\s*\}') { break }
        $m = $rx.Match($line)
        if (-not $m.Success) { continue }
        $lookup[(ConvertFrom-JsonText $m.Groups[1].Value)] = ConvertFrom-JsonText $m.Groups[2].Value
    }
    return $lookup
}

# Deshace los escapes de un literal de C# (Translations.cs usa \" para comillas
# dentro del texto). Se hace con un scriptblock en vez de -replace para no pelear
# con los escapes de PowerShell; un escape desconocido se deja tal cual.
function ConvertFrom-CsLiteral([string]$text) {
    return [regex]::Replace($text, '\\(.)', {
        param($m)
        switch ($m.Groups[1].Value) {
            '"' { '"' }
            '\' { '\' }
            'n' { "`n" }
            't' { "`t" }
            'r' { "`r" }
            default { $m.Value }
        }
    })
}

# Serializa un string a JSON a mano: mantiene los acentos tal cual (nada de \u00e9),
# que es lo que hace legible y diffeable el pack.
function ConvertTo-JsonText([string]$text) {
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append('"')
    foreach ($ch in $text.ToCharArray()) {
        switch ($ch) {
            '"'  { [void]$sb.Append('\"') }
            '\'  { [void]$sb.Append('\\') }
            "`n" { [void]$sb.Append('\n') }
            "`r" { [void]$sb.Append('\r') }
            "`t" { [void]$sb.Append('\t') }
            default {
                if ([int]$ch -lt 32) { [void]$sb.Append(('\u{0:x4}' -f [int]$ch)) }
                else { [void]$sb.Append($ch) }
            }
        }
    }
    [void]$sb.Append('"')
    return $sb.ToString()
}

function Get-Sha256Text([string]$text) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($text)
        return ([System.BitConverter]::ToString($sha.ComputeHash($bytes)) -replace '-', '').ToLowerInvariant()
    }
    finally { $sha.Dispose() }
}

function Write-JsonFile([string]$path, [System.Collections.Generic.List[string]]$lines) {
    $dir = Split-Path $path -Parent
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    # UTF-8 SIN BOM: el manifiesto se commitea y lo lee System.Text.Json.
    [System.IO.File]::WriteAllText($path, ($lines -join "`r`n"), (New-Object System.Text.UTF8Encoding($false)))
}

# ---------------------------------------------------------------------------
# 1) Leer la tabla fuente (claves en español + una columna por idioma)
# ---------------------------------------------------------------------------

if (-not (Test-Path $Source)) { throw "No existe la tabla de traducciones: $Source" }

Write-Output "== Leyendo $Source"
# Formato actual de la tabla: una sola columna traducida (inglés).
$entryRx = [regex]'^\s*\["(?<es>(?:[^"\\]|\\.)*)"\]\s*=\s*"(?<en>(?:[^"\\]|\\.)*)",?\s*$'
# Formato viejo (es,en,pt,de,fr): se sigue aceptando para poder volver atrás sin
# romper el script. Si una línea entra por acá, las columnas pt/de/fr se ignoran
# igual: las traducciones salen de languages-src/<code>.json.
$legacyRx = [regex]'^\s*\["(?<es>(?:[^"\\]|\\.)*)"\]\s*=\s*\("(?<en>(?:[^"\\]|\\.)*)",\s*"(?<pt>(?:[^"\\]|\\.)*)",\s*"(?<de>(?:[^"\\]|\\.)*)",\s*"(?<fr>(?:[^"\\]|\\.)*)"\),?\s*$'
$entries = New-Object System.Collections.Generic.List[object]
$dupes = New-Object System.Collections.Generic.List[string]
$seen = New-Object System.Collections.Generic.HashSet[string]

foreach ($line in [System.IO.File]::ReadAllLines($Source)) {
    $m = $entryRx.Match($line)
    if (-not $m.Success) { $m = $legacyRx.Match($line) }
    if (-not $m.Success) { continue }
    $es = ConvertFrom-CsLiteral $m.Groups["es"].Value
    if (-not $seen.Add($es)) {
        # Clave repetida en la tabla: gana la ÚLTIMA, igual que el diccionario de C#
        # (el indexer sobreescribe y deja el valor de la última entrada). Si no se
        # replicara eso, el pack traduciría distinto que la app embebida.
        $dupes.Add($es)
        $prev = @($entries | Where-Object { $_.es -eq $es })
        if ($prev.Count -gt 0) { $entries.Remove($prev[0]) }
    }
    # columns: columna -> texto traducido. Con la tabla de una sola columna solo
    # viene "en"; el resto de los grupos los llena el regex viejo, si aparece.
    $cols = @{}
    foreach ($g in @("en", "pt", "de", "fr")) {
        if ($m.Groups[$g].Success) { $cols[$g] = ConvertFrom-CsLiteral $m.Groups[$g].Value }
    }
    $entries.Add([pscustomobject]@{ es = $es; columns = $cols })
}

if ($entries.Count -eq 0) { throw "No se pudo parsear ninguna entrada de $Source" }
if ($dupes.Count -gt 0) { Write-Warning "Claves duplicadas en la tabla (gana la última, como en C#): $($dupes.Count)" }

# sourceHash: la receta que la app recalcula con su propia tabla (ver cabecera).
# El orden tiene que ser ORDINAL (StringComparer.Ordinal), igual que en C#: el
# Sort-Object de PowerShell ordena por cultura y daría un hash distinto.
$keysSorted = [string[]]@($entries | ForEach-Object { $_.es })
[Array]::Sort($keysSorted, [System.StringComparer]::Ordinal)
$sourceHash = Get-Sha256Text ($keysSorted -join "`n")
$keyCount = $entries.Count
Write-Output "Entradas: $keyCount | sourceHash: $($sourceHash.Substring(0, 12))…"

# ---------------------------------------------------------------------------
# 2) Resolver idiomas a construir
# ---------------------------------------------------------------------------

if ($Language.Count -eq 0) {
    $targets = @($Known.Keys | Where-Object { -not $Known[$_].builtin })
} else {
    $targets = $Language
}

foreach ($code in $targets) {
    if (-not $Known.Contains($code) -and -not $Endonym) {
        throw "Idioma desconocido: $code. Pasá -Endonym 'Русский' (y -EnglishName) para agregarlo, y creá languages-src/$code.json con las traducciones."
    }
}

# Plantilla de traducción: claves en español listas para completar (idioma nuevo).
function New-TranslationTemplate([string]$code) {
    $strings = [ordered]@{}
    foreach ($e in $entries) { $strings[$e.es] = "" }
    return $strings
}

# Traducciones de un idioma: primero languages-src/<code>.json, después la columna.
function Get-TranslationMap([string]$code) {
    $srcFile = Join-Path $sourceDir "$code.json"
    # Diccionario ORDINAL (case-sensitive): igual que el Dictionary de C#. Con un
    # hashtable de PowerShell "Tipo" y "tipo" se pisarían y el pack perdería claves.
    $lookup = New-Object 'System.Collections.Generic.Dictionary[string,string]' ([System.StringComparer]::Ordinal)
    if (Test-Path $srcFile) {
        # OJO: sin ConvertFrom-Json (ver Read-TranslationSource).
        $lookup = Read-TranslationSource $srcFile
        return @{ lookup = $lookup; from = "languages-src/$code.json" }
    }
    $column = $null
    if ($Known.Contains($code)) { $column = $Known[$code].column }
    # La tabla embebida trae es-AR (fuente) y la columna del inglés, nada más: para
    # cualquier otro idioma hace falta su languages-src/<code>.json. Sin este chequeo
    # un pt-BR sin archivo saldría como un pack con TODAS las claves vacías (y se
    # publicaría igual).
    $withColumn = @($entries | Where-Object { $column -and $_.columns.ContainsKey($column) })
    if ($withColumn.Count -eq 0) {
        throw "No hay languages-src/$code.json y la tabla embebida no tiene la columna $column para $code. La app solo trae es-AR y en-US en C#: los demas idiomas se traducen en languages-src/<code>.json (genera la plantilla con -ExportSources)."  # ASCII: lo imprime PS 5.1
    }
    foreach ($e in $entries) {
        $value = ""
        if ($e.columns.ContainsKey($column)) { $value = [string]$e.columns[$column] }
        $lookup[$e.es] = $value
    }
    return @{ lookup = $lookup; from = "Translations.cs (columna $column)" }
}

# ---------------------------------------------------------------------------
# 3) -ExportSources: volcar la tabla a languages-src/<code>.json
# ---------------------------------------------------------------------------

if ($ExportSources) {
    foreach ($code in $targets) {
        if ($Known.Contains($code)) {
            $resolved = Get-TranslationMap $code
        } else {
            $lookup = New-Object 'System.Collections.Generic.Dictionary[string,string]' ([System.StringComparer]::Ordinal)
            foreach ($e in $entries) { $lookup[$e.es] = "" }
            $resolved = @{ lookup = $lookup; from = "plantilla vacía" }
        }
        $lines = New-Object System.Collections.Generic.List[string]
        $lines.Add("{")
        $lines.Add('  "code": ' + (ConvertTo-JsonText $code) + ',')
        $lines.Add('  "note": ' + (ConvertTo-JsonText "Fuente de traducción: la clave es el texto en español de la app, traducí el valor. Una clave vacía cae al inglés.") + ',')
        $lines.Add('  "strings": {')
        for ($i = 0; $i -lt $entries.Count; $i++) {
            $comma = if ($i -lt $entries.Count - 1) { "," } else { "" }
            $lines.Add("    " + (ConvertTo-JsonText $entries[$i].es) + ": " + (ConvertTo-JsonText $resolved.lookup[$entries[$i].es]) + $comma)
        }
        $lines.Add("  }")
        $lines.Add("}")
        $file = Join-Path $sourceDir "$code.json"
        Write-JsonFile $file $lines
        Write-Output "Fuente escrita: $file  (origen: $($resolved.from))"
    }
    if (-not $Verify) { return }
}

# ---------------------------------------------------------------------------
# 4) Construir los packs
# ---------------------------------------------------------------------------

$packInfo = [ordered]@{}
foreach ($code in $targets) {
    $resolved = Get-TranslationMap $code
    $lookup = $resolved.lookup

    # Pares "clave": "valor" ya escapados, en el orden de la tabla (así el pack se
    # diffea clave por clave entre versiones). Solo viaja lo traducido: lo vacío cae
    # al fallback de la app (en-US → español).
    # Se recorre $entries (el orden y las claves de la tabla) y no el mapa: así el
    # pack lleva exactamente las mismas claves que el diccionario embebido, en el
    # mismo orden, y queda diffeable contra la tabla.
    $pairs = New-Object System.Collections.Generic.List[string]
    foreach ($e in $entries) {
        $value = $lookup[$e.es]
        if ([string]::IsNullOrWhiteSpace($value)) { continue }
        $pairs.Add((ConvertTo-JsonText $e.es) + ": " + (ConvertTo-JsonText $value))
    }
    $translated = $pairs.Count
    # Floor, no Round: 1599/1600 no es "100%".
    $completion = [int][math]::Floor(100 * $translated / $keyCount)

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("{")
    $lines.Add('  "schema": 1,')
    $lines.Add('  "code": ' + (ConvertTo-JsonText $code) + ',')
    $lines.Add('  "version": ' + (ConvertTo-JsonText $Version) + ',')
    $lines.Add('  "sourceHash": ' + (ConvertTo-JsonText $sourceHash) + ',')
    $lines.Add('  "keyCount": ' + $keyCount + ',')
    $lines.Add('  "completion": ' + $completion + ',')
    $lines.Add('  "strings": {')
    if ($Compact) {
        # Una sola línea: pack más chico para publicar, pero sin diff legible.
        $lines.Add("    " + ($pairs -join ", "))
    } else {
        for ($i = 0; $i -lt $pairs.Count; $i++) {
            $comma = if ($i -lt $pairs.Count - 1) { "," } else { "" }
            $lines.Add("    " + $pairs[$i] + $comma)
        }
    }
    $lines.Add("  }")
    $lines.Add("}")

    # Guardarraíl: paridad de claves contra la tabla. Con una fuente hecha a mano
    # (languages-src/<code>.json) es fácil olvidarse claves o arrastrar claves
    # viejas, y sin esto el usuario vería inglés sin que nadie se entere.
    $untranslated = @($entries | Where-Object { [string]::IsNullOrWhiteSpace($lookup[$_.es]) })
    if ($untranslated.Count -gt 0) {
        Write-Warning ("{0}: {1} claves sin traducir (caen al inglés). Ej.: {2}" -f `
            $code, $untranslated.Count, (($untranslated | Select-Object -First 3 | ForEach-Object { $_.es }) -join " | "))
    }
    $orphans = 0
    foreach ($k in $lookup.Keys) { if (-not $seen.Contains($k)) { $orphans++ } }
    if ($orphans -gt 0) {
        Write-Warning ("{0}: {1} claves de la fuente ya no existen en la tabla (se descartan)." -f $code, $orphans)
    }

    $fileName = "$code-$Version.json"
    $file = Join-Path $OutDir $fileName
    Write-JsonFile $file $lines

    $hash = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
    $size = (Get-Item $file).Length
    $packInfo[$code] = @{
        fileName = $fileName; sha256 = $hash; sizeBytes = $size
        keyCount = $keyCount; completion = $completion
    }
    Write-Output ("Pack {0}: {1}/{2} claves ({3}%), {4} KB -> {5}" -f $code, $translated, $keyCount, $completion, [math]::Round($size / 1KB, 1), $fileName)
}

# ---------------------------------------------------------------------------
# 5) Manifest (merge: no pisa lo de idiomas que no se regeneraron)
# ---------------------------------------------------------------------------

$catalog = if (Test-Path $Manifest) { [System.IO.File]::ReadAllText($Manifest) | ConvertFrom-Json } else { $null }
$previous = @{}
if ($catalog -and $catalog.languages) {
    foreach ($l in $catalog.languages) { $previous[$l.code] = $l }
    if ($catalog.sourceHash -and $catalog.sourceHash -ne $sourceHash) {
        Write-Output ""
        Write-Output "AVISO: el texto fuente en español cambió desde el último manifest."
        Write-Output "       Los packs publicados quedan con claves viejas: subí la versión y republicá."
    }
}

$languageEntries = New-Object System.Collections.Generic.List[object]
# Embebidos primero (es y en), así el orden del manifest es estable.
$orderedCodes = @($Known.Keys) + @($targets | Where-Object { -not $Known.Contains($_) })
foreach ($code in $orderedCodes) {
    if ($languageEntries | Where-Object { $_.code -eq $code }) { continue }
    if ($Known.Contains($code)) {
        $meta = $Known[$code]
    } else {
        $metaEng = if ($EnglishName) { $EnglishName } else { $code }
        $meta = @{ endonym = $Endonym; englishName = $metaEng; builtin = $false }
    }

    if ($packInfo.Contains($code)) {
        $info = $packInfo[$code]
        $languageEntries.Add([pscustomobject]@{
            code          = $code
            endonym       = $meta.endonym
            englishName   = $meta.englishName
            builtin       = $false
            version       = $Version
            url           = "https://github.com/$Repo/releases/download/$ReleaseTag/$($info.fileName)"
            sha256        = $info.sha256
            sizeBytes     = $info.sizeBytes
            keyCount      = $info.keyCount
            completion    = $info.completion
            minAppVersion = $MinAppVersion
        })
    } elseif ($meta.builtin) {
        # Embebido (sin URL ni sha256): el keyCount sale de la tabla ACTUAL, así que se
        # recalcula siempre en vez de arrastrar el número que tenía el manifest desde la
        # última vez que se lo escribió. Por eso va ANTES de la rama que conserva lo ya
        # publicado, que si no lo tapaba.
        $languageEntries.Add([pscustomobject]@{
            code = $code; endonym = $meta.endonym; englishName = $meta.englishName
            builtin = $true; version = ""; url = ""; sha256 = ""; sizeBytes = 0
            keyCount = $keyCount; completion = 100; minAppVersion = ""
        })
    } elseif ($previous.ContainsKey($code)) {
        $languageEntries.Add($previous[$code])   # se conserva lo ya publicado
    }
}

$manifestLines = New-Object System.Collections.Generic.List[string]
$manifestLines.Add("{")
$manifestLines.Add('  "catalogVersion": 1,')
$manifestLines.Add('  "sourceHash": ' + (ConvertTo-JsonText $sourceHash) + ',')
$manifestLines.Add('  "generatedAt": ' + (ConvertTo-JsonText (Get-Date -Format "yyyy-MM-dd")) + ',')
$manifestLines.Add('  "languages": [')
for ($i = 0; $i -lt $languageEntries.Count; $i++) {
    $l = $languageEntries[$i]
    $comma = if ($i -lt $languageEntries.Count - 1) { "," } else { "" }
    $manifestLines.Add("    {")
    $manifestLines.Add('      "code": ' + (ConvertTo-JsonText $l.code) + ',')
    $manifestLines.Add('      "endonym": ' + (ConvertTo-JsonText $l.endonym) + ',')
    $manifestLines.Add('      "englishName": ' + (ConvertTo-JsonText $l.englishName) + ',')
    $manifestLines.Add('      "builtin": ' + ($l.builtin.ToString().ToLowerInvariant()) + ',')
    $manifestLines.Add('      "version": ' + (ConvertTo-JsonText $l.version) + ',')
    $manifestLines.Add('      "url": ' + (ConvertTo-JsonText $l.url) + ',')
    $manifestLines.Add('      "sha256": ' + (ConvertTo-JsonText $l.sha256) + ',')
    $manifestLines.Add('      "sizeBytes": ' + $l.sizeBytes + ',')
    $manifestLines.Add('      "keyCount": ' + $l.keyCount + ',')
    $manifestLines.Add('      "completion": ' + $l.completion + ',')
    $manifestLines.Add('      "minAppVersion": ' + (ConvertTo-JsonText $l.minAppVersion))
    $manifestLines.Add('    }' + $comma)
}
$manifestLines.Add("  ]")
$manifestLines.Add("}")
Write-JsonFile $Manifest $manifestLines
Write-Output ""
Write-Output "== Manifest actualizado: $Manifest ($($languageEntries.Count) idiomas)"

# ---------------------------------------------------------------------------
# 6) -Verify: audita claves huérfanas / faltantes contra el pack anterior
# ---------------------------------------------------------------------------

if ($Verify) {
    Write-Output ""
    Write-Output "== Auditoría de claves"
    $sourceSet = New-Object System.Collections.Generic.HashSet[string]
    foreach ($e in $entries) { [void]$sourceSet.Add($e.es) }
    $found = 0
    foreach ($code in $targets) {
        $prevFiles = @(Get-ChildItem $OutDir -Filter "$code-*.json" -ErrorAction SilentlyContinue)
        $prevFiles = @($prevFiles | Where-Object { $_.Name -ne "$code-$Version.json" } | Sort-Object LastWriteTime -Descending)
        if ($prevFiles.Count -eq 0) {
            Write-Output "  $code : sin pack anterior para comparar"
            continue
        }
        # Mismo lector que las fuentes: ConvertFrom-Json de PS 5.1 no acepta un pack
        # con claves que difieren solo en mayúsculas ("Prioridad de CPU"/"prioridad de CPU").
        $oldKeys = @((Read-TranslationSource $prevFiles[0].FullName).Keys)
        $orphans = @($oldKeys | Where-Object { -not $sourceSet.Contains($_) })
        $missing = @($entries | Where-Object { -not ($oldKeys -contains $_.es) })
        $found++
        Write-Output ("  {0} vs {1}: huérfanas {2} | faltantes {3}" -f $code, $prevFiles[0].Name, $orphans.Count, $missing.Count)
        foreach ($o in ($orphans | Select-Object -First 5)) { Write-Output "      huérfana: $o" }
        foreach ($mi in ($missing | Select-Object -First 5)) { Write-Output "      faltante: $($mi.es)" }
    }
    if ($found -eq 0) { Write-Output "  (nada para comparar: es la primera publicación)" }
    Write-Output "  Si hay huérfanas, el pack saliente las arrastra sin uso; el nuevo ya las descarta."
    return
}

# ---------------------------------------------------------------------------
# 7) -Upload: subir los packs a la Release de idiomas
# ---------------------------------------------------------------------------

if ($Upload) {
    Write-Output ""
    Write-Output "== Subiendo packs a la Release '$ReleaseTag' con gh CLI"
    $files = @($packInfo.Keys | ForEach-Object { Join-Path $OutDir $packInfo[$_].fileName })
    # OJO: con ErrorActionPreference = 'Stop', PS 5.1 convierte en error
    # TERMINANTE la salida de error de un comando nativo. El "release not found"
    # que gh escribe por stderr abortaba este script antes de crear la release:
    # por eso el chequeo corre con la preferencia en Continue y sin salida.
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    gh release view $ReleaseTag -R $Repo *> $null
    $releaseExists = ($LASTEXITCODE -eq 0)
    $ErrorActionPreference = $prevEap
    if (-not $releaseExists) {
        gh release create $ReleaseTag -R $Repo --title "Paquetes de idioma" --notes "Assets de los paquetes de idioma de WinForge (los descarga la app desde languages.json)." @files
    } else {
        gh release upload $ReleaseTag -R $Repo --clobber @files
    }
    if ($LASTEXITCODE -ne 0) { throw "gh falló: revisá 'gh auth login'." }
    Write-Output "Assets publicados: la app los descarga desde el manifest."
}

Write-Output ""
Write-Output "Listo. Para que los usuarios lo vean: subí los packs a la Release (-Upload) y hacé commit de languages.json."
