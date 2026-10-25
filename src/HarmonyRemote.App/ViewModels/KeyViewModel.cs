using Avalonia.Media;
using HarmonyRemote.Core;
using HarmonyRemote.Core.Model;

namespace HarmonyRemote.App.ViewModels;

/// <summary>One remote key bound to a hub function. Press/Release come from pointer down/up (see <c>RemoteKey</c>).</summary>
public sealed class KeyViewModel
{
    private readonly RemoteViewModel _owner;
    private HeldButton? _held;

    public KeyViewModel(RemoteViewModel owner, HubFunction function, string? shortLabel = null)
    {
        _owner = owner;
        Function = function;
        Label = FunctionLabels.Humanize(function.Label);
        ShortLabel = shortLabel ?? Label;
        Icon = IconCatalog.For(function.Name);
    }

    public HubFunction Function { get; }

    /// <summary>The hub's label, used for tooltips and screen readers.</summary>
    public string Label { get; }

    /// <summary>What's printed on the key when there's no glyph.</summary>
    public string ShortLabel { get; }

    public Geometry? Icon { get; }

    public bool HasIcon => Icon is not null;

    /// <summary>Styled in the power colour on device pages.</summary>
    public bool IsPowerOff => Function.Name.Equals("PowerOff", StringComparison.OrdinalIgnoreCase);

    public void Press()
    {
        if (_held is null)
            _held = _owner.TryPress(Function);
    }

    public void Release()
    {
        var held = Interlocked.Exchange(ref _held, null);
        if (held is not null)
            _ = held.ReleaseAsync().AsTask();
    }

    public void Tap() => _ = _owner.TapAsync(Function);
}

public sealed class ControlGroupViewModel(string title, IReadOnlyList<KeyViewModel> keys)
{
    public string Title { get; } = title;

    public IReadOnlyList<KeyViewModel> Keys { get; } = keys;
}
