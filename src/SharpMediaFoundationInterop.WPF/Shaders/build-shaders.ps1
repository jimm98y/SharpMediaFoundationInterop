# Compiles the renderer's shaders (D3D11FrameRenderer) with fxc into the .cso files next to them, which are checked in
# and embedded: the renderer needs no shader compiler where it runs. Run after changing a .hlsl, on Windows with the
# Windows SDK installed:  pwsh Shaders/build-shaders.ps1
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$fxc = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\fxc.exe" | Sort-Object FullName | Select-Object -Last 1
if (-not $fxc) { throw 'fxc.exe not found (install the Windows SDK)' }
foreach ($shader in 'SphereShader', 'YuvShader') {
    foreach ($stage in @(@{ Entry = 'VS'; Profile = 'vs_4_0'; Suffix = 'vs' }, @{ Entry = 'PS'; Profile = 'ps_4_0'; Suffix = 'ps' })) {
        $out = Join-Path $here "$shader.$($stage.Suffix).cso"
        & $fxc.FullName /nologo /T $stage.Profile /E $stage.Entry /Fo $out (Join-Path $here "$shader.hlsl")
        if ($LASTEXITCODE -ne 0) { throw "fxc failed on $shader $($stage.Entry)" }
    }
}
