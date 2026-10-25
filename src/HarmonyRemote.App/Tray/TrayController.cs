using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using HarmonyRemote.App.ViewModels;
using HarmonyRemote.App.Views;

namespace HarmonyRemote.App.Tray;

/// <summary>
/// The tray icon (the app's only permanent presence). Left-click toggles the popup; right-click opens a menu with the
/// activities, power off, the remote, start-at-login and quit. On Linux this is a StatusNotifierItem over D-Bus.
/// </summary>
public sealed class TrayController : IDisposable
{
    private readonly Application _app;
    private readonly RemoteViewModel _vm;
    private readonly PopupWindow _popup;
    private readonly Action _quit;
    private readonly TrayIcon _tray;
    private readonly WindowIcon _iconOn = LoadIcon("tray-64.png");
    private readonly WindowIcon _iconOff = LoadIcon("tray-off-64.png");
    private string _menuSignature = "";

    public TrayController(Application app, RemoteViewModel vm, PopupWindow popup, Action quit)
    {
        _app = app;
        _vm = vm;
        _popup = popup;
        _quit = quit;
        // Exactly one TrayIcon for the app's lifetime: Avalonia names it org.kde.StatusNotifierItem-<pid>-<n>, and the
        // Flatpak manifest may only own the exact name ...-2-0. A second or re-created icon would silently not appear there.
        _tray = new TrayIcon { Icon = _iconOff, ToolTipText = "Harmony Remote", IsVisible = true };
        _tray.Clicked += (_, _) => _popup.Toggle();
        TrayIcon.SetIcons(app, [_tray]);

        _vm.PropertyChanged += OnViewModelChanged;
        _vm.Activities.CollectionChanged += OnActivitiesChanged;
        Refresh();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RemoteViewModel.IsConnected) or nameof(RemoteViewModel.IsOff) or nameof(RemoteViewModel.DisplayTitle)
            or nameof(RemoteViewModel.StartAtLogin))
            Refresh();
    }

    private void OnActivitiesChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        _tray.Icon = _vm.IsConnected ? _iconOn : _iconOff;
        _tray.ToolTipText = _vm.IsConnected ? $"Harmony: {_vm.DisplayTitle}" : "Harmony: not connected";

        // Rebuilding a D-Bus menu is cheap, but only do it when something visible changed.
        string signature = string.Join('|', _vm.Activities.Select(a => $"{a.Id}:{a.IsCurrent}")) + $"|{_vm.IsConnected}|{_vm.IsOff}|{_vm.StartAtLogin}";
        if (signature == _menuSignature)
            return;
        _menuSignature = signature;
        _tray.Menu = BuildMenu();
    }

    private NativeMenu BuildMenu()
    {
        var menu = new NativeMenu();
        foreach (var activity in _vm.Activities)
        {
            var item = new NativeMenuItem(activity.Label)
            {
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = activity.IsCurrent,
                IsEnabled = _vm.IsConnected,
            };
            var target = activity;
            item.Click += (_, _) => target.StartCommand.Execute(null);
            menu.Items.Add(item);
        }
        if (_vm.Activities.Count > 0)
        {
            var off = new NativeMenuItem("Turn everything off") { IsEnabled = _vm.IsConnected && !_vm.IsOff };
            off.Click += (_, _) => _vm.PowerOffCommand.Execute(null);
            menu.Items.Add(off);
            menu.Items.Add(new NativeMenuItemSeparator());
        }

        var open = new NativeMenuItem("Open remote");
        open.Click += (_, _) => _popup.ShowAnchored();
        menu.Items.Add(open);

        var autostart = new NativeMenuItem("Start when I log in")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _vm.StartAtLogin,
        };
        // The change may be async (portal) and may be refused; the menu is rebuilt from StartAtLogin either way.
        autostart.Click += (_, _) => _vm.StartAtLogin = !_vm.StartAtLogin;
        menu.Items.Add(autostart);

        menu.Items.Add(new NativeMenuItemSeparator());
        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) => _quit();
        menu.Items.Add(quit);
        return menu;
    }

    private static WindowIcon LoadIcon(string name) =>
        new(AssetLoader.Open(new Uri($"avares://harmony-remote/Assets/{name}")));

    public void Dispose()
    {
        _vm.PropertyChanged -= OnViewModelChanged;
        _vm.Activities.CollectionChanged -= OnActivitiesChanged;
        _tray.IsVisible = false;
        _tray.Dispose();
        TrayIcon.SetIcons(_app, []);
    }
}
