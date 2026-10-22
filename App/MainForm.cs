using System.Diagnostics;

namespace Bezoro.AhkManager;

public sealed class MainForm : Form
{
    readonly CatalogStore store;
    Catalog catalog;
    readonly WindowsProcesses processes = new();
    readonly ActionController actions;
    Snapshot snapshot = new([], false, "Atualize para consultar processos.");
    DiscoveryResult discovery = new([], [], []);
    readonly TextBox search = new() { PlaceholderText = "Buscar nome, descrição ou caminho", Dock = DockStyle.Fill };
    readonly DataGridView grid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, RowHeadersVisible = false, AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None, BackgroundColor = Color.White, BorderStyle = BorderStyle.None, AutoGenerateColumns = false };
    readonly Label coverage = new() { Dock = DockStyle.Fill, AutoEllipsis = true, Padding = new(0, 4, 0, 0), ForeColor = Color.FromArgb(112, 70, 15) };
    readonly Label title = new() { Dock = DockStyle.Fill, Font = new("Segoe UI", 13, FontStyle.Bold), AutoEllipsis = true };
    readonly TextBox path = new() { Dock = DockStyle.Fill, ReadOnly = true };
    readonly TextBox description = new() { Dock = DockStyle.Fill, Multiline = true, MaxLength = 4000, ScrollBars = ScrollBars.Vertical };
    readonly Label origin = new() { Dock = DockStyle.Fill };
    readonly Label interpreter = new() { Dock = DockStyle.Fill, AutoEllipsis = true };
    readonly TextBox details = new() { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.FixedSingle };
    readonly CheckBox blocked = new() { Text = "Bloquear início somente pelo gerenciador", AutoSize = true };
    readonly Label status = new() { Dock = DockStyle.Fill, AutoEllipsis = true, Padding = new(0, 8, 0, 0) };
    readonly Button refresh = new() { Text = "Atualizar", AutoSize = true };
    readonly Button register = new() { Text = "Cadastrar .ahk", AutoSize = true };
    readonly Panel interaction = new() { Dock = DockStyle.Fill };
    readonly ToolTip tips = new();
    readonly Dictionary<ScriptAction, Button> actionButtons = [];
    readonly bool fixture;
    bool busy, selecting;
    public Guid? SelectedId => grid.CurrentRow?.Tag is Guid id ? id : null;
    ScriptEntry? Selected => catalog.Scripts.FirstOrDefault(e => e.Id == SelectedId);

    public MainForm(CatalogStore store, bool fixture = false)
    {
        this.store = store; this.fixture = fixture; catalog = fixture ? FixtureCatalog() : store.Load(); actions = new(processes, new WindowsRecycler());
        Text = "Bezoro AHK Manager"; Size = new(1220, 800); MinimumSize = new(1000, 720); StartPosition = FormStartPosition.CenterScreen;
        Font = new("Segoe UI", 10); BackColor = Color.FromArgb(245, 247, 250); ForeColor = Color.FromArgb(30, 42, 56); AutoScaleMode = AutoScaleMode.Dpi;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(20), ColumnCount = 1, RowCount = 5 };
        root.RowStyles.Add(new(SizeType.Absolute, 45)); root.RowStyles.Add(new(SizeType.Absolute, 44)); root.RowStyles.Add(new(SizeType.Absolute, 64)); root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.Absolute, 46)); Controls.Add(root);
        root.Controls.Add(new Label { Text = "Seus scripts AutoHotkey", AutoSize = true, Font = new("Segoe UI", 19, FontStyle.Bold) }, 0, 0);
        var toolbar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4 }; toolbar.ColumnStyles.Add(new(SizeType.Absolute, 62)); toolbar.ColumnStyles.Add(new(SizeType.Percent, 100)); toolbar.ColumnStyles.Add(new(SizeType.Absolute, 110)); toolbar.ColumnStyles.Add(new(SizeType.Absolute, 155));
        toolbar.Controls.Add(new Label { Text = "Buscar", AutoSize = true, Padding = new(0, 5, 0, 0) }, 0, 0); toolbar.Controls.Add(search, 1, 0); toolbar.Controls.Add(refresh, 2, 0); toolbar.Controls.Add(register, 3, 0); root.Controls.Add(toolbar, 0, 1); root.Controls.Add(coverage, 0, 2);
        root.Controls.Add(interaction, 0, 3); root.Controls.Add(status, 0, 4);
        var split = new SplitContainer { Size = new(1160, 550), Dock = DockStyle.Fill, SplitterDistance = 620, Panel1MinSize = 430, Panel2MinSize = 420 };
        // Set size before panel minimums are applied by layout.
        split.Size = new(1160, 550); interaction.Controls.Add(split); split.Panel1.Controls.Add(grid);
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Script", Width = 160 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Execução atual", Width = 135 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Início externo", Width = 130 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Caminho", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 140 });
        grid.RowTemplate.Height = 38; grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(221, 234, 247); grid.DefaultCellStyle.SelectionForeColor = ForeColor;
        grid.DefaultCellStyle.Padding = new(6, 3, 6, 3); grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(248, 250, 252); grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(231, 236, 242); grid.ColumnHeadersDefaultCellStyle.ForeColor = ForeColor; grid.ColumnHeadersHeight = 36;
        var detailRoot = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 }; detailRoot.RowStyles.Add(new(SizeType.Percent, 100)); detailRoot.RowStyles.Add(new(SizeType.Absolute, 48)); detailRoot.RowStyles.Add(new(SizeType.Absolute, 46)); split.Panel2.Controls.Add(detailRoot);
        var scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true }; detailRoot.Controls.Add(scroller, 0, 0);
        var panel = new TableLayoutPanel { Dock = DockStyle.Top, Height = 560, Padding = new(12, 0, 0, 0), ColumnCount = 1, RowCount = 10 };
        int[] rows = [32, 22, 32, 24, 80, 36, 34, 34, 36, 0];
        for (int i = 0; i < rows.Length; i++) panel.RowStyles.Add(new(rows[i] == 0 ? SizeType.Percent : SizeType.Absolute, rows[i] == 0 ? 100 : rows[i]));
        scroller.Controls.Add(panel); panel.Controls.Add(title, 0, 0); panel.Controls.Add(new Label { Text = "Arquivo", AutoSize = true }, 0, 1); panel.Controls.Add(path, 0, 2); panel.Controls.Add(origin, 0, 3); panel.Controls.Add(description, 0, 4);
        var metadata = new FlowLayoutPanel { Dock = DockStyle.Fill }; var save = new Button { Text = "Salvar descrição", AutoSize = true }; var copy = new Button { Text = "Copiar caminho", AutoSize = true }; metadata.Controls.Add(save); metadata.Controls.Add(copy); panel.Controls.Add(metadata, 0, 5);
        panel.Controls.Add(interpreter, 0, 6); var selectInterpreter = new Button { Text = "Escolher interpretador...", AutoSize = true }; panel.Controls.Add(selectInterpreter, 0, 7);
        var blockedPanel = new FlowLayoutPanel { Dock = DockStyle.Fill }; blockedPanel.Controls.Add(blocked); panel.Controls.Add(blockedPanel, 0, 8); panel.Controls.Add(details, 0, 9);
        var controls = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new(12, 4, 0, 0) };
        foreach (var (action, label) in new[] { (ScriptAction.Start, "Iniciar"), (ScriptAction.Stop, "Parar"), (ScriptAction.Restart, "Reiniciar") })
        {
            var button = new Button { Text = label, AutoSize = true, MinimumSize = new(86, 32) }; button.Click += async (_, _) => await Act(action); controls.Controls.Add(button); actionButtons[action] = button;
        }
        detailRoot.Controls.Add(controls, 0, 1);
        var files = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new(12, 4, 0, 0) }; var edit = new Button { Text = "Editar arquivo", AutoSize = true }; var recycle = new Button { Text = "Enviar à lixeira", AutoSize = true, ForeColor = Color.FromArgb(145, 40, 40) }; files.Controls.Add(edit); files.Controls.Add(recycle); detailRoot.Controls.Add(files, 0, 2); actionButtons[ScriptAction.Recycle] = recycle;
        search.AccessibleName = "Buscar scripts"; description.AccessibleName = "Descrição do script"; grid.AccessibleName = "Scripts cadastrados"; details.AccessibleName = "Instâncias e fontes externas";
        search.TextChanged += (_, _) => Populate(SelectedId); grid.SelectionChanged += (_, _) => SelectEntry();
        refresh.Click += async (_, _) => await RefreshCatalog(); register.Click += (_, _) => Register(); save.Click += (_, _) => SaveDescription(); copy.Click += (_, _) => { if (Selected is { } e) Clipboard.SetText(e.Path); };
        blocked.CheckedChanged += (_, _) => { if (!selecting && Selected is { } e) Mutate(e, next => next.LaunchBlocked = blocked.Checked); };
        selectInterpreter.Click += (_, _) => ChooseInterpreter(); edit.Click += (_, _) => Edit(); recycle.Click += async (_, _) => await Act(ScriptAction.Recycle);
        FormClosing += (_, e) => { if (busy) { e.Cancel = true; status.Text = "Aguarde a ação terminar antes de fechar."; } };
        if (fixture)
        {
            var running = catalog.Scripts[0]; snapshot = new([new(4200, new DateTime(2026, 1, 15, 9, 30, 0, DateTimeKind.Utc).ToFileTimeUtc(), @"C:\Example\AutoHotkey64.exe", running.Path), new(4300, 0, "", null)], false, "Há 1 processo AutoHotkey elevado ou inacessível.");
            discovery = new([], [new(running.Path, "Atalho Startup: Automação.lnk", false)], ["Uma fonte externa está inacessível."]); Populate(); Coverage(); status.Text = "Exemplo isolado. Início recusado: identidade desconhecida pode ser uma duplicata.";
        }
        else { Populate(); status.Text = store.Notice; Shown += async (_, _) => await RefreshCatalog(); }
    }
    static Catalog FixtureCatalog() => new() { Scripts = [new() { Path = @"C:\Example\AutoHotkey\desktop.ahk", Description = "Mostra ou oculta os ícones da área de trabalho com duplo clique.", Interpreter = @"C:\Example\AutoHotkey64.exe" }, new() { Path = @"C:\Example\AutoHotkey\brush.ahk", Description = "Ajusta tamanho de pincel com a roda do mouse.", DescriptionSource = "Informada", LaunchBlocked = true, Interpreter = @"C:\Example\AutoHotkey64.exe" }, new() { Path = @"C:\Example\AutoHotkey\missing.ahk", Description = "Arquivo ausente, registro mantido para consulta." }] };
    string State(ScriptEntry e)
    {
        int count = snapshot.For(e.Path).Count;
        if (count > 0) return $"Executando ({count})";
        if (snapshot.Unknown) return "Execução incerta";
        return File.Exists(e.Path) ? "Parado" : "Arquivo ausente";
    }
    void Populate(Guid? preferred = null)
    {
        var selected = preferred ?? SelectedId; selecting = true; grid.Rows.Clear();
        foreach (var entry in catalog.Scripts.Where(e => Paths.Matches(e, search.Text)))
        {
            var sources = discovery.Sources.Where(s => Paths.Equal(s.Path, entry.Path)).ToList();
            int index = grid.Rows.Add(entry.Name, State(entry), sources.Count > 0 ? sources.Any(s => s.Elevated) ? "Tarefa elevada" : "Configurado" : "Não identificado", entry.Path); grid.Rows[index].Tag = entry.Id; grid.Rows[index].Cells[0].ToolTipText = grid.Rows[index].Cells[3].ToolTipText = entry.Path;
            if (entry.Id == selected) grid.CurrentCell = grid.Rows[index].Cells[0];
        }
        selecting = false; SelectEntry();
    }
    void SelectEntry()
    {
        if (selecting) return; selecting = true;
        var e = Selected; title.Text = e?.Name ?? "Selecione um script"; path.Text = e?.Path ?? ""; description.Text = e?.Description ?? ""; description.Enabled = e is not null && !store.ReadOnly; origin.Text = e is null ? "" : "Descrição " + e.DescriptionSource.ToLowerInvariant() + ", salva só no catálogo";
        blocked.Checked = e?.LaunchBlocked ?? false; blocked.Enabled = e is not null && !store.ReadOnly; interpreter.Text = e is null ? "" : "Interpretador: " + (string.IsNullOrEmpty(e.Interpreter) ? "não escolhido" : e.Interpreter);
        if (e is null) details.Text = "Cadastre um arquivo .ahk ou atualize a descoberta.";
        else
        {
            var instances = snapshot.For(e.Path).Select(p => $"PID {p.Pid} | criação UTC {DateTime.FromFileTimeUtc(p.Created):yyyy-MM-dd HH:mm:ss}\r\n{p.Executable}");
            var sources = discovery.Sources.Where(s => Paths.Equal(s.Path, e.Path)).Select(s => s.Source + (s.Elevated ? " | elevação configurada" : ""));
            details.Text = "Início automático externo\r\n" + (sources.Any() ? string.Join("\r\n", sources) : "Não identificado no escopo consultado.") + "\r\n\r\nInstâncias verificáveis\r\n" + (instances.Any() ? string.Join("\r\n", instances) : "Nenhuma. " + (snapshot.Unknown ? "Execução ainda incerta." : "")) + "\r\n\r\nO bloqueio local não altera atalhos, tarefas nem relançamento pelo supervisor.";
        }
        foreach (var pair in actionButtons)
        {
            var allowed = e is null ? Result.Fail("Selecione um script.") : Policy.Check(e, snapshot, pair.Key, fixture || File.Exists(e.Path)); pair.Value.Enabled = !busy && !fixture && allowed.Success; tips.SetToolTip(pair.Value, allowed.Message);
        }
        selecting = false;
    }
    void Coverage()
    {
        var unknown = snapshot.Processes.Count(p => p.Script is null); coverage.Text = $"Escopo: Documents\\AutoHotkey, cadastro manual e fontes Startup/Run/tarefas acessíveis.\r\n{snapshot.Note} AHK sem script identificado: {unknown}. Lacunas de descoberta: {discovery.Limits.Count}.";
        tips.SetToolTip(coverage, string.Join("\n", discovery.Limits) + "\n" + string.Join("\n", snapshot.Processes.Where(p => p.Script is null).Select(p => $"PID {p.Pid}: elevado ou inacessível, sem script atribuído.")));
    }
    void Busy(bool value, string message)
    {
        busy = value; refresh.Enabled = register.Enabled = search.Enabled = interaction.Enabled = !value; status.Text = message; UseWaitCursor = value; if (!value) SelectEntry();
    }
    async Task RefreshCatalog()
    {
        if (busy) return; var selected = SelectedId; Busy(true, "Consultando scripts, fontes externas e processos...");
        try
        {
            discovery = await Discovery.Find(); snapshot = await processes.Snapshot(CancellationToken.None);
            var next = new Catalog { Scripts = catalog.Scripts.Select(e => e.Copy()).ToList() };
            foreach (var file in discovery.Paths) if (!next.Scripts.Any(e => Paths.Equal(e.Path, file))) next.Scripts.Add(new() { Path = file, Description = Discovery.Infer(file), Interpreter = Discovery.DefaultInterpreter() });
            if (!store.ReadOnly) { store.Save(next); catalog = next; } else catalog = next;
            Populate(selected); Coverage(); Busy(false, store.ReadOnly ? store.Notice : $"Atualizado. {catalog.Scripts.Count} scripts. Descrições inferidas precisam de conferência.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { Busy(false, "Não foi possível salvar a descoberta. " + ex.Message); }
    }
    void Mutate(ScriptEntry current, Action<ScriptEntry> change)
    {
        if (busy || store.ReadOnly) return;
        var next = new Catalog { Scripts = catalog.Scripts.Select(e => e.Copy()).ToList() }; change(next.Scripts.Single(e => e.Id == current.Id));
        try { store.Save(next); catalog = next; Populate(current.Id); status.Text = "Metadados salvos. O script e a inicialização externa não foram alterados."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { status.Text = "Metadados não salvos. " + ex.Message; SelectEntry(); }
    }
    void SaveDescription() { if (Selected is { } e) { var text = description.Text; Mutate(e, n => { n.Description = text; n.DescriptionSource = "Informada"; }); } }
    void Register()
    {
        using var dialog = new OpenFileDialog { Filter = "Scripts AutoHotkey (*.ahk)|*.ahk", CheckFileExists = true, Title = "Cadastrar script sem executá-lo" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var file = Paths.Normalize(dialog.FileName); var existing = catalog.Scripts.FirstOrDefault(e => Paths.Equal(e.Path, file)); if (existing is not null) { Populate(existing.Id); status.Text = "Este arquivo já está cadastrado."; return; }
            var entry = new ScriptEntry { Path = file, Description = Discovery.Infer(file), Interpreter = Discovery.DefaultInterpreter() }; var next = new Catalog { Scripts = catalog.Scripts.Select(e => e.Copy()).Append(entry).ToList() }; store.Save(next); catalog = next; search.Clear(); Populate(entry.Id); status.Text = "Script cadastrado. Nenhum processo iniciado.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { status.Text = "Cadastro recusado. " + ex.Message; }
    }
    void ChooseInterpreter()
    {
        if (Selected is not { } entry) return;
        using var dialog = new OpenFileDialog { Filter = "Interpretador AutoHotkey (*.exe)|AutoHotkey*.exe", Title = "Escolher interpretador instalado" }; if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (!Commands.IsInterpreter(dialog.FileName)) { status.Text = "Escolha um executável AutoHotkey."; return; } Mutate(entry, n => n.Interpreter = dialog.FileName);
    }
    void Edit()
    {
        if (Selected is not { } entry || !File.Exists(entry.Path)) { status.Text = "Arquivo não encontrado."; return; }
        try { var info = new ProcessStartInfo(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe")) { UseShellExecute = false }; info.ArgumentList.Add(Paths.Normalize(entry.Path)); using var editor = Process.Start(info); status.Text = "Arquivo aberto no Bloco de Notas. Salvar no editor altera o script."; }
        catch (Exception ex) when (ex is IOException or ArgumentException or System.ComponentModel.Win32Exception) { status.Text = "Não foi possível abrir o editor. Copie o caminho para usar outro editor."; }
    }
    async Task Act(ScriptAction action)
    {
        if (busy || Selected is not { } current) return;
        var entry = current.Copy(); bool confirm = action == ScriptAction.Start;
        if (!confirm)
        {
            string message = action == ScriptAction.Recycle ? $"Enviar {entry.Name} à lixeira?\r\n{entry.Path}\r\n\r\nO registro e as fontes externas de inicialização serão mantidos." : $"{(action == ScriptAction.Stop ? "Encerrar" : "Reiniciar")} a instância verificável de {entry.Name}? O script será interrompido.";
            if (snapshot.SupervisorPath is not null && Paths.ChildOf(entry.Path, snapshot.SupervisorPath) && snapshot.Processes.Any(p => Paths.Equal(p.Script, snapshot.SupervisorPath))) message += "\r\nO supervisor pode relançar este filho em até 60 segundos.";
            if (Paths.Equal(entry.Path, snapshot.SupervisorPath)) message += "\r\nEncerrar o supervisor deixa os filhos vivos.";
            confirm = MessageBox.Show(this, message, action == ScriptAction.Recycle ? "Enviar à lixeira" : "Confirmar ação", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.OK;
        }
        if (!confirm) { status.Text = "Cancelado. Nenhuma alteração feita."; return; }
        Busy(true, "Verificando identidade e executando a ação...");
        try
        {
            entry.Path = Paths.Normalize(entry.Path); var result = await actions.Execute(entry, action, confirm); store.Log(action.ToString(), result.Success); snapshot = await processes.Snapshot(CancellationToken.None); Populate(entry.Id); Coverage(); Busy(false, result.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { Busy(false, "Ação recusada. " + ex.Message); }
    }
    protected override void Dispose(bool disposing) { if (disposing) tips.Dispose(); base.Dispose(disposing); }
}
