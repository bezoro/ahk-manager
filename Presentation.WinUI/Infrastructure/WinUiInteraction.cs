using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using Bezoro.AhkManager.WinUI.Resources;

namespace Bezoro.AhkManager.WinUI.Infrastructure;

internal sealed class WinUiInteraction(Window window, FrameworkElement root)
{
    public async Task<bool> ConfirmAsync(string title, string message, string? accept = null)
    {
        var dialog = new ContentDialog { XamlRoot = root.XamlRoot, Title = title, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, PrimaryButtonText = accept ?? Text.Get("Continue"), CloseButtonText = Text.Get("Cancel"), DefaultButton = ContentDialogButton.Close };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
    public async Task<IReadOnlyList<string>> PickScriptsAsync()
    {
        var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".ahk");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        var files = await picker.PickMultipleFilesAsync(); return files.Select(file => file.Path).ToArray();
    }
    public async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        return (await picker.PickSingleFolderAsync())?.Path;
    }
    public async Task<string?> PickFileAsync(string extension)
    {
        var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        return (await picker.PickSingleFileAsync())?.Path;
    }
    public async Task<string?> PromptAsync(string title, string initial = "")
    {
        var input = new TextBox { Text = initial, MinWidth = 300, MaxLength = 160 };
        var dialog = new ContentDialog { XamlRoot = root.XamlRoot, Title = title, Content = input, PrimaryButtonText = Text.Get("Save"), CloseButtonText = Text.Get("Cancel"), DefaultButton = ContentDialogButton.Primary };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? input.Text : null;
    }
}
