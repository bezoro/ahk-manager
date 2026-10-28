using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bezoro.AhkManager.WinUI.Models;

namespace Bezoro.AhkManager.Backend;

public sealed record RequestFrame(int Version, string Id, string Session, string Action, JsonElement Payload);
public sealed record ResponseFrame(int Version, string Kind, string? Id, string Session, string Action, string Code, JsonElement? Payload, string? Error = null);
public sealed record EmptyPayload;
public sealed record HelloPayload(int ParentPid, long ParentCreated);
public sealed record HelloResult(int ProtocolVersion, string ProfileRoot, string HostExecutable);
public enum AuthorityMode { Normal, Administrator }
public enum ElevationState { PendingApproval, Ready, CommitPending, CommitAcked, Cancelled, Denied, Failed, Expired, Completed, Unknown }
public sealed record ParentProof(int ParentPid, long ParentCreated, string ParentExecutable, int ChildPid, long ChildCreated, string UserSid, int SessionId, AuthorityMode AuthorityMode);
public sealed record ElevationOperationPayload(Guid OperationId);
public sealed record AdministrationBeginPayload(Guid OperationId, bool ResumeLogin = false);
public sealed record StartupElevationPayload(Guid OperationId, Guid ContinuationId);
public sealed record ElevationStatusPayload(Guid OperationId, long AfterRevision);
public sealed record ElevationStatus(Guid OperationId, long Revision, ElevationState State, ReasonCode? Reason, Guid? ReceiptId, bool CanResumeNormal);
public sealed record PathsPayload(string[] Paths);
public sealed record PathPayload(string Path);
public sealed record ScriptPayload(Guid ScriptId);
public sealed record RelinkPayload(SelectionContext Selection, string OldPath, string ReplacementPath, bool StartupConsent);
public sealed record RecyclePreparePayload(SelectionContext Selection);
public sealed record RecycleAuthorizePayload(Guid OperationId, SelectionContext Selection, bool Confirmed);
public sealed record RecycleCompletePayload(Guid OperationId, Outcome Receipt, string? Detail);
public sealed record MetadataPayload(Guid ScriptId, ScriptMetadata Metadata, bool StartupConsent);
public sealed record CollectionPayload(Guid? CollectionId, string Name);
public sealed record CollectionIdPayload(Guid CollectionId);
public sealed record PreparePayload(SelectionContext Selection, ScriptOperation Operation);
public sealed record PreparedResult(Guid PlanId, OperationPlan Plan);
public sealed record PerformPayload(Guid PlanId, SelectionContext CurrentSelection, bool Confirmed);
public sealed record SettingsPayload(ManagerSettings Settings, bool StartupConsent);
public sealed record ActivityPayload(ActivityQuery Query);
public sealed record ImportPathPayload(string SourcePath);
public sealed record PreviewPayload(Guid PreviewId);
public sealed record ImportPrepared(Guid PreviewId, ImportPreview Preview);
public sealed record MigrationPathPayload(string SourceRoot);
public sealed record ConfirmPreviewPayload(Guid PreviewId, bool Confirmed);
public sealed record MigrationPreview(Guid PreviewId, string SourceRoot, string SourceFingerprint, int FileCount, int PendingOperations, bool CanMigrate, ImmutableArray<Issue> Issues);
public sealed record StartupTransferPreview(Guid PreviewId, string? OldExecutable, string NewExecutable, string? OldCommand, string NewCommand, bool CanTransfer, ReasonCode Reason);

public sealed class ProtocolException(string code) : IOException(code)
{
    public string Code { get; } = code;
}

public sealed class DecimalInt64Converter : JsonConverter<long>
{
    public override long Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("Decimal string required.");
        string? value = reader.GetString();
        if (!long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long parsed) || parsed.ToString(CultureInfo.InvariantCulture) != value) throw new JsonException("Canonical Int64 string required.");
        return parsed;
    }
    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
}

public static class Protocol
{
    public const int MaximumFrameBytes = 8 * 1024 * 1024;
    public const int MaximumPending = 32;
    public static readonly TimeSpan HandshakeDeadline = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan ShutdownDeadline = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(5);
    public static readonly ProtocolJsonContext Json = CreateContext();
    static ProtocolJsonContext CreateContext()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true,
            MaxDepth = 64
        };
        options.Converters.Add(new DecimalInt64Converter());
        options.Converters.Add(new JsonStringEnumConverter<AuthorityMode>(allowIntegerValues: false));
        options.Converters.Add(new JsonStringEnumConverter<ElevationState>(allowIntegerValues: false));
        options.Converters.Add(new JsonStringEnumConverter<ScriptStatus>(allowIntegerValues: false));
        options.Converters.Add(new JsonStringEnumConverter<ExecutionOrigin>(allowIntegerValues: false));
        options.Converters.Add(new JsonStringEnumConverter<Outcome>(allowIntegerValues: false));
        options.Converters.Add(new JsonStringEnumConverter<ReasonCode>(allowIntegerValues: false));
        options.Converters.Add(new JsonStringEnumConverter<ScriptOperation>(allowIntegerValues: false));
        options.Converters.Add(new JsonStringEnumConverter<HotkeyOrigin>(allowIntegerValues: false));
        options.Converters.Add(new JsonStringEnumConverter<DeclaredGesture>(allowIntegerValues: false));
        options.Converters.Add(new JsonStringEnumConverter<AnalysisCoverage>(allowIntegerValues: false));
        options.Converters.Add(new JsonStringEnumConverter<StatusFilter>(allowIntegerValues: false));
        options.Converters.Add(new JsonStringEnumConverter<ScriptSort>(allowIntegerValues: false));
        options.Converters.Add(new JsonStringEnumConverter<AppThemePreference>(allowIntegerValues: false));
        options.Converters.Add(new JsonStringEnumConverter<CloseBehavior>(allowIntegerValues: false));
        return new(options);
    }
    public static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (var item in value.EnumerateObject())
            {
                if (!names.Add(item.Name)) throw new ProtocolException("InvalidRequest");
                RejectDuplicates(item.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
    }
    public static RequestFrame ReadRequest(byte[] frame)
    {
        using var document = JsonDocument.Parse(frame, new JsonDocumentOptions { MaxDepth = 64 });
        RejectDuplicates(document.RootElement);
        var request = JsonSerializer.Deserialize(frame, Json.RequestFrame) ?? throw new ProtocolException("InvalidRequest");
        if (request.Version != 1 || request.Id is null || !ulong.TryParse(request.Id, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) || id == 0 || id.ToString(CultureInfo.InvariantCulture) != request.Id || request.Session is null || request.Session.Length != 64 || request.Session.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) || request.Action is null || request.Action.Length is 0 or > 40 || request.Payload.ValueKind != JsonValueKind.Object) throw new ProtocolException("InvalidRequest");
        return request;
    }
}
