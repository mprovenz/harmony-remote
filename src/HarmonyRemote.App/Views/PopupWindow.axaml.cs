using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using HarmonyRemote.App.Settings;
using HarmonyRemote.App.ViewModels;

namespace HarmonyRemote.App.Views;

/// <summary>
/// The borderless remote that opens from the tray. Linux trays don't report where the icon is, so the window is
/// anchored to a corner of the work area, next to the panel that holds the tray where that can be found.
/// </summary>
public partial class PopupWindow : Window
{
    /// <summary>A tray click that arrives this soon after we hid on focus loss is the same click; don't reopen.</summary>
    private const long ReopenGuardMs = 350;
    private const int EdgeMargin = 8;
    private long _hiddenByDeactivateAt = long.MinValue / 2;

    public PopupWindow()
    {
        InitializeComponent();
        Deactivated += (_, _) =>
        {
            if (!IsVisible)
                return;
            _hiddenByDeactivateAt = Environment.TickCount64;
            Hide();
        };
        // The height follows the content; keep the anchored edge in place when it changes.
        SizeChanged += (_, _) => PlaceAtAnchor();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is RemoteViewModel vm)
                vm.AnchorChanged += PlaceAtAnchor;
        };
    }

    public void Toggle()
    {
        if (IsVisible)
        {
            Hide();
            return;
        }
        if (Environment.TickCount64 - _hiddenByDeactivateAt < ReopenGuardMs)
            return;
        ShowAnchored();
    }

    public void ShowAnchored()
    {
        Show();
        PlaceAtAnchor();
        Activate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    private void PlaceAtAnchor()
    {
        if (!IsVisible || Screens.Primary is not { } screen || DataContext is not RemoteViewModel vm)
            return;
        var area = screen.WorkingArea;
        double scale = screen.Scaling;
        // Never taller than the work area; the body scrolls instead (a device page with all its keys can be long).
        double maxHeight = area.Height / scale - 2 * EdgeMargin;
        if (Math.Abs(MaxHeight - maxHeight) > 0.5)
            MaxHeight = maxHeight;
        int w = (int)Math.Ceiling(Bounds.Width * scale);
        int h = (int)Math.Ceiling(Bounds.Height * scale);
        int m = (int)(EdgeMargin * scale);
        var anchor = vm.ResolvedAnchor;
        int x = anchor is PopupAnchor.TopLeft or PopupAnchor.BottomLeft ? area.X + m : area.Right - w - m;
        int y = anchor is PopupAnchor.TopLeft or PopupAnchor.TopRight ? area.Y + m : area.Bottom - h - m;
        // Under XWayland the move is applied asynchronously; don't read Position back immediately.
        Position = new PixelPoint(x, y);
    }
}
