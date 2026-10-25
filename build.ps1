# Builds dist\gbOS86-Installer.exe with the C# compiler that ships with
# .NET Framework 4.8, so the installer has no runtime dependency.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $csc)) {
    throw 'The .NET Framework 4.x C# compiler (csc.exe) was not found.'
}

$out = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $out | Out-Null

$sources = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }
$args = @(
    '/nologo',
    '/target:winexe',
    '/platform:anycpu',
    '/optimize+',
    '/warn:4',
    '/langversion:5',
    ('/out:' + (Join-Path $out 'gbOS86-Installer.exe')),
    ('/win32manifest:' + (Join-Path $root 'src\app.manifest')),
    '/r:System.dll',
    '/r:System.Core.dll',
    '/r:System.Drawing.dll',
    '/r:System.Windows.Forms.dll',
    '/r:System.Management.dll'
) + $sources

& $csc $args
if ($LASTEXITCODE -ne 0) { throw "Compilation failed ($LASTEXITCODE)." }

# Ship the Docker build files beside the executable.
$scriptsOut = Join-Path $out 'scripts'
if (Test-Path $scriptsOut) { Remove-Item -Recurse -Force $scriptsOut }
Copy-Item -Recurse (Join-Path $root 'scripts') $scriptsOut
if (Test-Path (Join-Path $root 'README.md')) {
    Copy-Item (Join-Path $root 'README.md') $out -Force
}

Write-Host "Built $out\gbOS86-Installer.exe"
