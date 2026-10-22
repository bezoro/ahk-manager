using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using WpfProgram = Bezoro.AhkManager.Presentation.Program;
using Bezoro.AhkManager;
using Bezoro.AhkManager.Presentation;

static partial class Tests
{
    sealed class NormalRig : IDisposable
    {
        public readonly CatalogStore Store;
        public readonly FixtureOwner Owner = new();
        public readonly FixtureClock Clock = new();
        public readonly RequestLedger Ledger;
        public readonly NormalCutoverManifest Manifest;
        public readonly FakeNormalEffects Effects;
        public NormalRig(string name, bool legacy = false, bool pending = false)
        {
            var dir = Path.Combine(root, name); Directory.CreateDirectory(dir); Store = new(Path.Combine(dir, "profile")); Ledger = RequestLedger.OpenFixture(Path.Combine(dir, "ledger"), Owner);
            Manifest = new() { Fixture = true, ExpectedSid = Owner.Sid, ExpectedSession = 1, Root = Path.Combine(dir, "scripts"), ProfileDirectory = Store.DirectoryPath, BackupDirectory = Path.Combine(dir, "backup-unique-transaction"), LegacyShortcut = Path.Combine(dir, "legacy.lnk"), NormalShortcut = Path.Combine(dir, "manager.lnk"), ManagerExecutable = Path.Combine(dir, "manager.exe"), Authority = Ledger.Binding! };
            File.WriteAllText(Manifest.LegacyShortcut, "legacy shortcut"); Manifest.LegacyShortcutSHA256 = NormalMigrationEffects.FileHash(Manifest.LegacyShortcut);
            File.WriteAllText(Manifest.ManagerExecutable, "fake manager"); Manifest.ManagerSHA256 = NormalMigrationEffects.FileHash(Manifest.ManagerExecutable);
            var catalog = new Catalog { Authority = Ledger.Binding };
            for (int i = 0; i < 4; i++)
            {
                var path = Path.Combine(Manifest.Root, NormalCutoverCoordinator.RelativePaths[i]); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "fixture " + i);
                Manifest.Choices.Add(new() { RelativePath = NormalCutoverCoordinator.RelativePaths[i], ExpectedSHA256 = NormalMigrationEffects.FileHash(path), Mode = i == 0 ? ManagementMode.Retired : i == 3 ? ManagementMode.ExternalPending : ManagementMode.Normal, RequestedEnabled = i is 1 or 2 });
                catalog.Scripts.Add(new() { Path = path, Description = "kept " + i, DescriptionSource = "Informada", Interpreter = Path.Combine(dir, "AutoHotkey64.exe"), ManagementMode = i == 1 ? ManagementMode.Normal : ManagementMode.Unmanaged, RequestedEnabled = i == 1 ? false : null });
            }
            var unrelatedPath = Path.Combine(Manifest.Root, "unrelated.ahk"); File.WriteAllText(unrelatedPath, "unrelated"); catalog.Scripts.Add(new() { Path = unrelatedPath, Description = "unrelated metadata", LaunchBlocked = true, RunAsAdministrator = true, Interpreter = Path.Combine(dir, "AutoHotkey64.exe") });
            catalog.Recovery.Add(new() { ScriptId = catalog.Scripts[1].Id, LoginKey = Owner.LoginKey, AttemptsUtcTicks = [Clock.UtcNow.Ticks], Failures = 1 });
            if (!legacy) Store.Save(catalog);
            else
            {
                // An actual old-schema byte document. Old records had no administrator/request/authority fields.
                string Quote(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
                var records = catalog.Scripts.Take(4).Select(e => "{\"Id\":" + Quote(e.Id.ToString()) + ",\"Path\":" + Quote(e.Path) + ",\"Description\":" + Quote(e.Description) + ",\"DescriptionSource\":\"Informada\",\"LaunchBlocked\":false,\"Interpreter\":" + Quote(e.Interpreter) + "}");
                File.WriteAllText(Store.FilePath, "{\"Schema\":1,\"Scripts\":[" + string.Join(',', records) + "]}"); Store.Load();
            }
            Manifest.CatalogSHA256 = NormalMigrationEffects.FileHash(Store.FilePath);
            if (pending)
            {
                var node = JsonNode.Parse(File.ReadAllText(Ledger.FilePath))!.AsObject();
                node["Requests"]!.AsArray().Add(new JsonObject { ["Nonce"] = Guid.NewGuid().ToString(), ["ScriptPath"] = catalog.Scripts[1].Path, ["InterpreterPath"] = catalog.Scripts[1].Interpreter, ["Sid"] = Owner.Sid, ["ManagerPid"] = 7100, ["ManagerCreated"] = 1000, ["LoginKey"] = Owner.LoginKey, ["SessionId"] = 1, ["RequestedUtcTicks"] = Clock.UtcNow.Ticks, ["Candidate"] = null, ["Context"] = new JsonObject { ["Kind"] = "Legacy", ["Origin"] = 0, ["Blob"] = "" } });
                File.WriteAllText(Ledger.FilePath, node.ToJsonString()); Ledger = RequestLedger.OpenFixture(Ledger.DirectoryPath, Owner, Store.FilePath);
            }
            Effects = new(Manifest, catalog.Scripts.Take(3).Select((e, i) => new ProcessIdentity(9000 + i, 1000 + i, e.Interpreter, e.Path)).ToList());
        }
        public Task<Result> Run(bool resume = false, bool preflight = false, CancellationToken token = default) => NormalCutoverCoordinator.Execute(Store, Manifest, Owner, Ledger, Clock, Effects, resume, preflight, token);
        public void Dispose() { Store.Dispose(); Owner.Dispose(); }
    }
    sealed class FakeNormalEffects(NormalCutoverManifest m, List<ProcessIdentity> identities) : INormalMigrationEffects
    {
        public bool Fixture { get; set; } = true;
        public Snapshot Current { get; set; } = new(identities, true, "isolated coordinator effects");
        public string? FailureStep; public bool AfterEffect; public bool BreakJournalWrite;
        public bool BreakFinalCatalog, FailBackup;
        public Action? BackupHook;
        public Action<int>? ObserveHook;
        public Action<MigrationJournal?>? CheckHook;
        public int Observations;
        public int Stops, Backups, ShortcutInstalls;
        public int ChildEffects = 0;
        public List<string> Order = [];
        public Result Check(NormalCutoverManifest manifest, MigrationJournal? journal)
        {
            CheckHook?.Invoke(journal);
            if (manifest.ExpectedSession != 1) return Result.Fail("session changed");
            if (BreakFinalCatalog && journal?.AppliedSteps.Contains("ChildrenAdopted") == true) Directory.CreateDirectory(Path.Combine(m.ProfileDirectory, "catalog.json.tmp"));
            if (NormalMigrationEffects.FileHash(m.ManagerExecutable) != m.ManagerSHA256) return Result.Fail("manager changed");
            for (int i = 0; i < 4; i++)
            {
                var path = Path.Combine(m.Root, NormalCutoverCoordinator.RelativePaths[i]);
                if (i == 0 && !File.Exists(path) && (journal?.AppliedSteps.Contains("SupervisorFileArchived") == true || journal?.PendingStep == "SupervisorFileArchived")) path = Path.Combine(m.BackupDirectory, "supervisor.bytes");
                if (!File.Exists(path) || NormalMigrationEffects.FileHash(path) != m.Choices[i].ExpectedSHA256) return Result.Fail("source changed");
            }
            return Result.Ok("fake exact files");
        }
        public Task<Snapshot> Observe(CancellationToken token) { ObserveHook?.Invoke(++Observations); return Task.FromResult(Current); }
        public string Backup(NormalCutoverManifest manifest)
        {
            Backups++; Directory.CreateDirectory(m.BackupDirectory);
            if (FailBackup) { File.WriteAllText(Path.Combine(m.BackupDirectory, "partial-copy.bytes"), "retained partial evidence"); throw new IOException("injected partial backup copy failure"); }
            var path = Path.Combine(m.BackupDirectory, "proof.bytes"); File.WriteAllText(path, NormalCutoverCoordinator.Hash(m)); BackupHook?.Invoke(); return NormalMigrationEffects.FileHash(path);
        }
        public Result VerifyBackup(NormalCutoverManifest manifest, MigrationJournal journal) => File.Exists(Path.Combine(m.BackupDirectory, "proof.bytes")) && NormalMigrationEffects.FileHash(Path.Combine(m.BackupDirectory, "proof.bytes")) == journal.BackupDigest ? Result.Ok("verified fake backup") : Result.Fail("backup changed");
        public Task<Result> Apply(string step, NormalCutoverManifest manifest, MigrationJournal journal, CancellationToken token)
        {
            Order.Add(step);
            if (FailureStep == step && !AfterEffect) return Task.FromResult(Result.Fail("injected before effect"));
            if (step == "LegacyShortcutArchived" && File.Exists(m.LegacyShortcut)) File.Move(m.LegacyShortcut, Path.Combine(m.BackupDirectory, "legacy.bytes"));
            if (step == "SupervisorStopped" && Current.For(Path.Combine(m.Root, "startup.ahk")).Count != 0) { Stops++; Current.Processes.RemoveAll(p => Paths.Equal(p.Script, Path.Combine(m.Root, "startup.ahk"))); }
            if (step == "SupervisorFileArchived" && File.Exists(Path.Combine(m.Root, "startup.ahk"))) File.Move(Path.Combine(m.Root, "startup.ahk"), Path.Combine(m.BackupDirectory, "supervisor.bytes"));
            if (step == "NormalLoginPrepared" && !File.Exists(m.NormalShortcut)) { File.WriteAllText(m.NormalShortcut, manifest.ShortcutArguments); ShortcutInstalls++; }
            if (step == "ChildrenAdopted" && !journal.Children.All(Current.Processes.Contains)) return Task.FromResult(Result.Fail("child changed"));
            if (FailureStep == step && BreakJournalWrite) Directory.CreateDirectory(JournalStore.PathFor(m.ProfileDirectory) + ".tmp");
            return Task.FromResult(FailureStep == step && AfterEffect && !BreakJournalWrite ? Result.Fail("injected after effect") : Result.Ok("fake effect verified"));
        }
    }
    static async Task RunNormalCutover()
    {
        Check(WpfProgram.RunNormalCutover(["--normal-cutover", "unused", "--data-dir", root]) == 2, "real CLI rejects fixture/profile/extra flags before UI");
        using (var rig = new NormalRig("static-com"))
        {
            var shortcut = Path.Combine(root, "typed-static-com.lnk"); ShortcutIO.CreateNormal(shortcut, rig.Manifest); ShortcutIO.VerifyNormal(shortcut, rig.Manifest);
            var properties = ShortcutIO.Read(shortcut); Check(properties.Arguments == "--tray --login" && properties.ShowCommand == 1 && properties.HotKey == 0 && (properties.Flags & 0x2000) == 0, "typed static COM roundtrip in isolated workspace, no launch");
        }
        foreach (var obsolete in new[] { "schema", "catalog", "args" })
        {
            using var old = new NormalRig("obsolete-" + obsolete);
            if (obsolete == "schema") old.Manifest.Schema = 2; if (obsolete == "catalog") old.Manifest.CatalogSchema = 4; if (obsolete == "args") old.Manifest.ShortcutArguments = "--tray";
            var before = File.ReadAllBytes(old.Store.FilePath); Check(!(await old.Run()).Success && old.Effects.Stops == 0 && before.SequenceEqual(File.ReadAllBytes(old.Store.FilePath)), "obsolete combined descriptor refuses zero effects " + obsolete);
        }
        using (var rig = new NormalRig("success"))
        {
            var before = rig.Store.Load(); var children = rig.Effects.Current.Processes.Skip(1).ToArray();
            Check((await rig.Run(preflight: true)).Success && rig.Effects.Backups == 0 && rig.Store.Load().Migration.State == MigrationState.NotPrepared, "preflight has zero catalog/effect mutations");
            var result = await rig.Run(); Check(result.Success, "actual coordinator success " + result.Message);
            var after = rig.Store.Load();
            Check(after.Schema == 5 && after.Runtime.Decided && after.Runtime.LoginKey == rig.Owner.LoginKey && after.Runtime.Wants(after.Scripts[1].Id) && after.Scripts[1].StartWithWindows == false, "migration current-login adoption independent of false next-login preference");
            Check(rig.Effects.Order.SequenceEqual(NormalCutoverCoordinator.Steps[2..^1]) && rig.Effects.Stops == 1 && rig.Effects.ChildEffects == 0 && children.All(rig.Effects.Current.Processes.Contains), "ordered supervisor-only stop and zero-effect child adoption");
            Check(after.Scripts[0].LaunchBlocked && after.Scripts[0].RequestedEnabled == false && after.Scripts[0].ManagementMode == ManagementMode.Retired, "retired supervisor blocked and disabled");
            Check(after.Scripts[1].RequestedEnabled == false && after.Scripts[2].RequestedEnabled == true && after.Scripts[3].ManagementMode == ManagementMode.ExternalPending && after.Scripts[3].StartWithWindows == (before.Scripts[3].StartWithWindows ?? false), "explicit disable preserved and brush metadata external");
            Check(after.Scripts.Zip(before.Scripts).All(p => p.First.Id == p.Second.Id && p.First.Description == p.Second.Description && p.First.DescriptionSource == p.Second.DescriptionSource && p.First.Interpreter == p.Second.Interpreter && p.First.RunAsAdministrator == p.Second.RunAsAdministrator) && after.Recovery[0].AttemptsUtcTicks.SequenceEqual(before.Recovery[0].AttemptsUtcTicks) && after.Scripts[4].LaunchBlocked, "IDs descriptions interpreter admin budgets and unrelated metadata preserved");
            Check(JournalStore.Compatible(rig.Store.DirectoryPath, after.Migration, true), "schema3 completion digest arms existing supervision contract");
            var supervision = new Supervision(rig.Store, rig.Owner, rig.Clock, rig.Ledger); Check(supervision.Gate(after, rig.Effects.Current, false, true).Success, "same-login completed gate ready");
            var disabledBudget = supervision.Budget(after, after.Scripts[1].Id); Check(disabledBudget.AttemptsUtcTicks.Count == 1, "same-login recovery budget preserved");
            var budgetLogin = rig.Owner.LoginKey; rig.Owner.LoginKey += "-new-login"; var newBudget = supervision.Budget(after, after.Scripts[1].Id); Check(newBudget.AttemptsUtcTicks.Count == 0 && after.Scripts[1].RequestedEnabled == false && after.Scripts[2].RequestedEnabled == true, "new-login budget reset preserves enabled and disabled choices");
            rig.Owner.LoginKey = budgetLogin;
            Check((await rig.Run(resume: true)).Success && rig.Effects.ShortcutInstalls == 1 && rig.Effects.Stops == 1, "completed resume idempotent without duplicate sources");
        }
        using (var rig = new NormalRig("schema1", true))
        {
            var before = rig.Store.Load(); Check(rig.Store.LoadedSchema == 1 && before.Authority is null, "legacy schema1 genuinely unbound");
            Check((await rig.Run()).Success && rig.Store.LoadedSchema == 5 && AuthorityBinding.Same(rig.Store.Load().Authority, rig.Ledger.Binding) && rig.Store.Load().Scripts.Zip(before.Scripts).All(p => p.First.Id == p.Second.Id && p.First.Description == p.Second.Description), "explicit schema1 conversion preserves IDs metadata and existing real-compatible authority");
        }
        using (var rig = new NormalRig("partial-backup"))
        {
            var before = File.ReadAllBytes(rig.Store.FilePath); rig.Effects.FailBackup = true;
            Check(!(await rig.Run()).Success && File.ReadAllBytes(rig.Store.FilePath).SequenceEqual(before) && !File.Exists(JournalStore.PathFor(rig.Store.DirectoryPath)) && rig.Effects.Stops == 0 && rig.Effects.ShortcutInstalls == 0 && rig.Effects.ChildEffects == 0 && File.Exists(Path.Combine(rig.Manifest.BackupDirectory, "partial-copy.bytes")), "partial backup failure preserves pristine catalog and evidence with zero active effects");
        }
        using (var rig = new NormalRig("final-catalog-flush"))
        {
            rig.Effects.BreakFinalCatalog = true;
            Check(!(await rig.Run()).Success && rig.Store.Load().Migration.State == MigrationState.Incomplete, "final completed catalog write failure retains incomplete primary");
            using (var journal = JsonDocument.Parse(File.ReadAllBytes(JournalStore.PathFor(rig.Store.DirectoryPath)))) Check(journal.RootElement.GetProperty("State").GetInt32() == (int)MigrationState.Completed && journal.RootElement.GetProperty("AppliedSteps").GetArrayLength() == NormalCutoverCoordinator.Steps.Length, "final journal durable while primary completion disagrees");
            var fresh = rig.Store.Load(); var supervision = new Supervision(rig.Store, rig.Owner, rig.Clock, rig.Ledger);
            Check(!supervision.Gate(fresh, rig.Effects.Current, false, true).Success && rig.Effects.Stops == 1 && rig.Effects.ChildEffects == 0, "fresh ordinary supervision remains disarmed across final completion disagreement");
            rig.Effects.BreakFinalCatalog = false; Directory.Delete(Path.Combine(rig.Store.DirectoryPath, "catalog.json.tmp"));
            Check((await rig.Run(resume: true)).Success && rig.Effects.Stops == 1 && rig.Effects.ShortcutInstalls == 1 && JournalStore.Compatible(rig.Store.DirectoryPath, rig.Store.Load().Migration, true), "same transaction repairs final catalog commit without repeated effects");
        }
        foreach (var phase in new[] { "observe", "backup", "resume", "archive", "stop", "install", "final-observe", "final-commit" })
            foreach (var fault in new[] { "metadata", "owner", "sid", "ledger", "pending", "cancel" })
            {
                using var rig = new NormalRig("late-" + phase + "-" + fault); using var cancellation = new CancellationTokenSource(); byte[]? edited = null; bool fired = false;
                void Mutate()
                {
                    if (fired) return; fired = true;
                    if (fault == "metadata")
                    {
                        var c = JsonSerializer.Deserialize(File.ReadAllBytes(rig.Store.FilePath), TestJson.Default.Catalog)!; c.Scripts[1].Description = "late valid user edit " + phase;
                        edited = JsonSerializer.SerializeToUtf8Bytes(c, TestJson.Default.Catalog); File.WriteAllBytes(rig.Store.FilePath, edited);
                    }
                    if (fault == "owner") rig.Owner.Held = false;
                    if (fault == "sid") rig.Owner.Sid += "changed";
                    if (fault == "ledger") File.AppendAllText(rig.Ledger.FilePath, " ");
                    if (fault == "pending")
                    {
                        var node = JsonNode.Parse(File.ReadAllText(rig.Ledger.FilePath))!.AsObject(); node["Requests"]!.AsArray().Add(new JsonObject { ["Nonce"] = Guid.NewGuid().ToString(), ["ScriptPath"] = rig.Store.Load().Scripts[1].Path, ["InterpreterPath"] = rig.Store.Load().Scripts[1].Interpreter, ["Sid"] = rig.Owner.Sid, ["ManagerPid"] = 7100, ["ManagerCreated"] = 1000, ["LoginKey"] = rig.Owner.LoginKey, ["SessionId"] = 1, ["RequestedUtcTicks"] = rig.Clock.UtcNow.Ticks, ["Candidate"] = null, ["Context"] = new JsonObject { ["Kind"] = "Legacy", ["Origin"] = 0, ["Blob"] = "" } }); File.WriteAllText(rig.Ledger.FilePath, node.ToJsonString());
                    }
                    if (fault == "cancel") cancellation.Cancel();
                }
                bool resume = phase == "resume";
                if (resume)
                {
                    // A crash before the first catalog commit leaves the initial durable journal and original primary.
                    rig.Effects.CheckHook = j => { if (j?.AppliedSteps.Count == 1) throw new IOException("injected before initial catalog commit"); };
                    Check(!(await rig.Run()).Success && File.Exists(JournalStore.PathFor(rig.Store.DirectoryPath)), "resume pre-catalog setup " + fault); rig.Effects.CheckHook = null;
                }
                if (phase == "backup") rig.Effects.BackupHook = Mutate;
                else if (phase == "final-commit") rig.Effects.CheckHook = j => { if (j?.State == MigrationState.Completed) Mutate(); };
                else rig.Effects.ObserveHook = count => { if (count == (phase is "observe" or "resume" ? 1 : phase == "archive" ? 2 : phase == "stop" ? 3 : phase == "install" ? 5 : 7)) Mutate(); };
                var result = await rig.Run(resume, token: cancellation.Token);
                Check(fired && !result.Success && (edited is null || File.ReadAllBytes(rig.Store.FilePath).SequenceEqual(edited)), phase + " late " + fault + " refuses and preserves exact late metadata");
                int expectedStops = phase is "install" or "final-observe" or "final-commit" ? 1 : 0;
                int expectedInstalls = phase is "final-observe" or "final-commit" ? 1 : 0;
                Check(rig.Effects.Stops == expectedStops && rig.Effects.ShortcutInstalls == expectedInstalls && rig.Effects.ChildEffects == 0 && (phase is not ("observe" or "backup" or "resume" or "archive") || File.Exists(rig.Manifest.LegacyShortcut)), phase + " late " + fault + " blocks the next active effect");
            }
        RunPrivateTemporaryAliases();
        for (int i = 2; i < NormalCutoverCoordinator.Steps.Length - 1; i++)
            foreach (var boundary in new[] { "before", "after", "journal" })
            {
                using var rig = new NormalRig("interrupt-" + i + "-" + boundary); var step = NormalCutoverCoordinator.Steps[i]; rig.Effects.FailureStep = step; rig.Effects.AfterEffect = boundary != "before"; rig.Effects.BreakJournalWrite = boundary == "journal";
                Check(!(await rig.Run()).Success && rig.Store.Load().Migration.State == MigrationState.Incomplete, step + " " + boundary + " remains incomplete");
                var supervision = new Supervision(rig.Store, rig.Owner, rig.Clock, rig.Ledger); Check(!supervision.Gate(rig.Store.Load(), rig.Effects.Current, false, true).Success, step + " interruption keeps ordinary startup disarmed");
                if (boundary == "journal") Directory.Delete(JournalStore.PathFor(rig.Store.DirectoryPath) + ".tmp");
                rig.Effects.FailureStep = null; Check((await rig.Run(resume: true)).Success && rig.Effects.Stops == 1 && rig.Effects.ShortcutInstalls == 1 && rig.Effects.ChildEffects == 0, step + " " + boundary + " actual resume completes idempotently");
            }
        foreach (var fault in new[] { "sid", "session", "owner", "fixture", "hash", "relative", "unknown", "duplicate", "child", "privileged", "primary", "pending" })
        {
            using var rig = new NormalRig("refuse-" + fault, pending: fault == "pending");
            switch (fault)
            {
                case "sid": rig.Owner.Sid += "wrong"; break;
                case "session": rig.Manifest.ExpectedSession = 2; break;
                case "owner": rig.Owner.Held = false; break;
                case "fixture": rig.Effects.Fixture = false; break;
                case "hash": File.AppendAllText(Path.Combine(rig.Manifest.Root, NormalCutoverCoordinator.RelativePaths[2]), "edit"); break;
                case "relative": rig.Manifest.Choices[1].RelativePath = @"..\escape.ahk"; break;
                case "unknown": rig.Effects.Current = new(rig.Effects.Current.Processes, false, "inaccessible"); break;
                case "duplicate": rig.Effects.Current.Processes.Add(rig.Effects.Current.Processes[0] with { Pid = 123 }); break;
                case "child": rig.Effects.Current.Processes.RemoveAt(1); break;
                case "privileged": var c = rig.Store.Load(); c.Scripts[2].RunAsAdministrator = true; rig.Store.Save(c); rig.Manifest.CatalogSHA256 = rig.Store.LoadedFingerprint; break;
                case "primary": rig.Store.Save(rig.Store.Load()); File.WriteAllText(rig.Store.FilePath, "invalid"); break;
                case "pending": Check(rig.Ledger.HasPending && rig.Ledger.Ready, "valid pending fixture ledger loaded"); break;
            }
            Check(!(await rig.Run()).Success && rig.Effects.Backups == 0 && rig.Effects.Stops == 0 && rig.Effects.ShortcutInstalls == 0, fault + " precondition refusal zero effects");
        }
        foreach (var fault in new[] { "childchanged", "reappeared", "backup", "metadata", "cancel" })
        {
            using var rig = new NormalRig("resume-refuse-" + fault); rig.Effects.FailureStep = "ChildrenAdopted"; Check(!(await rig.Run()).Success, "resume refusal setup " + fault); rig.Effects.FailureStep = null;
            if (fault == "childchanged") rig.Effects.Current.Processes[0] = rig.Effects.Current.Processes[0] with { Created = 50000 };
            if (fault == "reappeared") rig.Effects.Current.Processes.Add(new(999, 999, rig.Store.Load().Scripts[0].Interpreter, Path.Combine(rig.Manifest.Root, "startup.ahk")));
            if (fault == "backup") File.AppendAllText(Path.Combine(rig.Manifest.BackupDirectory, "proof.bytes"), "changed");
            if (fault == "metadata") { var c = rig.Store.Load(); c.Scripts[1].Description = "later user edit"; rig.Store.Save(c); }
            using var cancel = new CancellationTokenSource(); if (fault == "cancel") cancel.Cancel();
            Check(!(await rig.Run(true, token: cancel.Token)).Success && rig.Store.Load().Migration.State == MigrationState.Incomplete && rig.Effects.Stops == 1 && rig.Effects.ChildEffects == 0, fault + " resume refusal preserves incomplete state");
        }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CreateHardLink(string link, string existing, IntPtr security);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateFile(string path, uint access, uint sharing, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool DeviceIoControl(SafeFileHandle file, uint operation, byte[] input, int length, IntPtr output, int outputLength, out uint returned, IntPtr overlapped);
    static void Junction(string path, string target)
    {
        Directory.CreateDirectory(path); string substitute = @"\??\" + target; var first = Encoding.Unicode.GetBytes(substitute); var second = Encoding.Unicode.GetBytes(target); var bytes = new byte[16 + first.Length + 2 + second.Length + 2];
        BitConverter.GetBytes(0xA0000003u).CopyTo(bytes, 0); BitConverter.GetBytes((ushort)(bytes.Length - 8)).CopyTo(bytes, 4); BitConverter.GetBytes((ushort)first.Length).CopyTo(bytes, 10); BitConverter.GetBytes((ushort)(first.Length + 2)).CopyTo(bytes, 12); BitConverter.GetBytes((ushort)second.Length).CopyTo(bytes, 14); first.CopyTo(bytes, 16); second.CopyTo(bytes, 18 + first.Length);
        using var handle = CreateFile(path, 0x40000000, 0, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid || !DeviceIoControl(handle, 0x900A4, bytes, bytes.Length, IntPtr.Zero, 0, out _, IntPtr.Zero)) throw new IOException("Cannot create isolated junction fixture " + Marshal.GetLastWin32Error());
    }
    static void RunPrivateTemporaryAliases()
    {
        foreach (var kind in new[] { "hardlink", "junction" })
            foreach (var name in new[] { "catalog.json", "migration-journal.json" })
            {
                using var rig = new NormalRig("temp-alias-" + kind + "-" + name); var dir = Path.Combine(root, "innocent-" + kind + "-" + name); Directory.CreateDirectory(dir); var target = Path.Combine(dir, "innocent.bytes"); File.WriteAllText(target, "never truncate unrelated bytes"); var before = File.ReadAllBytes(target); var temporary = Path.Combine(rig.Store.DirectoryPath, name + ".tmp");
                if (kind == "hardlink") { if (!CreateHardLink(temporary, target, IntPtr.Zero)) throw new IOException("Cannot create isolated hardlink fixture " + Marshal.GetLastWin32Error()); }
                else Junction(temporary, dir);
                bool refused = false;
                try { if (name == "catalog.json") rig.Store.Save(rig.Store.Load()); else JournalStore.Save(rig.Store.DirectoryPath, new() { Fixture = true }); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { refused = true; }
                Check(refused && before.SequenceEqual(File.ReadAllBytes(target)) && (File.Exists(temporary) || Directory.Exists(temporary)), kind + " " + name + " temporary refused without target truncation or alias cleanup");
                if (kind == "junction") { bool guard = false; try { NormalMigrationEffects.SafePath(temporary); } catch (InvalidDataException) { guard = true; } Check(guard, "production reparse guard rejects isolated " + name + " temporary"); }
                else { bool guard = false; try { NormalMigrationEffects.FileHash(temporary); } catch (InvalidDataException) { guard = true; } Check(guard, "production file identity rejects isolated " + name + " hardlink temporary"); }
            }
    }
    static void RunNormalProfileGuard()
    {
        var normal = Path.Combine(root, "unique-regular-profile"); Directory.CreateDirectory(normal); NormalMigrationEffects.GuardProfileFiles(normal); Check(true, "read-only state guard accepts isolated empty profile without effects");
        var names = new[] { "catalog.json", "catalog.json.bak", "migration-journal.json", "migration-journal.json.bak", "launch-requests.json", "launch-requests.json.bak", "launch-authority.anchor.json", "launch-authority.anchor.json.bak" };
        foreach (var name in names) File.WriteAllText(Path.Combine(normal, name), "single-link fixture bytes");
        NormalMigrationEffects.GuardProfileFiles(normal); Check(true, "read-only state guard checks all fixed existing primary/backup/ledger/anchor paths");
        foreach (var kind in new[] { "hardlink", "junction" })
            foreach (var name in names)
            {
                var profile = Path.Combine(root, kind + "-" + name); Directory.CreateDirectory(profile); var targetDir = Path.Combine(root, "victim-" + kind + "-" + name); Directory.CreateDirectory(targetDir); var victim = Path.Combine(targetDir, "victim.bytes"); File.WriteAllText(victim, "preserved exact outside-state bytes"); var before = File.ReadAllBytes(victim); var alias = Path.Combine(profile, name);
                if (kind == "hardlink") { if (!CreateHardLink(alias, victim, IntPtr.Zero)) throw new IOException("Hardlink fixture failed"); } else Junction(alias, targetDir);
                bool refused = false; try { NormalMigrationEffects.GuardProfileFiles(profile); } catch (InvalidDataException) { refused = true; }
                Check(refused && before.SequenceEqual(File.ReadAllBytes(victim)) && (File.Exists(alias) || Directory.Exists(alias)), "actual fixed-state guard rejects " + kind + " " + name + " and preserves target/alias");
            }
        foreach (var name in new[] { "catalog.json.tmp", "migration-journal.json.tmp", "launch-requests.json.tmp", "launch-authority.anchor.json.tmp" })
        {
            var profile = Path.Combine(root, "existing-" + name); Directory.CreateDirectory(profile); var path = Path.Combine(profile, name); File.WriteAllText(path, "unresolved private evidence"); var before = File.ReadAllBytes(path); bool refused = false;
            try { NormalMigrationEffects.GuardProfileFiles(profile); } catch (InvalidDataException) { refused = true; }
            Check(refused && before.SequenceEqual(File.ReadAllBytes(path)), "actual fixed-state guard refuses and preserves existing " + name);
        }
    }
}
