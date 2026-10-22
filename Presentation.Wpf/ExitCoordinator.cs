namespace Bezoro.AhkManager.Presentation;

// Shared by the tray exit command and the missing-tray window-close fallback.
public sealed class ExitCoordinator(UiSession session, Func<Task<bool>> confirm, Action suspendProducers, Action finish, TimeSpan timeout)
{
    bool promptPending, completing;
    public bool ExitPending => promptPending || completing;
    public async Task<bool> Request()
    {
        if (promptPending || completing || session.BatchActive || session.ShuttingDown) return false;
        promptPending = true;
        try
        {
            if (!await confirm()) return false;
            await FinishConfirmed(); return true;
        }
        finally { promptPending = false; }
    }
    public async Task FinishConfirmed()
    {
        if (completing) return;
        completing = true;
        suspendProducers();
        if (!await session.PrepareExit(timeout)) await session.WhenIdle;
        finish();
    }
}
