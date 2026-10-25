using System.Collections.Immutable;
using Bezoro.AhkManager.WinUI.Models;

namespace Bezoro.AhkManager.WinUI.Services;

public static class ScriptViews
{
    public static ViewProjection Project(ManagerSnapshot state, ScriptQuery query, Guid? selectedId)
    {
        IEnumerable<ScriptSnapshot> scripts = state.Scripts;
        if (query.CollectionId.HasValue) scripts = scripts.Where(s => s.CollectionId == query.CollectionId);
        string term = query.Search.Trim();
        if (term.Length > 0) scripts = scripts.Where(s => (s.FriendlyName + " " + s.Path + " " + (string.IsNullOrWhiteSpace(s.HeaderDescription) ? s.Description : s.HeaderDescription)).Contains(term, StringComparison.CurrentCultureIgnoreCase));
        scripts = query.Status switch
        {
            StatusFilter.Running => scripts.Where(s => s.Status == ScriptStatus.Running),
            StatusFilter.Stopped => scripts.Where(s => s.Status == ScriptStatus.Stopped),
            StatusFilter.Problems => scripts.Where(s => s.Issues.Length > 0 || s.Status is ScriptStatus.Unknown or ScriptStatus.Multiple or ScriptStatus.Blocked or ScriptStatus.Missing),
            _ => scripts
        };
        var collectionNames = state.Collections.ToDictionary(c => c.Id, c => c.Name);
        scripts = query.Sort switch
        {
            ScriptSort.Status => scripts.OrderBy(s => s.Status).ThenBy(s => s.FriendlyName, StringComparer.CurrentCultureIgnoreCase),
            ScriptSort.Collection => scripts.OrderBy(s => s.CollectionId is Guid id ? collectionNames.GetValueOrDefault(id, "") : "", StringComparer.CurrentCultureIgnoreCase).ThenBy(s => s.FriendlyName, StringComparer.CurrentCultureIgnoreCase),
            _ => scripts.OrderBy(s => s.FriendlyName, StringComparer.CurrentCultureIgnoreCase)
        };
        var visible = scripts.ToImmutableArray();
        Guid? selected = selectedId.HasValue && visible.Any(s => s.Id == selectedId) ? selectedId : null;
        return new(visible.Select(s => s.Id).ToImmutableArray(), selected, state.Revision, query.Version);
    }
}
