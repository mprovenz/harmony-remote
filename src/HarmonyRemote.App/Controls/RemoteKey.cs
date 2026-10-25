using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using HarmonyRemote.App.ViewModels;

namespace HarmonyRemote.App.Controls;

/// <summary>
/// A button that behaves like a physical remote key: pointer down = press (then hold heartbeats), pointer up or
/// capture loss = release. Enter/Space send one tap. Styled as a regular <see cref="Button"/>.
/// </summary>
public class RemoteKey : Button
{
    public static readonly StyledProperty<KeyViewModel?> TargetProperty =
        AvaloniaProperty.Register<RemoteKey, KeyViewModel?>(nameof(Target));

    private KeyViewModel? _down;

    protected override Type StyleKeyOverride => typeof(Button);

    public KeyViewModel? Target
    {
        get => GetValue(TargetProperty);
        set => SetValue(TargetProperty, value);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_down is null && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && Target is { } key)
        {
            _down = key;
            key.Press();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        EndPress();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndPress();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space && Target is { } key)
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Alt))
                key.Tap();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        EndPress();
        base.OnDetachedFromVisualTree(e);
    }

    private void EndPress()
    {
        var key = _down;
        _down = null;
        key?.Release();
    }
}
