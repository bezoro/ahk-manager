namespace Bezoro.AhkManager.Presentation;

public sealed class FixtureElevationDispatcher : IManualElevationDispatcher
{
    public bool Fixture => true;
    public int Calls { get; private set; }
    public ManualLaunchPermit? LastPermit { get; private set; }
    public ElevationOutcome Outcome { get; set; } = new(ElevationDisposition.Accepted, "Fake: pedido aceito sem identidade; nenhum UAC real.");
    public Action<ManualLaunchPermit>? BeforeConsume { get; set; }
    public Action<ManualLaunchPermit>? AfterConsume { get; set; }
    public TaskCompletionSource? Hold { get; set; }
    public async Task<ElevationOutcome> Dispatch(ManualLaunchPermit permit, ScriptEntry entry, CancellationToken token)
    {
        BeforeConsume?.Invoke(permit);
        var consumed = permit.Consume(entry, true, token);
        if (!consumed.Success) return new(ElevationDisposition.RefusedBeforeDispatch, consumed.Message);
        token.ThrowIfCancellationRequested(); permit.MarkFixtureDispatch(); Calls++; LastPermit = permit;
        AfterConsume?.Invoke(permit);
        if (Hold is not null) await Hold.Task; // Cancellation after fake delivery does not undo its result.
        return Outcome;
    }
}
