using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Bezoro.AhkManager.Presentation;

public partial class MainWindow : Window
{
    public UiSession Session { get; }
    public bool CloseToTray { get; set; }
    public bool TrayAvailable { get; set; } = true;
    public event Action? HiddenToTray;
    bool painting;
    bool exitRequestQueued;
    readonly Dictionary<ScriptAction, Button> actionButtons;
    readonly Dictionary<Guid, string> descriptionDrafts = [];
    readonly Dictionary<Guid, RowControls> rows = [];
    Guid? displayedDescription;
    sealed record RowControls(ListBoxItem Item, TextBlock Name, TextBlock State, TextBlock Description, TextBlock Path, TextBlock Source, CheckBox Autostart, CheckBox AdminPreference);
    public IReadOnlyList<Control> FocusControls { get; }
    public MainWindow(UiSession session)
    {
        Session = session; painting = true; InitializeComponent();
        actionButtons = new() { [ScriptAction.Start] = StartButton, [ScriptAction.Stop] = StopButton, [ScriptAction.Restart] = RestartButton, [ScriptAction.Recycle] = RecycleButton };
        Control[] tabOrder = [RefreshButton, RegisterButton, SearchBox, ClearButton, StartAllButton, StopAllButton, RestartRunningButton, ScriptList, CatalogSplitter, ConvertButton, RetryButton, VerifyRequestButton, RecoverRequestButton, DescriptionBox, SaveButton, PathBox, CopyButton, InterpreterButton, BlockedBox, StartButton, StopButton, RestartButton, EditButton, RecycleButton, CoverageButton];
        FocusControls = tabOrder;
        for (int index = 0; index < tabOrder.Length; index++)
        {
            tabOrder[index].TabIndex = index;
            if (AutomationProperties.GetName(tabOrder[index]).Length == 0 && tabOrder[index] is ContentControl labeled)
                AutomationProperties.SetName(tabOrder[index], labeled.Content?.ToString()?.Replace("_", "", StringComparison.Ordinal) ?? "Controle");
        }
        Session.Changed += Paint; Session.DescriptionSaved += DescriptionCommitted; Closing += ClosingWindow;
        Closed += (_, _) => { Session.Changed -= Paint; Session.DescriptionSaved -= DescriptionCommitted; };
        Paint();
    }
    void ClosingWindow(object? sender, CancelEventArgs args)
    {
        if (CloseToTray)
        {
            args.Cancel = true;
            if (TrayAvailable) { Hide(); HiddenToTray?.Invoke(); }
            else
            {
                Session.ReportTrayFailure();
                if (!exitRequestQueued)
                {
                    exitRequestQueued = true;
                    Dispatcher.BeginInvoke(new Action(() => { exitRequestQueued = false; Session.RequestExit(); }));
                }
            }
            return;
        }
        args.Cancel = !Session.CanClose();
    }
    void Paint()
    {
        painting = true;
        try
        {
            SearchBox.Text = Session.Search;
            var visible = Session.Visible;
            var catalogIds = Session.Entries.Select(e => e.Id).ToHashSet();
            foreach (var removed in rows.Keys.Where(id => !catalogIds.Contains(id)).ToList()) rows.Remove(removed);
            var visibleIds = visible.Select(e => e.Id).ToHashSet();
            for (int index = ScriptList.Items.Count - 1; index >= 0; index--)
                if (!visibleIds.Contains((Guid)((ListBoxItem)ScriptList.Items[index]).Tag)) ScriptList.Items.RemoveAt(index);
            for (int index = 0; index < visible.Count; index++)
            {
                var entry = visible[index];
                if (!rows.TryGetValue(entry.Id, out var row)) { row = Row(entry); rows.Add(entry.Id, row); }
                UpdateRow(row, entry);
                if (index >= ScriptList.Items.Count || !ReferenceEquals(ScriptList.Items[index], row.Item))
                {
                    if (ScriptList.Items.Contains(row.Item)) ScriptList.Items.Remove(row.Item);
                    ScriptList.Items.Insert(index, row.Item);
                }
            }
            var selectedRow = Session.Selected is { } visibleSelected && rows.TryGetValue(visibleSelected.Id, out var selectedControls) ? selectedControls.Item : null;
            if (!ReferenceEquals(ScriptList.SelectedItem, selectedRow)) ScriptList.SelectedItem = selectedRow;
            CountText.Text = $"{visible.Count} de {Session.DisplayCount}";
            EmptyPanel.Visibility = ScriptList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyText.Text = Session.DisplayCount == 0 ? "Nenhum script cadastrado" : "Nenhum resultado para esta busca";
            var selected = Session.Selected;
            ConvertButton.Visibility = Session.NeedsSchemaConversion ? Visibility.Visible : Visibility.Collapsed;
            ConvertButton.IsEnabled = !Session.Busy && !Session.ReadOnly;
            StartAllButton.IsEnabled = StopAllButton.IsEnabled = RestartRunningButton.IsEnabled = !Session.Busy && !Session.ShuttingDown && Session.OwnsEffects && !Session.ReadOnly && !Session.NeedsSchemaConversion;
            PauseText.Text = Session.PauseNotice;
            IntentText.Text = selected is null ? "" : Session.IntentState(selected);
            var retry = Session.RetryAvailability();
            RetryButton.Visibility = retry.Success ? Visibility.Visible : Visibility.Collapsed;
            RetryButton.IsEnabled = retry.Success;
            RetryButton.ToolTip = retry.Message;
            SelectedTitle.Text = selected?.Name ?? "Selecione um script"; SelectedState.Text = selected is null ? "" : Session.State(selected);
            displayedDescription = selected?.Id;
            DescriptionBox.Text = selected is null ? "" : descriptionDrafts.GetValueOrDefault(selected.Id, selected.Description);
            OriginText.Text = selected is null ? "" : descriptionDrafts.TryGetValue(selected.Id, out var draft) && draft != selected.Description ? "Rascunho não salvo" : selected.DescriptionSource;
            PathBox.Text = selected?.Path ?? "";
            InterpreterText.Text = selected is null ? "" : string.IsNullOrEmpty(selected.Interpreter) ? "Não escolhido" : selected.Interpreter;
            BlockedBox.IsChecked = selected?.LaunchBlocked ?? false;
            RequestPanel.Visibility = !Session.Authority.Ready || Session.Authority.HasPending || Session.BrushPending(selected) ? Visibility.Visible : Visibility.Collapsed;
            RequestText.Text = Session.RequestDetails(selected);
            RecoverRequestButton.IsEnabled = Session.Authority.HasPending && Session.Authority.Ready && !Session.NeedsSchemaConversion && !Session.Busy && !Session.ShuttingDown && Session.OwnsEffects;
            VerifyRequestButton.IsEnabled = (Session.Authority.HasPending || Session.BrushPending(selected)) && Session.Authority.Ready && !Session.ReadOnly && !Session.NeedsSchemaConversion && !Session.Busy && !Session.ShuttingDown && Session.OwnsEffects;
            SourcesText.Text = selected is null ? "Selecione um script para consultar suas fontes." : string.Join("\n", Session.Discovery.Sources.Where(s => Paths.Equal(s.Path, selected.Path)).Select(s => s.Source + (s.Elevated ? " · elevação configurada" : "")));
            if (selected is not null && SourcesText.Text.Length == 0) SourcesText.Text = "Não identificado no escopo consultado.";
            InstancesText.Text = selected is null ? "" : string.Join("\n\n", Session.Snapshot.For(selected.Path).Select(p => $"PID {p.Pid} · criação UTC {DateTime.FromFileTimeUtc(p.Created):yyyy-MM-dd HH:mm:ss}\n{p.Executable}"));
            if (selected is not null && InstancesText.Text.Length == 0) InstancesText.Text = "Nenhuma instância verificável." + (Session.Snapshot.Unknown ? " Execução ainda incerta." : "");
            if (selected is not null && Session.Authority.Find(selected.Path) is not null) InstancesText.Text = "Observação separada; não confirma relação com o pedido.\n\n" + InstancesText.Text;
            if (selected is not null && Session.ObservationStale) InstancesText.Text = "Observação anterior; não confirma o estado atual.\n\n" + InstancesText.Text;
            SupervisorText.Text = selected is null ? "" : Session.SupervisorWarning(selected);
            var reasons = new List<string>();
            foreach (var pair in actionButtons)
            {
                var allowed = Session.Availability(pair.Key); pair.Value.IsEnabled = !Session.Busy && allowed.Success;
                pair.Value.ToolTip = allowed.Message;
                if (!allowed.Success) reasons.Add(Label(pair.Key) + ": " + allowed.Message);
            }
            PolicyHint.Text = Session.Busy ? "Ação em andamento. Aguarde; novos comandos estão bloqueados." : string.Join("\n", reasons.Distinct());
            PolicyHint.ToolTip = string.Join("\n", reasons.Distinct());
            bool writable = selected is not null && !Session.ReadOnly && !Session.NeedsSchemaConversion && !Session.Busy;
            DescriptionBox.IsEnabled = SaveButton.IsEnabled = InterpreterButton.IsEnabled = BlockedBox.IsEnabled = writable;
            CopyButton.IsEnabled = selected is not null && !Session.Busy;
            EditButton.IsEnabled = selected is not null && System.IO.File.Exists(selected.Path) && !Session.Busy;
            RefreshButton.IsEnabled = SearchBox.IsEnabled = ClearButton.IsEnabled = ScriptList.IsEnabled = !Session.Busy;
            RegisterButton.IsEnabled = !Session.Busy && !Session.ReadOnly;
            CoverageButton.IsEnabled = !Session.Busy;
            int unknown = Session.Snapshot.Processes.Count(p => p.Script is null);
            CoverageText.Text = (Session.ObservationStale ? "Observação anterior · consulta indisponível · " : "Cobertura parcial do PC · ") + $"AHK sem script identificado: {unknown} · lacunas de fontes: {Session.Discovery.Limits.Count}";
            CoverageDetailsText.Text = "Escopo: pasta AutoHotkey em Documents, cadastro manual e fontes Startup/Run/tarefas acessíveis.\n" + Session.Snapshot.Note + "\n" + string.Join("\n", Session.Discovery.Limits) + "\n" + string.Join("\n", Session.Snapshot.Processes.Where(p => p.Script is null).Select(p => $"PID {p.Pid}: elevado ou inacessível, sem script atribuído."));
            StatusText.Text = (Session.Error ? "⚠ " : Session.Busy ? "Em andamento · " : "") + Session.Status;
            StatusText.Foreground = (Brush)FindResource(Session.Error ? "TextBrush" : "MutedBrush");
            StatusBorder.BorderBrush = Session.Error ? new SolidColorBrush(Color.FromRgb(187, 146, 89)) : (Brush)FindResource("LineBrush");
        }
        finally { painting = false; }
    }
    RowControls Row(ScriptEntry entry)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var name = new TextBlock { Text = entry.Name, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(0, 0, 8, 0) };
        var state = new TextBlock { Text = Session.State(entry), Foreground = (Brush)FindResource("MutedBrush"), FontSize = 11, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(state, 1); grid.Children.Add(name); grid.Children.Add(state);
        var description = new TextBlock { Text = entry.Description, FontSize = 12, Foreground = (Brush)FindResource("MutedBrush"), TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(0, 4, 0, 0) };
        Grid.SetRow(description, 1); Grid.SetColumnSpan(description, 2); grid.Children.Add(description);
        var path = new TextBlock { Text = entry.Path, FontFamily = new("Cascadia Mono, Consolas"), FontSize = 11, Foreground = (Brush)FindResource("MutedBrush"), TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(0, 3, 8, 0) };
        Grid.SetRow(path, 2); grid.Children.Add(path);
        var source = new TextBlock { Text = Session.IntentState(entry) + " | " + Session.ExternalState(entry) + (entry.LaunchBlocked ? " · bloqueio local" : ""), FontSize = 11, Foreground = (Brush)FindResource("MutedBrush"), TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 200, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(source, 2); Grid.SetColumn(source, 1); grid.Children.Add(source);
        var item = new ListBoxItem { Content = grid, Tag = entry.Id, ToolTip = entry.Path, MinHeight = 110 };
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var preferences = new WrapPanel { Margin = new(0, 6, 0, 0) };
        var autostart = new CheckBox { Content = "Iniciar com o Windows", IsThreeState = false, Margin = new(0, 0, 12, 4) };
        var administrator = new CheckBox { Content = "Executar como administrador", Margin = new(0, 0, 0, 4) };
        AutomationProperties.SetName(autostart, "Iniciar com o Windows: " + entry.Path);
        AutomationProperties.SetName(administrator, "Preferência Executar como administrador: " + entry.Path);
        autostart.Click += (_, _) => { if (!painting) Session.SetStartWithWindows(entry.Id, autostart.IsChecked); };
        administrator.Click += (_, _) => { if (!painting) Session.SetAdministratorPreference(entry.Id, administrator.IsChecked == true); };
        preferences.Children.Add(autostart); preferences.Children.Add(administrator);
        Grid.SetRow(preferences, 3); Grid.SetColumnSpan(preferences, 2); grid.Children.Add(preferences);
        return new(item, name, state, description, path, source, autostart, administrator);
    }
    void UpdateRow(RowControls row, ScriptEntry entry)
    {
        row.Name.Text = entry.Name; row.State.Text = Session.State(entry) + " · Privilégio: " + Session.PrivilegeState(entry); row.Description.Text = entry.Description; row.Path.Text = entry.Path;
        row.Autostart.IsChecked = Session.StartupChoice(entry); row.AdminPreference.IsChecked = entry.RunAsAdministrator;
        row.Autostart.IsEnabled = Session.StartupEditable(entry) && !Session.Busy && !Session.ReadOnly && !Session.NeedsSchemaConversion && Session.OwnsEffects && !Session.ShuttingDown;
        row.AdminPreference.IsEnabled = !Session.Busy && !Session.ReadOnly && !Session.NeedsSchemaConversion && Session.OwnsEffects && !Session.ShuttingDown;
        row.Source.Text = Session.IntentState(entry) + " | " + Session.ExternalState(entry) + (entry.LaunchBlocked ? " · bloqueio local" : ""); row.Source.ToolTip = Session.IntentState(entry) + " | " + Session.ExternalState(entry); row.Autostart.ToolTip = Session.IsBrush(entry) ? Session.TaskNotice(entry) : "Escolha para o proximo login; execucao atual preservada."; row.Item.ToolTip = entry.Path;
        AutomationProperties.SetName(row.Item, $"{entry.Name}; {Session.State(entry)}; início externo {Session.ExternalState(entry)}; {entry.Path}");
    }
    void DescriptionChanged(object sender, TextChangedEventArgs args)
    {
        if (!painting && displayedDescription is { } id)
        {
            descriptionDrafts[id] = DescriptionBox.Text;
            OriginText.Text = "Rascunho não salvo";
        }
    }
    void DescriptionCommitted(Guid id) => descriptionDrafts.Remove(id);
    static string Label(ScriptAction action) => action switch { ScriptAction.Start => "Iniciar", ScriptAction.Stop => "Parar", ScriptAction.Restart => "Reiniciar", _ => "Lixeira" };
    void SearchChanged(object sender, TextChangedEventArgs args) { if (!painting) Session.SetSearch(SearchBox.Text); }
    void SelectionChanged(object sender, SelectionChangedEventArgs args) { if (!painting) Session.Select((ScriptList.SelectedItem as ListBoxItem)?.Tag is Guid id ? id : null); }
    async void VerifyRequestClicked(object sender, RoutedEventArgs args) { if (Session.BrushPending(Session.Selected)) await Session.InspectBrush(); else await Session.VerifyRequest(); }
    async void ConvertClicked(object sender, RoutedEventArgs args) => await Session.ConvertSchema();
    void RetryClicked(object sender, RoutedEventArgs args) => Session.RetryRecovery();
    void ClearClicked(object sender, RoutedEventArgs args) => Session.SetSearch("");
    async void RefreshClicked(object sender, RoutedEventArgs args) => await Session.Refresh();
    void RegisterClicked(object sender, RoutedEventArgs args) => Session.Register();
    void SaveClicked(object sender, RoutedEventArgs args) => Session.SaveDescription(DescriptionBox.Text);
    void CopyClicked(object sender, RoutedEventArgs args) => Session.CopyPath();
    void InterpreterClicked(object sender, RoutedEventArgs args) => Session.ChooseInterpreter();
    void BlockedChanged(object sender, RoutedEventArgs args) { if (!painting) Session.SetBlocked(BlockedBox.IsChecked == true); }
    void EditClicked(object sender, RoutedEventArgs args) => Session.Edit();
    async void StartClicked(object sender, RoutedEventArgs args) => await Session.Act(ScriptAction.Start);
    async void StopClicked(object sender, RoutedEventArgs args) => await Session.Act(ScriptAction.Stop);
    async void RestartClicked(object sender, RoutedEventArgs args) => await Session.Act(ScriptAction.Restart);
    async void RecycleClicked(object sender, RoutedEventArgs args) => await Session.Act(ScriptAction.Recycle);
    void CoverageClicked(object sender, RoutedEventArgs args)
    {
        CoveragePanel.Visibility = CoveragePanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        CoverageButton.Content = CoveragePanel.Visibility == Visibility.Visible ? "Ocultar detalhes" : "Ver detalhes";
    }
    async void StartAllClicked(object sender, RoutedEventArgs args) => await ShowBatch(BatchAction.Start);
    async void StopAllClicked(object sender, RoutedEventArgs args) => await ShowBatch(BatchAction.Stop);
    async void RestartRunningClicked(object sender, RoutedEventArgs args) => await ShowBatch(BatchAction.RestartRunning);
    async Task ShowBatch(BatchAction action) => await Session.RunBatch(action, acknowledge: (result, token) => { BatchResultWindow.Show(result, this, false, token); return Task.CompletedTask; });
    public IReadOnlyList<Button> BatchButtons => [StartAllButton, StopAllButton, RestartRunningButton];
    public Button ActionButton(ScriptAction action) => actionButtons[action];
    async void RecoverRequestClicked(object sender, RoutedEventArgs e) => await Session.RecoverAfterRestart();
    public Button RecoverRequestControl => RecoverRequestButton;
    public void ShowRequestDetails() { DetailScroller.ScrollToVerticalOffset(RequestPanel.TranslatePoint(new Point(0, 0), DetailBody).Y); DetailScroller.UpdateLayout(); }
    public TextBlock RequestControl => RequestText;
    public Button VerifyRequestControl => VerifyRequestButton;
    public TextBox SearchControl => SearchBox;
    public TextBox DescriptionControl => DescriptionBox;
    public ListBox ListControl => ScriptList;
    public FrameworkElement FixedActions => ActionArea;
    public TextBlock PolicyControl => PolicyHint;
}
