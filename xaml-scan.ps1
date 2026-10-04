# Barrido XAML: literales Text=/Content=/Header= que no son claves de la tabla.
# Acentos escritos con escapes unicode para evitar problemas de encoding de consola.
$ErrorActionPreference = 'Stop'
$t = [System.IO.File]::ReadAllText((Resolve-Path 'src/WHPO.UI/Translations.cs'))
$tableKeys = New-Object System.Collections.Generic.HashSet[string]
[regex]::Matches($t, '\[\s*"((?:[^"\\]|\\.)*)"\s*\]\s*=') | ForEach-Object { [void]$tableKeys.Add($_.Groups[1].Value) }
$files = @(
  'src/WHPO.UI/Views/Pages/LimpiezaPage.xaml',
  'src/WHPO.UI/Views/Pages/FanControlPage.xaml',
  'src/WHPO.UI/Views/Pages/OverlayPage.xaml',
  'src/WHPO.UI/Views/Pages/NucleosPage.xaml',
  'src/WHPO.UI/Views/Pages/GestionarProcesosPage.xaml',
  'src/WHPO.UI/Views/Pages/OverclockUsbPage.xaml'
)
$hasLetter = [regex]'[A-Za-z]'
foreach ($f in $files) {
  if (-not (Test-Path $f)) { continue }
  $c = [System.IO.File]::ReadAllText((Resolve-Path $f))
  $ms = [regex]::Matches($c, '(?:Text|Content|Header|PlaceholderText)="([^"{}][^"]*)"')
  $miss = 0
  foreach ($m in $ms) {
    $v = $m.Groups[1].Value
    if (-not $hasLetter.IsMatch($v)) { continue }
    if (-not $tableKeys.Contains($v)) {
      if ($miss -eq 0) { Write-Output ('--- ' + (Split-Path $f -Leaf)) }
      $miss++
      $short = $v
      if ($short.Length -gt 80) { $short = $short.Substring(0, 80) }
      Write-Output ('   NO-KEY: ' + $short)
    }
  }
}
Write-Output 'FIN'
