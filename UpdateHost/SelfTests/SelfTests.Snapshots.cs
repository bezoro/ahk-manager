namespace Bezoro.AhkManager.UpdateHost;

internal static partial class SelfTests
{
    static void CheckSnapshotAclComparison(Action<bool, string> check)
    {
        const string equivalentAcl = "D:P(A;OICI;FA;;;SY)(A;OICI;FR;;;BU)";
        check(
            SnapshotSecurity.Equivalent(
                equivalentAcl,
                equivalentAcl.Replace("D:P", "D:PAI", StringComparison.Ordinal)
            ),
            "DACL comparison ignores only auto-inheritance bookkeeping normalization"
        );
        foreach (
            string changedAcl in new[]
            {
                equivalentAcl.Replace("D:P", "D:", StringComparison.Ordinal),
                equivalentAcl.Replace("FR", "FA", StringComparison.Ordinal),
                equivalentAcl.Replace("BU", "AU", StringComparison.Ordinal),
                equivalentAcl.Replace("OICI;FR", "OICIIO;FR", StringComparison.Ordinal),
                equivalentAcl.Replace("OICI;FR", "OICIID;FR", StringComparison.Ordinal),
                "D:P(A;OICI;FR;;;BU)(A;OICI;FA;;;SY)",
            }
        )
            check(
                !SnapshotSecurity.Equivalent(equivalentAcl, changedAcl),
                "DACL comparison preserves protection access masks principals inheritance and ACE order"
            );
    }

    static async Task<string> CheckSnapshotContents(string root, Action<bool, string> check)
    {
        string original = Path.Combine(root, "original"),
            copy = Path.Combine(root, "copy");
        Directory.CreateDirectory(original);
        Directory.CreateDirectory(Path.Combine(original, "empty"));
        await File.WriteAllTextAsync(
            Path.Combine(original, "Bezoro.AhkManager.exe"),
            "inert owned fixture, never executed"
        );
        await File.WriteAllTextAsync(
            Path.Combine(original, "foreign-user-file.txt"),
            "preserved unrelated data"
        );
        var manifest = await InstallationSnapshot.Copy(original, copy);
        InstallationSnapshot.Verify(copy, manifest);
        check(
            manifest.Files.Length == 2
                && Directory.Exists(Path.Combine(copy, "empty"))
                && await File.ReadAllTextAsync(Path.Combine(copy, "foreign-user-file.txt"))
                    == "preserved unrelated data",
            "snapshot preserves every regular file and empty directory including unrelated data"
        );
        await File.WriteAllTextAsync(
            Path.Combine(copy, "foreign-user-file.txt"),
            "changed after backup"
        );
        bool changedCopy = false;
        try
        {
            InstallationSnapshot.Verify(copy, manifest);
        }
        catch (IOException)
        {
            changedCopy = true;
        }

        check(changedCopy, "a modified backup cannot authorize restoration");
        return original;
    }

    static async Task CheckSnapshotHardLinks(
        string root,
        string original,
        Action<bool, string> check
    )
    {
        string link = Path.Combine(original, "linked.txt");
        if (!Native.CreateHardLink(link, Path.Combine(original, "foreign-user-file.txt"), 0))
            throw new System.ComponentModel.Win32Exception(
                System.Runtime.InteropServices.Marshal.GetLastWin32Error()
            );
        bool linked = false;
        try
        {
            await InstallationSnapshot.Copy(original, Path.Combine(root, "refused-link-copy"));
        }
        catch (IOException)
        {
            linked = true;
        }

        check(linked, "multiply linked installation files are refused before any installer effect");
    }
}
