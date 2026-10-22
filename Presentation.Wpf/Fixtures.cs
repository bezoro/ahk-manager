using System.IO;

namespace Bezoro.AhkManager.Presentation;

// Explicit, isolated fixtures. Only the --render-fixture path constructs these adapters.
public sealed class FixtureProcesses : IProcessAccess
{
    public Snapshot Current { get; set; } = new([], true, "Fixture isolada, sem consulta ao PC.");
    public int Starts { get; private set; }
    public int Stops { get; private set; }
    public string? LastStarted { get; private set; }
    public bool FailStart { get; set; }
    public bool FailStop { get; set; }
    public bool RefuseProof { get; set; }
    public Action? BeforePreflight { get; set; }
    public Action? BeforeStart { get; set; }
    public Action? BeforeStop { get; set; }
    public bool ThrowSnapshot { get; set; }
    public int SnapshotCalls { get; private set; }
    public Action? BeforeSnapshot { get; set; }
    public int? ThrowSnapshotAt { get; set; }
    public TaskCompletionSource? HoldStart { get; set; }
    public TaskCompletionSource? HoldStop { get; set; }
    public Task<Result> Prove(ProcessIdentity identity, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return Task.FromResult(!RefuseProof && Current.For(identity.Script ?? "").Count == 1 && Current.Processes.Any(p => ProcessIdentity.SameCore(p, identity)) ? Result.Ok("Objeto fake positivamente revalidado.") : Result.Fail("Objeto fake não corresponde.")); }
    public Result Preflight(ScriptEntry entry) { BeforePreflight?.Invoke(); return Result.Ok("Fixture pronta"); }
    public Task<Snapshot> Snapshot(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); SnapshotCalls++; BeforeSnapshot?.Invoke();
        if (ThrowSnapshot || SnapshotCalls == ThrowSnapshotAt) throw new InvalidOperationException("Fixture snapshot failure");
        return Task.FromResult(Current);
    }
    public async Task<Result> Start(ScriptEntry entry, CancellationToken token)
    {
        BeforeStart?.Invoke(); token.ThrowIfCancellationRequested(); Starts++; LastStarted = entry.Path;
        if (FailStart) return Result.Fail("Fixture start refused");
        if (HoldStart is not null) await HoldStart.Task;
        Current = new([.. Current.Processes, new(7000 + Starts, new DateTime(2026, 10, 5, 14, 0, 0, DateTimeKind.Utc).ToFileTimeUtc(), entry.Interpreter, entry.Path, false)], true, "Fixture isolada.", Current.SupervisorPath);
        return Result.Ok("Fixture: início simulado, nenhum processo real criado.");
    }
    public async Task<Result> Stop(ProcessIdentity identity, CancellationToken token)
    {
        BeforeStop?.Invoke(); token.ThrowIfCancellationRequested();
        if (!Current.Processes.Any(p => ProcessIdentity.SameCore(p, identity))) return Result.Fail("Fixture same-object refused");
        Stops++; if (FailStop) return Result.Fail("Fixture stop refused");
        if (HoldStop is not null) await HoldStop.Task.WaitAsync(token);
        token.ThrowIfCancellationRequested(); Current = Current with { Processes = Current.Processes.Where(p => p.Pid != identity.Pid).ToList() }; return Result.Ok("Fixture: parada simulada.");
    }
}
public sealed class FixtureRecycler : IRecycler
{
    public int Calls { get; private set; }
    public Result Outcome { get; set; } = Result.Fail("Fixture: lixeira recusada, arquivo preservado.");
    public Result Recycle(string path) { Calls++; return Outcome; }
}
public sealed class FixtureInteraction : IUserInteraction
{
    public string? ScriptChoice { get; set; }
    public string? InterpreterChoice { get; set; }
    public bool Confirmed { get; set; }
    public TaskCompletionSource<bool>? HoldConfirmation { get; set; }
    public int Confirmations { get; private set; }
    public string LastConfirmation { get; private set; } = "";
    public string? Edited { get; private set; }
    public string? Copied { get; private set; }
    public string? PickScript() => ScriptChoice;
    public string? PickInterpreter() => InterpreterChoice;
    public Task<bool> Confirm(string title, string message) { Confirmations++; LastConfirmation = message; return HoldConfirmation?.Task ?? Task.FromResult(Confirmed); }
    public void CopyPath(string path) => Copied = path;
    public void EditFile(string path) => Edited = path;
}
public sealed record FixtureContext(UiSession Session, FixtureProcesses Processes, FixtureRecycler Recycler, FixtureInteraction Interaction, FixtureUnbiasedCounter? Counter = null);
public static class FixtureFactory
{
    public static FixtureContext Create(CatalogStore store, string directory, string scenario)
    {
        var fixtureFiles = Path.Combine(directory, "harmless-scripts"); Directory.CreateDirectory(fixtureFiles);
        var desktop = Path.Combine(fixtureFiles, "desktop.ahk"); var brush = Path.Combine(fixtureFiles, "brush.ahk");
        File.WriteAllText(desktop, "; harmless fixture; never executed\n#Requires AutoHotkey v2.0\n"); File.WriteAllText(brush, "; harmless fixture; never executed\n");
        var interpreter = Path.Combine(fixtureFiles, "AutoHotkey64.exe");
        var catalog = new Catalog { Scripts = [new() { Path = desktop, Description = "Mostra ou oculta os ícones da área de trabalho com duplo clique.", Interpreter = interpreter }, new() { Path = brush, Description = "Ajusta o tamanho do pincel com a roda do mouse.", DescriptionSource = "Informada", LaunchBlocked = true, Interpreter = interpreter }, new() { Path = Path.Combine(fixtureFiles, "missing.ahk"), Description = "Arquivo ausente; registro mantido para consulta." }] };
        if (scenario is "uac-pending" or "boot-recovery") catalog.Scripts[0].RunAsAdministrator = true;
        if (scenario == "empty") catalog.Scripts.Clear();
        if (scenario == "stress")
        {
            catalog.Scripts[0].Description = string.Join(" ", Enumerable.Repeat("Descrição extensa com contexto e atalhos para conferência.", 45));
            for (int i = 0; i < 60; i++) catalog.Scripts.Add(new() { Path = Path.Combine(fixtureFiles, $"script-com-nome-longo-e-identidade-preservada-{i:000}.ahk"), Description = "Registro de fixture com caminho e descrição longos, sem processo associado.", Interpreter = interpreter });
        }
        if (scenario is "enabled" or "disabled" or "pending" or "suspended")
        {
            catalog.Scripts[0].ManagementMode = scenario == "pending" ? ManagementMode.ExternalPending : ManagementMode.Normal;
            catalog.Scripts[0].RequestedEnabled = scenario != "disabled";
            if (scenario == "suspended") catalog.Recovery.Add(new() { ScriptId = catalog.Scripts[0].Id, LoginKey = "S-1-5-21-fixture|login1", Suspended = true, Failures = 3, NextTryUtcTicks = new DateTime(2026, 10, 5, 18, 4, 0, DateTimeKind.Utc).Ticks });
        }
        var owner = new FixtureOwner(); var authority = RequestLedger.OpenFixture(Path.Combine(directory, "fixture-authority"), owner, store.FilePath); catalog.Authority = authority.Binding;
        store.Save(catalog);
        if (scenario == "readonly") { store.Save(catalog); File.WriteAllText(store.FilePath, "{invalid fixture primary}"); }
        var processes = new FixtureProcesses { Current = new(catalog.Scripts.Count == 0 ? [] : [new(4200, new DateTime(2026, 10, 5, 14, 0, 0, DateTimeKind.Utc).ToFileTimeUtc(), interpreter, desktop)], scenario != "unknown", scenario == "unknown" ? "Visibilidade parcial: processo elevado ou inacessível." : "Snapshot fictício; nenhum processo real consultado.") };
        if (scenario is "uac-pending" or "boot-recovery") processes.Current = new([], true, "Fixture: nenhum processo real consultado.");
        if (scenario == "unknown") processes.Current.Processes.Add(new(4300, 0, "", null));
        var recycler = new FixtureRecycler(); var interaction = new FixtureInteraction();
        var sources = new DiscoveryResult([], [new(desktop, "Atalho Startup: Automação.lnk", false), new(brush, "Tarefa: Volume to Brush Size", true)], scenario == "unknown" ? ["Uma fonte externa está inacessível."] : []);
        var counter = new FixtureUnbiasedCounter();
        var session = new UiSession(store, processes, recycler, interaction, () => Task.FromResult(sources), owner, new FixtureClock(), authority, new FixtureElevationDispatcher(), new(true, counter, new FixtureContextProtector()));
        return new(session, processes, recycler, interaction, counter);
    }
}

public sealed class FixtureClock : IClock
{
    public DateTime UtcNow { get; set; } = new(2026, 10, 5, 18, 0, 0, DateTimeKind.Utc);
}
public sealed class FixtureOwner : IEffectOwner
{
    public bool Held { get; set; } = true;
    public bool Trusted { get; set; } = true;
    public bool Fixture => true;
    public string Sid { get; set; } = "S-1-5-21-fixture";
    public string LoginKey { get; set; } = "S-1-5-21-fixture|login1";
    public void Dispose() => Held = false;
}
