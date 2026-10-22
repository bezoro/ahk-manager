using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Bezoro.AhkManager.Presentation;

public interface IClock { DateTime UtcNow { get; } }
public sealed class SystemClock : IClock { public DateTime UtcNow => DateTime.UtcNow; }
public interface IEffectOwner : IDisposable
{
    bool Held { get; }
    bool Trusted { get; }
    bool Fixture { get; }
    string Sid { get; }
    string LoginKey { get; }
}
public sealed class UserEffectOwner : IEffectOwner
{
    readonly Mutex? mutex;
    static readonly HashSet<string> LocalOwners = [];
    bool registered;
    public bool Held { get; private set; }
    public bool Trusted { get; }
    public bool Fixture => false;
    public string Sid { get; } = "";
    public string LoginKey { get; } = "";
    [StructLayout(LayoutKind.Sequential)] struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] struct TokenStatistics
    {
        public Luid TokenId, AuthenticationId; public long Expiration; public int TokenType, ImpersonationLevel;
        public uint Charged, Available, Groups, Privileges; public Luid ModifiedId;
    }
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(SafeAccessTokenHandle token, int information, out TokenStatistics statistics, int length, out int returned);
    public static string MutexName(string sid) => "Global\\Bezoro.AhkManager.Effects." + sid;
    public UserEffectOwner()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            Sid = identity.User?.Value ?? "";
            if (Sid.Length == 0 || !GetTokenInformation(identity.AccessToken, 10, out var statistics, Marshal.SizeOf<TokenStatistics>(), out _) || statistics.AuthenticationId.Low == 0 && statistics.AuthenticationId.High == 0) return;
            LoginKey = Sid + "|" + statistics.AuthenticationId.High.ToString("X8") + statistics.AuthenticationId.Low.ToString("X8"); Trusted = true;
            lock (LocalOwners)
            {
                if (!LocalOwners.Add(Sid)) return;
                registered = true;
            }
            mutex = new Mutex(false, MutexName(Sid));
            try { Held = mutex.WaitOne(0); } catch (AbandonedMutexException) { Held = true; }
            if (!Held) { lock (LocalOwners) LocalOwners.Remove(Sid); registered = false; }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.ComponentModel.Win32Exception) { Trusted = false; if (registered) { lock (LocalOwners) LocalOwners.Remove(Sid); registered = false; } }
    }
    public void Dispose() { if (Held) { mutex!.ReleaseMutex(); Held = false; } if (registered) { lock (LocalOwners) LocalOwners.Remove(Sid); registered = false; } mutex?.Dispose(); }
}

// One nonblocking lane is shared by UI, intent changes, shutdown and timer producers.
public sealed class OperationLane
{
    int entered;
    TaskCompletionSource idle = Completed();
    static TaskCompletionSource Completed() { var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); completion.SetResult(); return completion; }
    public Task Idle => idle.Task;
    public bool TryEnter()
    {
        if (Interlocked.CompareExchange(ref entered, 1, 0) != 0) return false;
        idle = new(TaskCreationOptions.RunContinuationsAsynchronously); return true;
    }
    public void Exit() { Interlocked.Exchange(ref entered, 0); idle.TrySetResult(); }
}

