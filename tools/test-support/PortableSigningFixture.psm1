#requires -Version 7.0
# This CA exists only in elevated disposable runners. Machine-root access avoids
# CurrentUser protected-root dialogs without changing production verification.
Set-StrictMode -Version Latest
$script:fixtures = @{}

function Assert-FixtureRunner {
    if (-not $IsWindows -or $env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted' -or [string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
        throw 'Portable signing fixtures require a disposable GitHub-hosted Windows runner.'
    }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        if (-not [Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Portable signing fixtures require the disposable runner elevated administrative token.' }
    }
    finally { $identity.Dispose() }
}

function Write-FixtureStage($State, [string]$Stage) {
    $event = [ordered]@{ atUtc = [DateTimeOffset]::UtcNow.ToString('O'); stage = $Stage }
    $State.Stages.Add($event)
    [IO.File]::WriteAllText($State.CheckpointPath, (ConvertTo-Json -InputObject $State.Stages.ToArray() -Depth 5), [Text.UTF8Encoding]::new($false))
    Write-Host ('Portable signing fixture: ' + $Stage)
}

function Initialize-FixtureTrustWorker {
    if ('AhkManager.TestSupport.PortableFixtureTrust' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
namespace AhkManager.TestSupport {
    public static class PortableFixtureTrust {
        public static Task Add(byte[] bytes) {
            byte[] owned = (byte[])bytes.Clone();
            return Task.Run(() => Change(owned, true));
        }
        public static Task Remove(byte[] bytes) {
            byte[] owned = (byte[])bytes.Clone();
            return Task.Run(() => Change(owned, false));
        }
        private static void Change(byte[] bytes, bool add) {
            using (var certificate = X509CertificateLoader.LoadCertificate(bytes))
            using (var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine)) {
                if (certificate.HasPrivateKey) throw new CryptographicException("Fixture trust must contain only a public certificate.");
                store.Open(OpenFlags.ReadWrite);
                var existing = store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, false);
                if (add) {
                    if (existing.Count != 0) {
                        foreach (var item in existing) item.Dispose();
                        throw new CryptographicException("The fresh fixture root already exists.");
                    }
                    store.Add(certificate);
                } else {
                    foreach (var item in existing) {
                        using (item) {
                            if (!item.RawData.AsSpan().SequenceEqual(bytes)) throw new CryptographicException("Fixture trust identity changed.");
                            store.Remove(item);
                        }
                    }
                }
                var observed = store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, false);
                try {
                    if (observed.Count != (add ? 1 : 0)) throw new CryptographicException("Fixture trust mutation was not confirmed.");
                    if (add && !observed[0].RawData.AsSpan().SequenceEqual(bytes)) throw new CryptographicException("Fixture trust identity changed.");
                } finally { foreach (var item in observed) item.Dispose(); }
            }
        }
    }
    public sealed class PortableFixtureCrlServer {
        private readonly TcpListener listener;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        private readonly Task worker;
        private readonly string route;
        private byte[] crl;
        private int requests;
        private int stopping;
        private int disposed;
        public string Url { get; private set; }
        public int Requests { get { return Volatile.Read(ref requests); } }
        public PortableFixtureCrlServer(string id) {
            Guid parsed;
            if (!Guid.TryParseExact(id, "D", out parsed) || id != parsed.ToString("D")) throw new ArgumentException("Invalid fixture identity.");
            route = "/" + id + "/issuer.crl";
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + route;
            worker = Task.Run(Run);
        }
        public void SetCrl(byte[] bytes) {
            if (bytes == null || bytes.Length < 2 || bytes.Length > 65536 || crl != null) throw new ArgumentException("Invalid fixture CRL payload.");
            Volatile.Write(ref crl, (byte[])bytes.Clone());
        }
        private async Task Run() {
            try {
                for (int count = 0; count < 128 && !lifetime.IsCancellationRequested; count++) {
                    using (var client = await listener.AcceptTcpClientAsync(lifetime.Token)) {
                        var peer = client.Client.RemoteEndPoint as IPEndPoint;
                        if (peer == null || !IPAddress.IsLoopback(peer.Address)) continue;
                        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token)) {
                            deadline.CancelAfter(TimeSpan.FromSeconds(2));
                            try { await Serve(client.GetStream(), deadline.Token); }
                            catch (Exception error) when (error is IOException || error is OperationCanceledException || error is SocketException) { }
                        }
                    }
                }
            } catch (Exception error) when (error is OperationCanceledException || error is SocketException || error is ObjectDisposedException) {
            } finally { listener.Stop(); }
        }
        private async Task Serve(NetworkStream stream, CancellationToken cancellation) {
            using (var header = new MemoryStream()) {
                var buffer = new byte[512];
                string text = "";
                while (header.Length < 4096) {
                    int count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, 4096 - header.Length)), cancellation);
                    if (count == 0) return;
                    header.Write(buffer, 0, count);
                    text = Encoding.ASCII.GetString(header.GetBuffer(), 0, (int)header.Length);
                    if (text.Contains("\r\n\r\n")) break;
                }
                if (!text.Contains("\r\n\r\n")) return;
                int end = text.IndexOf("\r\n", StringComparison.Ordinal);
                string first = text.Substring(0, end);
                byte[] payload = Volatile.Read(ref crl);
                bool valid = payload != null && (first == "GET " + route + " HTTP/1.1" || first == "GET " + route + " HTTP/1.0");
                string response = valid ? "200 OK" : "404 Not Found";
                if (!valid) payload = Array.Empty<byte>();
                byte[] prefix = Encoding.ASCII.GetBytes("HTTP/1.1 " + response + "\r\nContent-Type: application/pkix-crl\r\nContent-Length: " + payload.Length + "\r\nConnection: close\r\nCache-Control: public, max-age=300\r\n\r\n");
                await stream.WriteAsync(prefix, cancellation);
                await stream.WriteAsync(payload, cancellation);
                await stream.FlushAsync(cancellation);
                if (valid) Interlocked.Increment(ref requests);
            }
        }
        public bool Stop(int milliseconds) {
            if (Interlocked.Exchange(ref stopping, 1) == 0) {
                lifetime.Cancel();
                listener.Stop();
            }
            bool complete = worker.Wait(milliseconds);
            if (complete && Interlocked.Exchange(ref disposed, 1) == 0) lifetime.Dispose();
            return complete;
        }
    }
}
'@
}

