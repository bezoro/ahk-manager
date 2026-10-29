using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Bezoro.AhkManager.Core.Models;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
)]
[JsonSerializable(typeof(SettingsDocument))]
[JsonSerializable(typeof(JournalDocument))]
[JsonSerializable(typeof(ActivitySnapshot))]
public partial class ManagerJson : JsonSerializerContext { }
