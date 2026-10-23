using System.Collections.Immutable;
using System.Text;
using Bezoro.AhkManager.WinUI.Infrastructure;
using Bezoro.AhkManager.WinUI.Models;

namespace Bezoro.AhkManager.WinUI.Services;

public sealed record HotkeyAnalysis(ImmutableArray<HotkeySnapshot> Hotkeys, AnalysisCoverage Coverage, ImmutableArray<Issue> Issues);

public sealed class HotkeyAnalyzer
{
    const int MaximumFiles = 64, MaximumDepth = 8, MaximumBytes = 1024 * 1024;
    public Task<HotkeyAnalysis> AnalyzeAsync(string scriptPath, CancellationToken ct = default) => Task.Run(async () =>
    {
        var hotkeys = ImmutableArray.CreateBuilder<HotkeySnapshot>();
        var issues = ImmutableArray.CreateBuilder<Issue>();
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
        int total = 0; string? context = null;
        try
        {
            await Read(scriptPath, 0).ConfigureAwait(false);
            return new HotkeyAnalysis(hotkeys.ToImmutable(), AnalysisCoverage.Partial, issues.ToImmutable());
        }
        catch (Exception ex) when (SafeJsonFiles.Known(ex) || ex is System.ComponentModel.Win32Exception)
        { issues.Add(new(ReasonCode.PartialAnalysis, ex.Message)); return new HotkeyAnalysis(hotkeys.ToImmutable(), hotkeys.Count == 0 ? AnalysisCoverage.Unanalysable : AnalysisCoverage.Partial, issues.ToImmutable()); }

        async Task Read(string path, int depth)
        {
            ct.ThrowIfCancellationRequested();
            if (depth > MaximumDepth || visited.Count >= MaximumFiles) { issues.Add(new(ReasonCode.IncludeLimitReached, path)); return; }
            path = ScriptPaths.Canonical(path);
            if (!visited.Add(path)) { issues.Add(new(ReasonCode.IncludeLimitReached, path)); return; }
            var bytes = await SafeJsonFiles.ReadAsync(path, ct).ConfigureAwait(false) ?? throw new FileNotFoundException();
            total += bytes.Length;
            if (total > MaximumBytes) { issues.Add(new(ReasonCode.IncludeLimitReached, path)); return; }
            var text = AutoHotkeyDiscovery.Decode(bytes);
            bool blockComment = false, continuation = false;
            int number = 0;
            foreach (var raw in text.Split('\n'))
            {
                ct.ThrowIfCancellationRequested(); number++;
                var trimmed = raw.Trim();
                if (blockComment) { if (trimmed.StartsWith("*/", StringComparison.Ordinal)) blockComment = false; continue; }
                if (trimmed.StartsWith("/*", StringComparison.Ordinal)) { blockComment = !trimmed.Contains("*/", StringComparison.Ordinal); continue; }
                if (continuation) { if (trimmed.StartsWith(')')) continuation = false; continue; }
                if (trimmed.StartsWith('(') && !trimmed.Contains("::", StringComparison.Ordinal)) { continuation = true; issues.Add(new(ReasonCode.PartialAnalysis, path + ":" + number)); continue; }
                string code = StripStringsAndComments(raw).Trim();
                if (code.Length == 0) continue;
                if (code.StartsWith("#HotIf", StringComparison.OrdinalIgnoreCase)) { context = trimmed.Length > 6 ? trimmed[6..].Trim() : null; if (context?.Length == 0) context = null; continue; }
                if (code.StartsWith("#Include", StringComparison.OrdinalIgnoreCase))
                {
                    string include = trimmed[(trimmed.StartsWith("#IncludeAgain", StringComparison.OrdinalIgnoreCase) ? 13 : 8)..].Trim();
                    if (include.StartsWith("*i ", StringComparison.OrdinalIgnoreCase)) include = include[3..].Trim();
                    include = include.Trim('"', '\'');
                    include = include.Replace("%A_ScriptDir%", Path.GetDirectoryName(scriptPath)!, StringComparison.OrdinalIgnoreCase);
                    if (include.Length == 0 || include.Contains('%') || include.StartsWith('<') || include.Contains(';')) { issues.Add(new(ReasonCode.IncludeUnresolved, include)); continue; }
                    string included = Path.IsPathFullyQualified(include) ? include : Path.Combine(Path.GetDirectoryName(path)!, include);
                    try { await Read(included, depth + 1).ConfigureAwait(false); }
                    catch (Exception ex) when (SafeJsonFiles.Known(ex) || ex is System.ComponentModel.Win32Exception) { issues.Add(new(ReasonCode.IncludeUnresolved, included)); }
                    continue;
                }
                if (code.Contains("Hotkey(", StringComparison.OrdinalIgnoreCase) || code.Contains("Hotkey (", StringComparison.OrdinalIgnoreCase)) issues.Add(new(ReasonCode.DynamicHotkeys, path + ":" + number));
                int separator = code.IndexOf("::", StringComparison.Ordinal);
                if (separator <= 0) continue;
                string chord = code[..separator].Trim();
                if (!IsChord(chord)) { issues.Add(new(ReasonCode.PartialAnalysis, path + ":" + number)); continue; }
                string body = code[(separator + 2)..].Trim();
                bool remap = IsRemapDestination(body);
                hotkeys.Add(new(chord, Normalize(chord), HotkeyOrigin.Parsed, context, path, number, remap));
            }
        }
    }, ct);

