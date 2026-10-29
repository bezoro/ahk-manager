using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bezoro.AhkManager.Core.Infrastructure;
using Bezoro.AhkManager.Core.Models;

namespace Bezoro.AhkManager.Core.Services;

[JsonSourceGenerationOptions(
    WriteIndented = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
)]
[JsonSerializable(typeof(StartupContinuationTicket))]
[JsonSerializable(typeof(StartupContinuationRecord))]
[JsonSerializable(typeof(StartupRecoveryReceipt))]
internal partial class ContinuationJson : JsonSerializerContext;
