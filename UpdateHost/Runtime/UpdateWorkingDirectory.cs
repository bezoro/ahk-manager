namespace Bezoro.AhkManager.UpdateHost;

internal static class UpdateWorkingDirectory
{
    // Windows retains a directory handle for the process current directory.
    // A helper launched by a reopened Manager may otherwise pin App itself and
    // prevent its own first promotion rename, even after the Manager exits.
    internal static void Detach(string trustedOperationDirectory)
    {
        string directory = UpdatePaths.Local(trustedOperationDirectory);
        UpdatePaths.NoRedirects(directory);
        ProfileOwnership.Verify(directory);
        Directory.SetCurrentDirectory(directory);
    }
}
