namespace Bezoro.AhkManager.AgentCli;

internal static class AgentCatalog
{
    // The resource preserves JSON escapes without passing them through MSBuild item paths.
    internal static string Json { get; } = Read();

    static string Read()
    {
        using var stream =
            typeof(AgentCatalog).Assembly.GetManifestResourceStream(
                "Bezoro.AhkManager.AgentCli.CommandCatalog.json"
            ) ?? throw new InvalidDataException("The command catalog is missing.");
        using var reader = new StreamReader(stream, AgentProtocol.Utf8);
        return reader.ReadToEnd();
    }
}