function Wait-FixtureTrust($State, [Threading.Tasks.Task]$Task, [string]$Operation) {
    $State.TrustWorker = $Task
    try {
        if (-not $Task.Wait(15000)) {
            Write-FixtureStage $State ($Operation + '-timeout-preserved-for-runner-disposal')
            throw ('The disposable fixture trust ' + $Operation + ' did not finish in 15 seconds. Its state was preserved for runner disposal.')
        }
        $null = $Task.GetAwaiter().GetResult()
    }
    finally { if ($Task.IsCompleted) { $State.TrustWorker = $null } }
}

function Assert-FixturePath([string]$Path) {
    if (-not [IO.Path]::IsPathFullyQualified($Path)) { throw 'Fixture paths must be absolute.' }
    for ($current = [IO.Path]::GetFullPath($Path); $current; $current = [IO.Path]::GetDirectoryName($current)) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Redirected fixture paths are refused.' }
    }
}

function New-FixtureDirectory([string]$Path) {
    Assert-FixturePath $Path
    if (Test-Path -LiteralPath $Path) { throw 'Signing fixture directories must be fresh.' }
    [IO.Directory]::CreateDirectory($Path) | Out-Null
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $acl = [Security.AccessControl.DirectorySecurity]::new()
        $acl.SetOwner($identity.User)
        $acl.SetAccessRuleProtection($true, $false)
        foreach ($sid in @($identity.User.Value, 'S-1-5-18', 'S-1-5-32-544')) {
            $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid), [Security.AccessControl.FileSystemRights]::FullControl, [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit', [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow))
        }
        Set-Acl -LiteralPath $Path -AclObject $acl
    }
    finally { $identity.Dispose() }
}

function Read-FixtureBytes([string]$Path, [long]$Maximum) {
    Assert-FixturePath $Path
    $file = Get-Item -LiteralPath $Path -Force
    if ($file -isnot [IO.FileInfo] -or $file.Length -lt 2 -or $file.Length -gt $Maximum) { throw 'The signing fixture input is not a bounded regular file.' }
    return ,([IO.File]::ReadAllBytes($file.FullName))
}

