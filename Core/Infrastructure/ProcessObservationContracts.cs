using System.Collections.Immutable;
using System.Diagnostics;
using System.Management;
using System.Security.Principal;
using Bezoro.AhkManager.Core.Models;
using Bezoro.AhkManager.Core.Services;

namespace Bezoro.AhkManager.Core.Infrastructure;

public sealed record ObservedProcesses(
    ImmutableArray<ProcessSnapshot> Processes,
    bool Complete,
    ImmutableArray<Issue> Issues
);

public sealed record ProcessCommandRow(
    int Pid,
    string Name,
    string? ExecutablePath,
    string? CommandLine,
    long? CreationFileTime,
    int? SessionId
);

public sealed record ObservedProcessCommands(
    ImmutableArray<ProcessCommandRow> Rows,
    bool Complete,
    ImmutableArray<Issue> Issues
);

public interface IProcessObservationSource
{
    // Synchronous Windows boundary; ProcessManager always invokes it off the UI thread.
    ObservedProcessCommands Read(CancellationToken ct);
}
