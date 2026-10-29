using System.Diagnostics;
using System.Security.Principal;
using Bezoro.AhkManager.Core.Infrastructure;

namespace Tests.Backend;

internal static class NativeInventoryDiagnostic
{
    // Read-only diagnosis of the retained fixture scope. Never reads script bodies,
    // prints argv/SIDs, starts a process, or changes an observation decision.
    internal static void Run(string root)
    {
        var excluded = new Dictionary<int, long>();
        foreach (string line in File.ReadLines(Path.Combine(root, "fixture-observation-scope.txt")))
            if (line.StartsWith("excludedOlderOpaquePid=", StringComparison.Ordinal))
            {
                var parts = line.Split(';');
                excluded.Add(
                    int.Parse(parts[0].AsSpan("excludedOlderOpaquePid=".Length)),
                    long.Parse(parts[1].AsSpan("creation=".Length))
                );
            }
        var rows = new WmiProcessObservationSource().Read(CancellationToken.None);
        using var self = Process.GetCurrentProcess();
        using var user = WindowsIdentity.GetCurrent();
        int shown = 0;
        var counts = new SortedDictionary<string, int>();
        void Count(string category) => counts[category] = counts.GetValueOrDefault(category) + 1;
        void Report(string category, int pid, string? image, int? session)
        {
            Count(category);
            if (shown++ < 128)
                Console.WriteLine(
                    $"candidate category={category};pid={pid};image={image ?? "unavailable"};session={session?.ToString() ?? "unavailable"}"
                );
        }
        foreach (var row in rows.Rows)
        {
            if (row.SessionId is int session && session != self.SessionId)
            {
                Count("other-session");
                continue;
            }
            if (
                excluded.TryGetValue(row.Pid, out long created)
                && row.CreationFileTime == created
                && (row.ExecutablePath is null || row.CommandLine is null)
            )
            {
                Count("retained-exclusion");
                continue;
            }
            using var held = new NativeProcessIdentity(row.Pid);
            if (!held.TryRead(out var stamp))
            {
                Report("native-unreadable", row.Pid, null, row.SessionId);
                continue;
            }
            if (stamp.Sid != user.User!.Value || stamp.Session != self.SessionId)
            {
                Count("other-user-session");
                continue;
            }
            try
            {
                string? product = FileVersionInfo.GetVersionInfo(stamp.Executable).ProductName;
                if (string.IsNullOrWhiteSpace(product))
                    Report(
                        "native-image-product-unclassified",
                        row.Pid,
                        stamp.Executable,
                        stamp.Session
                    );
                else if (product.Contains("AutoHotkey", StringComparison.OrdinalIgnoreCase))
                    Report(
                        row.CommandLine is null ? "AHK-argv-unavailable" : "AHK-argv-present",
                        row.Pid,
                        stamp.Executable,
                        stamp.Session
                    );
                else
                    Count("independent-non-AHK");
            }
            catch (Exception ex)
                when (ex
                        is IOException
                            or UnauthorizedAccessException
                            or System.ComponentModel.Win32Exception
                            or InvalidOperationException
                )
            {
                Report(
                    "native-image-metadata-unreadable",
                    row.Pid,
                    stamp.Executable,
                    stamp.Session
                );
            }
        }
        Console.WriteLine("WMI complete=" + rows.Complete + ";rows=" + rows.Rows.Length);
        foreach (var pair in counts)
            Console.WriteLine("count " + pair.Key + "=" + pair.Value);
        Console.WriteLine("candidate rows omitted=" + Math.Max(0, shown - 128));
    }
}
