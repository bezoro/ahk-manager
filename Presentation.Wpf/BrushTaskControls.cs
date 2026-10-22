using System.IO;
namespace Bezoro.AhkManager.Presentation;

public enum BrushOperation { Start = 1, Stop, Restart, SetLogonEnabled }
public sealed record BrushProbe(bool Known, bool TaskEnabled, bool DemandStart, bool? LogonEnabled, string Definition, string Security, string Notice)
{
    public bool Ready => Known && TaskEnabled && DemandStart;
    public static BrushProbe Unknown(string notice) => new(false, false, false, null, "", "", notice);
}
public sealed record BrushReply(bool RequestAccepted, bool ProcessStopped, bool StartupChanged, bool Confirmed, string Message, long? RunBeganFileTime = null, int? ConfirmedPid = null, long? ConfirmedCreated = null);
public interface IBrushObservationClock { DateTime UtcNow { get; } long Timestamp { get; } TimeSpan Elapsed(long start); Task Delay(CancellationToken token); }
public sealed class BrushObservationClock : IBrushObservationClock
{
    public DateTime UtcNow => DateTime.UtcNow;
    public long Timestamp => System.Diagnostics.Stopwatch.GetTimestamp();
    public TimeSpan Elapsed(long start) => System.Diagnostics.Stopwatch.GetElapsedTime(start);
    public Task Delay(CancellationToken token) => Task.Delay(50, token);

}
public interface IBrushTask
{
    BrushProbe Probe();
    Result Run(int session);
    Result SetLogon(bool enabled);
}
public interface IBrushDispatcher
{
    bool Fixture { get; }
    Task<BrushReply> Dispatch(BrushJournal journal, Func<string, Result> authorize, CancellationToken token);
}
public sealed record BrushTaskIdle(BrushProbe Probe, int? State);
public interface IBrushTaskIdleWitness { bool Fixture { get; } BrushTaskIdle ReadIdle(); }
public interface IBrushHelperQuiescence { bool Fixture { get; } Result CheckQuiescence(BrushJournal journal); }
public sealed class BrushTaskControls
{
    public const string Script = @"C:\Users\Bezor\Documents\AutoHotkey\scripts\elevated\ahk_volume-to-brush-size.ahk";
    public const string Interpreter = @"C:\Program Files\AutoHotkey\v2\AutoHotkey64.exe";
    public const string Sid = "S-1-5-21-1314942500-26635142-908828824-1001";
    public const string TaskName = @"\AutoHotkey\Volume to Brush Size";
    public static readonly Guid RecordId = Guid.Parse("dd23f0b0-9a08-4feb-b5c6-3ad339dbc466");
    readonly IBrushTask task;
    readonly IBrushDispatcher dispatcher;
    readonly BrushOperationStore journal;
    public bool Fixture => dispatcher.Fixture;
    public BrushProbe Probe { get; private set; } = BrushProbe.Unknown("Tarefa ainda nao consultada.");
    public bool Pending => journal.Pending;
    public bool? RequestedLogon => Pending ? journal.Current?.RequestedLogon : null;
    public BrushTaskControls(IBrushTask task, IBrushDispatcher dispatcher, BrushOperationStore journal)
    {
        if (dispatcher.Fixture && task is WindowsBrushTask) throw new ArgumentException("Fixture recusa task adapter real.");
        this.task = task; this.dispatcher = dispatcher; this.journal = journal;
    }
    public static bool Matches(ScriptEntry e) => e.Id == RecordId && Paths.Equal(e.Path, Script) && Paths.Equal(e.Interpreter, Interpreter) && e.ManagementMode == ManagementMode.ExternalPending && e.RunAsAdministrator && !e.LaunchBlocked && e.StartWithWindows == false;
    public void Refresh() { try { Probe = task.Probe(); if (!Pending && Probe.Known && Probe.LogonEnabled != (journal.Current?.AllowedLogon ?? true)) Probe = Probe with { Known = false, Notice = "Gatilho mudou sem operacao Brush confirmada; efeitos bloqueados." }; } catch (Exception e) { Probe = BrushProbe.Unknown("Consulta da tarefa nao confirmada: " + e.Message); } }
    public string TaskStateText => !Probe.Known ? "Tarefa e login: nao confirmados. " + Probe.Notice : "Tarefa: " + (Probe.TaskEnabled ? "habilitada" : "desabilitada") + "; login: " + (Probe.LogonEnabled is null ? "nao confirmado" : Probe.LogonEnabled == true ? "marcado" : "desmarcado");
    public string Notice => TaskStateText + (Pending ? ". Pedido Brush pendente; nao repetir. " + (journal.Current?.Outcome ?? "Journal invalido.") + (RequestedLogon is { } v ? " Inicio solicitado: " + (v ? "marcado" : "desmarcado") + "; ultimo confirmado: " + (journal.Current?.AllowedLogon == true ? "marcado" : "desmarcado") : "") : "");
    public Result Available(ScriptEntry e, BrushOperation op, Snapshot snapshot)
    {
        if (!Matches(e) || Pending || !Probe.Ready) return Result.Fail(Pending ? Notice : "Tarefa Brush indisponivel ou definicao alterada. " + Probe.Notice);
        if (op == BrushOperation.SetLogonEnabled) return Result.Ok("Alterar somente o gatilho de login.");
        if (!snapshot.Complete || snapshot.Unknown) return Result.Fail("Processos Brush nao confirmados.");
        var active = snapshot.For(Script);
        if (op == BrushOperation.Start && active.Count != 0 || op is BrushOperation.Stop or BrushOperation.Restart && active.Count != 1) return Result.Fail("Ausencia/instancia unica Brush nao confirmada.");
        return Result.Ok("Acao manual da tarefa Brush requer confirmacao e UAC.");
    }
    public async Task<BrushReply> Execute(BrushJournal j, Func<Result> gate, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var valid = gate(); if (!valid.Success) return new(false, false, false, false, valid.Message);
        if (Pending || j.Fixture != Fixture || journal.Current?.Nonce == j.Nonce) return new(false, false, false, false, "Pedido pendente ou modo incompatÃ­vel.");
        Refresh(); if (!Probe.Ready) return new(false, false, false, false, Probe.Notice);
        j.Definition = Probe.Definition; j.Security = Probe.Security; j.AllowedLogon = journal.Current?.AllowedLogon ?? true; journal.Save(j);
        var consumed = new HashSet<string>();
        Result Authorize(string stage)
        {
            token.ThrowIfCancellationRequested(); var ready = gate(); if (!ready.Success) return ready;
            if (!Allowed(j.Operation, consumed, stage) || !consumed.Add(stage)) return Result.Fail("Etapa Brush repetida ou invalida.");
            Refresh(); if (!Probe.Ready || Probe.Definition != j.Definition || Probe.Security != j.Security || Probe.LogonEnabled != j.AllowedLogon) return Result.Fail("Tarefa mudou antes do efeito.");
            if (stage == "Run") { j.Version = 2; j.RunAuthorizedFileTime = DateTime.UtcNow.ToFileTimeUtc(); }
            j.Stage = stage; journal.Save(j); return Result.Ok("Intencao Brush persistida.");
        }
        try
        {
            var reply = await dispatcher.Dispatch(j, Authorize, token);
            token.ThrowIfCancellationRequested(); var finalGate = gate();
            if (!finalGate.Success) reply = reply with { Confirmed = false, Message = "Resultado pendente: autoridade mudou depois do efeito." };
            if (j.Version == 2 && consumed.Contains("Run")) { j.RequestAccepted = reply.RequestAccepted; j.RunBeganFileTime = reply.RunBeganFileTime; j.ConfirmedPid = reply.ConfirmedPid; j.ConfirmedCreated = reply.ConfirmedCreated; BrushOperationStore.ValidateRunEvidence(j); }
            j.Outcome = reply.Message;
            // Only a positively acknowledged completed operation can release the block.
            if (reply.Confirmed) { if (reply.StartupChanged && j.RequestedLogon is { } enabled) j.AllowedLogon = enabled; j.Completed = true; j.Stage = "Completed"; }
            journal.Save(j); Refresh(); return reply;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException)
        {
            j.Outcome = "Resultado Brush nao confirmado; pedido permanece bloqueado. " + e.Message;
            try { journal.Save(j); } catch { }
            return new(false, false, false, false, j.Outcome);
        }
    }
    public static bool Allowed(BrushOperation op, HashSet<string> consumed, string stage) => op switch
    {
        BrushOperation.Start => stage == "Run" && consumed.Count == 0,
        BrushOperation.Stop => stage == "Stop" && consumed.Count == 0,
        BrushOperation.Restart => stage == "Stop" && consumed.Count == 0 || stage == "Run" && consumed.SetEquals(["Stop"]),
        BrushOperation.SetLogonEnabled => stage == "Register" && consumed.Count == 0,
        _ => false
    };
    public static async Task<BrushReply> RunHelper(BrushJournal j, IBrushTask task, IProcessAccess processes, Func<string, Task<Result>> step, Func<bool> alive, CancellationToken token, IBrushObservationClock? clock = null)
    {
        clock ??= new BrushObservationClock();
        if (j.Fixture && (task is WindowsBrushTask || processes is WindowsProcesses)) return new(false, false, false, false, "Helper fixture recusa adapters reais antes de qualquer consulta.");
        bool stopped = false;
        async Task<Result> Gate(string stage)
        {
            token.ThrowIfCancellationRequested(); if (!alive()) return Result.Fail("Solicitante ausente.");
            var p = task.Probe(); if (!p.Ready || p.Definition != j.Definition || p.Security != j.Security || p.LogonEnabled != j.AllowedLogon) return Result.Fail("Tarefa alterada ou desabilitada.");
            var ready = await step(stage); token.ThrowIfCancellationRequested();
            p = task.Probe(); return ready.Success && alive() && p.Ready && p.Definition == j.Definition && p.Security == j.Security && p.LogonEnabled == j.AllowedLogon ? ready : Result.Fail("Autoridade ou tarefa mudou antes do efeito.");
        }
        if (j.Operation == BrushOperation.SetLogonEnabled)
        {
            var gate = await Gate("Register"); if (!gate.Success) return new(false, false, false, false, gate.Message);
            var result = task.SetLogon(j.RequestedLogon!.Value); var after = task.Probe();
            bool confirmed = result.Success && after.Ready && after.Definition == j.Definition && after.Security == j.Security && after.LogonEnabled == j.RequestedLogon;
            return new(false, false, result.Success, confirmed, confirmed ? "Gatilho de login confirmado; tarefa continua habilitada e pode iniciar manualmente." : "Alteracao do gatilho nao confirmada; nao repetir.");
        }
        var snapshot = await processes.Snapshot(token);
        if (!snapshot.Complete || snapshot.Unknown) return new(false, false, false, false, "Consulta Brush incompleta.");
        if (j.Operation is BrushOperation.Stop or BrushOperation.Restart)
        {
            var active = snapshot.For(Script); if (active.Count != 1) return new(false, false, false, false, "Alvo Brush unico nao confirmado.");
            var ready = await Gate("Stop"); if (!ready.Success) return new(false, false, false, false, ready.Message);
            var result = await processes.Stop(active[0], token); if (!result.Success) return new(false, false, false, false, result.Message);
            stopped = true;
            if (j.Operation == BrushOperation.Stop) return new(false, true, false, true, "Instancia Brush exata encerrada e saida confirmada.");
        }
        var runGate = await Gate("Run"); if (!runGate.Success) return new(false, stopped, false, false, stopped ? "Brush parado; reinicio nao confirmado. " + runGate.Message : runGate.Message);
        snapshot = await processes.Snapshot(token);
        if (!snapshot.Complete || snapshot.Unknown || snapshot.For(Script).Count != 0 || !alive()) return new(false, stopped, false, false, "Ausencia Brush ou solicitante nao confirmado; nenhum Run.");
        token.ThrowIfCancellationRequested(); var finalTask = task.Probe();
        if (!finalTask.Ready || finalTask.Definition != j.Definition || finalTask.Security != j.Security || finalTask.LogonEnabled != j.AllowedLogon || !alive()) return new(false, stopped, false, false, stopped ? "Brush parado; tarefa mudou antes do reinicio." : "Tarefa mudou antes do Run; nenhum inicio.");
        long began = clock.UtcNow.ToFileTimeUtc();
        if (clock.UtcNow.Ticks >= j.DeadlineUtcTicks) return new(false, stopped, false, false, "Prazo original expirou antes do Run.");
        var run = task.Run(j.Session); if (!run.Success) return new(false, stopped, false, false, run.Message);
        var observed = await ObserveAccepted(j, task, processes, began, null, alive, token, clock, true);
        return new(true, stopped, false, observed is not null, observed is not null ? "Pedido aceito e processo Brush elevado confirmado." : "Pedido aceito pela tarefa; execucao nao confirmada. Nao repetir.", began, observed?.Pid, observed?.Created);
    }
    static bool TaskMatches(BrushJournal j, BrushProbe p) => p.Ready && p.Definition == j.Definition && p.Security == j.Security && p.LogonEnabled == j.AllowedLogon;
    public static async Task<ProcessIdentity?> ObserveAccepted(BrushJournal j, IBrushTask task, IProcessAccess processes, long lower, ProcessIdentity? pinned, Func<bool> alive, CancellationToken token, IBrushObservationClock clock, bool requestDeadline)
    {
        long start = clock.Timestamp; long upper = new DateTime(j.DeadlineUtcTicks, DateTimeKind.Utc).ToFileTimeUtc();
        bool Valid() => !token.IsCancellationRequested && clock.Elapsed(start) < TimeSpan.FromSeconds(2) && (!requestDeadline || clock.UtcNow.Ticks < j.DeadlineUtcTicks) && alive() && TaskMatches(j, task.Probe());
        try
        {
        while (Valid())
        {
            token.ThrowIfCancellationRequested(); var snapshot = await processes.Snapshot(token).WaitAsync(TimeSpan.FromSeconds(2) - clock.Elapsed(start), token);
            if (!Valid() || !snapshot.Complete || snapshot.Unknown) return null;
            var active = snapshot.For(Script); if (active.Count > 1) return null;
            if (active.Count == 0) { if (pinned is not null) return null; await clock.Delay(token); continue; }
            var candidate = active[0];
            if (!Paths.Equal(candidate.Executable, Interpreter) || candidate.Elevated != true || candidate.Created < lower || candidate.Created > upper || pinned is not null && !ProcessIdentity.SameCore(pinned, candidate)) return null;
            pinned = candidate;
            var proof = await processes.Prove(candidate, token).WaitAsync(TimeSpan.FromSeconds(2) - clock.Elapsed(start), token); if (!proof.Success || !Valid()) return null;
            var recent = await processes.Snapshot(token).WaitAsync(TimeSpan.FromSeconds(2) - clock.Elapsed(start), token); if (!Valid() || !recent.Complete || recent.Unknown || recent.For(Script).Count != 1 || !ProcessIdentity.SameCore(candidate, recent.For(Script)[0]) || recent.For(Script)[0].Elevated != true) return null;
            return candidate;
        }
        }
        catch (TimeoutException) { return null; }
        return null;
    }
    public const string LegacyIncidentHash = "321920AEA5C1B2CFD08E3367D7BD0175741045B313D454A93B5A35AF42772A94";
    public const long LegacyRunWitness = 134358570716616323;
    public static readonly ProcessIdentity LegacyTarget = new(50148, 134358570717049438, Interpreter, Script, true);
    const string LegacyBytes = "eyJWZXJzaW9uIjoxLCJGaXh0dXJlIjpmYWxzZSwiTm9uY2UiOiJhNjdhMjJkMC05MzI2LTRkZGMtYmY2NS1lYWNlNzVmNDNmNDEiLCJPcGVyYXRpb24iOjEsIlJlcXVlc3RlZExvZ29uIjpudWxsLCJTaWQiOiJTLTEtNS0yMS0xMzE0OTQyNTAwLTI2NjM1MTQyLTkwODgyODgyNC0xMDAxIiwiTG9naW5LZXkiOiJTLTEtNS0yMS0xMzE0OTQyNTAwLTI2NjM1MTQyLTkwODgyODgyNC0xMDAxfDAwMDAwMDAwMDAwNUMyREMiLCJNYW5hZ2VyUGlkIjoyOTI4NCwiTWFuYWdlckNyZWF0ZWQiOjEzNDM1ODU2ODg3OTU0NjYzNSwiU2Vzc2lvbiI6MSwiRXhlY3V0YWJsZSI6IlY6XFxSZXBvc1xcQmV6b3JvLkFoa01hbmFnZXJcXHB1Ymxpc2hcXEJlem9yby5BaGtNYW5hZ2VyLmV4ZSIsIkV4ZWN1dGFibGVIYXNoIjoiREI3NEM0NzJFNzI2N0U5OTg4M0YyRUY5QjUyREU2QzBDMUNBQkVFQzQ5QkE2REFEN0FEQjA5OTI4MDkxRkU5MiIsIkFzc2VtYmx5SGFzaCI6IjEzNjY2RUI1NTY0OEZEM0RFRDA0ODUyRUM1RTQ4N0ZFNkIxQUM3MkY3MzgxM0JBRTRBMDE4NzBDQ0E5MzdEMjciLCJBbGxvd2VkTG9nb24iOnRydWUsIkNhdGFsb2dIYXNoIjoiRTREQ0FCRTIzN0E3RTVDODUyRTlFQkE2NEY1MDM4MkYxQ0Q3RTlBQ0E1QkY1NzQ2OUJEQUZDNkU0NjhCODEzMSIsIkxlZGdlckhhc2giOiI0QTUxRDhGNzlCN0VFN0ZDREY2NjNBOEQ1RDcwRjg2NUZFQTcxNkYxQUZBOUYzQzg0MUI4RDg1RDYyNzVFNjE0IiwiQW5jaG9ySGFzaCI6IjQ3RjdCMDZBMjYwNjhGMjJBMzA4MkYxNkFBRUYyODk4RjM4RjAyNzU4OEM1OTNDMjlGMTM4N0VFQUE2MTcwMkMiLCJBdXRob3JpdHlJZCI6IjFiYzdmN2Q4LWFmNGEtNDkzNi1iZGQ3LWE5NWMxNGZlZGIxMiIsIkRlZmluaXRpb24iOiJcclxuXHUwMDNDVGFzayB2ZXJzaW9uPVx1MDAyMjEuM1x1MDAyMiB4bWxucz1cdTAwMjJodHRwOi8vc2NoZW1hcy5taWNyb3NvZnQuY29tL3dpbmRvd3MvMjAwNC8wMi9taXQvdGFza1x1MDAyMlx1MDAzRVxyXG4gIFx1MDAzQ1JlZ2lzdHJhdGlvbkluZm9cdTAwM0VcclxuICAgIFx1MDAzQ0Rlc2NyaXB0aW9uXHUwMDNFUnVuIHRoZSBBdXRvSG90a2V5IHZvbHVtZS10by1icnVzaC1zaXplIHNjcmlwdCBlbGV2YXRlZCBhdCB1c2VyIGxvZ29uLlx1MDAzQy9EZXNjcmlwdGlvblx1MDAzRVxyXG4gICAgXHUwMDNDVVJJXHUwMDNFXFxBdXRvSG90a2V5XFxWb2x1bWUgdG8gQnJ1c2ggU2l6ZVx1MDAzQy9VUklcdTAwM0VcclxuICBcdTAwM0MvUmVnaXN0cmF0aW9uSW5mb1x1MDAzRVxyXG4gIFx1MDAzQ1ByaW5jaXBhbHNcdTAwM0VcclxuICAgIFx1MDAzQ1ByaW5jaXBhbCBpZD1cdTAwMjJBdXRob3JcdTAwMjJcdTAwM0VcclxuICAgICAgXHUwMDNDVXNlcklkXHUwMDNFUy0xLTUtMjEtMTMxNDk0MjUwMC0yNjYzNTE0Mi05MDg4Mjg4MjQtMTAwMVx1MDAzQy9Vc2VySWRcdTAwM0VcclxuICAgICAgXHUwMDNDTG9nb25UeXBlXHUwMDNFSW50ZXJhY3RpdmVUb2tlblx1MDAzQy9Mb2dvblR5cGVcdTAwM0VcclxuICAgICAgXHUwMDNDUnVuTGV2ZWxcdTAwM0VIaWdoZXN0QXZhaWxhYmxlXHUwMDNDL1J1bkxldmVsXHUwMDNFXHJcbiAgICBcdTAwM0MvUHJpbmNpcGFsXHUwMDNFXHJcbiAgXHUwMDNDL1ByaW5jaXBhbHNcdTAwM0VcclxuICBcdTAwM0NTZXR0aW5nc1x1MDAzRVxyXG4gICAgXHUwMDNDRGlzYWxsb3dTdGFydElmT25CYXR0ZXJpZXNcdTAwM0VmYWxzZVx1MDAzQy9EaXNhbGxvd1N0YXJ0SWZPbkJhdHRlcmllc1x1MDAzRVxyXG4gICAgXHUwMDNDU3RvcElmR29pbmdPbkJhdHRlcmllc1x1MDAzRWZhbHNlXHUwMDNDL1N0b3BJZkdvaW5nT25CYXR0ZXJpZXNcdTAwM0VcclxuICAgIFx1MDAzQ0V4ZWN1dGlvblRpbWVMaW1pdFx1MDAzRVBUMFNcdTAwM0MvRXhlY3V0aW9uVGltZUxpbWl0XHUwMDNFXHJcbiAgICBcdTAwM0NNdWx0aXBsZUluc3RhbmNlc1BvbGljeVx1MDAzRUlnbm9yZU5ld1x1MDAzQy9NdWx0aXBsZUluc3RhbmNlc1BvbGljeVx1MDAzRVxyXG4gICAgXHUwMDNDSWRsZVNldHRpbmdzXHUwMDNFXHJcbiAgICAgIFx1MDAzQ0R1cmF0aW9uXHUwMDNFUFQxME1cdTAwM0MvRHVyYXRpb25cdTAwM0VcclxuICAgICAgXHUwMDNDV2FpdFRpbWVvdXRcdTAwM0VQVDFIXHUwMDNDL1dhaXRUaW1lb3V0XHUwMDNFXHJcbiAgICAgIFx1MDAzQ1N0b3BPbklkbGVFbmRcdTAwM0V0cnVlXHUwMDNDL1N0b3BPbklkbGVFbmRcdTAwM0VcclxuICAgICAgXHUwMDNDUmVzdGFydE9uSWRsZVx1MDAzRWZhbHNlXHUwMDNDL1Jlc3RhcnRPbklkbGVcdTAwM0VcclxuICAgIFx1MDAzQy9JZGxlU2V0dGluZ3NcdTAwM0VcclxuICAgIFx1MDAzQ1VzZVVuaWZpZWRTY2hlZHVsaW5nRW5naW5lXHUwMDNFdHJ1ZVx1MDAzQy9Vc2VVbmlmaWVkU2NoZWR1bGluZ0VuZ2luZVx1MDAzRVxyXG4gIFx1MDAzQy9TZXR0aW5nc1x1MDAzRVxyXG4gIFx1MDAzQ1RyaWdnZXJzXHUwMDNFXHJcbiAgICBcdTAwM0NMb2dvblRyaWdnZXJcdTAwM0VcclxuICAgICAgXHUwMDNDVXNlcklkXHUwMDNFQkVaT1JPXFxCZXpvclx1MDAzQy9Vc2VySWRcdTAwM0VcclxuICAgIFx1MDAzQy9Mb2dvblRyaWdnZXJcdTAwM0VcclxuICBcdTAwM0MvVHJpZ2dlcnNcdTAwM0VcclxuICBcdTAwM0NBY3Rpb25zIENvbnRleHQ9XHUwMDIyQXV0aG9yXHUwMDIyXHUwMDNFXHJcbiAgICBcdTAwM0NFeGVjXHUwMDNFXHJcbiAgICAgIFx1MDAzQ0NvbW1hbmRcdTAwM0VDOlxcUHJvZ3JhbSBGaWxlc1xcQXV0b0hvdGtleVxcdjJcXEF1dG9Ib3RrZXk2NC5leGVcdTAwM0MvQ29tbWFuZFx1MDAzRVxyXG4gICAgICBcdTAwM0NBcmd1bWVudHNcdTAwM0VcdTAwMjJDOlxcVXNlcnNcXEJlem9yXFxEb2N1bWVudHNcXEF1dG9Ib3RrZXlcXHNjcmlwdHNcXGVsZXZhdGVkXFxhaGtfdm9sdW1lLXRvLWJydXNoLXNpemUuYWhrXHUwMDIyXHUwMDNDL0FyZ3VtZW50c1x1MDAzRVxyXG4gICAgICBcdTAwM0NXb3JraW5nRGlyZWN0b3J5XHUwMDNFQzpcXFVzZXJzXFxCZXpvclxcRG9jdW1lbnRzXFxBdXRvSG90a2V5XFxzY3JpcHRzXFxlbGV2YXRlZFx1MDAzQy9Xb3JraW5nRGlyZWN0b3J5XHUwMDNFXHJcbiAgICBcdTAwM0MvRXhlY1x1MDAzRVxyXG4gIFx1MDAzQy9BY3Rpb25zXHUwMDNFXHJcblx1MDAzQy9UYXNrXHUwMDNFIiwiU2VjdXJpdHkiOiJPOkJBRzpTLTEtNS0yMS0xMzE0OTQyNTAwLTI2NjM1MTQyLTkwODgyODgyNC0xMDAxRDooQTtJRDsweDFmMDE5Zjs7O0JBKShBO0lEOzB4MWYwMTlmOzs7U1kpKEE7SUQ7RkE7OztCQSkoQTs7RlI7OztTLTEtNS0yMS0xMzE0OTQyNTAwLTI2NjM1MTQyLTkwODgyODgyNC0xMDAxKSIsIlN0YWdlIjoiUnVuIiwiQ29tcGxldGVkIjpmYWxzZSwiT3V0Y29tZSI6IlBlZGlkbyBhY2VpdG8gcGVsYSB0YXJlZmE7IGV4ZWN1Y2FvIG5hbyBjb25maXJtYWRhLiBOYW8gcmVwZXRpci4iLCJEZWFkbGluZVV0Y1RpY2tzIjo2MzkyNjk4MDM1OTcxODk3MDB9";
    public static bool LegacyEligible(BrushJournal j, string digest)
    {
        var original = System.Text.Json.JsonSerializer.Deserialize(Convert.FromBase64String(LegacyBytes), BrushJson.Default.BrushJournal)!;
        return digest == LegacyIncidentHash && j.Version == 1 && !j.Fixture && !j.Completed && j.Operation == BrushOperation.Start && j.Stage == "Run" && j.Nonce == Guid.Parse("a67a22d0-9326-4ddc-bf65-eace75f43f41") && j.AssemblyHash == "13666EB55648FD3DED04852EC5E487FE6B1AC72F73813BAE4A01870CCA937D27" && System.Text.Json.JsonSerializer.Serialize(j, BrushJson.Default.BrushJournal) == System.Text.Json.JsonSerializer.Serialize(original, BrushJson.Default.BrushJournal);
    }
    public const string RegisterIncidentHash = "C7E9E0CF465CA0B7D783A0CCAE263DF6155CD8987AB06DB47A411FF8F5135FA7";
    public static bool RegisterIncidentEligible(BrushJournal j, string committedHash) => committedHash == RegisterIncidentHash
        && j.Version == 1 && !j.Fixture && j.Nonce == Guid.Parse("e6efe180-a91a-4fad-b334-036c323f3433") && j.Operation == BrushOperation.SetLogonEnabled
        && j.RequestedLogon == false && j.Stage == "Register" && !j.Completed && j.AllowedLogon && j.Sid == Sid && j.Session == 1
        && j.LoginKey == Sid + "|000000000005C2DC" && j.ManagerPid == 42108 && j.ManagerCreated == 134358825259404629
        && j.AssemblyHash == "FFAD02B50F2C458E62BF9AC6FEFD15A75DDB5A267A49376578E345860E3B7651"
        && Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(j, BrushJson.Default.BrushJournal))) == RegisterIncidentHash;
    async Task<BrushReply> SettleRegisterRefusal(BrushJournal j, Func<BrushJournal,Result> verify, IProcessAccess processes, CancellationToken token, IBrushObservationClock clock)
    {
        var start = clock.Timestamp;
        bool Valid() => !token.IsCancellationRequested && clock.Elapsed(start) < TimeSpan.FromSeconds(2) && !journal.Blocked && RegisterIncidentEligible(j, journal.CommittedHash)
            && verify(j).Success && task is IBrushRegisterRefusalWitness witness && witness.ProvesRegisterRefusal(j)
            && NormalMigrationEffects.FileHash(journal.FilePath) == RegisterIncidentHash && clock.Elapsed(start) < TimeSpan.FromSeconds(2) && !token.IsCancellationRequested;
        bool SameTarget(Snapshot snapshot) => snapshot.Complete && !snapshot.Unknown && snapshot.For(Script).Count == 1 && snapshot.For(Script)[0].Elevated == true && ProcessIdentity.SameCore(snapshot.For(Script)[0], LegacyTarget);
        BrushReply Refused() => new(false,false,false,false,"Prova de recusa Register insuficiente; pedido preservado.");
        try
        {
            if (!Valid()) return Refused();
            var first = await processes.Snapshot(token).WaitAsync(TimeSpan.FromSeconds(2) - clock.Elapsed(start), token);
            if (!SameTarget(first) || !Valid()) return Refused();
            var proof = await processes.Prove(first.For(Script)[0],token).WaitAsync(TimeSpan.FromSeconds(2) - clock.Elapsed(start), token);
            if (!proof.Success || !Valid()) return Refused();
            var recent = await processes.Snapshot(token).WaitAsync(TimeSpan.FromSeconds(2) - clock.Elapsed(start), token);
            if (!SameTarget(recent) || !Valid()) return Refused();
            token.ThrowIfCancellationRequested();
            j.Completed = true; j.Stage = "Completed"; j.Outcome = "Recusado antes de Register; login nao alterado. Recusa exata comprovada somente leitura.";
            journal.Save(j); Refresh();
            // Completion proves non-execution. The requested change remains a failed operation.
            return new(false,false,false,false,j.Outcome);
        }
        catch (TimeoutException) { return Refused(); }
    }
    public const string PostRegisterIncidentHash = "4900158BB1468FC00EEAFD085E800E630D05CA829FD9F397E7450B74A34CEB12";
    public static bool PostRegisterIncidentEligible(BrushJournal j, string committedHash) => committedHash == PostRegisterIncidentHash
        && j.Version == 1 && !j.Fixture && j.Nonce == Guid.Parse("87432bb8-449b-4b77-be15-244308b7eea8") && j.Operation == BrushOperation.SetLogonEnabled
        && j.RequestedLogon == false && j.Stage == "Register" && !j.Completed && j.AllowedLogon && j.Sid == Sid && j.Session == 1
        && j.LoginKey == Sid + "|000000000005C2DC" && j.ManagerPid == 10864 && j.ManagerCreated == 134358948478308880
        && j.AssemblyHash == "7C431DE7EE95E3FDAF07DFC3BA516FF70CDA2D30C73414950F89E85048E75379"
        && Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(j, BrushJson.Default.BrushJournal))) == PostRegisterIncidentHash;
    async Task<BrushReply> SettlePostRegister(BrushJournal j, Func<BrushJournal,Result> verify, IProcessAccess processes, CancellationToken token, IBrushObservationClock clock)
    {
        var start = clock.Timestamp;
        bool Valid()
        {
            if (token.IsCancellationRequested || clock.Elapsed(start) >= TimeSpan.FromSeconds(2) || journal.Blocked || !PostRegisterIncidentEligible(j,journal.CommittedHash) || !verify(j).Success) return false;
            var current = task.Probe();
            return current.Ready && current.LogonEnabled == false && current.Definition == j.Definition && current.Security == j.Security
                && current.Definition == WindowsBrushTask.Canonical(WindowsBrushTask.BaselineXml)
                && NormalMigrationEffects.FileHash(journal.FilePath) == PostRegisterIncidentHash
                && !token.IsCancellationRequested && clock.Elapsed(start) < TimeSpan.FromSeconds(2);
        }
        bool SameTarget(Snapshot snapshot) => snapshot.Complete && !snapshot.Unknown && snapshot.For(Script).Count == 1 && snapshot.For(Script)[0].Elevated == true && ProcessIdentity.SameCore(snapshot.For(Script)[0], LegacyTarget);
        BrushReply Refused() => new(false,false,false,false,"Confirmacao do Register original insuficiente; pedido preservado.");
        try
        {
            if (!Valid()) return Refused();
            var remaining = TimeSpan.FromSeconds(2)-clock.Elapsed(start);
            if (remaining <= TimeSpan.Zero) return Refused();
            var first = await processes.Snapshot(token).WaitAsync(remaining,token);
            if (!SameTarget(first) || !Valid()) return Refused();
            remaining = TimeSpan.FromSeconds(2)-clock.Elapsed(start);
            if (remaining <= TimeSpan.Zero) return Refused();
            var proof = await processes.Prove(first.For(Script)[0],token).WaitAsync(remaining,token);
            if (!proof.Success || !Valid()) return Refused();
            remaining = TimeSpan.FromSeconds(2)-clock.Elapsed(start);
            if (remaining <= TimeSpan.Zero) return Refused();
            var recent = await processes.Snapshot(token).WaitAsync(remaining,token);
            if (!SameTarget(recent) || !Valid()) return Refused();
            token.ThrowIfCancellationRequested();
            j.AllowedLogon = false; j.Completed = true; j.Stage = "Completed"; j.Outcome = "Gatilho de login OFF original confirmado somente leitura; tarefa habilitada e Brush elevado preservados.";
            journal.Save(j); Refresh(); return new(false,false,true,true,j.Outcome);
        }
        catch (TimeoutException) { return Refused(); }
    }
    public static bool CanConcludeAbsent(BrushJournal j) => !j.Completed && (
        j.Operation == BrushOperation.Stop && j.Stage == "Stop"
        || j.Operation == BrushOperation.Restart && j.Stage == "Stop"
        || j.Operation is BrushOperation.Start or BrushOperation.Restart && j.Stage == "Run" && j.RequestAccepted != true);
    async Task<BrushReply> SettleAbsent(BrushJournal j, Func<BrushJournal,Result> verify, IProcessAccess processes, CancellationToken token, IBrushObservationClock clock)
    {
        BrushReply Refused() => new(false,false,false,false,"Ausencia ou quiescencia Brush nao confirmada; pedido preservado.");
        if (task is not IBrushTaskIdleWitness idle || dispatcher is not IBrushHelperQuiescence helpers
            || idle.Fixture != Fixture || helpers.Fixture != Fixture || Fixture && (task is WindowsBrushTask || processes is WindowsProcesses)) return Refused();
        string fingerprint = journal.CommittedHash;
        string Typed(BrushJournal value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value, BrushJson.Default.BrushJournal)));
        long start = clock.Timestamp;
        bool BindingsValid() => !token.IsCancellationRequested && clock.Elapsed(start) < TimeSpan.FromSeconds(2) && !journal.Blocked && fingerprint.Length == 64
            && journal.CommittedHash == fingerprint && Typed(j) == fingerprint && journal.Current is { } current && Typed(current) == fingerprint
            && NormalMigrationEffects.FileHash(journal.FilePath) == fingerprint && verify(j).Success && (Fixture || BrushTaskHelper.AuthorityFiles(j));
        bool TaskReady(BrushTaskIdle witness) => witness.State == 3 && witness.Probe.Ready && witness.Probe.Definition == j.Definition
            && witness.Probe.Security == j.Security && witness.Probe.LogonEnabled == j.AllowedLogon;
        bool Valid() => BindingsValid() && TaskReady(idle.ReadIdle()) && helpers.CheckQuiescence(j).Success && TaskReady(idle.ReadIdle()) && BindingsValid();
        try
        {
            if (!Valid()) return Refused();
            for (int count = 0; count < 2; count++)
            {
                var remaining = TimeSpan.FromSeconds(2)-clock.Elapsed(start); if (remaining <= TimeSpan.Zero) return Refused();
                var observed = await processes.Snapshot(token).WaitAsync(remaining,token);
                if (!observed.Complete || observed.Unknown || observed.For(Script).Count != 0 || !Valid()) return Refused();
            }
            if (!Valid()) return Refused();
            token.ThrowIfCancellationRequested();
            j.Completed = true; j.Stage = "Completed";
            j.Outcome = j.Operation == BrushOperation.Stop ? "Ausencia Brush confirmada somente leitura; encerramento anterior nao comprovado."
                : "Inicio ou reinicio falhou; Brush ausente confirmado somente leitura. Novo inicio exige acao explicita.";
            journal.Save(j); Refresh(); return new(false,false,false,j.Operation == BrushOperation.Stop,j.Outcome);
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException or IOException or UnauthorizedAccessException or InvalidOperationException) { return Refused(); }
    }
    public async Task<BrushReply> Settle(Func<BrushJournal, Result> verify, IProcessAccess processes, CancellationToken token, IBrushObservationClock? clock = null)
    {
        var j = journal.Current;
        if (journal.Blocked || j is null || j.Completed || j.Fixture != Fixture || Fixture && processes is WindowsProcesses) return new(false, false, false, false, "Pedido Brush sem evidencia elegivel; preservado.");
        if (CanConcludeAbsent(j)) return await SettleAbsent(j,verify,processes,token,clock ?? new BrushObservationClock());
        if (PostRegisterIncidentEligible(j,journal.CommittedHash)) return await SettlePostRegister(j,verify,processes,token,clock ?? new BrushObservationClock());
        if (RegisterIncidentEligible(j, journal.CommittedHash)) return await SettleRegisterRefusal(j, verify, processes, token, clock ?? new BrushObservationClock());
        if (j.Operation is not (BrushOperation.Start or BrushOperation.Restart) || j.Stage != "Run") return new(false, false, false, false, "Pedido Brush sem evidencia elegivel; preservado.");
        var ready = verify(j); if (!ready.Success) return new(false, false, false, false, ready.Message);
        bool legacy = LegacyEligible(j, journal.CommittedHash);
        if (!legacy && (j.Version != 2 || j.RequestAccepted != true || j.RunBeganFileTime is not > 0 || j.RunAuthorizedFileTime is not > 0)) return new(false, false, false, false, "Aceitacao tipada ausente; pedido preservado.");
        long lower = legacy ? LegacyRunWitness : j.RunBeganFileTime!.Value;
        var expected = legacy ? LegacyTarget : j.ConfirmedPid is { } pid && j.ConfirmedCreated is { } created ? new ProcessIdentity(pid,created,Interpreter,Script,true) : null;
        var observed = await ObserveAccepted(j, task, processes, lower, expected, () => verify(j).Success, token, clock ?? new BrushObservationClock(), false);
        if (observed is null || !verify(j).Success || token.IsCancellationRequested) return new(false, false, false, false, "Prova Brush nao confirmada; pedido preservado.");
        j.Completed = true; j.Stage = "Completed"; j.Outcome = "Instancia Brush elevada exata observada e revalidada; pedido concluido por verificacao somente leitura.";
        if (!legacy) { j.ConfirmedPid = observed.Pid; j.ConfirmedCreated = observed.Created; }
        journal.Save(j); Refresh(); return new(true,false,false,true,j.Outcome,j.RunBeganFileTime,observed.Pid,observed.Created);
    }
}
