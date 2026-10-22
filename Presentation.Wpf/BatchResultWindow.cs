using System.Windows;
using System.Windows.Controls;
namespace Bezoro.AhkManager.Presentation;

public sealed class BatchResultWindow : Window
{
    public static bool Show(BatchResult result, Window? owner, bool exitChoice, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var dialog = new BatchResultWindow(result, exitChoice) { Owner = owner is { IsVisible: true } ? owner : null };
        using var registration = token.Register(() => dialog.Dispatcher.BeginInvoke(new Action(() => { if (dialog.IsVisible) dialog.Close(); })));
        return dialog.ShowDialog() == true && !token.IsCancellationRequested;
    }
    public BatchResultWindow(BatchResult result, bool exitChoice)
    {
        Title = exitChoice ? "Parada parcial ou não confirmada" : "Resultado da ação nesta sessão";
        Width = 660; Height = 520; MinWidth = 500; MinHeight = 350; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("BackgroundBrush"); Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextBrush");
        var grid = new Grid { Margin = new(24) }; grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = (result.AutomaticPaused ? UiSession.PausedText : "Pausa automática preservada; escolhas do próximo login preservadas") + (exitChoice ? ". Ao sair mesmo assim, scripts restantes podem continuar vivos." : ". Escolhas de inicialização preservadas."), TextWrapping = TextWrapping.Wrap, Margin = new(0,0,0,16) });
        var body = new StackPanel();
        foreach (var category in new[] { true, false })
        {
            body.Children.Add(new TextBlock { Text = category ? "Paradas confirmadas" : "Demais resultados e sobreviventes", FontWeight = FontWeights.SemiBold, Margin = new(0,8,0,6) });
            foreach (var item in result.Items.Where(i => i.Stopped == category)) body.Children.Add(new TextBlock { Text = item.Path + "\n" + item.Outcome, TextWrapping = TextWrapping.Wrap, Margin = new(0,0,0,12) });
        }
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetRow(scroll,1); grid.Children.Add(scroll);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0,16,0,0) };
        var keep = new Button { Content = exitChoice ? "Manter aberto" : "Fechar resultado", IsDefault = true, IsCancel = true }; keep.Click += (_,_) => DialogResult = false; buttons.Children.Add(keep);
        if (exitChoice) { var exit = new Button { Content = "Sair mesmo assim", Margin = new(8,0,0,0) }; exit.Click += (_,_) => DialogResult = true; buttons.Children.Add(exit); }
        Grid.SetRow(buttons,2); grid.Children.Add(buttons); Content = grid;
    }
}
public sealed class StopAllExitCoordinator(UiSession session, ExitCoordinator ordinary, Action suspend, Action resume, Func<BatchResult, CancellationToken, Task<bool>> choose)
{
    bool active;
    public bool Active => active;
    public async Task<bool> Request()
    {
        if (active || ordinary.ExitPending || session.Busy || session.ShuttingDown) return false;
        active = true; suspend(); bool exit = false;
        try
        {
            var report = await session.RunBatch(BatchAction.Stop, forExit: true, acknowledge: async (result, token) => { exit = result.Complete || await choose(result, token); });
            if (!report.Cancelled && exit && !session.ShuttingDown) { await ordinary.FinishConfirmed(); return true; }
            return false;
        }
        finally { active = false; if (!session.ShuttingDown) resume(); }
    }
}
