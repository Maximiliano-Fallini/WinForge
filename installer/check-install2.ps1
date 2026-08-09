$ErrorActionPreference = 'Continue'
Write-Host '=== 1. Archivos clave en Program Files ==='
$pf = 'C:\Program Files\WinForge'
$all = Get-ChildItem $pf -Recurse -File
Write-Host "  Total de archivos instalados: $($all.Count)"
foreach ($f in @('WinForge.exe','WHPO.Core.dll','App.xbf','MainWindow.xbf','Views\Pages\SistemaPage.xbf','Views\Pages\InicioPage.xbf','Controls\Cards.xbf','WinForge.pri','Assets\WinForge.ico','logos\WinForge.png','logos\AmdLogo.png')) {
    Write-Host ("  {0,-42} {1}" -f $f, (Test-Path (Join-Path $pf $f)))
}
Write-Host "  XBF instalados: $((Get-ChildItem $pf -Recurse -Filter *.xbf).Count) (esperado 18)"

Write-Host '=== 2. Accesos directos ==='
$ws = New-Object -ComObject WScript.Shell
Get-ChildItem "$env:PUBLIC\Desktop\WinForge.lnk", "$env:USERPROFILE\Desktop\WinForge.lnk", "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\WinForge\*.lnk" -ErrorAction SilentlyContinue | ForEach-Object {
    $s = $ws.CreateShortcut($_.FullName)
    Write-Host ("  {0} -> {1} {2}" -f $_.FullName.Replace('C:\Users\Maxi\','~\'), $s.TargetPath, $s.Arguments)
}

Write-Host '=== 3. Registro de desinstalacion ==='
Get-ItemProperty 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
    Where-Object { $_.DisplayName -like '*WinForge*' } |
    ForEach-Object { Write-Host ("  {0} v{1}" -f $_.DisplayName, $_.DisplayVersion) }

Write-Host '=== 4. Prueba de arranque ==='
$proc = Start-Process -FilePath (Join-Path $pf 'WinForge.exe') -PassThru
Start-Sleep -Seconds 10
if ($proc.HasExited) {
    Write-Host "  FALLO: proceso salio con codigo $($proc.ExitCode)"
    Get-Content "$env:LOCALAPPDATA\WHPO\app.log" -Tail 8 -ErrorAction SilentlyContinue
} else {
    Write-Host "  OK: WinForge corriendo (PID $($proc.Id))"
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    Write-Host '  App cerrada.'
}
