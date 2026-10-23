using System.Collections.Immutable;
using System.Security.Cryptography;
using Bezoro.AhkManager.WinUI.Infrastructure;

namespace Bezoro.AhkManager.WinUI.Tests.Integration;

// Only the test executable composes this source. Production always observes the
// complete global WMI population and keeps its conservative incomplete refusal.
internal sealed class FixtureProcessObservation : IProcessObservationSource
{
    readonly WmiProcessObservationSource windows = new();
    readonly string root;
    readonly Dictionary<string,string> files;
    readonly Dictionary<int,long> olderOpaque;
    readonly long freshRootCreated;
    public FixtureProcessObservation(string root,IEnumerable<string> scripts)
    {
        this.root=Path.GetFullPath(root);freshRootCreated=Directory.GetCreationTimeUtc(root).ToFileTimeUtc();
        files=scripts.ToDictionary(Path.GetFullPath,Hash,StringComparer.OrdinalIgnoreCase);
        if(files.Count==0||files.Keys.Any(p=>!p.StartsWith(this.root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||File.GetCreationTimeUtc(p).ToFileTimeUtc()<freshRootCreated))throw new InvalidOperationException("Fresh exclusive fixture root proof failed");
        var initial=windows.Read(CancellationToken.None);
        if(!initial.Complete)throw new InvalidOperationException("Initial WMI source is incomplete");
        olderOpaque=initial.Rows.Where(row=>row.Name.StartsWith("AutoHotkey",StringComparison.OrdinalIgnoreCase)&&(row.ExecutablePath is null||row.CommandLine is null)&&row.CreationFileTime is long created&&created<freshRootCreated)
            .ToDictionary(row=>row.Pid,row=>row.CreationFileTime!.Value);
        File.WriteAllLines(Path.Combine(root,"fixture-observation-scope.txt"),new[]{"root="+this.root,"rootCreationFileTime="+freshRootCreated}.Concat(olderOpaque.Select(pair=>"excludedOlderOpaquePid="+pair.Key+";creation="+pair.Value)).Concat(files.Select(pair=>"ownedFile="+pair.Key+";sha256="+pair.Value)));
    }
    public ObservedProcessCommands Read(CancellationToken ct)
    {
        if(Directory.GetCreationTimeUtc(root).ToFileTimeUtc()!=freshRootCreated||files.Any(pair=>!File.Exists(pair.Key)||Hash(pair.Key)!=pair.Value))throw new InvalidOperationException("Fixture scope identity changed");
        var raw=windows.Read(ct);
        return raw with {Rows=raw.Rows.Where(row=>!olderOpaque.TryGetValue(row.Pid,out long created)||row.CreationFileTime!=created||row.ExecutablePath is not null&&row.CommandLine is not null).ToImmutableArray()};
    }
    static string Hash(string path)=>Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
}
