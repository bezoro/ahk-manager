namespace Bezoro.AhkManager.UpdateHost;

internal static partial class SelfTests
{
    internal static async Task<int> Run(string root)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows update validation requires Windows.");
        root = UpdatePaths.Local(root);
        UpdatePaths.NoRedirects(root);
        if (Directory.Exists(root) || File.Exists(root))
            throw new IOException("Self-test root must be absent.");
        Directory.CreateDirectory(root);
        int checks = 0;
        void Check(bool pass, string name)
        {
            if (!pass)
                throw new InvalidOperationException("FAIL " + name);
            checks++;
            Console.WriteLine("PASS " + name);
        }

        var plan = new UpdatePlan(
            1,
            Guid.NewGuid(),
            42,
            DateTimeOffset.UtcNow,
            Path.Combine(root, "installation"),
            Path.Combine(root, "installers", "1.2.1", "installer.exe"),
            new string('a', 64),
            "1.2.1"
        );
        CheckAdmissionDiagnostics(Check);
        CheckProtocol(root, plan, Check);
        CheckReleaseVersions(root, plan, Check);
        CheckInstallerCommandLine(root, plan, Check);
        CheckSnapshotAclComparison(Check);
        CheckPortableProtocol(root, plan, Check);
        CheckPortablePayloadPolicy(Check);
        CheckPortableSignatures(Check);
        CheckPortableLayouts(root, Check);
        CheckPortableDataMoveLeases(root, Check);
        await CheckPortableTransactions(root, Check);
        await CheckInstallerCache(root, plan, Check);
        await CheckTestInstallerCache(root, Check);
        await CheckApplyTransactions(root, plan, Check);
        await CheckShutdownAuthorization(root, plan, Check);
        string original = await CheckSnapshotContents(root, Check);
        await CheckSnapshotPermissions(root, Check);
        CheckProfilePermissions(root, Check);
        await CheckSnapshotHardLinks(root, original, Check);
        Console.WriteLine("UPDATE HOST checks=" + checks);
        return 0;
    }
}
