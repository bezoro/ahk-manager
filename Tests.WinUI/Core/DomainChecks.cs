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
        await CheckRequirementsAsync(root);
        await CheckHeaderDescriptionsAsync(root);
        await File.WriteAllTextAsync(path, "#Requires AutoHotkey v2.0\n; ^x:: comment\ntext := \"^z:: fake\"\n^!a::MsgBox 'ok'\n#HotIf WinActive('Example')\nF2::return\n#Include missing.ahk\nHotkey('F3', (*) => 0)\na::b\n");
        var result = await new HotkeyAnalyzer().AnalyzeAsync(path);
        Check(result.Hotkeys.Any(h => h.NormalizedChord == HotkeyAnalyzer.Normalize("!^a")), "static declaration detected");
        Check(!result.Hotkeys.Any(h => h.Chord.Contains("^z") || h.Chord.Contains("^x")), "strings and comments do not create hotkeys");
        Check(result.Hotkeys.Any(h => h.Chord == "F2" && h.Context is not null), "HotIf context retained as opaque");
        Check(result.Hotkeys.Any(h => h.Chord == "a" && h.IsRemap), "remap distinct from ordinary hotkey");
        Check(result.Issues.Any(i => i.Code == ReasonCode.IncludeUnresolved) && result.Issues.Any(i => i.Code == ReasonCode.DynamicHotkeys), "unresolved include and dynamic hotkeys keep analysis partial");
        Console.WriteLine("FIXTURE " + root);
    }

    static async Task CheckHeaderDescriptionsAsync(string root)
    {
        foreach (var (body, expected) in new (string, string?)[]
        {
            ("; @description first\n#Requires AutoHotkey v2\n\t ; @description second  \n#SingleInstance Off\n; @description   \nMsgBox 'body'\n; @description hidden", "first\nsecond"),
            ("\uFEFF; ordinary\n\n; @description only", "only"),
            ("; @Description wrong\n;@description wrong\n;  @description wrong\n; @description\twrong", null),
            ("; @description \n; @description    ", null),
            ("/* ; @description hidden */\n/*\n; @description hidden\n*/\n; @description root", "root"),
            ("; @description first\n/* ignored */ MsgBox 'code'\n; @description hidden", "first"),
            ("/* ignored */ ; @description not-at-physical-start\n; @description root", "root"),
            ("#RequiresExtra v2\n; @description hidden", null),
            ("#SingleInstanceExtra Off\n; @description hidden", null),
            ("#Include include.ahk\n; @description hidden", null),
            ("text := '; @description string'\n; @description body", null),
            ("(\n; @description continuation\n)", null),
            ("; @description first\r\n; @description second\r#Requires v2", "first\nsecond")
        })
        {
            var parsed = HotkeyAnalyzer.ReadHeader(body);
            Check(parsed.Description == expected && !parsed.Partial, "header grammar root/physical-line boundary " + expected);
        }
        foreach (string body in new[] { "; @description a\0b", "; @description valid\n; ordinary\0comment", "; @description " + new string('x', 4001), "; @description valid\n" + new string('\n', 64), "; @description valid\n;" + new string('x', 8192) })
        {
            var parsed = HotkeyAnalyzer.ReadHeader(body);
            Check(parsed.Description is null && parsed.Partial, "invalid/over-limit header rejects whole derived value");
        }
        Check(HotkeyAnalyzer.ReadHeader("; @description " + new string('x', 4000)).Description?.Length == 4000, "header description exact4000 boundary");
        Check(HotkeyAnalyzer.ReadHeader("; @description exact\n" + new string('\n', 63)).Description == "exact", "header exact64physical lines boundary");
        string path = System.IO.Path.Combine(root, "description.ahk");
        foreach (var encoding in new System.Text.Encoding[] { new System.Text.UTF8Encoding(false, true), new System.Text.UTF8Encoding(true, true), new System.Text.UnicodeEncoding(false, true, true), new System.Text.UnicodeEncoding(true, true, true) })
        {
            string body = "; @description Texto Unicode 漢字\n#Requires AutoHotkey v2\n^j::return\n";
            await File.WriteAllBytesAsync(path, encoding.GetPreamble().Concat(encoding.GetBytes(body)).ToArray());
            var result = await new HotkeyAnalyzer().AnalyzeAsync(path);
            Check(result.HeaderDescription == "Texto Unicode 漢字" && result.Hotkeys.Any(h => h.Chord == "^j"), "header decode reuses UTF8/BOM/UTF16 and preserves hotkeys " + encoding.WebName);
        }
        string include = System.IO.Path.Combine(root, "header-include.ahk");
        await File.WriteAllBytesAsync(include, [0xc3]);
        await File.WriteAllTextAsync(path, "; @description root survives\n#Include header-include.ahk\n; @description hidden");
        var included = await new HotkeyAnalyzer().AnalyzeAsync(path);
        Check(included.HeaderDescription == "root survives" && included.Issues.Any(i => i.Code == ReasonCode.IncludeUnresolved), "invalid include cannot erase valid root header");
        await File.WriteAllTextAsync(include, "; @description never root");
        await File.WriteAllTextAsync(path, "#Include header-include.ahk");
        Check((await new HotkeyAnalyzer().AnalyzeAsync(path)).HeaderDescription is null, "included description never becomes root header");
        await File.WriteAllBytesAsync(path, [0xc3]);
        Check((await new HotkeyAnalyzer().AnalyzeAsync(path)).HeaderDescription is null, "invalid decoding leaves absent header");
        Check((await new HotkeyAnalyzer().AnalyzeAsync(path + ".missing.ahk")).HeaderDescription is null, "read failure leaves absent header");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); bool propagated = false;
        try { await new HotkeyAnalyzer().AnalyzeAsync(path, cancelled.Token); } catch (OperationCanceledException) { propagated = true; }
        Check(propagated, "header analysis cancellation propagates");
        var id = Guid.NewGuid(); var state = ManagerSnapshot.Empty with { Scripts = [Row(id, "header", ScriptStatus.Stopped) with { Description = "manual hidden", HeaderDescription = "automatic match" }] };
        Check(ScriptViews.Project(state, new(Search: "automatic match"), id).EffectiveSelectedId == id && ScriptViews.Project(state, new(Search: "manual hidden"), id).VisibleIds.Length == 0, "backend search uses effective header and selection");
        state = state with { Scripts = [state.Scripts[0] with { HeaderDescription = null }] };
        Check(ScriptViews.Project(state, new(Search: "manual hidden"), id).EffectiveSelectedId == id, "backend search restores manual fallback");
    }

    static async Task CheckRequirementsAsync(string root)
    {
        var script = System.IO.Path.Combine(root, "requires.ahk");
        var executable = System.IO.Path.Combine(root, "metadata.exe");
        // A minimal header fixture is data only; no process or loader is involved.
        byte[] pe = new byte[512];
        BitConverter.GetBytes((ushort)0x5a4d).CopyTo(pe, 0);
        BitConverter.GetBytes(128).CopyTo(pe, 0x3c);
        BitConverter.GetBytes(0x00004550).CopyTo(pe, 128);
        BitConverter.GetBytes((ushort)0x8664).CopyTo(pe, 132);
        BitConverter.GetBytes((ushort)240).CopyTo(pe, 148);
        BitConverter.GetBytes((ushort)0x20b).CopyTo(pe, 152);
        await File.WriteAllBytesAsync(executable, pe);
        var stable = new InterpreterInfo(executable, "2.0.30.0", "fixture");
        async Task Requirement(string body, bool expected, InterpreterInfo? interpreter = null)
        {
            await File.WriteAllTextAsync(script, body);
            bool accepted = true;
            try { await AutoHotkeyDiscovery.ValidateScriptVersionAsync(script, interpreter ?? stable, CancellationToken.None); }
            catch (Exception ex) when (ex is InvalidDataException or IOException) { accepted = false; }
            Check(accepted == expected, "requires " + body.Replace('\n', '|') + " => " + expected);
        }
        await Requirement("#Requires AutoHotkey v2.0-rc-1", true);
        var hyphenated = stable with { ProductVersion = "2.0-rc-1" };
        await Requirement("#Requires AutoHotkey =2.0-rc-1", true, hyphenated);
        await Requirement("#Requires AutoHotkey <2.0", true, hyphenated);
        await Requirement("#Requires AutoHotkey >=2.0", false, hyphenated);
        foreach (string requirement in new[] { "v2.0", ">=2.0", "v2.0-rc.2", "64-bit", "v2.0 64-bit", ">=1.1 <3", "=2.0.30", "<=2.0.30.0", ">2.0.29", "v2.0.30+build.4", "v02.00.030", "v2.0 ; trailing comment", "", ">=2.0 <2.1 64-bit" })
            await Requirement("#Requires AutoHotkey " + requirement, true);
        foreach (string requirement in new[] { "v2.1", "v1.1", "32-bit", "<2.0.30", "<=2.0.29", ">2.0.30", "=2.0.31", "=>2", "v2..0", "v2.0-rc..2", "v2.0;bad", ">=", "64-bit garbage", "64-bit 32-bit" })
            await Requirement("#Requires AutoHotkey " + requirement, false);
        await Requirement("#Requires AutoHotkey >=2\n#Requires AutoHotkey <2", false);
        await Requirement("#RequiresSomething Other\n/*\n#Requires AutoHotkey v1\n*/\n(\n#Requires AutoHotkey v1\n)\n#Requires AutoHotkey v2", true);
        await Requirement("#Requires Other v2", false);
        await Requirement("#Requires", false);
        var prerelease = stable with { ProductVersion = "2.0-rc.2" };
        await Requirement("#Requires AutoHotkey >=2.0- <2", true, prerelease);
        await Requirement("#Requires AutoHotkey v2.0", false, prerelease);
        await Requirement("#Requires AutoHotkey =2.0.0-rc.2.0+ignored", true, prerelease);
        await Requirement("#Requires AutoHotkey >2-rc.1 <2-rc.3", true, prerelease);
        await Requirement("#Requires AutoHotkey >2-rc.a112 <2-rc.a20", true, stable with { ProductVersion = "2-rc.a15" });
        await Requirement("#Requires AutoHotkey >2-rc.9", true, stable with { ProductVersion = "2-rc.a" });
        await Requirement("#Requires AutoHotkey >=2- <2", true, stable with { ProductVersion = "2-1" });
        await Requirement("#Requires AutoHotkey >2-rc.A", true, stable with { ProductVersion = "2-rc.a" });
        await Requirement("#Requires AutoHotkey v2", false, stable with { ProductVersion = "invalid" });
        await Requirement("#Requires AutoHotkey 64-bit", false, stable with { Path = System.IO.Path.Combine(root, "absent.exe") });
        await File.WriteAllBytesAsync(executable, new byte[64]);
        await Requirement("#Requires AutoHotkey 64-bit", false);
        BitConverter.GetBytes((ushort)0x14c).CopyTo(pe, 132);
        BitConverter.GetBytes((ushort)0x10b).CopyTo(pe, 152);
        await File.WriteAllBytesAsync(executable, pe);
        await Requirement("#Requires AutoHotkey 32-bit", true);
        await Requirement("#Requires AutoHotkey 64-bit", false);
        BitConverter.GetBytes(int.MaxValue).CopyTo(pe, 0x3c);
        await File.WriteAllBytesAsync(executable, pe);
        await Requirement("#Requires AutoHotkey 32-bit", false);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        bool stopped = false;
        try { await AutoHotkeyDiscovery.ValidateScriptVersionAsync(script, stable, cancelled.Token); } catch (OperationCanceledException) { stopped = true; }
        Check(stopped, "requires preserves cancellation");
    }
}
