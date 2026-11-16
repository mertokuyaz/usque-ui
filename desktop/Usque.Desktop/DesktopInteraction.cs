using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;

namespace Usque.Desktop;

// OS-facing presentation effects live here so commands can be tested without
// opening dialogs, changing the clipboard, or launching a browser.
public interface IDesktopInteraction
{
    MessageBoxResult Show(string text, string title, MessageBoxButton buttons, MessageBoxImage image);
    string? SelectProfile(string title);
    string? SelectReport(string title, string fileName);
    void CopyText(string text);
    void OpenTerms();
}

internal sealed class DesktopInteraction : IDesktopInteraction
{
    public MessageBoxResult Show(string text, string title, MessageBoxButton buttons, MessageBoxImage image) =>
        Application.Current.MainWindow is { IsVisible: true } owner
            ? MessageBox.Show(owner, text, title, buttons, image)
            : MessageBox.Show(text, title, buttons, image);

    public string? SelectProfile(string title)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = AppLocalization.Instance["file.profile"], CheckFileExists = true };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
    public string? SelectReport(string title, string fileName)
    {
        var dialog = new SaveFileDialog { Title = title, Filter = AppLocalization.Instance["file.report"], FileName = fileName };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
    public void CopyText(string text) => Clipboard.SetText(text);
    public void OpenTerms() => Process.Start(new ProcessStartInfo("https://www.cloudflare.com/application/terms/") { UseShellExecute = true });
}
