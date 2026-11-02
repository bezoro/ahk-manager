#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PortableRoot,
    [switch]$Apply,
    [string]$BackupRoot
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:OS -ne 'Windows_NT') { throw 'This maintenance script requires Windows.' }

# The pins reject redirects and retain names while the inventory and DACLs are checked.
# Windows Cloud Files tags describe local placeholders, not namespace redirects.
if (-not ('Bezoro.PermissionRepair.Pin' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Text;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
namespace Bezoro.PermissionRepair {
 public sealed class Pin : IDisposable {
  [StructLayout(LayoutKind.Sequential, Pack=4)] struct Info {
   public uint Attributes; public long Creation, Access, Write;
   public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
  }
  [StructLayout(LayoutKind.Sequential)] struct TagInfo { public uint Attributes, Tag; }
  [StructLayout(LayoutKind.Sequential)] struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true, EntryPoint="CreateFileW")]
  static extern SafeFileHandle Open(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Info info);
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, out TagInfo info, uint bytes);
  [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true, EntryPoint="ConvertStringSecurityDescriptorToSecurityDescriptorW")]
  static extern bool ParseSecurity(string text, uint revision, out IntPtr descriptor, out uint size);
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true, EntryPoint="CreateDirectoryW")]
  static extern bool CreateDirectory(string path, ref SecurityAttributes security);
  [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr value);
  readonly SafeFileHandle handle; readonly FileStream stream;
  public readonly string Identity; public readonly long Length;
  public Pin(string path, bool directory) {
   handle = Open("\\\\?\\" + path, directory ? 0x81u : 0x80u, directory ? 3u : 1u, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
   try {
    if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot retain " + path);
    TagInfo tag; Info info;
    if (!GetFileInformationByHandleEx(handle, 9, out tag, 8) || !GetFileInformationByHandle(handle, out info)) throw new Win32Exception(Marshal.GetLastWin32Error());
    ValidateMetadata(info.Attributes, tag.Tag, directory, info.Links, path);
    Identity = info.Volume + ":" + info.IndexHigh + ":" + info.IndexLow + ":" + info.Creation;
    if (!directory) {
     stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
     Info opened;
     if (!GetFileInformationByHandle(stream.SafeFileHandle, out opened) || opened.Volume != info.Volume || opened.IndexHigh != info.IndexHigh || opened.IndexLow != info.IndexLow || opened.Links != 1) throw new IOException("File identity changed: " + path);
     Length = stream.Length;
    }
   } catch { Dispose(); throw; }
  }
  public string Hash() {
   stream.Position = 0;
   using (var sha = SHA256.Create()) { string result = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); stream.Position = 0; return result; }
  }
  public static void ValidateMetadata(uint attributes, uint tag, bool directory, uint links, string path = null) {
   if ((attributes & 0x40) != 0 || ((attributes & 0x10) != 0) != directory || ((attributes & 0x400) != 0 && (tag & ~0xF000u) != 0x9000001A)) throw new IOException("Unsupported redirected entry: " + path);
   if (!directory && (links != 1 || (attributes & 0x401000) != 0)) throw new IOException("File must be single-linked and available offline: " + path);
  }
  public string Text(int maximum) {
   if (Length > maximum) throw new IOException("Descriptor exceeds its size limit.");
   stream.Position = 0;
   using (var reader = new StreamReader(stream, new UTF8Encoding(false, true), false, 4096, true)) { string result = reader.ReadToEnd(); stream.Position = 0; return result; }
  }
  public static void PrivateDirectory(string path, string sid) {
   IntPtr descriptor; uint size;
   if (!ParseSecurity("O:" + sid + "D:P(A;OICI;FA;;;" + sid + ")(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)", 1, out descriptor, out size)) throw new Win32Exception(Marshal.GetLastWin32Error());
   try { var security = new SecurityAttributes { Length = Marshal.SizeOf(typeof(SecurityAttributes)), Descriptor = descriptor }; if (!CreateDirectory(path, ref security)) throw new Win32Exception(Marshal.GetLastWin32Error()); }
   finally { LocalFree(descriptor); }
  }
  public void Dispose() { if (stream != null) stream.Dispose(); if (handle != null) handle.Dispose(); }
 }
}
'@
}

