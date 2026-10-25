using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using HarmonyRemote.App.Settings;
using HarmonyRemote.App.Tray;
using HarmonyRemote.App.ViewModels;
using HarmonyRemote.App.Views;

namespace HarmonyRemote.App;

public partial class App : Application
{
    private TrayController? _tray;
    private RemoteViewModel? _vm;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var store = SettingsStore.Load();
            _vm = new RemoteViewModel(store);
            var popup = new PopupWindow { DataContext = _vm };
            _tray = new TrayController(this, _vm, popup, () => desktop.Shutdown());
            desktop.Exit += (_, _) =>
            {
                _tray?.Dispose();
                // Close the hub socket cleanly; don't hang exit on a dead network.
                _vm?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
            };

            _vm.Start();
            Program.Instance?.Listen(() => Dispatcher.UIThread.Post(popup.ShowAnchored));
            if (Program.ShowOnStart || !_vm.HasHub)
                popup.ShowAnchored();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
