using System.Globalization;
using System.Text;

namespace Bezoro.AhkManager.Backend;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // A fixed-path, read-only uninstall inspection opens neither profile nor
        // process authority. Additional arguments never select another target.
        if (args.Length > 0 && args[0] == "--inspect-owned-startup")
            return args.Length == 1 ? StartupUninstallInspection.Run() : 3;
        Console.InputEncoding = new UTF8Encoding(false, true);
        Console.OutputEncoding = new UTF8Encoding(false, true);
        try
        {
            if (args.Length > 0 && args[0] == "--administrator-bootstrap")
                return await ManagerElevation.Bootstrap(args).ConfigureAwait(false);
            if (
                args.Length is not (4 or 6)
                || args[0] != "--parent-pid"
                || !int.TryParse(
                    args[1],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int pid
                )
                || pid <= 0
                || args[2] != "--session"
                || args[3].Length != 64
                || args[3].Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
                || args.Length == 6 && args[4] != "--startup-adoption"
            )
                throw new ProtocolException("NotAuthenticated");
            string? startupDescriptor = args.Length == 6 ? args[5] : null;
            if (startupDescriptor is not null)
                _ = StartupAdoptionDescriptor.Parse(startupDescriptor);
            using var parent = new ParentGuard(pid, AppContext.BaseDirectory);
            string profile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Bezoro",
                "AhkManagerElectron"
            );
            var host = new StdioHost(
                Console.OpenStandardInput(),
                Console.OpenStandardOutput(),
                Console.Error,
                parent,
                args[3],
                profile,
                () =>
                    BackendSession.CreateProduction(
                        profile,
                        parent.Proof.ParentExecutable,
                        parent.Proof,
                        startupDescriptor
                    )
            );
            return await host.RunAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
            when (ex
                    is IOException
                        or ArgumentException
                        or UnauthorizedAccessException
                        or System.ComponentModel.Win32Exception
                        or System.Management.ManagementException
                        or System.Runtime.InteropServices.COMException
            )
        {
            Console.Error.WriteLine("backend:startup-refused:" + ex.GetType().Name);
            return 65;
        }
    }
}
