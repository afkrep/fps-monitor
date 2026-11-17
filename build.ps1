$ErrorActionPreference = 'Stop'
$candidates = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)
$csc = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $csc) { throw 'Не найден csc.exe из .NET Framework.' }
& $csc /nologo /target:winexe "/win32icon:$PSScriptRoot\tracker.ico" "/out:$PSScriptRoot\AppTimeTracker-Apps-v2.exe" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "$PSScriptRoot\AppTimeTracker.cs"
if ($LASTEXITCODE -ne 0) { throw "Компиляция завершилась с кодом $LASTEXITCODE." }

