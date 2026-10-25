# Compiles and runs the offline test harness with the built-in C# compiler.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $csc)) { throw 'csc.exe not found.' }

$names = @(
    'Native.cs', 'Util.cs', 'FsLabel.cs', 'GptDisk.cs', 'LooperImage.cs',
    'SparseImage.cs', 'RawDisk.cs', 'DiskService.cs', 'UefiBoot.cs',
    'PackedImage.cs', 'DockerService.cs', 'BuildService.cs', 'InstallService.cs'
)
$sources = $names | ForEach-Object { Join-Path $root ('src\' + $_) }
$sources += Join-Path $root 'tests\TestProgram.cs'
$exe = Join-Path $root 'tests\gbos86-tests.exe'

& $csc /nologo /target:exe /out:$exe /r:System.dll /r:System.Core.dll /r:System.Management.dll $sources
if ($LASTEXITCODE -ne 0) { throw "Test compilation failed ($LASTEXITCODE)." }

& $exe
exit $LASTEXITCODE
