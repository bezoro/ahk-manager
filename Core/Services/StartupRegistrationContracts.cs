using System.Collections.Immutable;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Bezoro.AhkManager.Core.Infrastructure;
using Bezoro.AhkManager.Core.Models;
using Microsoft.Win32;

namespace Bezoro.AhkManager.Core.Services;

public sealed record StartupInspection(
    bool Complete,
    ImmutableArray<string> ScriptPaths,
    ImmutableArray<Issue> Issues
);

public interface IUserStartupAdapter
{
    string? ReadEntry();
    bool CompareExchangeEntry(string? expected, string? replacement);
    Task<StartupInspection> InspectAsync(CancellationToken ct);
}

public sealed record StartupTaskRegistration(
    string Command,
    string UserSid,
    bool Enabled,
    bool HighestPrivileges,
    bool InteractiveToken,
    bool UserLogonOnly,
    string Source,
    string Fingerprint = ""
);

// Kept separate so existing read-only/fake registry adapters remain usable.
public interface IUserStartupTaskAdapter
{
    string UserSid { get; }
    StartupTaskRegistration? ReadTask();
    bool CompareExchangeTask(
        StartupTaskRegistration? expected,
        StartupTaskRegistration? replacement
    );
}
