using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace HarmonyRemote.App.ViewModels;

public sealed partial class ActivityItemViewModel : ObservableObject
{
    private readonly RemoteViewModel _owner;

    public ActivityItemViewModel(RemoteViewModel owner, string id, string label)
    {
        _owner = owner;
        Id = id;
        Label = label;
    }

    public string Id { get; }

    public string Label { get; }

    [ObservableProperty]
    private bool _isCurrent;

    [ObservableProperty]
    private bool _isPending;

    [RelayCommand]
    private Task StartAsync() => _owner.StartActivityAsync(Id, Label);
}
