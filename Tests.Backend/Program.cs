namespace Tests.Backend;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == "--inventory-scoped-readonly") return await OwnedAhkChecks.RunReadOnlyDiagnosticAsync(args);
            if (args.Length != 3 || args[1] != "--root" || args[0] is not ("--backend" or "--core" or "--corrections" or "--inventory" or "--ahk-owned")) throw new ArgumentException("Use --backend|--core|--corrections|--inventory|--ahk-owned --root <fixture directory>.");
            if (args[0] == "--ahk-owned" && !Path.IsPathFullyQualified(args[2])) throw new ArgumentException("Owned AHK fixture root must be an absolute local path.");
            string root = Path.GetFullPath(args[2]);
            if (args[0] == "--inventory") { NativeInventoryDiagnostic.Run(root); return 0; }
            if (Directory.Exists(root) || File.Exists(root)) throw new ArgumentException("Fixture root must be absent.");
            if (args[0] == "--ahk-owned") await OwnedAhkChecks.RunAsync(root);
            else
            {
                Directory.CreateDirectory(root);
                if (args[0] == "--corrections") await CorrectionChecks.RunAsync(root);
                else if (args[0] == "--core") await Bezoro.AhkManager.WinUI.Tests.Core.CoreSuite.RunAsync(root);
                else await BackendChecks.RunAsync(root);
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