if ($PortableRoot -notmatch '^[A-Za-z]:\\') { throw 'Select an absolute local portable product folder.' }
$root = [IO.Path]::GetFullPath($PortableRoot).TrimEnd('\')
if ($root -notmatch '^[A-Za-z]:\\' -or $root -eq [IO.Path]::GetPathRoot($root).TrimEnd('\')) { throw 'Select an absolute local portable product folder, not a drive root.' }
if ([IO.DriveInfo]::new([IO.Path]::GetPathRoot($root)).DriveFormat -ne 'NTFS') { throw 'The portable folder must be on NTFS.' }
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$trusted = @($sid, 'S-1-5-18', 'S-1-5-32-544')
$pins = @{}
$snapshots = @{}
$backup = $null
$changed = [Collections.Generic.List[string]]::new()

function Retain([string]$Path, [bool]$Directory) {
    if (-not $pins.ContainsKey($Path)) { $pins[$Path] = [Bezoro.PermissionRepair.Pin]::new($Path, $Directory) }
    return $pins[$Path]
}
function Retain-Ancestors([string]$Path) {
    $ancestors = [Collections.Generic.List[string]]::new()
    for ($cursor = $Path; $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) { $ancestors.Add($cursor) }
    for ($index = $ancestors.Count - 1; $index -ge 0; $index--) { $null = Retain $ancestors[$index] $true }
}
function Prepare-BackupRoot([string]$Path) {
    $missing = [Collections.Generic.List[string]]::new()
    $cursor = $Path
    while (-not [IO.Directory]::Exists($cursor)) {
        if (-not $cursor -or [IO.File]::Exists($cursor)) { throw 'The backup parent is unavailable or is not a directory.' }
        $missing.Add($cursor)
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
    # Validate and retain existing ancestors before creating any missing component.
    Retain-Ancestors $cursor
    for ($index = $missing.Count - 1; $index -ge 0; $index--) {
        [Bezoro.PermissionRepair.Pin]::PrivateDirectory($missing[$index], $sid)
        $null = Retain $missing[$index] $true
        $null = Access $missing[$index] $true
    }
}
function Access([string]$Path, [bool]$Directory) {
    $acl = Get-Acl -LiteralPath $Path
    $raw = [Security.AccessControl.RawSecurityDescriptor]::new($acl.GetSecurityDescriptorBinaryForm(), 0)
    if ($null -eq $raw.DiscretionaryAcl) { throw "An unrestricted DACL is not private: $Path" }
    $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
    if ($trusted -notcontains $owner) { throw "Untrusted owner on $Path" }
    $allowed = @{}
    foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and $trusted -notcontains $rule.IdentityReference.Value) { throw "Another principal has access to $Path" }
        if ($rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -eq 0) {
            $key = $rule.IdentityReference.Value
            $allowed[$key] = [long]$allowed[$key] -bor [long]$rule.FileSystemRights
        }
    }
    foreach ($key in $trusted) { if (([long]$allowed[$key] -band [long][Security.AccessControl.FileSystemRights]::FullControl) -ne [long][Security.AccessControl.FileSystemRights]::FullControl) { throw "The existing private FullControl policy is incomplete: $Path" } }
    return [pscustomobject]@{ Path = $Path; Directory = $Directory; Owner = $owner; Protected = $acl.AreAccessRulesProtected; Dacl = $acl.GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]::Access); Acl = $acl }
}
function Persist-Dacl($State) {
    $entry = if ($State.Directory) { [IO.DirectoryInfo]::new($State.Path) } else { [IO.FileInfo]::new($State.Path) }
    $access = [Security.AccessControl.AccessControlSections]::Access
    $dacl = if ($State.Directory) { [Security.AccessControl.DirectorySecurity]::new() } else { [Security.AccessControl.FileSecurity]::new() }
    $dacl.SetSecurityDescriptorSddlForm($State.Acl.GetSecurityDescriptorSddlForm($access), $access)
    if ('System.IO.FileSystemAclExtensions' -as [type]) { [IO.FileSystemAclExtensions]::SetAccessControl($entry, $dacl) }
    else { $entry.SetAccessControl($dacl) }
}
function Rule-Fingerprint($Acl) {
    $rules = @{}
    foreach ($rule in $Acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        $key = $rule.IdentityReference.Value + '|' + $rule.AccessControlType + '|' + [int]$rule.InheritanceFlags + '|' + [int]$rule.PropagationFlags
        $rules[$key] = [long]$rules[$key] -bor [long]$rule.FileSystemRights
    }
    return (@($rules.Keys | Sort-Object | ForEach-Object { $_ + '|' + $rules[$_] }) -join ';')
}
function Write-Report([string]$Path, $Value) {
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($Value | ConvertTo-Json -Depth 12))
    $output = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try { $output.Write($bytes, 0, $bytes.Length); $output.Flush($true) } finally { $output.Dispose() }
}

