using HarmonyRemote.Core.Model;

namespace HarmonyRemote.Core.Tests;

public class DeviceTests
{
    private static HubConfig Config() =>
        HubConfig.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "config-small.json")));

    [Fact]
    public void Devices_are_listed_by_name_and_share_the_control_lookup_with_activities()
    {
        var config = Config();
        Assert.Equal(["Amp", "Player"], config.OrderedDevices.Select(d => d.Label));

        var amp = config.Device("11")!;
        Assert.Equal("Acme A-200", amp.Description);
        Assert.NotNull(amp.Group("Power"));
        Assert.Equal("{\"command\":\"InputHdmi1\",\"type\":\"IRCommand\",\"deviceId\":\"11\"}", amp.Function("InputHdmi1")!.Action);
        Assert.Equal(["PowerToggle", "InputHdmi1", "AnalogBalanced"], amp.AllFunctions().Select(f => f.Name));
    }

    [Theory]
    [InlineData("Volume Down", "Volume Down")]   // already readable
    [InlineData("AnalogBalanced", "Analog Balanced")]
    [InlineData("InputHdmi1", "Input HDMI 1")]
    [InlineData("InputHDMI1", "Input HDMI 1")]
    [InlineData("InputPc-Usb", "Input PC-USB")]
    [InlineData("Am/Fm", "AM/FM")]
    [InlineData("PrimeVideo", "Prime Video")]
    [InlineData("InputAnalog7.1", "Input Analog 7.1")]
    [InlineData("X", "X")]
    public void Raw_command_names_become_readable(string raw, string expected) =>
        Assert.Equal(expected, FunctionLabels.Humanize(raw));

    [Theory]
    [InlineData("InputHdmi1", "HDMI 1")]
    [InlineData("InputAES/EBU", "AES/EBU")]
    [InlineData("InputOptical2", "Optical 2")]
    [InlineData("InputNext", "Next")]
    [InlineData("Input3", "Input 3")]
    public void Input_keys_drop_the_prefix_unless_only_a_number_is_left(string name, string expected) =>
        Assert.Equal(expected, FunctionLabels.InputKeyLabel(new HubFunction(name, name, "{}")));

    [Fact]
    public void Only_named_inputs_count_as_inputs()
    {
        Assert.True(FunctionLabels.IsInput(new HubFunction("InputTv", "InputTv", "{}")));
        Assert.False(FunctionLabels.IsInput(new HubFunction("Input", "Input", "{}")));
        Assert.False(FunctionLabels.IsInput(new HubFunction("Info", "Info", "{}")));
    }
}
