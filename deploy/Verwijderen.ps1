# Borgion System Information - verwijderen (als administrator uitvoeren)
$ErrorActionPreference = 'Stop'
$name   = 'Borgion System Information'
$target = Join-Path $env:ProgramFiles 'Borgion\SystemInformation'

Get-Process -Name 'BorgionSystemInfo' -ErrorAction SilentlyContinue | Stop-Process -Force
foreach ($dir in @((Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs'),
                   [Environment]::GetFolderPath('CommonDesktopDirectory'))) {
    Remove-Item (Join-Path $dir "$name.lnk") -ErrorAction SilentlyContinue
}
Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue
$parent = Split-Path $target
if ((Test-Path $parent) -and -not (Get-ChildItem $parent)) { Remove-Item $parent }
Write-Host 'Borgion System Information is verwijderd.' -ForegroundColor Green
