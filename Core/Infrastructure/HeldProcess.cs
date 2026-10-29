using System.Collections.Immutable;
using System.Diagnostics;
using System.Management;
using System.Security.Principal;
using Bezoro.AhkManager.Core.Models;
using Bezoro.AhkManager.Core.Services;

namespace Bezoro.AhkManager.Core.Infrastructure;

internal sealed record HeldProcess(NativeProcessIdentity Handle, ProcessSnapshot Snapshot);
