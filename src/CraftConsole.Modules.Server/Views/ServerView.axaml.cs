using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CraftConsole.Modules.Server.ViewModels;

namespace CraftConsole.Modules.Server.Views;

public partial class ServerView : UserControl
{
    public ServerView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is not ServerViewModel vm) return;

        vm.BrowseJarRequested = async () =>
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null) return null;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select Server JAR",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Java Archive") { Patterns = ["*.jar"] },
                    new FilePickerFileType("All Files")    { Patterns = ["*"] },
                ]
            });

            return files.Count > 0 ? files[0].Path.LocalPath : null;
        };

    }
}
