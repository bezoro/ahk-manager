#requires -Version 7.0
Set-StrictMode -Version Latest

function Build-PortableWin32Executable {
    param(
        [Parameter(Mandatory = $true)][string]$SourcePath,
        [Parameter(Mandatory = $true)][string]$OutputPath,
        [string]$ResourcePath
    )
    if (-not $IsWindows -or -not [IO.Path]::IsPathFullyQualified($SourcePath) -or -not [IO.Path]::IsPathFullyQualified($OutputPath) -or (Test-Path -LiteralPath $OutputPath)) { throw 'Use Windows and fresh absolute native build paths.' }
    $vswhere = Join-Path ([Environment]::GetEnvironmentVariable('ProgramFiles(x86)')) 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) { throw 'Visual Studio Build Tools are required to build the portable launcher.' }
    $installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($installation)) { throw 'The x64 MSVC toolchain is unavailable.' }
    $developerCommand = Join-Path $installation 'Common7/Tools/VsDevCmd.bat'
    if ($developerCommand -match '["%\r\n]') { throw 'The native toolchain command path is ambiguous.' }
    $environmentStart = [Diagnostics.ProcessStartInfo]::new($env:ComSpec)
    $environmentStart.UseShellExecute = $false
    $environmentStart.RedirectStandardOutput = $true
    $environmentStart.RedirectStandardError = $true
    # cmd has its own quoting grammar. Use its raw command line, rather than
    # PowerShell's native argument escaping, for the quoted batch-file path.
    $environmentStart.Arguments = '/d /v:off /s /c ""{0}" -no_logo -arch=x64 -host_arch=x64 && set"' -f $developerCommand
    $environmentProcess = [Diagnostics.Process]::Start($environmentStart)
    try {
        $environmentOutput = $environmentProcess.StandardOutput.ReadToEndAsync()
        $environmentError = $environmentProcess.StandardError.ReadToEndAsync()
        $environmentProcess.WaitForExit()
        $developerEnvironment = $environmentOutput.GetAwaiter().GetResult() -split '\r?\n'
        $null = $environmentError.GetAwaiter().GetResult()
        if ($environmentProcess.ExitCode -ne 0) { throw 'The x64 native build environment could not be prepared.' }
    }
    finally { $environmentProcess.Dispose() }
    $compiler = @(& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find 'VC/Tools/MSVC/*/bin/Hostx64/x64/cl.exe' | Sort-Object | Select-Object -Last 1)
    if ($LASTEXITCODE -ne 0 -or $compiler.Count -ne 1) { throw 'The x64 MSVC compiler could not be resolved.' }
    $directory = [IO.Path]::GetDirectoryName($OutputPath)
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $start = [Diagnostics.ProcessStartInfo]::new($compiler[0])
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $directory
    foreach ($line in $developerEnvironment) {
        if ($line -match '^([^=]+)=(.*)$') { $start.Environment[$Matches[1]] = $Matches[2] }
    }
    $resourceOutput = $null
    if ($ResourcePath) {
        if (-not [IO.Path]::IsPathFullyQualified($ResourcePath) -or -not (Test-Path -LiteralPath $ResourcePath -PathType Leaf)) { throw 'Use an absolute native resource source path.' }
        $resourceCompiler = @($start.Environment['PATH'].Split(';') | Where-Object { $_ } | ForEach-Object { Join-Path $_ 'rc.exe' } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1)
        if ($resourceCompiler.Count -ne 1) { throw 'The Windows SDK resource compiler is unavailable.' }
        $resourceOutput = [IO.Path]::ChangeExtension($OutputPath, '.res')
        $resourceStart = [Diagnostics.ProcessStartInfo]::new($resourceCompiler[0])
        $resourceStart.UseShellExecute = $false
        $resourceStart.WorkingDirectory = $directory
        foreach ($entry in $start.Environment.GetEnumerator()) { $resourceStart.Environment[$entry.Key] = $entry.Value }
        foreach ($argument in @('/nologo', '/fo', $resourceOutput, $ResourcePath)) { $resourceStart.ArgumentList.Add($argument) }
        $resourceProcess = [Diagnostics.Process]::Start($resourceStart)
        try { $resourceProcess.WaitForExit(); if ($resourceProcess.ExitCode -ne 0) { throw "Portable native resource build failed: $($resourceProcess.ExitCode)." } }
        finally { $resourceProcess.Dispose() }
    }
    foreach ($argument in @('/nologo', '/O2', '/Os', '/W4', '/WX', '/MT', '/EHsc', '/guard:cf', '/Brepro', '/utf-8', $SourcePath, ('/Fo' + [IO.Path]::ChangeExtension($OutputPath, '.obj')))) { $start.ArgumentList.Add($argument) }
    if ($resourceOutput) { $start.ArgumentList.Add($resourceOutput) }
    foreach ($argument in @('/link', '/SUBSYSTEM:WINDOWS', '/DYNAMICBASE', '/NXCOMPAT', '/HIGHENTROPYVA', '/CETCOMPAT', '/OPT:REF', '/OPT:ICF', '/Brepro', ('/OUT:' + $OutputPath), 'user32.lib', 'shell32.lib')) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try { $process.WaitForExit(); if ($process.ExitCode -ne 0) { throw "Portable native build failed: $($process.ExitCode)." } }
    finally { $process.Dispose() }
    if (-not (Test-Path -LiteralPath $OutputPath -PathType Leaf)) { throw 'The native build did not produce its executable.' }
}

Export-ModuleMember -Function Build-PortableWin32Executable
