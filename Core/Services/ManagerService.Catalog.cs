using System.Collections.Immutable;
using Bezoro.AhkManager.Core.Infrastructure;
using Bezoro.AhkManager.Core.Models;

namespace Bezoro.AhkManager.Core.Services;

public sealed partial class ManagerService
{
    public Task<CatalogResult> AddFilesAsync(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default
    ) => Add(paths, [], cancellationToken);

    async Task<CatalogResult> Add(
        IReadOnlyList<string> paths,
        ImmutableArray<Issue> initialIssues,
        CancellationToken ct
    )
    {
        try
        {
            return await Exclusive(
                    async () =>
                    {
                        var issues = initialIssues.ToBuilder();
                        var added = ImmutableArray.CreateBuilder<Guid>();
                        int duplicates = 0;
                        var scripts = document.Scripts.ToList();
                        var seen = scripts
                            .Select(s => s.Path)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                        foreach (string path in paths.Take(2001))
                        {
                            ct.ThrowIfCancellationRequested();
                            if (scripts.Count >= 2000)
                            {
                                issues.Add(new(ReasonCode.FolderLimitReached));
                                break;
                            }
                            try
                            {
                                string canonical = ScriptPaths.Canonical(path);
                                if (!seen.Add(canonical))
                                {
                                    duplicates++;
                                    continue;
                                }
                                var record = new ScriptRecord
                                {
                                    Path = canonical,
                                    FriendlyName = Path.GetFileNameWithoutExtension(canonical),
                                };
                                scripts.Add(record);
                                added.Add(record.Id);
                            }
                            catch (Exception ex)
                                when (SafeJsonFiles.Known(ex)
                                    || ex is System.ComponentModel.Win32Exception
                                )
                            {
                                issues.Add(new(ReasonCode.InvalidPath, path));
                            }
                        }
                        if (paths.Count > 2000)
                            issues.Add(new(ReasonCode.FolderLimitReached));
                        if (added.Count == 0)
                            return new CatalogResult(
                                new(issues.Count > 0 ? Outcome.Partial : Outcome.Confirmed),
                                [],
                                duplicates,
                                issues.ToImmutable()
                            );
                        var saved = await Commit(document with { Scripts = scripts }, null, ct)
                            .ConfigureAwait(false);
                        return new CatalogResult(
                            saved.Succeeded && issues.Count > 0
                                ? saved with
                                {
                                    Outcome = Outcome.Partial,
                                }
                                : saved,
                            saved.Succeeded ? added.ToImmutable() : [],
                            duplicates,
                            issues.ToImmutable()
                        );
                    },
                    ct
                )
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new(
                new(Outcome.CancelledBeforeDispatch, ReasonCode.Cancelled),
                [],
                0,
                initialIssues
            );
        }
        catch (Exception ex) when (SafeJsonFiles.Known(ex))
        {
            return new(
                new(Outcome.Failed, ReasonCode.StorageFailure, ex.Message),
                [],
                0,
                initialIssues
            );
        }
    }

