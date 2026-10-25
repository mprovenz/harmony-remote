using CommunityToolkit.Mvvm.Input;
using HarmonyRemote.Core.Model;

namespace HarmonyRemote.App.ViewModels;

/// <summary>One row of the device list: opens that device's page.</summary>
public sealed partial class DeviceItemViewModel(RemoteViewModel owner, HubDevice device)
{
    public string Id { get; } = device.Id;

    public string Label { get; } = device.Label;

    /// <summary>Manufacturer and model, e.g. "Sony KD-55X80K".</summary>
    public string Description { get; } = device.Description;

    [RelayCommand]
    private void Open() => owner.OpenDevice(this);
}
