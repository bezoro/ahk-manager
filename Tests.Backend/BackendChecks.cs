using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bezoro.AhkManager.Backend;
using Bezoro.AhkManager.WinUI.Models;
using Bezoro.AhkManager.WinUI.Services;
using Bezoro.AhkManager.WinUI.Infrastructure;

namespace Tests.Backend;

internal static class BackendChecks
{
    static int count;
    const string Session = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    static void Check(bool value, string name) { if (!value) throw new InvalidOperationException("FAIL " + name); count++; Console.WriteLine("PASS " + name); }
    static JsonElement Payload(string json = "{}") { using var parsed = JsonDocument.Parse(json); return parsed.RootElement.Clone(); }
    static byte[] Request(string id, string action, JsonElement payload) => JsonSerializer.SerializeToUtf8Bytes(new RequestFrame(1, id, Session, action, payload), Protocol.Json.RequestFrame);
    static async Task Send(BytePipe input, string id, string action, JsonElement payload) { await input.WriteAsync(Request(id, action, payload)); await input.WriteAsync(new byte[] { 10 }); }
    static async Task<ResponseFrame> Receive(FrameReader reader) => JsonSerializer.Deserialize(await reader.ReadAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)) ?? throw new IOException(), Protocol.Json.ResponseFrame)!;
    public static async Task RunAsync(string root)
    {
        ObservationInventory(); OwnedAhkChecks.ScopeChecks(root, Check); await ElevationChecks.RunAsync(Check); await Framing(); await HostBoundaries(); await QueueAndDeadlines(); await Tokens(root); await Migration(root); Startup(root); StartupCommandChecks.Run(root, Check); await HeaderSnapshotAsync(root);
        Console.WriteLine("BACKEND checks=" + count);
    }
    static void ObservationInventory()
    {
        var source = new OpaqueObservation();
        var held = ProcessObservation.Read([@"C:\fixtures\renamed-interpreter.exe"], out bool complete, out var issues, CancellationToken.None, source);
        try { Check(!complete && issues.Any(i => i.Code == ReasonCode.ObservationIncomplete), "renamed configured opaque interpreter cannot disappear as complete inventory"); }
        finally { foreach (var item in held) item.Handle.Dispose(); }
        var ordinary = new InventoryRows(new(int.MaxValue, "ordinary-host.exe", null, null, null, System.Diagnostics.Process.GetCurrentProcess().SessionId));
        held = ProcessObservation.Read([], out complete, out issues, CancellationToken.None, ordinary);
        try { Check(complete && held.Count == 0 && issues.IsEmpty, "unrelated inaccessible process does not freeze the recognized interpreter inventory"); }
        finally { foreach (var item in held) item.Handle.Dispose(); }
        InventoryRelevance();
    }
    static void InventoryRelevance()
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var ordinary = new ProcessCommandRow(int.MaxValue, "ordinary-host.exe", null, null, null, self.SessionId);
        string renamed = @"C:\fixture\renamed-interpreter.exe";
        foreach (var row in new[]
        {
            ordinary with { Name = "AUTOHOTKEY.EXE" }, ordinary with { Name = "autohotkey64.EXE" }, ordinary with { Name = "AutoHotkey32.exe" },
            ordinary with { Name = "renamed-interpreter.EXE" }, ordinary with { ExecutablePath = renamed },
            ordinary with { CommandLine = "\"" + renamed + "\" \"C:\\fixture\\inert.ahk\"" },
            ordinary with { CommandLine = "\"C:\\other\\AutoHotkey64.exe\" \"C:\\fixture\\inert.ahk\"" }
        })
        {
            Check(ProcessRelevance.IsCandidate(row, [renamed], null, "Other product"), "recognized path basename or argv signal survives unrelated product metadata");
            var held = ProcessObservation.Read([renamed], out bool complete, out var issues, CancellationToken.None, new InventoryRows(row));
            try { Check(!complete && held.Count == 0 && issues.Any(i => i.Code == ReasonCode.ObservationIncomplete), "recognized inaccessible candidate cannot prove absence or authorize adoption"); }
            finally { foreach (var item in held) item.Handle.Dispose(); }
        }
        Check(ProcessRelevance.IsCandidate(ordinary, [renamed], renamed, null), "native configured renamed image remains relevant with null WMI and no token proof");
        var readableImage = ProcessObservation.Read([renamed], out bool readableComplete, out var readableIssues, CancellationToken.None, new InventoryRows(ordinary), readImage: _ => renamed);
        try { Check(!readableComplete && readableImage.Count == 0 && readableIssues.Any(i => i.Code == ReasonCode.ObservationIncomplete), "readable configured native image with unavailable full identity stays incomplete and unadopted"); }
        finally { foreach (var item in readableImage) item.Handle.Dispose(); }
        var unreadableMetadata = ProcessObservation.Read([], out bool metadataComplete, out _, CancellationToken.None, new InventoryRows(ordinary), readImage: _ => @"C:\fixture\absent-ordinary-image.exe");
        try { Check(metadataComplete && unreadableMetadata.Count == 0, "metadata read failure for an unrecognized native image does not freeze the catalog"); }
        finally { foreach (var item in unreadableMetadata) item.Handle.Dispose(); }
        Check(ProcessRelevance.IsCandidate(ordinary, [], @"C:\renamed\worker.exe", "AutoHotkey v2"), "native AutoHotkey product is a positive relevance signal only");
        foreach (string? product in new string?[] { null, "", "Ordinary product" })
            Check(!ProcessRelevance.IsCandidate(ordinary, [renamed], @"C:\ordinary\host.exe", product), "unrelated image with unavailable empty or ordinary product is outside declared domain");
        Check(!ProcessRelevance.IsCandidate(ordinary with { Name = "AutoHotkeyHelper.exe" }, [], null, null), "interpreter basenames are exact rather than unrestricted prefixes");
        var incomplete = ProcessObservation.Read([], out bool completeInput, out _, CancellationToken.None, new InventoryRows(ordinary, false));
        try { Check(!completeInput, "relevance never promotes an incomplete enumeration to complete"); }
        finally { foreach (var item in incomplete) item.Handle.Dispose(); }
        var otherSession = ProcessObservation.Read([], out bool completeOther, out _, CancellationToken.None, new InventoryRows(ordinary with { Name = "AutoHotkey64.exe", SessionId = self.SessionId + 1 }));
        try { Check(completeOther && otherSession.Count == 0, "recognized candidate in independently reported other session does not block"); }
        finally { foreach (var item in otherSession) item.Handle.Dispose(); }
        using var native = new NativeProcessIdentity(self.Id);
        Check(native.TryReadImage(out string image) && ScriptPaths.Equal(image, Environment.ProcessPath) && native.TryRead(out var stamp) && ScriptPaths.Equal(image, stamp.Executable), "partial native image and full identity agree on retained live handle");
        var hiddenNative = ProcessObservation.Read([image], out bool hiddenComplete, out _, CancellationToken.None, new InventoryRows(ordinary with { Pid = self.Id }));
        try { Check(!hiddenComplete && hiddenNative.Count == 0, "native configured image is considered before misleading ordinary WMI name but never bypasses interpreter validation"); }
        finally { foreach (var item in hiddenNative) item.Handle.Dispose(); }
        var changedImage = ProcessObservation.Read([renamed], out bool changedComplete, out _, CancellationToken.None, new InventoryRows(ordinary with { Pid = self.Id }), readImage: _ => renamed);
        try { Check(!changedComplete && changedImage.Count == 0, "partial native image conflicting with full retained identity cannot authorize adoption"); }
        finally { foreach (var item in changedImage) item.Handle.Dispose(); }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); bool observedCancel = false;
        try { _ = ProcessObservation.Read([], out _, out _, cancelled.Token, new InventoryRows(ordinary)); } catch (OperationCanceledException) { observedCancel = true; }
        Check(observedCancel, "cancelled inventory remains cancelled without a false complete observation");
    }
    sealed class InventoryRows(ProcessCommandRow row, bool complete = true) : IProcessObservationSource
    {
        public ObservedProcessCommands Read(CancellationToken ct) => new([row], complete, []);
    }
    sealed class OpaqueObservation : IProcessObservationSource
    {
        public ObservedProcessCommands Read(CancellationToken ct) => new([new(int.MaxValue, "renamed-interpreter.exe", null, null, null, System.Diagnostics.Process.GetCurrentProcess().SessionId)], true, []);
    }
    static async Task HeaderSnapshotAsync(string root)
    {
        string folder = Path.Combine(root, "header-wire"); Directory.CreateDirectory(folder); string path = Path.Combine(folder, "nul.ahk");
        await File.WriteAllTextAsync(path, "; @description a\0b\n#Requires AutoHotkey v2");
        using var owner = new HeaderOwner(); var processes = new HeaderProcesses(); var registration = new FakeStartup();
        await using var service = new ManagerService(Path.Combine(folder, "data"), owner, processes, new UserStartupService(registration, Path.Combine(folder, "Manager.exe")), Path.Combine(folder, "legacy-absent"));
        await service.LoadAsync(); var added = await service.AddFilesAsync([path]); var id = added.Added.Single();
        Check((await service.SaveMetadataAsync(id, new("NUL header fixture", "manual fallback survives", null, false, false, false, []))).Succeeded, "header fixture manual metadata persists separately");
        var snapshot = service.Snapshot; var row = snapshot.Scripts.Single();
        Check(row.HeaderDescription is null && row.Description == "manual fallback survives" && row.Issues.Any(i => i.Code == ReasonCode.PartialAnalysis && i.Detail is { Length: <= 128 } d && !d.Contains('\0')), "NUL file produces safe null header snapshot with manual fallback");
        foreach (string? header in new string?[] { null, "Header line1\n<script>literal</script>", new string('x', 4000) })
        {
            var value = snapshot with { Scripts = [row with { HeaderDescription = header }] };
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Protocol.Json.ManagerSnapshot);
            var restored = JsonSerializer.Deserialize(bytes, Protocol.Json.ManagerSnapshot)!;
            Check(restored.Scripts.Single().HeaderDescription == header && restored.Scripts.Single().Description == row.Description, "source-generated header nullable/text roundtrip preserves manual field");
        }
        await File.WriteAllBytesAsync(Path.Combine(root, "header-description-snapshot.json"), JsonSerializer.SerializeToUtf8Bytes(snapshot, Protocol.Json.ManagerSnapshot));
        Check(processes.Effects == 0 && registration.Writes == 0, "header wire fixture has zero process/startup effects");
    }
    sealed class HeaderOwner : IEffectOwner
    {
        public bool Held => true; public bool Trusted => true; public string Sid => "S-1-5-21-header-fixture"; public string LoginKey => "header-fixture";
        public bool Allows(string script, string interpreter) => false;
        public void Dispose() { }
    }
    sealed class HeaderProcesses : IProcessManager
    {
        public int Effects; public event EventHandler? Invalidated { add { } remove { } }
        public bool Supports(ScriptOperation operation) => false;
        public Task<ObservedProcesses> ObserveAsync(IReadOnlyCollection<string> known, CancellationToken ct = default) => Task.FromResult(new ObservedProcesses([], true, []));
        public Task<LaunchResult> StartAsync(string interpreter, string script, bool elevated, CancellationToken ct = default) { Effects++; throw new InvalidOperationException("Header fixture cannot launch"); }
        public Task<OperationResult> ControlAsync(ProcessIdentity expected, ScriptOperation operation, CancellationToken ct = default) { Effects++; throw new InvalidOperationException("Header fixture cannot control"); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    static async Task Framing()
    {
        var contractIdentity = new ProcessIdentity(1, 1, @"C:\fixture\AHK.exe", @"C:\fixture\safe.ahk", "S-1-fixture", 1);
        var contractProcess = new ProcessSnapshot(contractIdentity, ExecutionOrigin.External, "2", DateTimeOffset.UnixEpoch, ControlUnavailableReason: ReasonCode.None);
        Check(contractProcess.ControlUnavailableReason is null && (contractProcess with { ControlUnavailableReason = ReasonCode.None }).ControlUnavailableReason is null && (ManagerSnapshot.Empty with { NormalLaunchUnavailableReason = ReasonCode.None }).NormalLaunchUnavailableReason is null, "optional authority reasons normalize None to null on construction and updates");
        foreach (var access in new[] { ReasonCode.AccessDenied, ReasonCode.ManualElevationRequired })
        {
            var current = contractProcess with { ControlUnavailableReason = access };
            Check(JsonSerializer.SerializeToElement(current, Protocol.Json.ProcessSnapshot).GetProperty("controlUnavailableReason").GetString() == access.ToString(), "control reason is a strict enum string " + access);
        }
        bool invalidAccess = false; try { _ = contractProcess with { ControlUnavailableReason = ReasonCode.DispatchFailed }; } catch (ArgumentOutOfRangeException) { invalidAccess = true; }
        Check(invalidAccess, "non-whitelisted process access reason cannot leak to wire");
        var huge = ManagerSnapshot.Empty with { Revision = 9007199254740993L };
        var serialized = JsonSerializer.SerializeToElement(huge, Protocol.Json.ManagerSnapshot);
        Check(serialized.GetProperty("revision").GetString() == "9007199254740993", "revision larger than JS integer precision remains exact decimal string");
        Check(Protocol.ReadRequest(Request("18446744073709551615", "snapshot", Payload())).Id == "18446744073709551615", "UInt64 request ID remains lossless");
        foreach (string bad in new[] { "{\"version\":1,\"version\":1}", "{\"version\":1,\"id\":\"01\",\"session\":\"" + Session + "\",\"action\":\"snapshot\",\"payload\":{}}", "{\"version\":1,\"id\":\"1\",\"session\":\"" + Session + "\",\"action\":\"snapshot\",\"payload\":{},\"extra\":true}" })
        {
            bool rejected = false; try { Protocol.ReadRequest(Encoding.UTF8.GetBytes(bad)); } catch (Exception ex) when (ex is ProtocolException or JsonException) { rejected = true; }
            Check(rejected, "malformed/duplicate/noncanonical request rejected");
        }
        foreach (byte[] bytes in new[] { new byte[] { 0xc3, 10 }, Encoding.UTF8.GetBytes("{}"), new byte[] { 10 }, new byte[Protocol.MaximumFrameBytes + 2] })
        {
            bool rejected = false;
            try { await new FrameReader(new MemoryStream(bytes)).ReadAsync(CancellationToken.None); } catch (ProtocolException) { rejected = true; }
            Check(rejected, "invalid UTF8/incomplete/empty/oversized frame is explicit error");
        }
        byte[] unicode = Encoding.UTF8.GetBytes("{\"text\":\"ação 😀\"}\r\n");
        Check(Encoding.UTF8.GetString((await new FrameReader(new MemoryStream(unicode)).ReadAsync(CancellationToken.None))!).Contains("ação 😀", StringComparison.Ordinal), "Unicode and CRLF framing survive unchanged");
        var prepare = JsonSerializer.SerializeToElement(new PreparePayload(new(Guid.NewGuid(), new(), 0), ScriptOperation.Stop), Protocol.Json.PreparePayload);
        var numeric = JsonNode.Parse(prepare.GetRawText())!; numeric["operation"] = 1;
        bool enumRejected = false; try { _ = Payload(numeric.ToJsonString()).Deserialize(Protocol.Json.PreparePayload); } catch (JsonException) { enumRejected = true; }
        Check(enumRejected, "numeric enum never crosses protocol");
    }
    static async Task HostBoundaries()
    {
        int compositions = 0;
        var invalid = new FakeParent { Live = false };
        var rejected = new StdioHost(new BytePipe(), new BytePipe(), new StringWriter(), invalid, Session, @"C:\fixture", () => { compositions++; return new FakeSession(); });
        Check(await rejected.RunAsync() == 65 && compositions == 0, "parent mismatch never composes owner/effects");
        var input = new BytePipe(); var output = new BytePipe(); var parent = new FakeParent(); var fake = new FakeSession();
        var host = new StdioHost(input, output, new StringWriter(), parent, Session, @"C:\fixture", () => { compositions++; return fake; });
        Task<int> run = host.RunAsync(); var read = new FrameReader(output);
        var proof = await Receive(read); Check(proof.Kind == "parentProof" && compositions == 0, "only native parent proof precedes handshake/composition");
        await Send(input, "1", "hello", JsonSerializer.SerializeToElement(new HelloPayload(parent.Proof.ParentPid, parent.Proof.ParentCreated), Protocol.Json.HelloPayload));
        Check((await Receive(read)).Code == "Ok" && compositions == 1, "matching handshake composes exactly once");
        await Send(input, "2", "snapshot", Payload()); await Receive(read);
        await Send(input, "2", "snapshot", Payload()); await Receive(read);
        Check(fake.Calls == 1, "same ID same request returns cached response without replay");
        await Send(input, "2", "refresh", Payload()); Check((await Receive(read)).Code == "IdConflict" && fake.Calls == 1, "different payload same ID cannot replay");
        await Send(input, "3", "shutdown", Payload()); Check((await Receive(read)).Code == "Ok", "orderly shutdown reports completion");
        Check(await run.WaitAsync(TimeSpan.FromSeconds(5)) == 0 && fake.Disposals > 0, "normal pipe lifecycle disposes without process-tree actions");
        input = new(); output = new(); fake = new(); parent = new();
        var eof = new StdioHost(input, output, new StringWriter(), parent, Session, @"C:\fixture", () => fake); run = eof.RunAsync(); read = new(output); await Receive(read);
        await Send(input, "1", "hello", JsonSerializer.SerializeToElement(new HelloPayload(parent.Proof.ParentPid, parent.Proof.ParentCreated), Protocol.Json.HelloPayload)); await Receive(read);
        input.End(); Check(await run.WaitAsync(TimeSpan.FromSeconds(5)) == 0 && fake.Disposals > 0, "stdin EOF disposes authenticated session without replay");
        foreach (bool die in new[] { false, true })
        {
            input = new(); output = new(); parent = new(); int before = compositions;
            host = new(input, output, new StringWriter(), parent, Session, @"C:\fixture", () => { compositions++; return new FakeSession(); });
            run = host.RunAsync(); read = new(output); await Receive(read);
            if (die) parent.Live = false;
            await Send(input, "1", "hello", JsonSerializer.SerializeToElement(new HelloPayload(parent.Proof.ParentPid, parent.Proof.ParentCreated + (die ? 0 : 1)), Protocol.Json.HelloPayload));
            Check(await run.WaitAsync(TimeSpan.FromSeconds(5)) == 65 && compositions == before, "wrong creation or dead parent cannot compose effects");
        }
    }
    static async Task QueueAndDeadlines()
    {
        var input = new BytePipe(); var output = new BytePipe(); var parent = new FakeParent(); var fake = new FakeSession { HoldCalls = true };
        var host = new StdioHost(input, output, new StringWriter(), parent, Session, @"C:\fixture", () => fake);
        Task<int> run = host.RunAsync(); var read = new FrameReader(output); await Receive(read);
        await Send(input, "1", "hello", JsonSerializer.SerializeToElement(new HelloPayload(parent.Proof.ParentPid, parent.Proof.ParentCreated), Protocol.Json.HelloPayload)); await Receive(read);
        for (int id = 2; id <= 34; id++) await Send(input, id.ToString(System.Globalization.CultureInfo.InvariantCulture), "snapshot", Payload());
        Check((await Receive(read)).Code == "QueueFull", "thirty-third outstanding request gets explicit QueueFull");
        fake.Hold.SetResult(JsonSerializer.SerializeToElement(new OperationResult(Outcome.Confirmed), Protocol.Json.OperationResult));
        for (int i = 0; i < 32; i++) await Receive(read);
        Check(fake.Calls == 32, "only bounded accepted requests execute after queue backpressure");
        await Send(input, "35", "shutdown", Payload()); await Receive(read); await run.WaitAsync(TimeSpan.FromSeconds(5));

        input = new(); output = new(); parent = new(); fake = new() { StallDispose = true };
        host = new(input, output, new StringWriter(), parent, Session, @"C:\fixture", () => fake, TimeSpan.FromMilliseconds(200));
        run = host.RunAsync(); read = new(output); await Receive(read);
        await Send(input, "1", "hello", JsonSerializer.SerializeToElement(new HelloPayload(parent.Proof.ParentPid, parent.Proof.ParentCreated), Protocol.Json.HelloPayload)); await Receive(read);
        await Send(input, "2", "shutdown", Payload());
        var unknown = await Receive(read);
        Check(unknown.Payload!.Value.Deserialize(Protocol.Json.OperationResult)!.Outcome == Outcome.Unknown && await run.WaitAsync(TimeSpan.FromSeconds(5)) == 75, "unresponsive disposal yields unknown failed shutdown, never success or forced kill");
    }
    static async Task Tokens(string root)
    {
        var manager = new FakeManager(); var startup = new FakeStartup();
        await using var session = new BackendSession(manager, Path.Combine(root, "session"), Path.Combine(root, "Bezoro.AhkManager.exe"), () => true, startup);
        await session.ExecuteAsync("load", Payload(), CancellationToken.None);
        var selection = new SelectionContext(Guid.NewGuid(), new(Version: 9007199254740993L), 4);
        var prepared = await session.ExecuteAsync("prepare", JsonSerializer.SerializeToElement(new PreparePayload(selection, ScriptOperation.Stop), Protocol.Json.PreparePayload), CancellationToken.None);
        var token = prepared.Deserialize(Protocol.Json.PreparedResult)!;
        var forged = await session.ExecuteAsync("perform", JsonSerializer.SerializeToElement(new PerformPayload(Guid.NewGuid(), selection, true), Protocol.Json.PerformPayload), CancellationToken.None);
        Check(forged.Deserialize(Protocol.Json.OperationResult)!.Code == ReasonCode.StaleSelection && manager.Performs == 0, "forged plan token cannot reach manager effect");
        await session.ExecuteAsync("perform", JsonSerializer.SerializeToElement(new PerformPayload(token.PlanId, selection, true), Protocol.Json.PerformPayload), CancellationToken.None);
        Check(manager.Performs == 1 && manager.LastPerformed == token.Plan, "perform resolves backend-held immutable plan");
        await session.ExecuteAsync("perform", JsonSerializer.SerializeToElement(new PerformPayload(token.PlanId, selection, true), Protocol.Json.PerformPayload), CancellationToken.None);
        Check(manager.Performs == 1, "operation token is single-use even when request ID changes");
        bool forgedBody = false;
        try
        {
            var forgedNode = JsonNode.Parse(JsonSerializer.SerializeToElement(new PerformPayload(token.PlanId, selection, true), Protocol.Json.PerformPayload).GetRawText())!;
            forgedNode["plan"] = new JsonObject();
            await session.ExecuteAsync("perform", Payload(forgedNode.ToJsonString()), CancellationToken.None);
        }
        catch (JsonException) { forgedBody = true; }
        Check(forgedBody, "extra forged complete plan is rejected rather than trusted");
        prepared = await session.ExecuteAsync("prepare", JsonSerializer.SerializeToElement(new PreparePayload(selection, ScriptOperation.Stop), Protocol.Json.PreparePayload), CancellationToken.None);
        token = prepared.Deserialize(Protocol.Json.PreparedResult)!;
        var stale = await session.ExecuteAsync("perform", JsonSerializer.SerializeToElement(new PerformPayload(token.PlanId, selection with { StateRevision = 5 }, true), Protocol.Json.PerformPayload), CancellationToken.None);
        Check(stale.Deserialize(Protocol.Json.OperationResult)!.Code == ReasonCode.StaleSelection, "current selection revision is passed to core revalidation after modal gap");
    }
    static async Task Migration(string root)
    {
        string source = Path.Combine(root, "migration-source"), target = Path.Combine(root, "migration-target"); Directory.CreateDirectory(source);
        var settings = new SettingsDocument { LastLoginKey = "same-logon", Settings = new() { LaunchManagerAtLogin = true } };
        byte[] original = JsonSerializer.SerializeToUtf8Bytes(settings, ManagerJson.Default.SettingsDocument);
        await File.WriteAllBytesAsync(Path.Combine(source, "settings.json"), original);
        var pending = new OperationRecord(Guid.NewGuid(), Guid.NewGuid(), ScriptOperation.Start, Path.Combine(source, "owned.ahk"), "", "", "", "S-1-fixture", "same-logon", DateTimeOffset.UtcNow, Outcome.Unknown);
        await File.WriteAllBytesAsync(Path.Combine(source, "operations.json"), JsonSerializer.SerializeToUtf8Bytes(new JournalDocument { Operations = [pending] }, ManagerJson.Default.JournalDocument));
        var migration = new ProfileMigrationService(target, () => true);
        var preview = await migration.PreviewAsync(source, CancellationToken.None);
        Check(preview.CanMigrate && preview.PendingOperations == 1, "migration previews pending journal without replaying it");
        Check((await migration.CommitAsync(preview.PreviewId, true, CancellationToken.None)).Succeeded, "migration atomically installs validated new profile with exact backup");
        Check(File.ReadAllBytes(Path.Combine(source, "settings.json")).SequenceEqual(original) && File.ReadAllBytes(Path.Combine(target, "settings.json")).SequenceEqual(original), "migration preserves source bytes/startup preference/login marker");
        Check(Directory.GetFiles(Path.Combine(target, "migration-backup"), "manifest.json", SearchOption.AllDirectories).Length == 1, "migration retains backup manifest");
        Check(!(await migration.PreviewAsync(source, CancellationToken.None)).CanMigrate, "nonempty destination refuses migration");
        migration = new(Path.Combine(root, "migration-changed"), () => true); preview = await migration.PreviewAsync(source, CancellationToken.None);
        await File.AppendAllTextAsync(Path.Combine(source, "settings.json"), " ");
        Check((await migration.CommitAsync(preview.PreviewId, true, CancellationToken.None)).Code == ReasonCode.ImportSourceChanged, "migration refuses source fingerprint change");
        migration = new(Path.Combine(root, "migration-owner"), () => false); preview = await migration.PreviewAsync(source, CancellationToken.None);
        Check((await migration.CommitAsync(preview.PreviewId, true, CancellationToken.None)).Code == ReasonCode.EffectOwnerUnavailable, "migration commit requires effect ownership");
        migration = new(Path.Combine(root, "migration-cancel"), () => true); preview = await migration.PreviewAsync(source, CancellationToken.None);
        Check((await migration.CommitAsync(preview.PreviewId, false, CancellationToken.None)).Outcome == Outcome.CancelledBeforeDispatch && !Directory.Exists(Path.Combine(root, "migration-cancel")), "cancelled migration writes no destination");
        Check((await migration.CommitAsync(preview.PreviewId, true, CancellationToken.None)).Code == ReasonCode.ImportSourceChanged, "migration preview token cannot be replayed after cancel");
        await File.WriteAllTextAsync(Path.Combine(source, "operations.json"), "{invalid");
        Check(!(await new ProfileMigrationService(Path.Combine(root, "migration-corrupt"), () => true).PreviewAsync(source, CancellationToken.None)).CanMigrate, "corrupt source journal refuses migration rather than dropping pending authority");
    }
    static void Startup(string root)
    {
        string old = Path.Combine(root, "old WinUI.exe"), host = Path.Combine(root, "Bezoro.AhkManager.exe"); File.WriteAllText(old, "old"); File.WriteAllText(host, "new");
        var adapter = new FakeStartup { Entry = new UserStartupService(new FakeStartup(), old).Command };
        var state = ManagerSnapshot.Empty with { Revision = 7, Settings = new() { LaunchManagerAtLogin = true } };
        bool owner = true;
        var transfer = new StartupTransferService(adapter, host, () => owner, () => state, _ => true);
        var preview = transfer.Preview(); Check(preview.CanTransfer, "startup transfer derives exact old registration and trusted new host");
        Check(transfer.Commit(preview.PreviewId, false).Outcome == Outcome.CancelledBeforeDispatch && adapter.Writes == 0, "native cancel transfers nothing");
        preview = transfer.Preview(); owner = false;
        Check(transfer.Commit(preview.PreviewId, true).Code == ReasonCode.EffectOwnerUnavailable && adapter.Writes == 0, "lost owner blocks fake startup write"); owner = true;
        preview = transfer.Preview(); adapter.Entry += " --foreign";
        Check(transfer.Commit(preview.PreviewId, true).Code == ReasonCode.StartupEntryConflict && adapter.Writes == 0, "fresh exact compare-exchange preserves changed registration");
        adapter.Entry = new UserStartupService(adapter, old).Command; preview = transfer.Preview(); File.AppendAllText(old, "changed");
        Check(transfer.Commit(preview.PreviewId, true).Code == ReasonCode.IdentityChanged && adapter.Writes == 0, "changed old executable cannot transfer startup");
        preview = transfer.Preview(); Check(transfer.Commit(preview.PreviewId, true).Succeeded && adapter.Entry == new UserStartupService(adapter, host).Command && adapter.Writes == 1, "explicit transfer changes one owned value to final host only");
        state = state with { Settings = new() { LaunchManagerAtLogin = false } };
        Check(!transfer.Preview().CanTransfer && adapter.Writes == 1, "startup off never implicitly registers");
        state = state with { Settings = new() { LaunchManagerAtLogin = true } }; adapter.Entry = new UserStartupService(adapter, old).Command;
        preview = transfer.Preview(); state = state with { Revision = 8 };
        Check(transfer.Commit(preview.PreviewId, true).Code == ReasonCode.StaleSelection && adapter.Writes == 1, "startup transfer preview binds profile revision");
        adapter.Entry += " --extra"; Check(!transfer.Preview().CanTransfer, "unexpected startup arguments are never treated as owned registration");
        adapter.Entry = new UserStartupService(adapter, old).Command;
        var foreign = new StartupTransferService(adapter, host, () => true, () => state, _ => false);
        Check(!foreign.Preview().CanTransfer && adapter.Writes == 1, "unverified foreign executable cannot transfer owned startup entry");
    }
}
