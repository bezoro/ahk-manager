#requires -Version 5.1
param([Parameter(Mandatory = $true)][string]$ManagerExecutable)

# Read-only uninstall gate. Paths are command arguments, never executable source.
# Exit 0: no owned task for this installation; 2: disable startup first; 3: unknown.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$service = $null
$folder = $null
$task = $null
$identity = $null
$result = 3

function Test-OwnedStartupTask([string]$Xml, [string]$ExpectedExecutable, [string]$UserSid) {
    if ($Xml.Length -gt 65536) { throw 'Task definition exceeds the inspection limit.' }
    $settings = New-Object Xml.XmlReaderSettings
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.MaxCharactersInDocument = 65536
    $text = New-Object IO.StringReader($Xml)
    $reader = [Xml.XmlReader]::Create($text, $settings)
    try {
        $document = New-Object Xml.XmlDocument
        $document.XmlResolver = $null
        $document.Load($reader)
    }
    finally { $reader.Dispose(); $text.Dispose() }
    $namespaces = New-Object Xml.XmlNamespaceManager($document.NameTable)
    $namespaces.AddNamespace('t', 'http://schemas.microsoft.com/windows/2004/02/mit/task')
    $source = $document.SelectSingleNode('/t:Task/t:RegistrationInfo/t:Source', $namespaces)
    if ($null -eq $source -or $source.InnerText -ne 'Bezoro.AhkManager.Electron.Startup.v1') { return $false }
    $actions = $document.SelectNodes('/t:Task/t:Actions/*', $namespaces)
    $principals = $document.SelectNodes('/t:Task/t:Principals/*', $namespaces)
    $triggers = $document.SelectNodes('/t:Task/t:Triggers/*', $namespaces)
    if ($actions.Count -ne 1 -or $actions[0].LocalName -ne 'Exec' -or $principals.Count -ne 1 -or $principals[0].LocalName -ne 'Principal' -or $triggers.Count -ne 1 -or $triggers[0].LocalName -ne 'LogonTrigger') { return $false }
    $command = $actions[0].SelectSingleNode('t:Command', $namespaces)
    $arguments = $actions[0].SelectSingleNode('t:Arguments', $namespaces)
    $principalSid = $principals[0].SelectSingleNode('t:UserId', $namespaces)
    $logonType = $principals[0].SelectSingleNode('t:LogonType', $namespaces)
    $triggerSid = $triggers[0].SelectSingleNode('t:UserId', $namespaces)
    if ($null -eq $command -or $null -eq $arguments -or $null -eq $principalSid -or $null -eq $logonType -or $null -eq $triggerSid) { return $false }
    if ($command.InnerText -notmatch '^[A-Za-z]:\\' -or $arguments.InnerText -cne '--login --background' -or $principalSid.InnerText -ne $UserSid -or $triggerSid.InnerText -ne $UserSid -or $logonType.InnerText -cne 'InteractiveToken') { return $false }
    return [IO.Path]::GetFullPath($command.InnerText).Equals($ExpectedExecutable, [StringComparison]::OrdinalIgnoreCase)
}

try {
    if ($ManagerExecutable -notmatch '^[A-Za-z]:\\') { throw 'Absolute local installation path required.' }
    $expected = [IO.Path]::GetFullPath($ManagerExecutable)
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $sid = $identity.User.Value
    if ([string]::IsNullOrEmpty($sid)) { throw 'Current user unavailable.' }
    $service = New-Object -ComObject 'Schedule.Service'
    $service.Connect()
    $folder = $service.GetFolder('\')
    try { $task = $folder.GetTask('Bezoro.AhkManager.Electron.' + $sid) }
    catch {
        $failure = $_.Exception
        while ($null -ne $failure.InnerException) { $failure = $failure.InnerException }
        if ($failure.HResult -eq -2147024894 -or $failure.HResult -eq -2147216625) { $result = 0 }
        else { throw }
    }
    if ($null -ne $task) {
        if (Test-OwnedStartupTask $task.Xml $expected $sid) { $result = 2 }
        else { $result = 0 }
    }
}
catch { $result = 3 }
finally {
    foreach ($value in @($task, $folder, $service)) {
        if ($null -ne $value -and [Runtime.InteropServices.Marshal]::IsComObject($value)) { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($value) }
    }
    if ($null -ne $identity) { $identity.Dispose() }
}
exit $result