function Read-FixtureJson([string]$Path) {
    $options = [Text.Json.JsonDocumentOptions]::new()
    $options.MaxDepth = 12
    $document = [Text.Json.JsonDocument]::Parse([ReadOnlyMemory[byte]]::new((Read-FixtureBytes $Path 65536)), $options)
    try { Assert-FixtureJsonMembers $document.RootElement; return $document }
    catch { $document.Dispose(); throw }
}

function Assert-FixtureJsonMembers($Element) {
    if ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Object) {
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($property in $Element.EnumerateObject()) {
            if (-not $names.Add($property.Name)) { throw 'Duplicate signing fixture metadata fields are refused.' }
            Assert-FixtureJsonMembers $property.Value
        }
    }
    elseif ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Array) { foreach ($value in $Element.EnumerateArray()) { Assert-FixtureJsonMembers $value } }
}

function Get-FixturePolicy([string]$ApplicationRoot, [string]$PublisherPolicyPath, [string]$CandidateManifestPath, [string]$BaselineManifestPath) {
    $policy = Read-FixtureJson $PublisherPolicyPath
    $candidate = $null
    $baseline = $null
    try {
        $root = $policy.RootElement
        $expected = @('schemaVersion', 'names', 'candidatePolicySha256', 'baselinePolicySha256', 'sourceCommit')
        $names = @($root.EnumerateObject() | ForEach-Object Name)
        if ($root.ValueKind -ne [Text.Json.JsonValueKind]::Object -or $names.Count -ne $expected.Count -or @($names | Where-Object { $_ -cnotin $expected }).Count -ne 0 -or $root.GetProperty('schemaVersion').GetInt32() -ne 1) { throw 'The test-only publisher policy schema is invalid.' }
        $publishers = $root.GetProperty('names')
        if ($publishers.ValueKind -ne [Text.Json.JsonValueKind]::Array -or $publishers.GetArrayLength() -gt 32) { throw 'The test-only publisher names are invalid.' }
        $values = [Collections.Generic.List[string]]::new()
        foreach ($publisher in $publishers.EnumerateArray()) {
            if ($publisher.ValueKind -ne [Text.Json.JsonValueKind]::String) { throw 'The test-only publisher names are invalid.' }
            $name = $publisher.GetString()
            if ([string]::IsNullOrWhiteSpace($name) -or $name.Length -gt 1024 -or $name.ToCharArray().Where({ [char]::IsControl($_) }).Count -ne 0) { throw 'The test-only publisher names are invalid.' }
            $values.Add($name)
        }
        $actual = (Get-FileHash -LiteralPath (Join-Path $ApplicationRoot 'resources/app-update.yml') -Algorithm SHA256).Hash.ToLowerInvariant()
        foreach ($field in @('candidatePolicySha256', 'baselinePolicySha256')) {
            $hash = $root.GetProperty($field).GetString()
            if ($hash -cnotmatch '\A[0-9a-f]{64}\z' -or $hash -cne $actual) { throw 'The packaged publisher policy differs from the verified test-only policy.' }
        }
        $commit = $root.GetProperty('sourceCommit').GetString()
        $candidate = Read-FixtureJson $CandidateManifestPath
        $baseline = Read-FixtureJson $BaselineManifestPath
        if ($commit -cnotmatch '\A[0-9a-f]{40}\z' -or $candidate.RootElement.GetProperty('commit').GetString() -cne $commit -or $baseline.RootElement.GetProperty('commit').GetString() -cne $commit) { throw 'Publisher fixture provenance differs from the exact tested manifests.' }
        return [pscustomobject]@{ Names = $values.ToArray(); SourceCommit = $commit; PolicySha256 = $actual }
    }
    finally { if ($baseline) { $baseline.Dispose() }; if ($candidate) { $candidate.Dispose() }; $policy.Dispose() }
}

