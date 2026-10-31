using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Bezoro.AhkManager.UpdateHost;

internal static partial class SelfTests
{
    internal static int RunPortablePolicy()
    {
        int checks = 0;
        void Check(bool pass, string name)
        {
            if (!pass)
                throw new InvalidOperationException("FAIL " + name);
            checks++;
            Console.WriteLine("PASS " + name);
        }
        CheckPortablePayloadPolicy(Check);
        CheckPortableSignatures(Check);
        CheckStoragePathPolicy(Check);
        CheckAdmissionDiagnostics(Check);
        CheckApplyDiagnostics(Check);
        CheckPortablePromotionPolicy(Check);
        Console.WriteLine("PORTABLE POLICY checks=" + checks);
        return 0;
    }

    static string PortableHash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    static byte[] PortableBytes(string version, string path) =>
        Encoding.UTF8.GetBytes(version + "|" + path);

    static PortableDescriptor PortableFixtureDescriptor(string version) =>
        new(
            1,
            PortableLayout.ApplicationId,
            2,
            version,
            new string('a', 40),
            PortablePayload
                .RequiredFiles.Order(StringComparer.Ordinal)
                .Select(path => new PortableFile(
                    path,
                    PortableBytes(version, path).Length,
                    PortableHash(PortableBytes(version, path))
                ))
                .ToArray()
        );

    static void PortableRefused(Action action, Action<bool, string> check, string name)
    {
        bool refused = false;
        try
        {
            action();
        }
        catch (Exception ex) when (UpdateProtocol.Known(ex) || ex is CryptographicException)
        {
            refused = true;
        }
        check(refused, name);
    }

    static void CheckPortableProtocol(string root, UpdatePlan installed, Action<bool, string> check)
    {
        var plan = installed with
        {
            SchemaVersion = 2,
            Deployment = "portable",
            InstallerPath = Path.Combine(root, "installers", installed.Version, "archive.zip"),
        };
        UpdateProtocol.Validate(plan);
        UpdatePaths.ValidatePlanLocation(
            Path.Combine(root, "operations", plan.OperationId.ToString("D"), "plan.json"),
            plan,
            root
        );
        check(
            UpdateProtocol.Portable(plan)
                && UpdateProtocol.Read<UpdatePlan>(
                    JsonSerializer.SerializeToUtf8Bytes(plan, UpdateProtocol.Json)
                ) == plan,
            "portable protocol binds explicit deployment and an archive without changing installed defaults"
        );
        foreach (
            var invalid in new[]
            {
                plan with
                {
                    Deployment = null,
                },
                plan with
                {
                    SchemaVersion = 1,
                },
                plan with
                {
                    Deployment = "other",
                },
            }
        )
            PortableRefused(
                () => UpdateProtocol.Validate(invalid),
                check,
                "portable deployment cannot be selected by an ambiguous or legacy plan"
            );
        PortableRefused(
            () =>
                UpdatePaths.ValidateInstallerLocation(
                    installed.InstallerPath,
                    installed.Version,
                    root,
                    true
                ),
            check,
            "portable application cannot consume an installer cache entry"
        );
    }