public sealed class Supervision(CatalogStore store, IEffectOwner owner, IClock clock, RequestLedger authority)
{
    public IEffectOwner Owner => owner;
    public IClock Clock => clock;
    public Result Gate(Catalog catalog, Snapshot snapshot, bool shuttingDown, bool exclusiveLane, bool manualStop = false)
    {
        if (shuttingDown || !exclusiveLane) return Result.Fail("Supervisão pausada por saída ou outra operação.");
        if (!owner.Held || !owner.Trusted || owner.LoginKey.Length == 0) return Result.Fail("Outro gerenciador possui os efeitos, ou a identidade do login não é confiável.");
        if (!store.PrimaryValid || store.ReadOnly || store.LastWriteFailed || store.LoadedSchema != 5 || !store.VerifyPrimary(catalog)) return Result.Fail("Primário inválido, antigo, alterado ou sem gravação. Supervisão desarmada.");
        var ledger = authority.Verify(catalog.Authority); if (!ledger.Success) return ledger;
        if (authority.HasPending && !manualStop) return Result.Fail(authority.PendingReason);
        if (catalog.Migration.State != MigrationState.Completed || catalog.Migration.OwnerSid != owner.Sid || !JournalStore.Compatible(store.DirectoryPath, catalog.Migration, owner.Fixture)) return Result.Fail("Migração/journal não concluídos de forma durável e compatível. Nenhum efeito automático.");
        if (!snapshot.Complete || snapshot.Unknown) return Result.Fail("Snapshot parcial ou AHK desconhecido. Nenhum efeito automático, inclusive parada.");
        return Result.Ok("Supervisão pronta.");
    }
    public RecoveryRecord Budget(Catalog catalog, Guid id)
    {
        var budget = catalog.Recovery.SingleOrDefault(r => r.ScriptId == id);
        if (budget is null) { budget = new() { ScriptId = id }; catalog.Recovery.Add(budget); }
        if (budget.LoginKey != owner.LoginKey)
        {
            budget.LoginKey = owner.LoginKey; budget.AttemptsUtcTicks.Clear(); budget.Failures = 0; budget.Suspended = false; budget.NextTryUtcTicks = 0;
        }
        budget.AttemptsUtcTicks.RemoveAll(t => t < clock.UtcNow.AddMinutes(-10).Ticks);
        return budget;
    }
    public Result ReserveStart(Catalog next, ScriptEntry entry)
    {
        var budget = Budget(next, entry.Id);
        if (budget.Suspended) return Result.Fail("Recuperação suspensa. Use Tentar novamente após o cooldown.");
        if (clock.UtcNow.Ticks < budget.NextTryUtcTicks) return Result.Fail("Aguardando cooldown até " + new DateTime(budget.NextTryUtcTicks, DateTimeKind.Utc).ToString("HH:mm:ss 'UTC'"));
        if (budget.AttemptsUtcTicks.Count >= 3)
        {
            budget.Suspended = true; budget.NextTryUtcTicks = Math.Max(clock.UtcNow.AddSeconds(240).Ticks, budget.AttemptsUtcTicks.Min() + TimeSpan.FromMinutes(10).Ticks);
            return Result.Fail("Limite de três inícios em dez minutos. Recuperação suspensa.");
        }
        budget.AttemptsUtcTicks.Add(clock.UtcNow.Ticks); return Result.Ok("Tentativa reservada.");
    }
    public void RecordOutcome(Catalog next, ScriptEntry entry, bool success)
    {
        var budget = Budget(next, entry.Id);
        if (success) { budget.Failures = 0; budget.NextTryUtcTicks = 0; return; }
        budget.Failures = Math.Min(3, budget.Failures + 1);
        budget.NextTryUtcTicks = clock.UtcNow.AddSeconds(budget.Failures switch { 1 => 60, 2 => 120, _ => 240 }).Ticks;
        if (budget.Failures == 3) budget.Suspended = true;
    }
    public Result Retry(Catalog next, ScriptEntry entry)
    {
        var budget = Budget(next, entry.Id);
        if (clock.UtcNow.Ticks < budget.NextTryUtcTicks || budget.AttemptsUtcTicks.Count >= 3) return Result.Fail("Cooldown ou limite de dez minutos ainda ativo; nenhuma tentativa extra.");
        budget.Suspended = false; budget.Failures = 0; return Result.Ok("Recuperação liberada; próximo ciclo ainda depende de todos os gates.");
    }
    public string Status(Catalog catalog, ScriptEntry entry)
    {
        if (entry.ManagementMode == ManagementMode.ExternalPending) return "Tarefa externa · gerenciamento pendente de autorização específica.";
        if (entry.ManagementMode == ManagementMode.Retired) return "Supervisor aposentado · excluído da supervisão.";
        if (entry.ManagementMode != ManagementMode.Normal || entry.StartWithWindows is null) return "Não gerenciado · nenhuma intenção automática inferida.";
        var budget = catalog.Recovery.SingleOrDefault(r => r.ScriptId == entry.Id && r.LoginKey == owner.LoginKey);
        string state = entry.StartWithWindows == true ? "Próximo login: iniciar" : "Próximo login: não iniciar";
        if (entry.RunAsAdministrator && entry.StartWithWindows == true) state += " · elevação somente manual";
        if (budget?.Suspended == true) return state + " · recuperação suspensa";
        if (budget is not null && clock.UtcNow.Ticks < budget.NextTryUtcTicks) return state + " · aguardando cooldown";
        return state;
    }
}
