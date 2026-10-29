using System.Collections.Immutable;
using System.Diagnostics;
using System.Management;
using System.Security.Principal;
using Bezoro.AhkManager.Core.Models;
using Bezoro.AhkManager.Core.Services;

namespace Bezoro.AhkManager.Core.Infrastructure;

public sealed class WmiProcessObservationSource : IProcessObservationSource
{
    public ObservedProcessCommands Read(CancellationToken ct)
    {
        var rows = ImmutableArray.CreateBuilder<ProcessCommandRow>();
        try
        {
            using var search = new ManagementObjectSearcher(
                "SELECT ProcessId,CreationDate,ExecutablePath,CommandLine,Name,SessionId FROM Win32_Process"
            );
            search.Options.Timeout = TimeSpan.FromSeconds(5);
            using var results = search.Get();
            foreach (ManagementObject row in results)
                using (row)
                {
                    ct.ThrowIfCancellationRequested();
                    if (rows.Count >= 8192)
                        return new(
                            rows.ToImmutable(),
                            false,
                            [new(ReasonCode.ObservationIncomplete)]
                        );
                    long? created = null;
                    if (row["CreationDate"] is string date)
                        try
                        {
                            created = ManagementDateTimeConverter
                                .ToDateTime(date)
                                .ToUniversalTime()
                                .ToFileTimeUtc();
                        }
                        catch (ArgumentException) { }
                    rows.Add(
                        new(
                            checked((int)(uint)row["ProcessId"]),
                            row["Name"] as string ?? "",
                            row["ExecutablePath"] as string,
                            row["CommandLine"] as string,
                            created,
                            row["SessionId"] is uint session ? checked((int)session) : null
                        )
                    );
                }
            return new(rows.ToImmutable(), true, []);
        }
        catch (Exception ex)
            when (ex
                    is ManagementException
                        or System.Runtime.InteropServices.COMException
                        or UnauthorizedAccessException
                        or InvalidOperationException
            )
        {
            return new(
                rows.ToImmutable(),
                false,
                [new(ReasonCode.ObservationIncomplete, ex.Message)]
            );
        }
    }
}