function New-FixtureCertificate($State, [string]$Publisher) {
    $now = [DateTimeOffset]::UtcNow
    $State.RootKey = [Security.Cryptography.RSA]::Create(2048)
    $State.LeafKey = [Security.Cryptography.RSA]::Create(2048)
    $rootName = [Security.Cryptography.X509Certificates.X500DistinguishedNameBuilder]::new()
    $rootName.AddCommonName('AHK Manager disposable CI root ' + $State.Id)
    $rootRequest = [Security.Cryptography.X509Certificates.CertificateRequest]::new($rootName.Build(), $State.RootKey, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $null = $rootRequest.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($true, $true, 0, $true))
    $null = $rootRequest.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new([Security.Cryptography.X509Certificates.X509KeyUsageFlags]'KeyCertSign, CrlSign', $true))
    $null = $rootRequest.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509SubjectKeyIdentifierExtension]::new($rootRequest.PublicKey, $false))
    $State.RootCertificate = $rootRequest.CreateSelfSigned($now.AddMinutes(-5), $now.AddHours(2))
    $State.RootDer = $State.RootCertificate.RawData
    $State.RootThumbprint = $State.RootCertificate.Thumbprint
    $State.CrlPath = Join-Path $State.Root 'issuer.crl'
    $crlUri = [Uri]::new($State.CrlServer.Url)
    if ($crlUri.Scheme -cne 'http' -or $crlUri.Host -cne '127.0.0.1' -or -not $crlUri.IsLoopback) { throw 'The fixture CRL must use its owned loopback HTTP endpoint.' }
    $builder = [Security.Cryptography.X509Certificates.CertificateRevocationListBuilder]::new()
    $crl = $builder.Build($State.RootCertificate, [Numerics.BigInteger]::One, $now.AddHours(2), [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1, $now.AddMinutes(-2))
    [IO.File]::WriteAllBytes($State.CrlPath, $crl)
    $State.CrlServer.SetCrl($crl)
    $leafName = [Security.Cryptography.X509Certificates.X500DistinguishedNameBuilder]::new()
    $leafName.AddCommonName($Publisher)
    $leafRequest = [Security.Cryptography.X509Certificates.CertificateRequest]::new($leafName.Build(), $State.LeafKey, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $null = $leafRequest.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
    $null = $leafRequest.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new([Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature, $true))
    $usage = [Security.Cryptography.OidCollection]::new()
    $usage.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3')) | Out-Null
    $null = $leafRequest.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($usage, $true))
    $null = $leafRequest.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509SubjectKeyIdentifierExtension]::new($leafRequest.PublicKey, $false))
    $null = $leafRequest.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509AuthorityKeyIdentifierExtension]::CreateFromCertificate($State.RootCertificate, $true, $false))
    $null = $leafRequest.CertificateExtensions.Add([Security.Cryptography.X509Certificates.CertificateRevocationListBuilder]::BuildCrlDistributionPointExtension([string[]]@($crlUri.AbsoluteUri), $false))
    $serial = [byte[]]::new(16)
    [Security.Cryptography.RandomNumberGenerator]::Fill($serial)
    $publicLeaf = $leafRequest.Create($State.RootCertificate, $now.AddMinutes(-4), $now.AddMinutes(55), $serial)
    try { $State.LeafCertificate = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::CopyWithPrivateKey($publicLeaf, $State.LeafKey) }
    finally { $publicLeaf.Dispose() }
}

function Add-FixtureTrust($State) {
    Initialize-FixtureTrustWorker
    $State.TrustAttempted = $true
    Write-FixtureStage $State 'adding-public-root-to-disposable-machine-store'
    Wait-FixtureTrust $State ([AhkManager.TestSupport.PortableFixtureTrust]::Add($State.RootDer)) 'add'
    Write-FixtureStage $State 'public-machine-root-added'
}

function New-FixtureSignature($Certificate, [byte[]]$Manifest, [string]$Output) {
    $cms = [Security.Cryptography.Pkcs.SignedCms]::new([Security.Cryptography.Pkcs.ContentInfo]::new($Manifest), $true)
    $signer = [Security.Cryptography.Pkcs.CmsSigner]::new($Certificate)
    $signer.DigestAlgorithm = [Security.Cryptography.Oid]::new('2.16.840.1.101.3.4.2.1')
    $signer.IncludeOption = [Security.Cryptography.X509Certificates.X509IncludeOption]::EndCertOnly
    $cms.ComputeSignature($signer)
    [IO.File]::WriteAllBytes($Output, $cms.Encode())
}

