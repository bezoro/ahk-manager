using System.Collections.Immutable;
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Windowing;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Bezoro.AhkManager.WinUI.Models;
using Bezoro.AhkManager.WinUI.Resources;
using Bezoro.AhkManager.WinUI.ViewModels;
using Bezoro.AhkManager.WinUI.Infrastructure;

namespace Bezoro.AhkManager.WinUI.Views;

public sealed partial class MainWindow : Window
{
    readonly IManagerService service;
    readonly WinUiInteraction interaction;
    readonly TaskCompletionSource readySignal=new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly CancellationTokenSource windowLifetime=new();
    public ManagerViewModel ViewModel { get; }
    bool ready, painting, closed;
    CancellationTokenSource? effectCancellation;
    Guid? displayedDraft;
    Guid? collectionFilter;
    string page = "scripts";
    string collectionSignature = "";
    string draftCollectionSignature = "";
    public event EventHandler? ExitRequested;
    public FrameworkElement VisualRoot => RootGrid;
    internal Task WhenReady=>readySignal.Task;
    public MainWindow(IManagerService service)
    {
        this.service = service; ViewModel = new(service);
        InitializeComponent();
        interaction = new(this, RootGrid);
        Title = Text.Get("ApplicationTitle");
        var productIcon = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(productIcon)) AppWindow.SetIcon(productIcon);
        NativeTitleBar.IconSource = new BitmapIconSource { UriSource = new Uri("ms-appx:///Assets/AppIcon-32.png"), ShowAsMonochrome = false };
        ExtendsContentIntoTitleBar = true; SetTitleBar(NativeTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        RootGrid.ActualThemeChanged += RootThemeChanged;
        AppWindow.Resize(new SizeInt32(1240, 820));
        ViewModel.PresentationChanged += PresentationChanged;
        ViewModel.Error += (_, error) => ShowMessage(Text.Get("UnexpectedError"), error.Message, InfoBarSeverity.Error);
        service.Changed += ServiceChanged;
        Activated += WindowActivated;
        Closed += (_, _) => { closed = true; readySignal.TrySetCanceled();windowLifetime.Cancel();windowLifetime.Dispose();effectCancellation?.Cancel();RootGrid.ActualThemeChanged-=RootThemeChanged; service.Changed -= ServiceChanged; ViewModel.PresentationChanged -= PresentationChanged; };
        ScriptList.RightTapped += ScriptRightTapped;
        RootGrid.KeyboardAccelerators.Add(new() { Key = Windows.System.VirtualKey.F, Modifiers = Windows.System.VirtualKeyModifiers.Control });
        RootGrid.KeyboardAccelerators[^1].Invoked += (_, args) => { SearchBox.Focus(FocusState.Keyboard); args.Handled = true; };
        RootGrid.KeyboardAccelerators.Add(new() { Key = Windows.System.VirtualKey.F5 });
        RootGrid.KeyboardAccelerators[^1].Invoked += (_, args) => { ViewModel.RefreshCommand.Execute(null); args.Handled = true; };
    }
    async void RootLoaded(object sender, RoutedEventArgs args)
    {
        if (ready) return; ready = true;
        ViewModel.Apply(service.Snapshot);
        if (Navigation.SettingsItem is NavigationViewItem settingsItem) settingsItem.Content = Text.Get("Settings");
        await RunAsync(async () => { await service.LoadAsync(); ViewModel.Apply(service.Snapshot); });
        readySignal.TrySetResult();
    }
    void ServiceChanged(object? sender, ManagerSnapshot snapshot)
    {
        if (!closed) DispatcherQueue.TryEnqueue(() => { if (!closed) { CaptureDraft(); ViewModel.Apply(snapshot); } });
    }
    async void WindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (ready && args.WindowActivationState != WindowActivationState.Deactivated && !ViewModel.Busy)
            await RunAsync(() => service.RefreshAsync());
    }
    void PresentationChanged(object? sender, EventArgs args)
    {
        if (!ready || painting) return;
        painting = true;
        try
        {
            var state = ViewModel.Snapshot;
            CounterSubtitle.Text = string.Format(Text.Get("ScriptsCounts"), state.Scripts.Length, state.RunningCount);
            RefreshAction.IsEnabled = !ViewModel.Busy;
            AddAction.IsEnabled = !ViewModel.Busy && !state.ReadOnly;
            ScriptList.IsEnabled = !ViewModel.Busy;
            ScriptList.SelectedItem = ViewModel.Rows.FirstOrDefault(row => row.Id == ViewModel.Selected?.Id);
            EmptyState.Visibility = ViewModel.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Footer.Text = string.Format(Text.Get(state.ObservedAt.HasValue?"FooterFormat":"FooterNotObservedFormat"), state.Scripts.Length, state.RunningCount, state.ProblemCount,
                state.ObservedAt?.ToLocalTime().ToString("T") ?? Text.Get("NotObserved"));
            ApplyTheme(state.Settings.Theme);
            var signature = string.Join("|", state.Collections.Select(c => c.Id + ":" + c.Name));
            if (signature != collectionSignature)
            {
                collectionSignature = signature;
                foreach (var item in Navigation.MenuItems.OfType<NavigationViewItem>().Where(n => n.Tag is Guid).ToArray()) Navigation.MenuItems.Remove(item);
                foreach (var collection in state.Collections) Navigation.MenuItems.Add(new NavigationViewItem { Content = collection.Name, Tag = collection.Id, Icon = new SymbolIcon(Symbol.Folder) });
            }
            PaintInspector();
            if (page != "scripts") PaintOther();
            var issue = state.Issues.FirstOrDefault();
            if (issue is not null) ShowMessage(Text.Reason(issue.Code), issue.Detail, InfoBarSeverity.Warning);
        }
        finally { painting = false; }
    }
    public void ApplyTheme(AppThemePreference theme)
    {
        RootGrid.RequestedTheme = theme switch { AppThemePreference.Light => ElementTheme.Light, AppThemePreference.Dark => ElementTheme.Dark, _ => ElementTheme.Default };
        ApplyCaptionTheme();
    }
    void RootThemeChanged(FrameworkElement sender,object args)=>ApplyCaptionTheme();
    void ApplyCaptionTheme() {if(!closed&&AppWindow is {} native)native.TitleBar.PreferredTheme = RootGrid.ActualTheme == ElementTheme.Dark ? TitleBarTheme.Dark : TitleBarTheme.Light;}
    void PaintInspector()
    {
        var script = ViewModel.Selected;
        bool selected = script is not null;
        MetadataExpander.IsEnabled = selected;
        foreach (var button in new[] { StartAction, StopAction, RestartAction, PauseAction, SuspendAction, EditAction, FolderAction, RemoveAction, RecoverAction, SaveDraftAction }) button.IsEnabled = selected && !ViewModel.Busy;
        foreach (var control in new Control[] { NameDraft, DescriptionDraft, HotkeysDraft, CollectionDraft, AdminDraft, BlockedDraft }) control.IsEnabled = selected && !ViewModel.Busy && !ViewModel.Snapshot.ReadOnly;
        SelectedTitle.Text = script?.FriendlyName ?? Text.Get("SelectScript");
        SelectedPath.Text = script?.Path ?? "";
        SelectedState.Text = script is null ? "" : Text.Status(script.Status) + (script.LaunchBlocked ? " · " + Text.Get("BlockedMetadata") : "");
        SelectedProcess.Text = script?.Process is {} process
            ? ProcessDescription(process)
            : Text.Get("ProcessUnconfirmed");
        ExecutionHeading.Visibility = SelectedProcess.Visibility = script?.Process is not null ? Visibility.Visible : Visibility.Collapsed;
        SelectedIssues.Text = script is null ? "" : string.Join(Environment.NewLine, script.Issues.Select(issue => Text.Reason(issue.Code) + (issue.Detail is {} detail ? Environment.NewLine + Text.Get("DiagnosticDetails") + ": " + detail : "")));
        SelectedIssues.Visibility = string.IsNullOrEmpty(SelectedIssues.Text) ? Visibility.Collapsed : Visibility.Visible;
        foreach(var (button,operation) in new[]{(StartAction,ScriptOperation.Start),(StopAction,ScriptOperation.Stop),(RestartAction,ScriptOperation.Restart),(PauseAction,ScriptOperation.TogglePause),(SuspendAction,ScriptOperation.ToggleSuspend),(EditAction,ScriptOperation.Edit),(FolderAction,ScriptOperation.OpenFolder),(RecoverAction,ScriptOperation.Recover)})
        {
            var reason=ActionReason(operation);button.IsEnabled=reason==ReasonCode.None;
            string help=reason==ReasonCode.None?Text.Operation(operation):Text.Reason(reason);ToolTipService.SetToolTip(button,help);AutomationProperties.SetHelpText(button,help);
        }
        SaveDraftAction.IsEnabled &= !ViewModel.Snapshot.ReadOnly;
        RemoveAction.IsEnabled &= !ViewModel.Snapshot.ReadOnly;
        CapabilityNote.Visibility = script?.Status == ScriptStatus.Running && script.Process is {} observed && (observed.Paused is null || observed.Suspended is null) ? Visibility.Visible : Visibility.Collapsed;
        SelectedHotkeys.Text = script is null ? "" : Text.Get("HotkeysPartial") + Environment.NewLine + string.Join(Environment.NewLine, script.Hotkeys.Select(h => h.Chord + " · " + Text.Get("HotkeyOrigin_" + h.Origin) + (h.PossibleConflict ? " · " + Text.Get("PossibleConflict") : "")));
        SelectedActivity.Text = script is null ? "" : string.Join(Environment.NewLine, ViewModel.Snapshot.Activity.Where(item => item.ScriptId == script.Id).OrderByDescending(item => item.Timestamp).Take(5).Select(item => item.Timestamp.ToLocalTime().ToString("t") + " · " + Text.Outcome(item.Outcome) + " · " + Text.Reason(item.Code)));
        if (string.IsNullOrEmpty(SelectedActivity.Text)) SelectedActivity.Text = Text.Get("NoRelatedActivity");
        bool draftChanged=displayedDraft != script?.Id;
        if (draftChanged)
        {
            displayedDraft = script?.Id;
            var draft = ViewModel.GetDraft();
            NameDraft.Text = draft?.Name ?? ""; DescriptionDraft.Text = draft?.Description ?? ""; HotkeysDraft.Text = draft?.Hotkeys ?? "";
            AdminDraft.IsChecked = draft?.Admin ?? false; BlockedDraft.IsChecked = draft?.Blocked ?? false;
        }
        if(draftChanged||draftCollectionSignature!=collectionSignature)
        {
            draftCollectionSignature=collectionSignature;var draft=ViewModel.GetDraft();
            if(draft?.CollectionId is {} member&&!ViewModel.Snapshot.Collections.Any(c=>c.Id==member))draft.CollectionId=null;
            CollectionDraft.ItemsSource=new[]{new CollectionChoice(null,Text.Get("NoCollection"))}.Concat(ViewModel.Snapshot.Collections.Select(c=>new CollectionChoice(c.Id,c.Name))).ToArray();
            CollectionDraft.SelectedItem=((IEnumerable<CollectionChoice>)CollectionDraft.ItemsSource).FirstOrDefault(c=>c.Id==draft?.CollectionId);
        }
    }
    string ProcessDescription(ProcessSnapshot process)
    {
        var reference=ViewModel.Snapshot.ObservedAt??DateTimeOffset.UtcNow;var elapsed=reference-process.StartedAt;if(elapsed<TimeSpan.Zero)elapsed=TimeSpan.Zero;
        return string.Format(Text.Get("ProcessFormat"),process.Identity.Pid,process.InterpreterVersion,process.StartedAt.ToLocalTime().ToString("g"),Text.Get("Origin_"+process.Origin),elapsed.ToString("c"),reference.ToLocalTime().ToString("T"));
    }
    ReasonCode ActionReason(ScriptOperation operation)
    {
        var script=ViewModel.Selected;if(script is null)return ReasonCode.StaleSelection;if(ViewModel.Busy)return ReasonCode.PendingOperation;
        if(operation==ScriptOperation.Recover)return ReasonCode.None;
        if(operation is ScriptOperation.Edit or ScriptOperation.OpenFolder)return script.Status==ScriptStatus.Missing?ReasonCode.MissingFile:ReasonCode.None;
        if(ViewModel.Snapshot.ReadOnly)return ReasonCode.ReadOnlySettings;
        if(!ViewModel.Snapshot.EffectsAvailable)return ReasonCode.EffectOwnerUnavailable;
        if(ViewModel.EffectiveSelectedId!=script.Id)return ReasonCode.ObservationIncomplete;
        if((operation is ScriptOperation.Start or ScriptOperation.Restart)&&script.LaunchBlocked)return ReasonCode.LegacyGuard;
        if(operation==ScriptOperation.Start)return script.Status==ScriptStatus.Stopped?ReasonCode.None:script.Status==ScriptStatus.Multiple?ReasonCode.MultipleInstances:ReasonCode.AlreadyRunning;
        if(operation==ScriptOperation.Restart&&script.RunAsAdministrator)return ReasonCode.ManualElevationRequired;
        if(script.Status!=ScriptStatus.Running)return ReasonCode.IdentityChanged;
        if(script.Process?.WindowVerified!=true)return ReasonCode.WindowUnverified;
        if(operation==ScriptOperation.ToggleSuspend&&script.Process.Suspended is null)return ReasonCode.StateNotObservable;
        return ReasonCode.None;
    }
    void ScriptSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!ready || painting) return;
        CaptureDraft();
        if (ViewModel.Busy)
        {
            painting = true;
            try { ScriptList.SelectedItem = ViewModel.Rows.FirstOrDefault(row => row.Id == ViewModel.Selected?.Id); }
            finally { painting = false; }
            return;
        }
        if ((ScriptList.SelectedItem as ScriptRow)?.Id != ViewModel.SelectedId) effectCancellation?.Cancel();
        ViewModel.Select((ScriptList.SelectedItem as ScriptRow)?.Id);
    }
    void SearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) { if (ready && args.Reason == AutoSuggestionBoxTextChangeReason.UserInput) UpdateQuery(); }
    void QueryChanged(object sender, SelectionChangedEventArgs args) { if (ready) UpdateQuery(); }
    void UpdateQuery()
    {
        CaptureDraft();
        effectCancellation?.Cancel();
        ViewModel.ChangeQuery(SearchBox.Text, (StatusFilter)Math.Max(0, StatusBox.SelectedIndex), (ScriptSort)Math.Max(0, SortBox.SelectedIndex), collectionFilter);
    }
    void DraftChanged(object sender, TextChangedEventArgs args) => CaptureDraft();
    void DraftChecked(object sender, RoutedEventArgs args) => CaptureDraft();
    void DraftCollectionChanged(object sender, SelectionChangedEventArgs args) => CaptureDraft();
    void CaptureDraft()
    {
        if (!ready || painting || ViewModel.GetDraft() is not {} draft) return;
        draft.Name = NameDraft.Text; draft.Description = DescriptionDraft.Text; draft.Hotkeys = HotkeysDraft.Text;
        draft.Admin = AdminDraft.IsChecked == true; draft.Blocked = BlockedDraft.IsChecked == true;
        draft.CollectionId = (CollectionDraft.SelectedItem as CollectionChoice)?.Id;
    }
    async void SaveDraftClicked(object sender, RoutedEventArgs args)
    {
        CaptureDraft(); if (ViewModel.Selected?.Id is not Guid id || ViewModel.GetDraft() is not {} draft) return;
        await RunAsync(async () => ShowResult(await service.SaveMetadataAsync(id, draft.ToMetadata())));
    }
    async void StartupToggled(object sender, RoutedEventArgs args)
    {
        if (!ready || painting || ViewModel.Busy || sender is not ToggleSwitch toggle || toggle.Tag is not Guid id) return;
        var script = ViewModel.Snapshot.Scripts.FirstOrDefault(s => s.Id == id);
        if (script is null || toggle.IsOn == (script.RunAtLogin == true)) return;
        bool enabled = toggle.IsOn;
        await RunAsync(async () =>
        {
            bool consent = !enabled || ViewModel.Snapshot.Settings.LaunchManagerAtLogin || await interaction.ConfirmAsync(Text.Get("StartupConsentTitle"), Text.Get("StartupConsentMessage"), Text.Get("Enable"));
            if (!consent) { ViewModel.Apply(service.Snapshot); return; }
            var metadata = new ScriptMetadata(script.FriendlyName, script.Description, script.CollectionId, enabled, script.RunAsAdministrator, script.LaunchBlocked, [.. script.Hotkeys.Where(h => h.Origin == HotkeyOrigin.Manual).Select(h => h.Chord)]);
            ShowResult(await service.SaveMetadataAsync(id, metadata, enabled));
            if (ViewModel.Drafts.TryGetValue(id, out var draft)) draft.Startup = service.Snapshot.Scripts.FirstOrDefault(s => s.Id == id)?.RunAtLogin;
        });
    }
    async void OperationClicked(object sender, RoutedEventArgs args)
    {
        if (sender is Button button && Enum.TryParse<ScriptOperation>(button.Tag?.ToString(), out var operation)) await ExecuteOperationAsync(operation);
    }
    internal async Task ExecuteOperationAsync(ScriptOperation operation)
    {
        bool observational = operation is ScriptOperation.Edit or ScriptOperation.OpenFolder or ScriptOperation.Recover;
        var captured = observational && ViewModel.Selected is {} inspected ? new SelectionContext(inspected.Id, ViewModel.Query, ViewModel.Snapshot.Revision) : ViewModel.Selection();
        if (captured is not {} selection) return;
        await RunAsync(async () =>
        {
            using var cancellation = new CancellationTokenSource();
            effectCancellation = cancellation;
            try
            {
            var plan = await service.PrepareAsync(selection, operation, cancellation.Token);
            if (!plan.Allowed) { ShowResult(new(Outcome.Failed, plan.Reason)); return; }
            bool confirmed = !plan.RequiresConfirmation || await interaction.ConfirmAsync(Text.Operation(operation), Text.Get("ConfirmOperation") + Environment.NewLine + (ViewModel.Selected?.Path ?? ""));
            if (!confirmed) { ShowResult(new(Outcome.CancelledBeforeDispatch, ReasonCode.Cancelled)); return; }
            var observed = observational && ViewModel.Selected is {} currentInspected ? new SelectionContext(currentInspected.Id, ViewModel.Query, ViewModel.Snapshot.Revision) : ViewModel.Selection();
            if (observed is not {} current) { ShowResult(new(Outcome.Failed, ReasonCode.StaleSelection)); return; }
            ShowResult(await service.PerformAsync(plan, current, confirmed, cancellation.Token));
            }
            finally { if (ReferenceEquals(effectCancellation, cancellation)) effectCancellation = null; }
        });
    }
    async void RemoveClicked(object sender, RoutedEventArgs args)
    {
        if (ViewModel.Selected?.Id is not Guid id) return;
        await RunAsync(async () => { if (await interaction.ConfirmAsync(Text.Get("RemoveTitle"), Text.Get("RemoveMessage"))) ShowResult(await service.RemoveAsync(id)); });
    }
    async void GlobalRecoveryClicked(object sender, RoutedEventArgs args) => await RunAsync(async () => ShowResult(await service.RecoverPendingAsync()));
    async void AddFilesClicked(object sender, RoutedEventArgs args) => await RunAsync(async () => { var paths = await interaction.PickScriptsAsync(); if (paths.Count > 0) ShowCatalog(await service.AddFilesAsync(paths)); });
    async void AddFolderClicked(object sender, RoutedEventArgs args) => await RunAsync(async () => { if (await interaction.PickFolderAsync() is {} path) ShowCatalog(await service.AddFolderAsync(path)); });
    void NavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (!ready) return;
        if (args.IsSettingsSelected) { _ = ShowSettingsAsync(); return; }
        if (args.SelectedItem is not NavigationViewItem item) return;
        if (item.Tag is Guid collection) { collectionFilter = collection; page = "scripts"; UpdateQuery(); }
        else { var target = item.Tag?.ToString() ?? "scripts"; if (target == "all") { collectionFilter = null; page = "scripts"; UpdateQuery(); } else page = target; }
        ScriptsPage.Visibility = page == "scripts" ? Visibility.Visible : Visibility.Collapsed;
        OtherPage.Visibility = page == "scripts" ? Visibility.Collapsed : Visibility.Visible;
        PaintOther();
    }
    void PaintOther()
    {
        if (page == "scripts") return;
        OtherTitle.Text = Text.Get("Page_" + page); OtherAction.Content = Text.Get(page == "collections" ? "ManageCollections" : page == "help" ? "ImportLegacy" : "CopyDetails");
        string filter = OtherFilter.Text;
        IEnumerable<DetailRow> rows = page switch
        {
            "hotkeys" => ViewModel.Snapshot.Scripts.SelectMany(s => s.Hotkeys.Select(h => new DetailRow(h.Chord + " · " + s.FriendlyName, Text.Get("HotkeyOrigin_" + h.Origin) + " · " + Text.Get("HotkeysPartial") + (h.PossibleConflict ? " · " + Text.Get("PossibleConflict") : "") + Environment.NewLine + (h.Context ?? "") + Environment.NewLine + (h.SourcePath ?? s.Path)))),
            "activity" => ViewModel.Snapshot.Activity.OrderByDescending(a => a.Timestamp).Select(a => new DetailRow(a.Timestamp.ToLocalTime().ToString("g") + " · " + ActivityScript(a.ScriptId) + " · " + Text.Outcome(a.Outcome), (a.Operation is {} operation ? Text.Operation(operation) + " · " : "") + Text.Reason(a.Code) + Environment.NewLine + (a.Detail ?? ""))),
            "collections" => ViewModel.Snapshot.Collections.Select(c => new DetailRow(c.Name, string.Format(Text.Get("CollectionCount"), ViewModel.Snapshot.Scripts.Count(s => s.CollectionId == c.Id)))),
            _ => new[] { new DetailRow(Text.Get("HelpSafetyTitle"), Text.Get("HelpSafety")), new DetailRow(Text.Get("HelpStartupTitle"), Text.Get("HelpStartup")), new DetailRow(Text.Get("HelpHotkeysTitle"), Text.Get("HotkeysPartial")), new DetailRow(Text.Get("HelpKeysTitle"), Text.Get("HelpKeys")) }
        };
        OtherList.ItemsSource = rows.Where(r => (r.Title + r.Detail).Contains(filter, StringComparison.CurrentCultureIgnoreCase)).ToArray();
    }
    string ActivityScript(Guid? id)=>id is null?Text.Get("ManagerActivity"):ViewModel.Snapshot.Scripts.FirstOrDefault(script=>script.Id==id) is {} script?script.FriendlyName+" · "+script.Path:Text.Get("RemovedScriptActivity")+" · "+id.Value.ToString("D");
    void OtherFilterChanged(object sender, TextChangedEventArgs args) { if (ready) PaintOther(); }
    async void OtherActionClicked(object sender, RoutedEventArgs args)
    {
        if (page == "collections") await ManageCollectionsAsync();
        else if (page == "help") await ImportAsync();
        else { var rows = (OtherList.ItemsSource as IEnumerable<DetailRow>) ?? []; var data = new DataPackage(); data.SetText(string.Join(Environment.NewLine + Environment.NewLine, rows.Select(r => r.Title + Environment.NewLine + r.Detail))); Clipboard.SetContent(data); ShowMessage(Text.Get("Copied")); }
    }
    async Task ManageCollectionsAsync()
    {
        await RunAsync(async () =>
        {
            var choices = new ComboBox { Name="CollectionChoice",ItemsSource = ViewModel.Snapshot.Collections, DisplayMemberPath = "Name", HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = Text.Get("ChooseCollection") };
            var name = new TextBox { Name="CollectionName",Header = Text.Get("CollectionName") };
            choices.SelectionChanged += (_, _) => name.Text = (choices.SelectedItem as CollectionSnapshot)?.Name ?? "";
            var panel = new StackPanel { Spacing = 12 }; panel.Children.Add(choices); panel.Children.Add(name);
            var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = Text.Get("ManageCollections"), Content = panel, PrimaryButtonText = Text.Get("Save"), SecondaryButtonText = Text.Get("Remove"), CloseButtonText = Text.Get("Cancel") };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary) ShowResult(await service.SaveCollectionAsync((choices.SelectedItem as CollectionSnapshot)?.Id, name.Text));
            else if (result == ContentDialogResult.Secondary && choices.SelectedItem is CollectionSnapshot selected && await interaction.ConfirmAsync(Text.Get("RemoveTitle"), Text.Get("RemoveCollectionMessage"))) ShowResult(await service.RemoveCollectionAsync(selected.Id));
        });
    }
    async Task ImportAsync()
    {
        await RunAsync(async () =>
        {
            if (await interaction.PickFileAsync(".json") is not {} path) return;
            var preview = await service.PreviewImportAsync(path);
            if (!preview.CanImport) { ShowMessage(string.Join(Environment.NewLine, preview.Issues.Select(i => Text.Reason(i.Code))), null, InfoBarSeverity.Warning); return; }
            if (await interaction.ConfirmAsync(Text.Get("ImportLegacy"), string.Format(Text.Get("ImportMessage"), preview.ScriptCount))) ShowResult(await service.ImportAsync(preview));
        });
    }
    public async Task ShowSettingsAsync()
    {
        if (ViewModel.Busy) return;
        await RunAsync(async () =>
        {
            var settings = ViewModel.Snapshot.Settings;
            var interpreter = new TextBox { Name="SettingsInterpreter",Header = Text.Get("Interpreter"), Text = settings.InterpreterPath ?? "", PlaceholderText = Text.Get("AutoDetect") };
            var editor = new TextBox { Name="SettingsEditor",Header = Text.Get("Editor"), Text = settings.EditorPath ?? "", PlaceholderText = Text.Get("DefaultEdit") };
            var arguments = new TextBox { Name="SettingsEditorArguments",Header = Text.Get("EditorArguments"), Text = string.Join(Environment.NewLine, settings.EditorArguments), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60 };
            var theme = new ComboBox { Name="SettingsTheme",Header = Text.Get("Theme"), ItemsSource = new[] { Text.Get("ThemeSystem"), Text.Get("ThemeLight"), Text.Get("ThemeDark") }, SelectedIndex = (int)settings.Theme, HorizontalAlignment = HorizontalAlignment.Stretch };
            var login = new ToggleSwitch { Header = Text.Get("ManagerLogin"), IsOn = settings.LaunchManagerAtLogin };
            var show = new ToggleSwitch { Header = Text.Get("ShowAtLogin"), IsOn = settings.ShowWindowAtLogin };
            var close = new ComboBox { Header = Text.Get("CloseBehavior"), ItemsSource = new[] { Text.Get("HideToTray"), Text.Get("ExitManager") }, SelectedIndex = (int)settings.CloseBehavior, HorizontalAlignment = HorizontalAlignment.Stretch };
            var backup = new Button { Content = Text.Get("RestoreBackup") };
            var panel = new StackPanel { Spacing = 16, MinWidth = 380 }; foreach (var control in new UIElement[] { interpreter, editor, arguments, theme, login, show, close, new TextBlock { Text = Text.Get("StartupExplanation"), TextWrapping = TextWrapping.Wrap }, backup }) panel.Children.Add(control);
            var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = Text.Get("Settings"), Content = new ScrollViewer { Content = panel, MaxHeight = 540 }, PrimaryButtonText = Text.Get("Save"), CloseButtonText = Text.Get("Cancel") };
            bool restore = false; backup.Click += (_, _) => { restore = true; dialog.Hide(); };
            var result = await dialog.ShowAsync();
            if (restore) { if (await interaction.ConfirmAsync(Text.Get("RestoreBackup"), Text.Get("RestoreBackupMessage"))) ShowResult(await service.RestoreBackupAsync()); return; }
            if (result != ContentDialogResult.Primary) return;
            bool consent = !login.IsOn || settings.LaunchManagerAtLogin || await interaction.ConfirmAsync(Text.Get("StartupConsentTitle"), Text.Get("StartupConsentMessage"), Text.Get("Enable"));
            if (!consent) return;
            ShowResult(await service.SaveSettingsAsync(settings with { InterpreterPath = NullIfEmpty(interpreter.Text), EditorPath = NullIfEmpty(editor.Text), EditorArguments = [.. arguments.Text.Split(['\r','\n'], StringSplitOptions.RemoveEmptyEntries)], Theme = (AppThemePreference)theme.SelectedIndex, LaunchManagerAtLogin = login.IsOn, ShowWindowAtLogin = show.IsOn, CloseBehavior = (CloseBehavior)close.SelectedIndex }, login.IsOn));
        });
    }
    internal async Task ShowSettingsWhenReadyAsync()
    {
        await WhenReady;if(closed)return;
        if(ViewModel.Busy)
        {
            var idle=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler changed=(_,_)=>{if(!ViewModel.Busy)idle.TrySetResult();};
            ViewModel.PresentationChanged+=changed;
            try {if(!ViewModel.Busy)idle.TrySetResult();await idle.Task.WaitAsync(windowLifetime.Token);}
            finally {ViewModel.PresentationChanged-=changed;}
        }
        if(!closed)await ShowSettingsAsync();
    }
    static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    void RootSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (InspectorColumn is null||CompactInspector is null||Inspector is null) return;
        bool narrow = args.NewSize.Width < 1120;
        bool compact=args.NewSize.Width<900;Control? focused=null;
        if(RootGrid.XamlRoot is {} root)focused=FocusManager.GetFocusedElement(root) as Control;
        bool inspectorFocused=false;for(DependencyObject? node=focused;node is not null;node=Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))if(ReferenceEquals(node,Inspector)){inspectorFocused=true;break;}
        if(compact&&ReferenceEquals(Inspector.Parent,ScriptsPage))
        {
            ScriptsPage.Children.Remove(Inspector);CompactInspector.Content=Inspector;
            if(inspectorFocused)CompactInspector.IsExpanded=true;
        }
        else if(!compact&&ReferenceEquals(CompactInspector.Content,Inspector))
        {
            CompactInspector.Content=null;ScriptsPage.Children.Add(Inspector);
        }
        CompactInspector.Visibility=compact?Visibility.Visible:Visibility.Collapsed;
        Inspector.Padding=compact?new Thickness(0):new Thickness(20,0,0,0);Inspector.BorderThickness=compact?new Thickness(0):new Thickness(1,0,0,0);
        InspectorScroll.MaxHeight=compact?Math.Max(80,Math.Min(320,args.NewSize.Height-350)):double.PositiveInfinity;
        ScriptsPage.ColumnSpacing=compact?0:20;InspectorColumn.Width = new GridLength(compact?0:narrow?280:310);
        HotkeyHeaderColumn.Width=new GridLength(args.NewSize.Width<720?0:105);HotkeyHeader.Visibility=args.NewSize.Width<720?Visibility.Collapsed:Visibility.Visible;
        Navigation.PaneDisplayMode = args.NewSize.Width < 1060 ? NavigationViewPaneDisplayMode.LeftCompact : NavigationViewPaneDisplayMode.Left;
        if(inspectorFocused&&focused is not null)DispatcherQueue.TryEnqueue(()=>focused.Focus(FocusState.Programmatic));
    }
    async Task RunAsync(Func<Task> action)
    {
        if (ViewModel.Busy || closed) return;
        ViewModel.Busy = true;
        try { await action(); }
        catch (OperationCanceledException) { ShowResult(new(Outcome.CancelledBeforeDispatch, ReasonCode.Cancelled)); }
        catch (Exception error) { ShowMessage(Text.Get("UnexpectedError"), error.Message, InfoBarSeverity.Error); }
        finally { if (!closed) { ViewModel.Apply(service.Snapshot); ViewModel.Busy = false; } }
    }
    void ShowCatalog(CatalogResult result) { ShowResult(result.Result); if (result.DuplicateCount > 0) ShowMessage(string.Format(Text.Get("DuplicateCount"), result.DuplicateCount)); }
    void ShowResult(OperationResult result) => ShowMessage(Text.Outcome(result.Outcome) + (result.Code == ReasonCode.None ? "" : " · " + Text.Reason(result.Code)), result.Detail, result.Succeeded ? InfoBarSeverity.Success : result.Outcome == Outcome.CancelledBeforeDispatch ? InfoBarSeverity.Informational : InfoBarSeverity.Warning);
    void ShowMessage(string message, string? detail = null, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        GlobalInfo.Title = message; GlobalInfo.Message = detail is null ? "" : Text.Get("DiagnosticDetails") + ": " + detail; GlobalInfo.Severity = severity; GlobalInfo.IsOpen = true;
    }
    void ScriptRightTapped(object sender, RightTappedRoutedEventArgs args)
    {
        if (ViewModel.Selected is null) return;
        var flyout = CreateScriptMenu();
        flyout.ShowAt(ScriptList, args.GetPosition(ScriptList));
    }
    internal MenuFlyout CreateScriptMenu()
    {
        var flyout = new MenuFlyout();
        foreach (var operation in new[] { ScriptOperation.Start, ScriptOperation.Stop, ScriptOperation.Restart, ScriptOperation.Edit, ScriptOperation.OpenFolder })
        {
            var reason=ActionReason(operation);var item = new MenuFlyoutItem { Text = Text.Operation(operation),IsEnabled=reason==ReasonCode.None };ToolTipService.SetToolTip(item,reason==ReasonCode.None?Text.Operation(operation):Text.Reason(reason)); item.Click += async (_, _) => await ExecuteOperationAsync(operation); flyout.Items.Add(item);
        }
        return flyout;
    }
    internal void RequestExit() => ExitRequested?.Invoke(this, EventArgs.Empty);
}
