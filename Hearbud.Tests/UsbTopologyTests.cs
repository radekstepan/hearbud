using Hearbud;
using Xunit;

namespace Hearbud.Tests;

public class UsbTopologyTests
{
    [Theory]
    [InlineData("{1}.USB\\VID_1532&PID_0531&MI_00\\8&38F8A13&0&0000",
                "USB\\VID_1532&PID_0531&MI_00\\8&38F8A13&0&0000")]
    [InlineData("USB\\VID_1A40&PID_0101\\6&13211795&0&1",
                "USB\\VID_1A40&PID_0101\\6&13211795&0&1")]
    [InlineData("{1}.USB\\VID_1532&PID_0531&MI_00\\8&38F8A13&0&0000#{6994ad04-93ef-11d0-a3cc-00a0c9223196}",
                "USB\\VID_1532&PID_0531&MI_00\\8&38F8A13&0&0000")]
    [InlineData("  USB\\VID_1A40&PID_0101\\6&13211795&0&1  ",
                "USB\\VID_1A40&PID_0101\\6&13211795&0&1")]
    public void NormalizeEndpointInstanceId_StripsMmDevicePrefixAndInterface(string raw, string expected)
    {
        Assert.Equal(expected, UsbTopologyProbe.NormalizeEndpointInstanceId(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeEndpointInstanceId_Empty_ReturnsNull(string? raw)
    {
        Assert.Null(UsbTopologyProbe.NormalizeEndpointInstanceId(raw));
    }

    [Fact]
    public void Check_NoEndpoint_ReturnsNullWithoutProbing()
    {
        Assert.Null(UsbTopologyProbe.Check(null, null));
        Assert.Null(UsbTopologyProbe.Check("", "Microphone"));
    }

    [Fact]
    public void FindSharedHub_MicAndMouseBehindSameExternalHub_ReturnsHub()
    {
        const string mic = @"USB\VID_1532&PID_0531\UC2047L";
        const string mouse = @"USB\VID_1BCF&PID_0005\7&1C434A90&0&1";
        const string hub = @"USB\VID_1A40&PID_0101\6&13211795&0&1";
        const string root = @"USB\ROOT_HUB30\5&ab65b47&0&0";
        var parents = new Dictionary<string, string?>
        {
            [mic] = hub,
            [mouse] = hub,
            [hub] = root,
            [root] = @"PCI\VEN_1022&DEV_43BB&SUBSYS_83E9103C&REV_02\4&1485E5A0&0&000A",
        };
        var services = new Dictionary<string, string?>
        {
            [mic] = "usbccgp",
            [mouse] = "usbccgp",
            [hub] = "USBHUB",
            [root] = "USBHUB3",
        };

        Assert.Equal(hub, UsbTopologyProbe.FindSharedHub(parents, services, mic, new[] { mouse }));
    }

    [Fact]
    public void FindSharedHub_OnlySharedRootHub_ReturnsNull()
    {
        const string mic = @"USB\VID_1532&PID_0531\UC2047L";
        const string mouse = @"USB\VID_1BCF&PID_0005\7&1C434A90&0&1";
        const string root = @"USB\ROOT_HUB30\5&ab65b47&0&0";
        var parents = new Dictionary<string, string?>
        {
            [mic] = root,
            [mouse] = root,
            [root] = @"PCI\VEN_1022&DEV_43BB",
        };
        var services = new Dictionary<string, string?>
        {
            [mic] = "usbccgp",
            [mouse] = "usbccgp",
            [root] = "USBHUB3",
        };

        Assert.Null(UsbTopologyProbe.FindSharedHub(parents, services, mic, new[] { mouse }));
    }

    [Fact]
    public void FindSharedHub_DifferentHubs_ReturnsNull()
    {
        const string mic = @"USB\VID_1532&PID_0531\UC2047L";
        const string mouse = @"USB\VID_1BCF&PID_0005\7&1C434A90&0&1";
        const string hubA = @"USB\VID_1A40&PID_0101\6&13211795&0&1";
        const string hubB = @"USB\VID_1A40&PID_0101\6&99887766&0&1";
        const string root = @"USB\ROOT_HUB30\5&ab65b47&0&0";
        var parents = new Dictionary<string, string?>
        {
            [mic] = hubA,
            [mouse] = hubB,
            [hubA] = root,
            [hubB] = root,
            [root] = @"PCI\VEN_1022&DEV_43BB",
        };
        var services = new Dictionary<string, string?>
        {
            [mic] = "usbccgp",
            [mouse] = "usbccgp",
            [hubA] = "USBHUB",
            [hubB] = "USBHUB",
            [root] = "USBHUB3",
        };

        Assert.Null(UsbTopologyProbe.FindSharedHub(parents, services, mic, new[] { mouse }));
    }
}