    static string StripStringsAndComments(string raw)
    {
        StringBuilder text = new(); char quote = '\0'; bool escaped = false;
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (escaped) { text.Append(' '); escaped = false; continue; }
            if (c == '`') { escaped = true; text.Append(' '); continue; }
            if (quote != '\0') { if (c == quote) quote = '\0'; text.Append(' '); continue; }
            if (c is '"' or '\'') { quote = c; text.Append(' '); continue; }
            if (c == ';' && (i == 0 || char.IsWhiteSpace(raw[i - 1]))) break;
            text.Append(c);
        }
        return text.ToString();
    }
    static bool IsChord(string value)
    {
        if (value.Length is 0 or > 200 || value.Any(c => "(){}=\"'".Contains(c))) return false;
        var keys = value.Split('&', StringSplitOptions.TrimEntries);
        if (keys.Length > 2) return false;
        return keys.All(key =>
        {
            key = key.TrimStart('~', '*', '$', '^', '!', '+', '#', '<', '>');
            if (key.EndsWith(" up", StringComparison.OrdinalIgnoreCase)) key = key[..^3];
            return key.Length > 0 && !key.Any(char.IsWhiteSpace) && key.All(c => char.IsLetterOrDigit(c) || "`-=[]\\;',./".Contains(c));
        });
    }
    static bool IsRemapDestination(string body) => body.Length > 0 && body.Length <= 24 && !body.Equals("return", StringComparison.OrdinalIgnoreCase) && !body.Any(char.IsWhiteSpace) && (body.Length == 1 || new[] { "LButton", "RButton", "MButton", "CapsLock", "Escape", "Enter", "Tab", "Space", "Backspace", "LControl", "RControl", "LShift", "RShift", "LAlt", "RAlt" }.Contains(body, StringComparer.OrdinalIgnoreCase));
    public static string Normalize(string chord)
    {
        chord = chord.Trim();
        if (chord.Contains('+') && !chord.StartsWith('+'))
        {
            var parts = chord.Split('+', StringSplitOptions.TrimEntries);
            if (parts.Take(parts.Length - 1).All(p => p.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || p.Equals("Control", StringComparison.OrdinalIgnoreCase) || p.Equals("Alt", StringComparison.OrdinalIgnoreCase) || p.Equals("Shift", StringComparison.OrdinalIgnoreCase) || p.Equals("Win", StringComparison.OrdinalIgnoreCase)))
                chord = string.Concat(parts.Take(parts.Length - 1).Select(p => p.ToUpperInvariant() switch { "CTRL" or "CONTROL" => "^", "ALT" => "!", "SHIFT" => "+", _ => "#" })) + parts[^1];
        }
        List<string> modifiers = []; int index = 0;
        while (index < chord.Length && "~$*^!+#<>".Contains(chord[index]))
        {
            char current = chord[index++];
            if (current is '~' or '$') continue;
            if (current is '<' or '>' && index < chord.Length && "^!+#".Contains(chord[index])) modifiers.Add(current.ToString() + chord[index++]);
            else modifiers.Add(current.ToString());
        }
        return string.Concat(modifiers.Distinct().Order(StringComparer.Ordinal)) + chord[index..].Replace(" & ", "&", StringComparison.Ordinal).ToUpperInvariant();
    }
    public static ImmutableArray<ScriptSnapshot> MarkConflicts(ImmutableArray<ScriptSnapshot> scripts)
    {
        var overlaps = scripts.SelectMany(s => s.Hotkeys.Select(h => (s.Id, h.NormalizedChord)))
            .Where(h => h.NormalizedChord.Length > 0).GroupBy(h => h.NormalizedChord, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(h => h.Id).Distinct().Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return scripts.Select(s => s with { Hotkeys = s.Hotkeys.Select(h => h with { PossibleConflict = overlaps.Contains(h.NormalizedChord) }).ToImmutableArray() }).ToImmutableArray();
    }
}
