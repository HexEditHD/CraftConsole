using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CraftConsole.Modules.Console.ViewModels;

public partial class PlayerAvatarItem : ObservableObject
{
    public string Username { get; }

    [ObservableProperty]
    private Bitmap? _avatar;

    public PlayerAvatarItem(string username) => Username = username;
}
