using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Bezoro.AhkManager.Presentation;

public static class Program
{
    public static Application CreateApplication()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Bezoro.AhkManager;component/Theme.xaml", UriKind.Relative) });
        return app;
    }
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Any(a => a.StartsWith("--brush-helper", StringComparison.Ordinal))) return BrushTaskHelper.Run(args);
        // One-time migration returns before Application, UiSession, TrayHost or any timer exists.
        if (args.Any(a => a.StartsWith("--normal-cutover", StringComparison.Ordinal))) return RunNormalCutover(args);
        if (!TryLaunch(args, out var options)) return 2;
        if (options.Fixture) return RunApplication(options, null, null, null);
        UserEffectOwner? owner = null; CatalogStore? store = null; ExistingInstanceServer? server = null;
        try
        {
            owner = new UserEffectOwner();
            return RouteOwnership(owner,
                () =>
                {
                    var scope = ExistingScope.Current(options.Data);
                    store = new CatalogStore(options.Data);
                    server = new ExistingInstanceServer(scope);
                    return RunApplication(options, owner, store, server);
                },
                () =>
                {
                    var reply = ExistingInstanceClient.Request(ExistingScope.Current(options.Data), options.Tray ? ExistingOperation.Dedupe : ExistingOperation.ShowExisting).GetAwaiter().GetResult();
                    if (!reply.Success && !options.Tray) MessageBox.Show("Nao foi possivel reabrir o gerenciador existente (" + reply.Status + "). Aguarde a inicializacao ou a saida e tente novamente.", "AHK Manager", MessageBoxButton.OK, MessageBoxImage.Information);
                    return reply.Success ? 0 : 1;
                });
        }
        catch (Exception ex)
        {
            if (!options.Tray) MessageBox.Show("Nao foi possivel abrir o gerenciador. " + ex.Message, "AHK Manager", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
        finally
        {
            server?.BeginClosing();
            try { if (server is not null) server.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            finally { try { store?.Dispose(); } finally { owner?.Dispose(); } }
        }
    }
    public static int RouteOwnership(IEffectOwner owner, Func<int> primary, Func<int> secondary)
        => !owner.Trusted ? 1 : owner.Held ? primary() : secondary();
    public sealed record LaunchOptions(string Data, string? Render, string Scenario, bool Small, double Scale, bool Tray, bool Login, bool Fixture);
    public static bool TryLaunch(string[] args, out LaunchOptions options)
    {
        options = null!;
        try
        {
            string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bezoro", "AhkManager");
            string? render = null; string scenario = "normal"; bool small = false; double scale = 1; bool explicitData = false; bool tray = false; bool login = false; bool fixtureUi = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--data-dir" && i + 1 < args.Length) { var supplied = args[++i]; ExistingScope.ValidateDirectorySpelling(supplied); data = Path.GetFullPath(supplied); explicitData = true; }
                else if (args[i] == "--render-fixture" && i + 1 < args.Length) render = Path.GetFullPath(args[++i]);
                else if (args[i] == "--fixture-ui") fixtureUi = true;
                else if (args[i] == "--tray") tray = true;
                else if (args[i] == "--login") { if (login) return false; login = true; }
                else if (args[i] == "--render-small") small = true;
                else if (args[i] == "--scenario" && i + 1 < args.Length) scenario = args[++i];
                else if (args[i] == "--scale" && i + 1 < args.Length && double.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)) scale = parsed;
                else return false;
            }
            bool fixtureMode = render is not null || fixtureUi;
            if (!ValidLoginOrigin(login, tray, fixtureMode, explicitData)) return false;
            if (fixtureMode && (!explicitData || tray) || fixtureUi && render is not null || !fixtureMode && (small || scale != 1 || scenario != "normal") || scale is not (1 or 1.5 or 2)) return false;
            options = new(data, render, scenario, small, scale, tray, login, fixtureMode); return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return false; }
    }
    static int RunApplication(LaunchOptions options, UserEffectOwner? realOwner, CatalogStore? realStore, ExistingInstanceServer? server)
    {
        string data = options.Data; string? render = options.Render; string scenario = options.Scenario;
        bool small = options.Small, tray = options.Tray, login = options.Login, fixtureMode = options.Fixture; double scale = options.Scale;
        // A fixture never opens the requested directory's existing catalog, even if args point at a real profile.
        if (fixtureMode) data = Path.Combine(data, "fixture-" + Guid.NewGuid().ToString("N"));
        var app = CreateApplication(); int exit = 0; CatalogStore? lifetimeStore = null; TrayHost? lifetimeTray = null;
        app.Exit += (_, _) => { server?.BeginClosing(); lifetimeTray?.Dispose(); if (fixtureMode) lifetimeStore?.Dispose(); };
        app.Startup += async (_, _) =>
        {
            try
            {
                var store = realStore ?? new CatalogStore(data); lifetimeStore = store;
                if (fixtureMode)
                {
                    var fixture = FixtureFactory.Create(store, data, scenario);
                    await fixture.Session.Refresh();
                    if (scenario is "uac-pending" or "boot-recovery") { fixture.Interaction.Confirmed = true; await fixture.Session.Act(ScriptAction.Start); }
                    if (scenario == "boot-recovery") { fixture.Counter!.Value = 10; await fixture.Session.VerifyRequest(); }
                    var window = new MainWindow(fixture.Session);
                    if (render is not null)
                    {
                        Render(window, render, small ? 1000 : 1220, small ? 720 : 800, scale);
                        if (scenario == "boot-recovery") { window.ShowRequestDetails(); Render(window, render, small ? 1000 : 1220, small ? 720 : 800, scale); }
                        window.Close(); app.Shutdown(0); return;
                    }
                    window.Title = "AHK Manager - DEMONSTRACAO ISOLADA (efeitos ficticios)";
                    window.Closed += (_, _) => app.Shutdown(); window.Show(); return;
                }
                var owner = realOwner ?? throw new InvalidOperationException("Owner primario obrigatorio.");
                var interaction = new DesktopInteraction();
                var authority = RequestLedger.OpenReal(owner, store);
                var session = new UiSession(store, new WindowsProcesses(), new WindowsRecycler(), interaction, () => Bezoro.AhkManager.Discovery.Find(), owner, new SystemClock(), authority, new WindowsElevationDispatcher(), brush: new BrushTaskControls(new WindowsBrushTask(), new BrushTaskHelper(), new BrushOperationStore(store.DirectoryPath)));
                var main = new MainWindow(session) { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/AppIcon.png")) };
                interaction.Owner = main;
                await session.Refresh();
                await session.PrepareLogin(login);
                lifetimeTray = new TrayHost(app, main, () => server?.BeginClosing());
                if (!tray) main.Show();
                await session.Reconcile();
                if (server is not null) server.MarkReady(token => main.Dispatcher.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested || server.Lifecycle != ExistingLifecycle.Ready || session.ShuttingDown) return ExistingStatus.Closing;
                    return ExistingWindowDisplay.Show(new WpfExistingWindow(main), token);
                }, System.Windows.Threading.DispatcherPriority.Normal, token).Task);
            }
            catch (Exception ex)
            {
                exit = 1;
                if (!fixtureMode) MessageBox.Show("Não foi possível abrir o gerenciador. " + ex.Message, "AHK Manager", MessageBoxButton.OK, MessageBoxImage.Error);
                app.Shutdown(1);
            }
        };
        var result = app.Run(); return exit == 0 ? result : exit;
    }
    public static bool ValidLoginOrigin(bool login, bool tray, bool fixture, bool explicitData) => !login || tray && !fixture && !explicitData;
    public static int RunNormalCutover(string[] args)
    {
        if (args.Length != 4 || args[0] is not ("--normal-cutover" or "--normal-cutover-resume" or "--normal-cutover-preflight") || args[2] != "--result") return 2;
        Result result;
        try
        {
            var manifest = NormalCutoverCoordinator.ReadManifest(Path.GetFullPath(args[1]));
            if (manifest.Fixture) throw new InvalidDataException("CLI real recusa manifesto fixture.");
            NormalCutoverCoordinator.ValidateManifest(manifest);
            using var owner = new UserEffectOwner();
            // The adapter checks user/integrity/Manager/games before even opening a writable profile.
            var effects = new NormalMigrationEffects(new WindowsProcesses());
            var gate = effects.Check(manifest, null);
            // Recovery's file preconditions differ, and are checked against the journal by the coordinator.
            if (!gate.Success && args[0] != "--normal-cutover-resume") result = gate;
            else
            {
                var context = NormalMigrationEffects.CheckContext(manifest);
                if (!context.Success) result = context;
                else if (!File.Exists(Path.Combine(manifest.ProfileDirectory, "catalog.json")) || !File.Exists(Path.Combine(manifest.ProfileDirectory, "launch-requests.json")) || !File.Exists(Path.Combine(manifest.ProfileDirectory, "launch-authority.anchor.json"))) result = Result.Fail("Primário/autoridade existente obrigatório; nenhuma inicialização real implícita.");
                else
                {
                    using var store = new CatalogStore(manifest.ProfileDirectory);
                    var ledger = RequestLedger.OpenReal(owner, store);
                    result = NormalCutoverCoordinator.Execute(store, manifest, owner, ledger, new SystemClock(), effects, args[0] == "--normal-cutover-resume", args[0] == "--normal-cutover-preflight", CancellationToken.None).GetAwaiter().GetResult();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or System.Text.Json.JsonException) { result = Result.Fail(ex.Message); }
        try
        {
            var output = Path.GetFullPath(args[3]); NormalMigrationEffects.SafePath(output);
            if (!output.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || File.Exists(output)) return 2;
            using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            file.Write(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(result, MigrationJson.Default.Result)); file.Flush(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException) { return 1; }
        return result.Success ? 0 : 1;
    }
    public static void Render(MainWindow window, string path, double width, double height, double scale)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        var image = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen()) drawing.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
        image.Render(background);
        image.Render(content); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var output = File.Create(path); encoder.Save(output);
    }
}
