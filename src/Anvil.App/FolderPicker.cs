using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace Anvil.App;

/// <summary>
/// Der Ordnerdialog des Systems (Portal unter KDE/Wayland). Er braucht das Hauptfenster als
/// Elternteil und lebt deshalb in der App; das ViewModel bekommt nur den Delegaten.
/// </summary>
public static class FolderPicker
{
    public static async Task<string?> PickAsync(string title)
    {
        if (Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
        {
            return null;
        }

        var folders = await window.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }
}
