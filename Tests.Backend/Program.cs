namespace Tests.Backend;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length != 3 || args[1] != "--root" || args[0] is not ("--backend" or "--core")) throw new ArgumentException("Use --backend|--core --root <exclusive fixture directory>.");
            string root = Path.GetFullPath(args[2]);
            if (Directory.Exists(root) || File.Exists(root)) throw new ArgumentException("Fixture root must be absent.");
            Directory.CreateDirectory(root);
            if (args[0] == "--core") await Bezoro.AhkManager.WinUI.Tests.Core.CoreSuite.RunAsync(root);
            else await BackendChecks.RunAsync(root);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
