using System.Collections.Immutable;
using Bezoro.AhkManager.WinUI.Models;
using Bezoro.AhkManager.WinUI.Services;

namespace Bezoro.AhkManager.WinUI.Tests.Core;

public static partial class CoreSuite
{
    static ScriptSnapshot Row(Guid id, string name, ScriptStatus status, Guid? collection = null) =>
        new(id, "C:\\fixtures\\" + name + ".ahk", name, "", collection, false, false, false,
            status, null, [], AnalysisCoverage.Partial, []);

    static async Task RunDomainAsync()
    {
        var selected = Guid.NewGuid();
        var other = Guid.NewGuid();
        var collection = Guid.NewGuid();
        var state = ManagerSnapshot.Empty with { Revision = 12, Scripts = [Row(selected, "Zulu", ScriptStatus.Running), Row(other, "Alpha", ScriptStatus.Stopped, collection)] };
        var projection = ScriptViews.Project(state, new(Version: 3), selected);
        Check(projection.VisibleIds.SequenceEqual(new[] { other, selected }) && projection.EffectiveSelectedId == selected, "sort preserves visible identity");
        Check(ScriptViews.Project(state, new(Search: "Alpha"), selected).EffectiveSelectedId is null, "hidden selection loses authority immediately");
        Check(ScriptViews.Project(state, new(Status: StatusFilter.Running), other).VisibleIds.SequenceEqual(new[] { selected }), "status query only includes observed running");
        Check(ScriptViews.Project(state, new(CollectionId: collection), selected).EffectiveSelectedId is null, "collection change hides selected identity");
        var unknown = state with { Scripts = [Row(selected, "Zulu", ScriptStatus.Unknown)] };
        Check(ScriptViews.Project(unknown, new(), selected).EffectiveSelectedId is null, "unknown transition invalidates selected action identity");
        Check(HotkeyAnalyzer.Normalize("^!a") == HotkeyAnalyzer.Normalize("!^A"), "common modifiers normalize equivalent chords");
        Check(HotkeyAnalyzer.Normalize("<^a") != HotkeyAnalyzer.Normalize(">^a"), "left and right modifiers retain distinction");
        var root = System.IO.Path.Combine(FixtureRoot, "hotkeys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = System.IO.Path.Combine(root, "main.ahk");
        await File.WriteAllTextAsync(path, "#Requires AutoHotkey v2.0\n; ^x:: comment\ntext := \"^z:: fake\"\n^!a::MsgBox 'ok'\n#HotIf WinActive('Example')\nF2::return\n#Include missing.ahk\nHotkey('F3', (*) => 0)\na::b\n");
        var result = await new HotkeyAnalyzer().AnalyzeAsync(path);
        Check(result.Hotkeys.Any(h => h.NormalizedChord == HotkeyAnalyzer.Normalize("!^a")), "static declaration detected");
        Check(!result.Hotkeys.Any(h => h.Chord.Contains("^z") || h.Chord.Contains("^x")), "strings and comments do not create hotkeys");
        Check(result.Hotkeys.Any(h => h.Chord == "F2" && h.Context is not null), "HotIf context retained as opaque");
        Check(result.Hotkeys.Any(h => h.Chord == "a" && h.IsRemap), "remap distinct from ordinary hotkey");
        Check(result.Issues.Any(i => i.Code == ReasonCode.IncludeUnresolved) && result.Issues.Any(i => i.Code == ReasonCode.DynamicHotkeys), "unresolved include and dynamic hotkeys keep analysis partial");
        Console.WriteLine("FIXTURE " + root);
    }
}
