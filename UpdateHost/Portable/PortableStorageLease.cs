namespace Bezoro.AhkManager.UpdateHost;

// Keep the active portable data namespace stable for its Manager lifetime.
// Paths come only from the authenticated native parent and its fixed marker.
internal static class PortableStorageLease
{
    internal static DirectoryLease Acquire(PortableLayout layout)
    {
        var retained = new DirectoryLease([layout.Root, layout.Data]);
        try
        {
            ProfileOwnership.Verify(layout.Root);
            ProfileOwnership.Verify(layout.Application);
            ProfileOwnership.Verify(layout.Data);
            if (PortableLayout.Read(layout.Root) != layout)
                throw new InvalidDataException("The active portable storage settings changed.");
            return retained;
        }
        catch
        {
            retained.Dispose();
            throw;
        }
    }

    internal static async Task<int> Run()
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                throw new InvalidOperationException("Portable storage retention requires Windows.");
            var layout =
                PortableLayout.FromParent()
                ?? throw new InvalidDataException("The parent is not a portable Manager.");
            PortableInitialization.VerifySourceHelper(layout);
            using var retained = Acquire(layout);
            Console.WriteLine("{\"ready\":true}");
            await Console.Out.FlushAsync().ConfigureAwait(false);
            // The sole command is closing stdin. No path, mutation, or arbitrary
            // request can be dispatched through a storage retention process.
            return await Console.In.ReadAsync(new char[1]).ConfigureAwait(false) == 0 ? 0 : 65;
        }
        catch (Exception error) when (UpdateProtocol.Known(error))
        {
            return 65;
        }
    }
}
