#requires -Version 7.0
param(
    [Parameter(Mandatory = $true)][string]$OutputRoot,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$IconPath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not [IO.Path]::IsPathFullyQualified($OutputRoot) -or (Test-Path -LiteralPath $OutputRoot)) { throw 'Use a fresh absolute launcher output directory.' }
Import-Module (Join-Path $PSScriptRoot 'Release-Versions.psm1')
if (-not (Test-ReleaseVersion $Version) -or -not [IO.Path]::IsPathFullyQualified($IconPath) -or -not (Test-Path -LiteralPath $IconPath -PathType Leaf)) { throw 'A launcher requires its exact release version and application icon.' }
$sourceRoot = Join-Path (Split-Path -Parent $PSScriptRoot) 'PortableLauncher'
Import-Module (Join-Path $sourceRoot 'Build.psm1') -Force
[IO.Directory]::CreateDirectory($OutputRoot) | Out-Null
$core = [version]$Version.Split('-')[0]
$icon = $IconPath.Replace('\', '/')
$resource = @"
#pragma code_page(65001)
#include <windows.h>
1 ICON "$icon"
1 VERSIONINFO
 FILEVERSION $($core.Major),$($core.Minor),$($core.Build),0
 PRODUCTVERSION $($core.Major),$($core.Minor),$($core.Build),0
 FILEFLAGSMASK 0x3fL
 FILEFLAGS 0
 FILEOS VOS_NT_WINDOWS32
 FILETYPE VFT_APP
 FILESUBTYPE 0
BEGIN
  BLOCK "StringFileInfo"
  BEGIN
    BLOCK "040904b0"
    BEGIN
      VALUE "CompanyName", "Bezoro\0"
      VALUE "FileDescription", "AHK Manager portable launcher\0"
      VALUE "FileVersion", "$Version\0"
      VALUE "InternalName", "Bezoro.AhkManager\0"
      VALUE "OriginalFilename", "Bezoro.AhkManager.exe\0"
      VALUE "ProductName", "AHK Manager\0"
      VALUE "ProductVersion", "$($core.Major).$($core.Minor).$($core.Build).0\0"
    END
  END
  BLOCK "VarFileInfo"
  BEGIN
    VALUE "Translation", 0x409, 1200
  END
END
"@
$resourcePath = Join-Path $OutputRoot 'launcher.rc'
[IO.File]::WriteAllText($resourcePath, $resource, [Text.UTF8Encoding]::new($false))
$executable = Join-Path $OutputRoot 'Bezoro.AhkManager.exe'
Build-PortableWin32Executable -SourcePath (Join-Path $sourceRoot 'Launcher.cpp') -OutputPath $executable -ResourcePath $resourcePath
if ([Diagnostics.FileVersionInfo]::GetVersionInfo($executable).FileVersion -cne $Version) { throw 'The portable launcher lost its exact release version resource.' }
Write-Host 'Built the standalone Windows GUI portable launcher.'
