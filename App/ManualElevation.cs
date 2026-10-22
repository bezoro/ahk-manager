using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32.SafeHandles;

namespace Bezoro.AhkManager;

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record LaunchCandidate(int Pid, long Created, string Executable);
public enum ElevationDisposition { Accepted, Cancelled, RefusedBeforeDispatch, Uncertain }
public sealed record ElevationOutcome(ElevationDisposition Disposition, string Message, LaunchCandidate? Candidate = null)
{
    public bool Accepted => Disposition == ElevationDisposition.Accepted;
}
public interface IManualElevationDispatcher
{
    bool Fixture { get; }
    Task<ElevationOutcome> Dispatch(ManualLaunchPermit permit, ScriptEntry entry, CancellationToken token);
}

// Only the durable manual flow in this assembly can construct a permit. No bool enables elevation.
public sealed class ManualLaunchPermit
{
    readonly Func<Result> verify;
    int consumed;
    int dispatchStarted;
    public bool Fixture { get; }
    public Guid Nonce { get; }
    public Guid LedgerId { get; }
    public string ScriptPath { get; }
    public string InterpreterPath { get; }
    public bool DispatchStarted => Volatile.Read(ref dispatchStarted) != 0;
    public bool Consumed => Volatile.Read(ref consumed) != 0;
    internal ManualLaunchPermit(bool fixture, Guid ledgerId, Guid nonce, string script, string interpreter, Func<Result> verify)
    { Fixture = fixture; LedgerId = ledgerId; Nonce = nonce; ScriptPath = script; InterpreterPath = interpreter; this.verify = verify; }
    public Result Validate(ScriptEntry entry, bool fixture)
    {
        if (Fixture != fixture || Consumed || !entry.RunAsAdministrator || !Paths.Equal(entry.Path, ScriptPath) || !Paths.Equal(entry.Interpreter, InterpreterPath)) return Result.Fail("Permit incompatível, já usado ou preferência/caminhos alterados.");
        return verify();
    }
    public Result Consume(ScriptEntry entry, bool fixture, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var valid = Validate(entry, fixture); if (!valid.Success) return valid;
        if (Interlocked.CompareExchange(ref consumed, 1, 0) != 0) return Result.Fail("Permit já consumido.");
        return Result.Ok("Permit manual consumido uma única vez.");
    }
    internal void BeginDispatch() => Interlocked.Exchange(ref dispatchStarted, 1);
    // Fake dispatcher uses the same boundary; this cannot make a fixture permit real.
    public void MarkFixtureDispatch()
    { if (!Fixture || !Consumed) throw new InvalidOperationException("Permit fixture consumido necessário."); BeginDispatch(); }
}

public sealed class WindowsElevationDispatcher : IManualElevationDispatcher
{
    public bool Fixture => false;
    public static ProcessStartInfo BuildInfo(string interpreter, string script)
    {
        var start = new ProcessStartInfo(interpreter) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(script)! };
        start.ArgumentList.Add(script); return start;
    }
    public async Task<ElevationOutcome> Dispatch(ManualLaunchPermit permit, ScriptEntry entry, CancellationToken token)
    {
        // A fake permit must be rejected BEFORE any native query or dispatch.
        if (permit.Fixture) return new(ElevationDisposition.RefusedBeforeDispatch, "Permit de demonstração não alcança o Windows.");
        try
        {
            var valid = permit.Validate(entry, false); if (!valid.Success) return new(ElevationDisposition.RefusedBeforeDispatch, valid.Message);
            entry = entry.Copy(); entry.Path = Paths.Normalize(entry.Path); entry.Interpreter = Paths.NormalizeInterpreter(entry.Interpreter);
            var compatible = Discovery.Compatible(entry); if (!compatible.Success) return new(ElevationDisposition.RefusedBeforeDispatch, compatible.Message);
            var snapshot = await new WindowsProcesses().Snapshot(token);
            var policy = Policy.Check(entry, snapshot, ScriptAction.Start, File.Exists(entry.Path));
            if (!policy.Success) return new(ElevationDisposition.RefusedBeforeDispatch, policy.Message);
            // ShellExecute may wait for UAC. Keep the lane/owner until it actually returns.
            return await Task.Run(() =>
            {
                var consumed = permit.Consume(entry, false, token);
                if (!consumed.Success) return new ElevationOutcome(ElevationDisposition.RefusedBeforeDispatch, consumed.Message);
                token.ThrowIfCancellationRequested();
                permit.BeginDispatch();
                try
                {
                    using var process = Process.Start(BuildInfo(entry.Interpreter, entry.Path));
                    LaunchCandidate? candidate = null;
                    if (process is not null)
                    {
                        try
                        {
                            using var query = Native.OpenProcess(0x1000 | 0x00100000, false, process.Id);
                            if (!query.IsInvalid && Native.Identity(process.SafeHandle, out var returnedCreation, out var returnedExe) && Native.Identity(query, out var queriedCreation, out var queriedExe) && returnedCreation == queriedCreation && Paths.Equal(returnedExe, queriedExe) && Paths.Equal(queriedExe, entry.Interpreter) && Native.WaitForSingleObject(query, 0) == 258)
                                candidate = new(process.Id, queriedCreation, queriedExe);
                        }
                        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException) { }
                    }
                    return new ElevationOutcome(ElevationDisposition.Accepted, "Início solicitado; execução não confirmada. O pedido permanece registrado até verificação correlacionada.", candidate);
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return new(ElevationDisposition.Cancelled, "Solicitação UAC cancelada pelo usuário."); }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException or IOException)
                { return new(ElevationDisposition.Uncertain, "O Windows não confirmou o resultado do início. O pedido permanece registrado; nenhuma nova tentativa automática."); }
            });
        }
        catch (OperationCanceledException) { return permit.DispatchStarted ? new(ElevationDisposition.Uncertain, "Pedido já entregue; encerramento não desfaz a solicitação ao Windows.") : new(ElevationDisposition.Cancelled, "Cancelado antes de entregar a solicitação ao Windows."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or Win32Exception or InvalidOperationException)
        { return new(ElevationDisposition.RefusedBeforeDispatch, "Pré-verificação recusou o início elevado. " + ex.Message); }
    }
}
