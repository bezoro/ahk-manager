namespace Bezoro.AhkManager.UpdateHost;

internal sealed class PortableDataInitializationException(Exception failure)
    : IOException("Portable data initialization failed.", failure)
{
    internal Exception Failure { get; } = failure;
}