    static void CheckPortablePayloadPolicy(Action<bool, string> check)
    {
        var descriptor = PortableFixtureDescriptor("1.2.0");
        byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(descriptor, UpdateProtocol.Json);
        check(
            PortablePayload.ReadDescriptor(serialized).Files.Length
                == PortablePayload.RequiredFiles.Length,
            "portable inventory retains every required launcher runtime and publisher policy file"
        );
        foreach (
            var invalid in new[]
            {
                descriptor with
                {
                    ApplicationId = "other",
                },
                descriptor with
                {
                    UpdateProtocol = 1,
                },
                descriptor with
                {
                    Commit = new string('A', 40),
                },
                descriptor with
                {
                    Files = descriptor.Files[..^1],
                },
                descriptor with
                {
                    Files = descriptor
                        .Files.Where(file => file.Path != "App/backend/AhkManager.Backend.exe")
                        .ToArray(),
                },
                descriptor with
                {
                    Files = descriptor.Files.Reverse().ToArray(),
                },
                descriptor with
                {
                    Files = [.. descriptor.Files, descriptor.Files[^1]],
                },
                descriptor with
                {
                    Files =
                    [
                        descriptor.Files[0] with
                        {
                            Path = "Data/settings.json",
                        },
                        .. descriptor.Files[1..],
                    ],
                },
                descriptor with
                {
                    Files =
                    [
                        descriptor.Files[0] with
                        {
                            Bytes = PortablePayload.MaximumFileBytes + 1,
                        },
                        .. descriptor.Files[1..],
                    ],
                },
            }
        )
            PortableRefused(
                () =>
                    PortablePayload.ReadDescriptor(
                        JsonSerializer.SerializeToUtf8Bytes(invalid, UpdateProtocol.Json)
                    ),
                check,
                "invalid portable inventory identity order ownership size and completeness are refused"
            );
        foreach (
            string path in new[]
            {
                "../outside",
                "App/../outside",
                "App/NUL.txt",
                "App/x:stream",
                "App/x.",
                "App//x",
                "App\\x",
                "/App/x",
            }
        )
            PortableRefused(
                () => PortablePayload.Relative(path),
                check,
                "portable inventory paths refuse traversal aliases streams and device names"
            );

        foreach (
            var entries in new (string Name, int Attributes)[][]
            {
                [("../outside", 0)],
                [("App/x:stream", 0)],
                [("App/NUL", 0)],
                [("App/a", 0), ("App/A", 0)],
                [("App/a", 0), ("App/a/b", 0)],
                [("App/a", unchecked((int)0xA0000000))],
                [("App/a", 0x400)],
                [("Data/", 0)],
                [("App\\a", 0)],
                [("App/a/", unchecked((int)0x80000000))],
            }
        )
        {
            using var memory = new MemoryStream();
            using (var writer = new ZipArchive(memory, ZipArchiveMode.Create, true))
                foreach (var entry in entries)
                {
                    var created = writer.CreateEntry(entry.Name);
                    created.ExternalAttributes = entry.Attributes;
                }
            memory.Position = 0;
            using var archive = new ZipArchive(memory, ZipArchiveMode.Read);
            PortableRefused(
                () => PortablePayload.ReadEntries(archive),
                check,
                "portable ZIP refuses traversal duplicates type collisions links and unexpected data directories"
            );
        }
        using var validMemory = new MemoryStream();
        using (var writer = new ZipArchive(validMemory, ZipArchiveMode.Create, true))
        {
            writer.CreateEntry("App/");
            using var output = writer.CreateEntry("App/empty.txt").Open();
        }
        validMemory.Position = 0;
        using var validArchive = new ZipArchive(validMemory, ZipArchiveMode.Read);
        check(
            PortablePayload.ReadEntries(validArchive).Count == 1,
            "portable ZIP accepts bounded ordinary files including legitimate empty resources"
        );
    }