try {
    if (@(Get-Process -Name 'Bezoro.AhkManager' -ErrorAction SilentlyContinue).Count) { throw 'Exit Manager normally from the notification area before previewing or applying this repair. Independent AutoHotkey scripts can keep running.' }
    Retain-Ancestors $root
    $rootAccess = Access $root $true
    if (-not $rootAccess.Protected) { throw 'This narrow repair requires an already protected, privately owned portable root.' }
    $marker = Retain (Join-Path $root 'portable.json') $false
    $descriptor = Retain (Join-Path $root 'portable-package.json') $false
    $configuration = $marker.Text(4096) | ConvertFrom-Json
    $inventory = $descriptor.Text(4194304) | ConvertFrom-Json
    if ($configuration.schemaVersion -ne 1 -or $configuration.applicationId -cne 'Bezoro.AhkManager.Electron' -or -not ($configuration.dataDirectory -is [string]) -or [string]::IsNullOrWhiteSpace($configuration.dataDirectory)) { throw 'The portable configuration identity is invalid.' }
    if ($inventory.schemaVersion -ne 1 -or $inventory.applicationId -cne 'Bezoro.AhkManager.Electron' -or $inventory.updateProtocol -ne 2 -or $inventory.version -notmatch '^\d+\.\d+\.\d+(?:-test\.\d+\.\d+\.\d+)?$' -or $inventory.commit -cnotmatch '^[a-f0-9]{40}$' -or @($inventory.files).Count -notin 1..4096) { throw 'The portable inventory identity is invalid.' }
    if ($configuration.dataDirectory -match '[/\x00-\x1f]' -or @($configuration.dataDirectory.Split('\') | Where-Object { $_ -in '.', '..' }).Count -or ([IO.Path]::IsPathRooted($configuration.dataDirectory) -and $configuration.dataDirectory -notmatch '^[A-Za-z]:\\')) { throw 'The configured data path is invalid.' }
    $data = if ([IO.Path]::IsPathRooted($configuration.dataDirectory)) { [IO.Path]::GetFullPath($configuration.dataDirectory) } else { [IO.Path]::GetFullPath((Join-Path $root $configuration.dataDirectory)) }
    $app = Join-Path $root 'App'
    if ($data -notmatch '^[A-Za-z]:\\' -or $data -eq $root -or $root.StartsWith($data.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or $data -eq $app -or $data.StartsWith($app + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Portable data overlaps the application or is not local.' }
    $expected = @{}
    $declaredDirectories = @{ App = $true }
    [long]$totalBytes = 0
    foreach ($row in $inventory.files) {
        $relative = [string]$row.path
        if (($relative -cne 'Bezoro.AhkManager.exe' -and -not $relative.StartsWith('App/', [StringComparison]::Ordinal)) -or $relative -match '[\\:\x00-\x1f]' -or @($relative.Split('/') | Where-Object { $_ -eq '' -or $_ -in '.', '..' -or $_ -match '[. ]$' }).Count -or $row.sha256 -cnotmatch '^[a-f0-9]{64}$' -or $row.bytes -lt 0 -or $row.bytes -gt 536870912 -or $expected.ContainsKey($relative)) { throw 'The portable inventory contains an unsafe or duplicate entry.' }
        $expected[$relative] = $row
        $totalBytes += [long]$row.bytes
        if ($totalBytes -gt 2147483648) { throw 'The portable inventory exceeds its byte limit.' }
        for ($slash = $relative.LastIndexOf('/'); $slash -gt 0; $slash = $relative.LastIndexOf('/', $slash - 1)) { $declaredDirectories[$relative.Substring(0, $slash)] = $true }
    }
    foreach ($required in @('Bezoro.AhkManager.exe', 'App/Bezoro.AhkManager.exe', 'App/backend/AhkManager.Backend.exe', 'App/backend/update/AhkManager.UpdateHost.exe')) { if (-not $expected.ContainsKey($required)) { throw "The inventory is missing $required" } }
    $directories = [Collections.Generic.List[string]]::new()
    $directories.Add((Join-Path $root 'App'))
    for ($index = 0; $index -lt $directories.Count; $index++) {
        $directory = $directories[$index]
        if (-not $declaredDirectories.ContainsKey($directory.Substring($root.Length + 1).Replace('\', '/'))) { throw "Undeclared application directory: $directory" }
        Retain-Ancestors $directory
        $null = Access $directory $true
        foreach ($path in [IO.Directory]::EnumerateFileSystemEntries($directory)) {
            if ([IO.Directory]::Exists($path)) { $directories.Add($path) }
            elseif (-not $expected.ContainsKey($path.Substring($root.Length + 1).Replace('\', '/'))) { throw "Undeclared application file: $path" }
        }
        if ($directories.Count -gt 4096) { throw 'The application directory limit was exceeded.' }
    }
    foreach ($row in $expected.Values) {
        $path = Join-Path $root $row.path.Replace('/', '\')
        $pin = Retain $path $false
        $null = Access $path $false
        if ($pin.Length -ne $row.bytes -or $pin.Hash() -cne $row.sha256) { throw "Inventory verification failed for $path" }
    }
    $targets = @(@{ Path = $root; Directory = $true }, @{ Path = (Join-Path $root 'App'); Directory = $true }) + @('Bezoro.AhkManager.exe', 'portable-package.json', 'portable.json', 'App\Bezoro.AhkManager.exe', 'App\backend\AhkManager.Backend.exe', 'App\backend\update\AhkManager.UpdateHost.exe' | ForEach-Object { @{ Path = (Join-Path $root $_); Directory = $false } })
    foreach ($target in $targets) { if (-not $target.Directory -and ($target.Path -eq $data -or $target.Path.StartsWith($data.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase))) { throw 'Configured data overlaps a package file.' } }
    $states = @($targets | ForEach-Object { Access $_.Path $_.Directory })
    $planned = @($states | Where-Object { -not $_.Protected })
    $view = @($states | Select-Object Path, Directory, Owner, Protected, @{ Name = 'Action'; Expression = { if ($_.Protected) { 'No change' } else { 'Freeze existing inheritance' } } })
    Write-Host ($view | Format-Table -AutoSize | Out-String)
    if (-not $Apply -or $planned.Count -eq 0) {
        return [pscustomobject]@{ Mode = $(if ($Apply) { 'Applied' } else { 'Preview' }); PortableRoot = $root; Version = $inventory.version; ChangedPaths = @($planned | ForEach-Object { $_.Path }); BackupDirectory = $null; Targets = $view }
    }
    if (-not $BackupRoot) { $BackupRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Bezoro\AhkManagerElectronDiagnostics\PermissionRepairs' }
    if ($BackupRoot -notmatch '^[A-Za-z]:\\') { throw 'The backup location must be an absolute local folder.' }
    $BackupRoot = [IO.Path]::GetFullPath($BackupRoot).TrimEnd('\')
    if ($BackupRoot.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase) -or $BackupRoot -eq $root -or $BackupRoot.StartsWith($data.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or $BackupRoot -eq $data) { throw 'The backup location must be outside the portable app and Data folders.' }
    if ([IO.DriveInfo]::new([IO.Path]::GetPathRoot($BackupRoot)).DriveFormat -ne 'NTFS') { throw 'The diagnostic backup folder must be on NTFS.' }
    Prepare-BackupRoot $BackupRoot
    $backup = Join-Path $BackupRoot ([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' + [Guid]::NewGuid().ToString('N'))
    [Bezoro.PermissionRepair.Pin]::PrivateDirectory($backup, $sid)
    Retain-Ancestors $backup
    $null = Access $backup $true
    $backupTargets = @($states | ForEach-Object { [ordered]@{ Path = $_.Path; Directory = $_.Directory; Owner = $_.Owner; Protected = $_.Protected; Dacl = $_.Dacl; Identity = $pins[$_.Path].Identity; Bytes = $(if ($_.Directory) { $null } else { $pins[$_.Path].Length }); Sha256 = $(if ($_.Directory) { $null } else { $pins[$_.Path].Hash() }) } })
    Write-Report (Join-Path $backup 'before.json') ([ordered]@{ SchemaVersion = 1; ApplicationId = 'Bezoro.AhkManager.Electron'; PortableRoot = $root; Version = $inventory.version; Commit = $inventory.commit; InventorySha256 = $descriptor.Hash(); ConfigurationSha256 = $marker.Hash(); Targets = $backupTargets; InventoryFilesVerified = $expected.Count })
    foreach ($state in $planned) {
        $current = Access $state.Path $state.Directory
        if ($current.Owner -cne $state.Owner -or $current.Dacl -cne $state.Dacl) { throw "Permissions changed during validation: $($state.Path)" }
        $rulesBefore = Rule-Fingerprint $current.Acl
        $current.Acl.SetAccessRuleProtection($true, $true)
        Persist-Dacl $current
        $after = Access $state.Path $state.Directory
        if (-not $after.Protected -or $after.Owner -cne $state.Owner -or (Rule-Fingerprint $after.Acl) -cne $rulesBefore) { throw "Permission verification failed: $($state.Path)" }
        $changed.Add($state.Path)
    }
    Write-Report (Join-Path $backup 'result.json') ([ordered]@{ Completed = $true; ChangedPaths = $changed.ToArray(); OwnerChanges = 0; DataAclChanges = 0; ParentAclChanges = 0 })
    [pscustomobject]@{ Mode = 'Applied'; PortableRoot = $root; Version = $inventory.version; ChangedPaths = $changed.ToArray(); BackupDirectory = $backup; Targets = @($states | ForEach-Object { Access $_.Path $_.Directory } | Select-Object Path, Directory, Owner, Protected) }
}
catch {
    if ($backup) { Write-Warning "Recovery evidence is preserved in $backup. Completed paths: $($changed -join ', ')" }
    throw
}
finally { foreach ($pin in $pins.Values) { $pin.Dispose() } }
