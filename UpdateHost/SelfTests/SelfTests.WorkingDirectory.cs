namespace Bezoro.AhkManager.UpdateHost;

internal static partial class SelfTests
{
    static void CheckUpdateWorkingDirectory(string root, Action<bool, string> check)
    {
        string fixture = Path.Combine(root, "working directory promotion");
        Directory.CreateDirectory(fixture);
        string application = Path.Combine(fixture, "App");
        string previous = Path.Combine(fixture, ".app-previous");
        string operation = Path.Combine(fixture, "operation");
        Directory.CreateDirectory(application);
        SnapshotSecurity.CreatePrivateDirectory(operation);
        File.WriteAllText(Path.Combine(application, "payload.txt"), "original payload");
        string originalDirectory = Directory.GetCurrentDirectory();
        bool blocked = false;
        try
        {
            Directory.SetCurrentDirectory(application);
            try
            {
                Directory.Move(application, previous);
            }
            catch (IOException)
            {
                blocked = true;
            }
            check(
                blocked && Directory.Exists(application) && !Directory.Exists(previous),
                "Windows current-directory handle reproduces an isolated portable application's first-rename failure"
            );
            UpdateWorkingDirectory.Detach(operation);
            Directory.Move(application, previous);
            check(
                UpdatePaths.Equal(Directory.GetCurrentDirectory(), operation)
                    && !Directory.Exists(application)
                    && File.ReadAllText(Path.Combine(previous, "payload.txt"))
                        == "original payload",
                "trusted-operation working directory releases the payload namespace before native promotion"
            );
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
        }
    }
}