    static void CheckPortableSignatures(Action<bool, string> check)
    {
        using var rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest(
            "CN=Portable fixture trust root",
            rootKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );
        rootRequest.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(true, false, 0, true)
        );
        rootRequest.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
                true
            )
        );
        using var root = rootRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(2)
        );
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=Portable fixture publisher",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true)
        );
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.3") }, true)
        );
        using var publicCertificate = request.Create(
            root,
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow.AddDays(1),
            RandomNumberGenerator.GetBytes(16)
        );
        using var certificate = publicCertificate.CopyWithPrivateKey(key);
        byte[] bytes = Encoding.UTF8.GetBytes("{\"portable\":\"exact UTF8 manifest\"}");
        var cms = new SignedCms(new ContentInfo(bytes), true);
        var signer = new CmsSigner(certificate)
        {
            DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"),
            IncludeOption = X509IncludeOption.EndCertOnly,
        };
        cms.ComputeSignature(signer);
        byte[] signature = cms.Encode();
        PortableSignatureVerification.Verify(
            bytes,
            signature,
            ["Portable fixture publisher"],
            root
        );
        check(
            true,
            "portable detached CMS authenticates exact metadata bytes and a trusted code-signing publisher"
        );
        PortableRefused(
            () =>
                PortableSignatureVerification.Verify(
                    Encoding.UTF8.GetBytes("changed"),
                    signature,
                    ["Portable fixture publisher"],
                    root
                ),
            check,
            "portable CMS rejects metadata tampering"
        );
        PortableRefused(
            () =>
                PortableSignatureVerification.Verify(bytes, signature, ["Another publisher"], root),
            check,
            "portable CMS rejects a different expected publisher"
        );
        PortableRefused(
            () =>
                PortableSignatureVerification.Verify(
                    bytes,
                    signature,
                    ["Portable fixture publisher"]
                ),
            check,
            "portable CMS never adds its fixture root to the system trust store"
        );
    }

    static PortableLayout CreatePortableFixture(
        string root,
        string version,
        string? dataDirectory = null,
        bool protect = true
    )
    {
        SnapshotSecurity.CreatePrivateDirectory(root);
        var descriptor = PortableFixtureDescriptor(version);
        foreach (var entry in descriptor.Files)
        {
            string file = Path.Combine(root, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, PortableBytes(version, entry.Path));
        }
        Evidence.WriteNew(Path.Combine(root, PortableLayout.Descriptor), descriptor);
        Evidence.WriteNew(
            Path.Combine(root, "portable.json"),
            new PortableConfiguration(1, PortableLayout.ApplicationId, dataDirectory ?? "Data")
        );
        var layout = PortableLayout.Read(root);
        if (protect)
        {
            SnapshotSecurity.ProtectOwnedPath(layout.Application, true);
            foreach (
                string file in descriptor
                    .Files.Select(entry =>
                        Path.Combine(root, entry.Path.Replace('/', Path.DirectorySeparatorChar))
                    )
                    .Append(layout.Marker)
                    .Append(Path.Combine(root, PortableLayout.Descriptor))
            )
                SnapshotSecurity.ProtectOwnedPath(file, false);
        }
        PortableInitialization.InitializeData(layout);
        Directory.CreateDirectory(layout.Updates);
        return layout;
    }

    static void CheckPortableLayouts(string root, Action<bool, string> check)
    {
        string container = Path.Combine(root, "portable layout");
        var layout = CreatePortableFixture(container, "1.2.0");
        check(
            layout.Application == Path.Combine(container, "App")
                && layout.Data == Path.Combine(container, "Data"),
            "portable marker selects separate fixed application and mutable data directories"
        );
        foreach (
            string invalid in new[]
            {
                ".",
                "..",
                "Data/alias",
                "App",
                "App\\nested",
                "portable.json",
                "portable-package.json\\nested",
                "portable-data-move.json",
                "portable-data-move.json\\nested",
                ".app-stage-" + Guid.NewGuid().ToString("N"),
                ".app-previous-" + Guid.NewGuid().ToString("N") + "\\nested",
                ".portable-data-config-" + Guid.NewGuid().ToString("D") + ".tmp",
                ".portable-data-move-" + Guid.NewGuid().ToString("D") + ".tmp\\nested",
                Path.GetPathRoot(root)!,
            }
        )
        {
            File.WriteAllText(
                layout.Marker,
                JsonSerializer.Serialize(
                    new PortableConfiguration(1, PortableLayout.ApplicationId, invalid),
                    UpdateProtocol.Json
                )
            );
            PortableRefused(
                () => PortableLayout.Read(container),
                check,
                "portable data paths refuse application overlap file aliases ancestors and noncanonical forms"
            );
        }
        File.WriteAllText(
            layout.Marker,
            "{\"schemaVersion\":1,\"applicationId\":\"Bezoro.AhkManager.Electron\",\"dataDirectory\":\"Data\",\"dataDirectory\":\"Other\"}"
        );
        PortableRefused(
            () => PortableLayout.Read(container),
            check,
            "portable configuration rejects duplicate properties"
        );
        File.WriteAllText(
            layout.Marker,
            JsonSerializer.Serialize(
                new PortableConfiguration(1, PortableLayout.ApplicationId, "Data"),
                UpdateProtocol.Json
            )
        );

        string unsafeContainer = Path.Combine(root, "foreign portable root");
        var unsafeLayout = CreatePortableFixture(unsafeContainer, "1.2.0", protect: false);
        File.WriteAllText(Path.Combine(unsafeContainer, "foreign.txt"), "unrelated owner data");
        string before = SnapshotSecurity.Read(unsafeContainer, true);
        PortableRefused(
            () => PortableInitialization.Initialize(unsafeLayout),
            check,
            "first-time portable initialization refuses a container with unrelated root files"
        );
        check(
            before == SnapshotSecurity.Read(unsafeContainer, true),
            "refused portable initialization leaves existing container permissions unchanged"
        );

        string foreign = Path.Combine(container, "notes.txt");
        File.WriteAllText(foreign, "private user note");
        PortableInitialization.Initialize(layout);
        check(
            File.ReadAllText(foreign) == "private user note",
            "already-private portable startup preserves unrelated root files without changing their permissions"
        );
        string fakeStage = Path.Combine(container, ".app-stage-" + Guid.NewGuid().ToString("N"));
        SnapshotSecurity.CreatePrivateDirectory(fakeStage);
        PortableRefused(
            () => PortableInitialization.VerifyRootMembers(layout),
            check,
            "portable recovery directories need a matching owned operation plan"
        );

        string pinnedParent = Path.Combine(root, "portable pinned parent");
        SnapshotSecurity.CreatePrivateDirectory(pinnedParent);
        var pinnedLayout = CreatePortableFixture(Path.Combine(pinnedParent, "Manager"), "1.2.0");
        using (var retained = new DirectoryLease([pinnedLayout.Root]))
        {
            PortableRefused(
                () => Directory.Move(pinnedParent, pinnedParent + "-moved"),
                check,
                "portable apply retains repository ancestors while replacing the application"
            );
            PortableRefused(
                () => Directory.Move(pinnedLayout.Root, pinnedLayout.Root + "-moved"),
                check,
                "portable apply retains its dedicated container against concurrent renames"
            );
            string movedApplication = pinnedLayout.Application + "-moved";
            Directory.Move(pinnedLayout.Application, movedApplication);
            Directory.Move(movedApplication, pinnedLayout.Application);
            check(
                Directory.Exists(pinnedLayout.Application),
                "portable container retention still permits atomic application directory promotion"
            );
        }
    }

    sealed class PortableExitedParent : IParentProcess
    {
        public bool Exited => true;
        public int ExitCode => 0;
    }

    static void CheckPortableDataMoveLeases(string root, Action<bool, string> check)
    {
        var layout = CreatePortableFixture(Path.Combine(root, "portable data lease"), "1.2.0");
        string destinationParent = Path.Combine(root, "portable data destination parent");
        SnapshotSecurity.CreatePrivateDirectory(destinationParent);
        string destination = Path.Combine(destinationParent, "Selected Data");
        string operation = Guid.NewGuid().ToString("D");
        string stage = Path.Combine(
            destinationParent,
            ".Selected Data.ahk-manager-move-" + operation
        );
        SnapshotSecurity.CreatePrivateDirectory(stage);
        PortableDataMove pending;
        using (var identities = new DirectoryLease([layout.Data, stage]))
            pending = new(
                1,
                operation,
                layout.Data,
                destination,
                stage,
                DateTimeOffset.UtcNow.ToString(
                    "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                    System.Globalization.CultureInfo.InvariantCulture
                ),
                PortableDataMoveLease.Identify(identities.Identity(layout.Data)),
                PortableDataMoveLease.Identify(identities.Identity(stage)),
                null
            );
        string intent = Path.Combine(layout.Root, "portable-data-move.json");
        Evidence.WriteNew(intent, pending);
        using (var retained = PortableDataMoveLease.Acquire(layout))
        {
            PortableRefused(
                () => Directory.Move(stage, stage + "-replaced"),
                check,
                "portable data copy retains the private stage against replacement before copying profile data"
            );
            PortableRefused(
                () => Directory.Move(layout.Data, layout.Data + "-replaced"),
                check,
                "portable data copy retains the original profile namespace"
            );
            PortableRefused(
                () => Directory.Move(destinationParent, destinationParent + "-replaced"),
                check,
                "portable data copy retains the selected destination ancestors"
            );
            File.WriteAllText(Path.Combine(stage, "profile-sentinel.txt"), "private copied state");
            retained.ReleaseStage();
            Directory.Move(stage, destination);
            check(
                File.ReadAllText(Path.Combine(destination, "profile-sentinel.txt"))
                    == "private copied state",
                "releasing only the data staging leaf permits promotion of the private verified copy"
            );
            PortableRefused(
                () => Directory.Move(destinationParent, destinationParent + "-replaced"),
                check,
                "data promotion keeps destination ancestors retained after the stage leaf is released"
            );
        }
        using (var resumed = PortableDataMoveLease.Acquire(layout))
            check(
                true,
                "portable data lease resumes a promoted copy before the marker is committed"
            );
        File.WriteAllText(
            layout.Marker,
            JsonSerializer.Serialize(
                new PortableConfiguration(1, PortableLayout.ApplicationId, destination),
                UpdateProtocol.Json
            )
        );
        Directory.Delete(layout.Data, true);
        var committed = PortableLayout.Read(layout.Root);
        using (var resumed = PortableDataMoveLease.Acquire(committed))
        {
            File.Delete(intent);
            check(
                !File.Exists(intent),
                "committed data move cleanup needs neither the old backup nor a retained intent file"
            );
        }
        Evidence.WriteNew(
            intent,
            pending with
            {
                StageIdentity = pending.StageIdentity with { Ino = "0" },
            }
        );
        PortableRefused(
            () =>
            {
                using var changed = PortableDataMoveLease.Acquire(committed);
            },
            check,
            "portable data lease refuses a destination whose retained identity differs from prepared intent"
        );
    }

    static void CheckPortableStorageLeases(string root, Action<bool, string> check)
    {
        string parent = Path.Combine(root, "portable lifetime parent");
        SnapshotSecurity.CreatePrivateDirectory(parent);
        string data = Path.Combine(parent, "Selected Data");
        var layout = CreatePortableFixture(
            Path.Combine(root, "portable lifetime Manager"),
            "1.2.0",
            data
        );
        string marker = File.ReadAllText(layout.Marker);
        using (var retained = PortableStorageLease.Acquire(layout))
        {
            PortableRefused(
                () => Directory.Move(parent, parent + "-replaced"),
                check,
                "active portable storage retains custom data ancestors during normal application writes"
            );
            PortableRefused(
                () => Directory.Move(data, data + "-replaced"),
                check,
                "active portable storage retains its custom data leaf against replacement"
            );
            PortableRefused(
                () => Directory.Move(layout.Root, layout.Root + "-replaced"),
                check,
                "active portable storage retains its application container against replacement"
            );
            File.WriteAllText(Path.Combine(data, "active-profile.txt"), "normal profile write");
            File.WriteAllText(layout.Marker, marker);
            check(
                File.ReadAllText(Path.Combine(data, "active-profile.txt"))
                    == "normal profile write",
                "portable lifetime retention permits ordinary profile and settings writes"
            );
        }
        string moved = data + "-moved";
        Directory.Move(data, moved);
        Directory.Move(moved, data);
        check(
            Directory.Exists(data),
            "normal portable shutdown releases the custom data namespace for deliberate relocation"
        );
    }

    static byte[] PortableArchive(string version)
    {
        using var bytes = new MemoryStream();
        using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        {
            foreach (var file in PortableFixtureDescriptor(version).Files)
            {
                using var output = archive.CreateEntry(file.Path).Open();
                output.Write(PortableBytes(version, file.Path));
            }
            using (var output = archive.CreateEntry(PortableLayout.Descriptor).Open())
                output.Write(
                    JsonSerializer.SerializeToUtf8Bytes(
                        PortableFixtureDescriptor(version),
                        UpdateProtocol.Json
                    )
                );
            using (var output = archive.CreateEntry("portable.json").Open())
                output.Write(
                    JsonSerializer.SerializeToUtf8Bytes(
                        new PortableConfiguration(1, PortableLayout.ApplicationId, "Data"),
                        UpdateProtocol.Json
                    )
                );
        }
        return bytes.ToArray();
    }

    static async Task CheckPortableTransactions(string root, Action<bool, string> check)
    {
        string[] phases =
        [
            "success",
            "Previous portable payload preserved.",
            "New portable payload promoted.",
            "Portable launcher replaced atomically.",
            "Portable inventory replaced atomically.",
        ];
        for (int index = 0; index < phases.Length; index++)
        {
            string fixture = Path.Combine(root, "portable transaction " + index);
            string externalData = Path.Combine(root, "portable custom data " + index);
            var layout = CreatePortableFixture(fixture, "1.2.0", externalData);
            string sentinel = Path.Combine(layout.Data, "settings-sentinel.txt");
            File.WriteAllText(sentinel, "unchanged portable user state");
            using var pinnedData = FileLease.Open(sentinel, 4096);
            string note = Path.Combine(layout.Root, "notes.txt");
            File.WriteAllText(note, "unrelated root file");
            byte[] bytes = PortableArchive("1.2.1");
            string cache = Path.Combine(layout.Updates, "installers", "1.2.1");
            Directory.CreateDirectory(cache);
            string archive = Path.Combine(cache, "archive.zip");
            File.WriteAllBytes(archive, bytes);
            string hash = PortableHash(bytes);
            Evidence.WriteNew(
                Path.Combine(cache, "receipt.json"),
                new UpdateReceipt(1, 2, "1.2.1", hash, bytes.Length)
            );
            Guid id = Guid.NewGuid();
            string operation = UpdatePaths.OperationDirectory(layout.Updates, id);
            Directory.CreateDirectory(operation);
            var plan = new UpdatePlan(
                2,
                id,
                42,
                DateTimeOffset.UtcNow,
                layout.Application,
                archive,
                hash,
                "1.2.1",
                Deployment: "portable"
            );
            Evidence.WriteNew(Path.Combine(operation, "plan.json"), plan);
            Evidence.WriteNew(
                Path.Combine(operation, "commit.json"),
                new UpdateCommit(1, id, 42, plan.ParentStartedAtUtc)
            );
            using var package = new PackageLease(archive, "1.2.1", hash, layout.Updates, true);
            _ = PortablePayload.ValidateArchive(package, "1.2.1");
            using var original = new PortableInventory(layout.Root);
            bool fault = false;
            void Log(string message)
            {
                if (!fault && message == phases[index])
                {
                    fault = true;
                    throw new IOException("injected promotion boundary failure");
                }
            }
            void VerifyImage(string application, string version)
            {
                if (
                    !File.ReadAllBytes(Path.Combine(application, PortableLayout.Executable))
                        .AsSpan()
                        .SequenceEqual(PortableBytes(version, "App/" + PortableLayout.Executable))
                )
                    throw new InvalidDataException("The inert version fixture changed.");
            }
            var effects = new PortableApplyEffects(
                plan,
                operation,
                new PortableExitedParent(),
                package,
                layout,
                original,
                Log,
                VerifyImage
            );
            check(
                await effects.WaitForParentExit(),
                "portable filesystem transaction requires the exact normal-shutdown commit"
            );
            var backup = await effects.Backup();
            var result = await effects.Install();
            string stage = Path.Combine(layout.Root, ".app-stage-" + id.ToString("N"));
            string previous = Path.Combine(layout.Root, ".app-previous-" + id.ToString("N"));
            void WriteOutcome(string outcome)
            {
                string outcomePath = Path.Combine(operation, "outcome.json");
                File.Delete(outcomePath);
                Evidence.WriteNew(
                    outcomePath,
                    new UpdateOutcome(
                        1,
                        id,
                        outcome,
                        "Portable cleanup policy fixture.",
                        plan.Version,
                        0,
                        null,
                        DateTimeOffset.UtcNow,
                        backup.Directory
                    )
                );
            }
            if (index == 0)
            {
                check(
                    result.ExitCode == 0,
                    "portable ZIP payload launcher and inventory promote without invoking an installer"
                );
                effects.VerifyInstalled("1.2.1");
                PortableInitialization.Initialize(layout);
                check(
                    File.ReadAllText(layout.Marker)
                        .Contains(externalData.Replace("\\", "\\\\"), StringComparison.Ordinal),
                    "portable update preserves custom data configuration instead of the archive template"
                );
                check(
                    !effects.CleanupCompleted()
                        && Directory.Exists(stage)
                        && Directory.Exists(previous),
                    "portable staging is preserved before a durable completed outcome exists"
                );
                WriteOutcome("Unknown");
                check(
                    !effects.CleanupCompleted()
                        && Directory.Exists(stage)
                        && Directory.Exists(previous),
                    "unknown portable outcomes never clean staging or previous application copies"
                );
                WriteOutcome("Installed");
                string foreignStage = Path.Combine(stage, "foreign.txt");
                File.WriteAllText(foreignStage, "unexpected staging file");
                check(
                    !effects.CleanupCompleted()
                        && File.ReadAllText(foreignStage) == "unexpected staging file"
                        && Directory.Exists(previous),
                    "portable cleanup preserves both workspaces when an unrelated staged file appears"
                );
                File.Delete(foreignStage);
                string foreignPrevious = Path.Combine(previous, "foreign.txt");
                File.WriteAllText(foreignPrevious, "unexpected previous application file");
                check(
                    !effects.CleanupCompleted()
                        && File.ReadAllText(foreignPrevious)
                            == "unexpected previous application file"
                        && Directory.Exists(stage),
                    "portable cleanup refuses a changed previous application before deleting either workspace"
                );
                File.Delete(foreignPrevious);
                check(
                    effects.CleanupCompleted()
                        && !Directory.Exists(stage)
                        && !Directory.Exists(previous),
                    "durably installed portable updates remove only verified staging duplicates"
                );
                check(
                    File.Exists(Path.Combine(backup.Directory, PortableLayout.Executable))
                        && File.Exists(Path.Combine(operation, "launcher-before.exe"))
                        && File.Exists(Path.Combine(operation, "descriptor-before.json"))
                        && File.Exists(Path.Combine(operation, "outcome.json")),
                    "portable staging cleanup preserves complete recovery snapshots metadata and durable outcome"
                );
                effects.VerifyInstalled("1.2.1");
            }
            else
            {
                check(
                    fault && result.ExitCode == 20,
                    "portable promotion failure is confirmed at each directory launcher and descriptor boundary"
                );
                _ = await effects.Restore(backup);
                effects.VerifyInstalled("1.2.0");
                check(
                    File.ReadAllBytes(layout.Launcher)
                        .AsSpan()
                        .SequenceEqual(PortableBytes("1.2.0", PortableLayout.Executable)),
                    "portable recovery restores the exact previous launcher with the complete application"
                );
                WriteOutcome("RolledBack");
                check(
                    !effects.CleanupCompleted()
                        && Directory.Exists(stage)
                        && Directory.Exists(previous),
                    "failed portable updates retain staging and preserved applications after automatic recovery"
                );
            }
            check(
                pinnedData.Hash()
                    == PortableHash(Encoding.UTF8.GetBytes("unchanged portable user state"))
                    && File.ReadAllText(note) == "unrelated root file",
                "portable update and recovery preserve pinned external data and unrelated root files"
            );
        }
    }
}