function New-FixtureProbe($State, [string]$ApplicationRoot) {
    $root = Join-Path $State.Root 'native-probe'
    foreach ($directory in @($root, (Join-Path $root 'App'), (Join-Path $root 'App/backend'), (Join-Path $root 'App/backend/update'), (Join-Path $root 'Data'))) { New-FixtureDirectory $directory }
    [IO.File]::Copy((Join-Path $ApplicationRoot 'backend/update/AhkManager.UpdateHost.exe'), (Join-Path $root 'App/backend/update/AhkManager.UpdateHost.exe'), $false)
    [IO.File]::WriteAllText((Join-Path $root 'portable.json'), '{"schemaVersion":1,"applicationId":"Bezoro.AhkManager.Electron","dataDirectory":"Data"}', [Text.UTF8Encoding]::new($false))
    $source = @'
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
internal static class Program {
    private static int Main(string[] args) {
        try {
            if (args.Length != 1) return 64;
            var request = File.ReadAllBytes(args[0]);
            if (request.Length < 2 || request.Length > 65536) return 64;
            var helper = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "backend", "update", "AhkManager.UpdateHost.exe");
            var info = new ProcessStartInfo(helper, "--verify-portable-manifest") {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var child = Process.Start(info)) {
                if (child == null) return 70;
                var output = child.StandardOutput.ReadToEndAsync();
                var error = child.StandardError.ReadToEndAsync();
                child.StandardInput.BaseStream.Write(request, 0, request.Length);
                child.StandardInput.BaseStream.Flush();
                child.StandardInput.BaseStream.Close();
                if (!child.WaitForExit(30000)) return 75;
                if (output.Result.Length > 4096 || error.Result.Length > 65536) return 65;
                Console.Write(output.Result);
                return child.ExitCode;
            }
        } catch (Exception error) { Console.Error.WriteLine(error.GetType().Name); return 65; }
    }
}
'@
    $sourcePath = Join-Path $State.Root 'probe-parent.cs'
    [IO.File]::WriteAllText($sourcePath, $source, [Text.UTF8Encoding]::new($false))
    $State.ProbePath = Join-Path $root 'App/Bezoro.AhkManager.exe'
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw 'The disposable runner needs its installed .NET Framework C# compiler.' }
    & $compiler '/nologo' '/warnaserror+' '/target:exe' '/platform:x64' (('/out:' + $State.ProbePath)) $sourcePath | Out-Host
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $State.ProbePath -PathType Leaf)) { throw 'The isolated read-only native CMS probe could not be compiled.' }
}

