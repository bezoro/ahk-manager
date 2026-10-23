using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Bezoro.AhkManager.WinUI.Models;

namespace Bezoro.AhkManager.Backend;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(RequestFrame))]
[JsonSerializable(typeof(ResponseFrame))]
[JsonSerializable(typeof(EmptyPayload))]
[JsonSerializable(typeof(HelloPayload))]
[JsonSerializable(typeof(HelloResult))]
[JsonSerializable(typeof(ParentProof))]
[JsonSerializable(typeof(PathsPayload))]
[JsonSerializable(typeof(PathPayload))]
[JsonSerializable(typeof(ScriptPayload))]
[JsonSerializable(typeof(MetadataPayload))]
[JsonSerializable(typeof(CollectionPayload))]
[JsonSerializable(typeof(CollectionIdPayload))]
[JsonSerializable(typeof(PreparePayload))]
[JsonSerializable(typeof(PreparedResult))]
[JsonSerializable(typeof(PerformPayload))]
[JsonSerializable(typeof(SettingsPayload))]
[JsonSerializable(typeof(ActivityPayload))]
[JsonSerializable(typeof(ImportPathPayload))]
[JsonSerializable(typeof(PreviewPayload))]
[JsonSerializable(typeof(ImportPrepared))]
[JsonSerializable(typeof(MigrationPathPayload))]
[JsonSerializable(typeof(ConfirmPreviewPayload))]
[JsonSerializable(typeof(MigrationPreview))]
[JsonSerializable(typeof(StartupTransferPreview))]
[JsonSerializable(typeof(MigrationReceipt))]
[JsonSerializable(typeof(ManagerSnapshot))]
[JsonSerializable(typeof(OperationResult))]
[JsonSerializable(typeof(CatalogResult))]
[JsonSerializable(typeof(ImmutableArray<ActivitySnapshot>))]
public partial class ProtocolJsonContext : JsonSerializerContext;