    public async Task<CatalogResult> AddFolderAsync(
        string path,
        CancellationToken cancellationToken = default
    )
    {
        var scan = await Task.Run(
                () =>
                {
                    var files = new List<string>();
                    var issues = ImmutableArray.CreateBuilder<Issue>();
                    try
                    {
                        path = ScriptPaths.LocalPath(path);
                        SafeJsonFiles.SafePath(path);
                        var pending = new Stack<(string Path, int Depth)>();
                        pending.Push((path, 0));
                        int entries = 0;
                        while (pending.Count > 0 && files.Count < 2000 && entries < 20000)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var current = pending.Pop();
                            try
                            {
                                foreach (
                                    var item in Directory.EnumerateFileSystemEntries(current.Path)
                                )
                                {
                                    cancellationToken.ThrowIfCancellationRequested();
                                    if (++entries > 20000 || files.Count >= 2000)
                                        break;
                                    var attributes = File.GetAttributes(item);
                                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                                    {
                                        issues.Add(new(ReasonCode.InvalidPath, item));
                                        continue;
                                    }
                                    if ((attributes & FileAttributes.Directory) != 0)
                                    {
                                        if (current.Depth < 12)
                                            pending.Push((item, current.Depth + 1));
                                        else
                                            issues.Add(new(ReasonCode.FolderLimitReached, item));
                                    }
                                    else if (
                                        item.EndsWith(".ahk", StringComparison.OrdinalIgnoreCase)
                                    )
                                        files.Add(item);
                                }
                            }
                            catch (Exception ex) when (SafeJsonFiles.Known(ex))
                            {
                                issues.Add(new(ReasonCode.AccessDenied, current.Path));
                            }
                        }
                        if (pending.Count > 0 || entries >= 20000 || files.Count >= 2000)
                            issues.Add(new(ReasonCode.FolderLimitReached));
                    }
                    catch (Exception ex) when (SafeJsonFiles.Known(ex))
                    {
                        issues.Add(new(ReasonCode.InvalidPath, path));
                    }
                    return (Files: files, Issues: issues.ToImmutable());
                },
                cancellationToken
            )
            .ConfigureAwait(false);
        return await Add(scan.Files, scan.Issues, cancellationToken).ConfigureAwait(false);
    }

    public Task<OperationResult> RemoveAsync(
        Guid scriptId,
        CancellationToken cancellationToken = default
    ) =>
        ResultAction(
            async () =>
            {
                if (!document.Scripts.Any(s => s.Id == scriptId))
                    return new(Outcome.Failed, ReasonCode.InvalidMetadata);
                // Only catalog intent is removed; running processes and source files remain untouched.
                return await Commit(
                        document with
                        {
                            Scripts = document.Scripts.Where(s => s.Id != scriptId).ToList(),
                        },
                        scriptId,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            },
            cancellationToken
        );

    public Task<OperationResult> SaveMetadataAsync(
        Guid scriptId,
        ScriptMetadata metadata,
        bool startupConsent = false,
        CancellationToken cancellationToken = default
    ) =>
        ResultAction(
            async () =>
            {
                var old = document.Scripts.SingleOrDefault(s => s.Id == scriptId);
                if (old is null)
                    return new(Outcome.Failed, ReasonCode.InvalidMetadata);
                if (
                    startupConsent
                    && metadata.RunAtLogin == true
                    && (!owner.Held || !owner.Trusted)
                )
                    return new(Outcome.Failed, ReasonCode.EffectOwnerUnavailable);
                if (
                    metadata.RunAtLogin == true
                    && old.RunAtLogin != true
                    && !document.Settings.LaunchManagerAtLogin
                )
                {
                    if (!startupConsent)
                        return new(Outcome.Failed, ReasonCode.StartupConsentRequired);
                    if (!owner.Held || !owner.Trusted)
                        return new(Outcome.Failed, ReasonCode.EffectOwnerUnavailable);
                }
                var updated = old with
                {
                    FriendlyName = metadata.FriendlyName.Trim(),
                    Description = metadata.Description,
                    CollectionId = metadata.CollectionId,
                    RunAtLogin = metadata.RunAtLogin,
                    RunAsAdministrator = metadata.RunAsAdministrator,
                    LaunchBlocked = metadata.LaunchBlocked,
                    ManualHotkeys = metadata.ManualHotkeys,
                };
                var next = document with
                {
                    Scripts = document.Scripts.Select(s => s.Id == scriptId ? updated : s).ToList(),
                    Settings =
                        metadata.RunAtLogin == true && startupConsent
                            ? document.Settings with
                            {
                                LaunchManagerAtLogin = true,
                            }
                            : document.Settings,
                };
                var saved = await Commit(next, scriptId, cancellationToken).ConfigureAwait(false);
                if (saved.Succeeded && startupConsent && metadata.RunAtLogin == true)
                {
                    var registered =
                        document.Settings.LaunchManagerAsAdministrator && !processes.IsAdministrator
                            ? new OperationResult(
                                Outcome.Failed,
                                ReasonCode.ManualElevationRequired,
                                "The preference was saved. Reopen Manager as administrator to finish configuring Windows startup."
                            )
                            : await startup
                                .SetEnabledAsync(
                                    true,
                                    true,
                                    document.Settings.LaunchManagerAsAdministrator,
                                    CancellationToken.None
                                )
                                .ConfigureAwait(false);
                    await Publish(false, CancellationToken.None).ConfigureAwait(false);
                    if (!registered.Succeeded)
                        return registered with { Outcome = Outcome.Partial };
                }
                return saved;
            },
            cancellationToken
        );

    public Task<OperationResult> SaveCollectionAsync(
        Guid? collectionId,
        string name,
        CancellationToken cancellationToken = default
    ) =>
        ResultAction(
            async () =>
            {
                name = name.Trim();
                if (
                    name.Length is 0 or > 120
                    || document.Collections.Any(c =>
                        c.Id != collectionId
                        && c.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)
                    )
                    || collectionId.HasValue && !document.Collections.Any(c => c.Id == collectionId)
                )
                    return new(Outcome.Failed, ReasonCode.InvalidMetadata);
                var collections = document.Collections.ToList();
                if (collectionId.HasValue)
                    collections = collections
                        .Select(c => c.Id == collectionId ? c with { Name = name } : c)
                        .ToList();
                else
                    collections.Add(new(Guid.NewGuid(), name));
                return await Commit(
                        document with
                        {
                            Collections = collections,
                        },
                        null,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            },
            cancellationToken
        );

    public Task<OperationResult> RemoveCollectionAsync(
        Guid collectionId,
        CancellationToken cancellationToken = default
    ) =>
        ResultAction(
            () =>
                Commit(
                    document with
                    {
                        Collections = document
                            .Collections.Where(c => c.Id != collectionId)
                            .ToList(),
                        Scripts = document
                            .Scripts.Select(s =>
                                s.CollectionId == collectionId ? s with { CollectionId = null } : s
                            )
                            .ToList(),
                    },
                    null,
                    cancellationToken
                ),
            cancellationToken
        );

    public async Task<ImportPreview> PreviewImportAsync(
        string sourcePath,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            return (
                await importer.ReadAsync(sourcePath, cancellationToken).ConfigureAwait(false)
            ).Preview;
        }
        catch (Exception ex) when (SafeJsonFiles.Known(ex) || ex is InvalidOperationException)
        {
            return new(sourcePath, "", 0, [new(ReasonCode.ImportInvalid, ex.Message)], false);
        }
    }

    public Task<OperationResult> ImportAsync(
        ImportPreview preview,
        CancellationToken cancellationToken = default
    ) =>
        ResultAction(
            async () =>
            {
                if (!owner.Held || !owner.Trusted)
                    return new(Outcome.Failed, ReasonCode.EffectOwnerUnavailable);
                if (!preview.CanImport)
                    return new(Outcome.Failed, ReasonCode.ImportInvalid);
                var import = await importer
                    .ReadAsync(preview.SourcePath, cancellationToken)
                    .ConfigureAwait(false);
                if (import.Preview.SourceHash != preview.SourceHash)
                    return new(Outcome.Failed, ReasonCode.ImportSourceChanged);
                var backup = await importer
                    .PreserveSourceAsync(import, cancellationToken)
                    .ConfigureAwait(false);
                if (!backup.Succeeded)
                    return backup;
                var paths = document
                    .Scripts.Select(s => s.Path)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var ids = document.Scripts.Select(s => s.Id).ToHashSet();
                var scripts = document.Scripts.ToList();
                foreach (var script in import.Scripts)
                {
                    if (!paths.Add(script.Path))
                        continue;
                    var entry = script;
                    if (!ids.Add(entry.Id))
                    {
                        entry = entry with { Id = Guid.NewGuid() };
                        ids.Add(entry.Id);
                    }
                    scripts.Add(entry);
                }
                // Imported preferences do not change the Windows registration or Manager login preference.
                return await Commit(document with { Scripts = scripts }, null, cancellationToken)
                    .ConfigureAwait(false);
            },
            cancellationToken
        );
}