function Invoke-FixtureNative($State, [string]$ManifestPath, [string]$SignaturePath, [string[]]$Publishers) {
    $requestPath = Join-Path $State.Root ('request-' + [Guid]::NewGuid().ToString('N') + '.json')
    [IO.File]::WriteAllText($requestPath, (@{ manifestPath = $ManifestPath; signaturePath = $SignaturePath; publishers = $Publishers } | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
    $start = [Diagnostics.ProcessStartInfo]::new($State.ProbePath)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.ArgumentList.Add($requestPath)
    $process = [Diagnostics.Process]::Start($start)
    try {
        $output = $process.StandardOutput.ReadToEndAsync()
        $errorOutput = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(45000)) { throw 'The isolated native CMS probe did not exit before its deadline.' }
        $text = $output.GetAwaiter().GetResult().Trim()
        $errorText = $errorOutput.GetAwaiter().GetResult()
        if ($text.Length -gt 4096 -or $errorText.Length -gt 65536) { throw 'The isolated native CMS response exceeded its limits.' }
        return [pscustomobject]@{ ExitCode = $process.ExitCode; Text = $text; ErrorType = $errorText.Trim() }
    }
    finally { $process.Dispose() }
}

function Get-FixtureChainStatus($Certificate) {
    $chain = [Security.Cryptography.X509Certificates.X509Chain]::new()
    try {
        $chain.ChainPolicy.RevocationMode = [Security.Cryptography.X509Certificates.X509RevocationMode]::Online
        $chain.ChainPolicy.RevocationFlag = [Security.Cryptography.X509Certificates.X509RevocationFlag]::ExcludeRoot
        $chain.ChainPolicy.UrlRetrievalTimeout = [TimeSpan]::FromSeconds(5)
        $null = $chain.ChainPolicy.ApplicationPolicy.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3'))
        $null = $chain.Build($Certificate)
        return (@($chain.ChainStatus | ForEach-Object { $_.Status.ToString() }) -join ',')
    }
    finally { $chain.Dispose() }
}

function New-PortableSigningFixture {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$ApplicationRoot,
        [Parameter(Mandatory = $true)][string]$PublisherPolicyPath,
        [Parameter(Mandatory = $true)][string]$CandidateManifestPath,
        [Parameter(Mandatory = $true)][string]$BaselineManifestPath,
        [Parameter(Mandatory = $true)][string]$OutputRoot
    )
    Assert-FixtureRunner
    Assert-FixturePath $OutputRoot
    $output = [IO.Path]::GetFullPath($OutputRoot)
    $temporary = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($env:RUNNER_TEMP)) + [IO.Path]::DirectorySeparatorChar
    if (-not $output.StartsWith($temporary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Signing fixture files must remain inside the disposable runner temporary directory.' }
    $policy = Get-FixturePolicy $ApplicationRoot $PublisherPolicyPath $CandidateManifestPath $BaselineManifestPath
    $checkpoint = $output + '.stages.json'
    if (Test-Path -LiteralPath $checkpoint) { throw 'The signing fixture checkpoint file must be fresh.' }
    New-FixtureDirectory $output
    $id = [Guid]::NewGuid().ToString('D')
    $state = [pscustomobject]@{ Id = $id; Root = $output; RootKey = $null; LeafKey = $null; RootCertificate = $null; LeafCertificate = $null; RootDer = $null; RootThumbprint = $null; TrustAttempted = $false; TrustWorker = $null; CrlPath = $null; CrlServer = $null; ProbePath = $null; CheckpointPath = $checkpoint; Stages = [Collections.Generic.List[object]]::new() }
    $script:fixtures.Add($id, $state)
    [IO.File]::WriteAllText((Join-Path $output 'fixture-owner.txt'), $id, [Text.UTF8Encoding]::new($false))
    $handle = [pscustomobject]@{ Id = $id; OutputRoot = $output }
    try {
        Add-Type -AssemblyName System.Security.Cryptography.Pkcs
        Initialize-FixtureTrustWorker
        Write-FixtureStage $state 'starting-owned-loopback-crl-server'
        $state.CrlServer = [AhkManager.TestSupport.PortableFixtureCrlServer]::new($id)
        Write-FixtureStage $state 'owned-loopback-crl-server-started'
        $publisher = if ($policy.Names.Count) { $policy.Names[0] } else { 'AHK Manager disposable portable CI publisher' }
        Write-FixtureStage $state 'creating-ephemeral-ca-leaf-and-signed-crl'
        New-FixtureCertificate $state $publisher
        Write-FixtureStage $state 'ephemeral-ca-leaf-and-signed-crl-created'
        Add-FixtureTrust $state
        $candidateBytes = Read-FixtureBytes $CandidateManifestPath 65536
        $baselineBytes = Read-FixtureBytes $BaselineManifestPath 65536
        $candidateHash = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($candidateBytes))
        $baselineHash = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($baselineBytes))
        $candidateSignature = Join-Path $output 'candidate-release.json.p7s'
        $baselineSignature = Join-Path $output 'baseline-release.json.p7s'
        Write-FixtureStage $state 'signing-exact-tested-manifest-bytes'
        New-FixtureSignature $state.LeafCertificate $candidateBytes $candidateSignature
        New-FixtureSignature $state.LeafCertificate $baselineBytes $baselineSignature
        Write-FixtureStage $state 'compiling-isolated-read-only-native-probe'
        New-FixtureProbe $state $ApplicationRoot
        Write-FixtureStage $state 'isolated-native-probe-compiled'
        foreach ($entry in @(@{ Manifest = $CandidateManifestPath; Signature = $candidateSignature; Hash = $candidateHash }, @{ Manifest = $BaselineManifestPath; Signature = $baselineSignature; Hash = $baselineHash })) {
            Write-FixtureStage $state ('verifying-production-native-manifest-' + $entry.Hash)
            $result = Invoke-FixtureNative $state $entry.Manifest $entry.Signature @($publisher)
            if ($result.ExitCode -ne 0 -or $result.Text -cne ('{"valid":true,"manifestSha256":"' + $entry.Hash + '"}')) {
                $chainStatus = Get-FixtureChainStatus $state.LeafCertificate
                throw "The production native CMS verifier refused the disposable fixture. Exit=$($result.ExitCode); response=$($result.Text); chain=$chainStatus; bridge=$($result.ErrorType)."
            }
        }
        $negativePublisher = 'AHK CI mismatched publisher ' + $id
        if ($negativePublisher -ceq $publisher) { $negativePublisher += ' other' }
        $wrongPublisher = Invoke-FixtureNative $state $CandidateManifestPath $candidateSignature @($negativePublisher)
        if ($wrongPublisher.ExitCode -ne 65 -or $wrongPublisher.Text -cne '{"valid":false}') { throw 'The production native CMS verifier accepted a different publisher.' }
        $tampered = Join-Path $output 'tampered-manifest.json'
        [IO.File]::WriteAllBytes($tampered, [byte[]]($candidateBytes + [byte]32))
        $changed = Invoke-FixtureNative $state $tampered $candidateSignature @($publisher)
        if ($changed.ExitCode -ne 65 -or $changed.Text -cne '{"valid":false}') { throw 'The production native CMS verifier accepted altered manifest bytes.' }
        if ((Get-FileHash -LiteralPath $CandidateManifestPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $candidateHash -or (Get-FileHash -LiteralPath $BaselineManifestPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $baselineHash) { throw 'Signing fixtures changed a tested manifest.' }
        if ($state.CrlServer.Requests -lt 1) { throw 'The production verifier did not retrieve the fixture CRL from its owned loopback endpoint.' }
        Write-FixtureStage $state 'production-native-signature-proofs-complete'
        return [pscustomobject]@{
            Id = $id; OutputRoot = $output; CandidateSignaturePath = $candidateSignature; BaselineSignaturePath = $baselineSignature; PublisherNames = $policy.Names; ProbePublisherName = $publisher
            Proof = [ordered]@{ fixture = $true; productionNativeCli = $true; publisherPolicyConfigured = $policy.Names.Count -gt 0; sourceCommit = $policy.SourceCommit; publisherPolicySha256 = $policy.PolicySha256; candidateManifestSha256 = $candidateHash; baselineManifestSha256 = $baselineHash; candidateVerified = $true; baselineVerified = $true; alteredManifestRefused = $true; differentPublisherRefused = $true; onlineRevocation = $true; crlDistribution = 'http-loopback'; crlRequests = $state.CrlServer.Requests; trustScope = 'LocalMachine/Root'; trustGuard = 'github-hosted-elevated-disposable-runner'; privateKeysPersisted = $false }
        }
    }
    catch {
        $failure = $_
        try { Remove-PortableSigningFixture $handle }
        catch { Write-Host ('Portable signing fixture cleanup: ' + $_.Exception.Message) }
        throw $failure
    }
}

function Remove-PortableSigningFixture {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)]$Fixture)
    Assert-FixtureRunner
    if (-not $script:fixtures.ContainsKey($Fixture.Id)) { return }
    $state = $script:fixtures[$Fixture.Id]
    try {
        if ($state.TrustWorker -and -not $state.TrustWorker.IsCompleted) {
            Write-FixtureStage $state 'cleanup-deferred-unobserved-trust-worker'
            throw 'The disposable fixture trust worker is still active. Its state was preserved for runner disposal.'
        }
        if ($state.CrlServer) {
            Write-FixtureStage $state 'stopping-owned-loopback-crl-server'
            if (-not $state.CrlServer.Stop(5000)) { throw 'The owned fixture CRL server did not stop. Its state was preserved for runner disposal.' }
            Write-FixtureStage $state 'owned-loopback-crl-server-stopped'
        }
        if ($state.TrustAttempted) {
            Write-FixtureStage $state 'removing-owned-public-machine-root'
            Wait-FixtureTrust $state ([AhkManager.TestSupport.PortableFixtureTrust]::Remove($state.RootDer)) 'remove'
            Write-FixtureStage $state 'owned-public-machine-root-removed'
        }
        Assert-FixturePath $state.Root
        if ([IO.File]::ReadAllText((Join-Path $state.Root 'fixture-owner.txt')) -cne $state.Id) { throw 'The owned signing fixture directory changed before cleanup.' }
        foreach ($entry in Get-ChildItem -LiteralPath $state.Root -Force -Recurse) { if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'A redirected signing fixture file was preserved during cleanup.' } }
        [IO.Directory]::Delete($state.Root, $true)
        Write-FixtureStage $state 'owned-fixture-files-removed'
    }
    finally {
        foreach ($resource in @($state.LeafCertificate, $state.RootCertificate, $state.LeafKey, $state.RootKey)) { if ($resource) { $resource.Dispose() } }
        $script:fixtures.Remove($state.Id)
    }
}

Export-ModuleMember -Function New-PortableSigningFixture, Remove-PortableSigningFixture
