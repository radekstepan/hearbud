// Guards the macOS PortAudio capture backend seam: construction, device
// enumeration, and the documented no-notifications behavior. Fully
// deterministic — enumeration swallows PortAudio failures (empty list) and
// init failures surface as InvalidOperationException per the seam contract,
// so these are safe headless on any OS, including hosts without the native
// PortAudio library. No timing asserts.

using System;
using Xunit;

namespace Hearbud.Tests
{
    public sealed class PortAudioCaptureTests
    {
        [Fact]
        public void Factory_Constructs()
        {
            IAudioCaptureFactory factory = new PortAudioCaptureFactory();
            Assert.NotNull(factory);
        }

        [Fact]
        public void MacAudioCapture_ExposesFactory()
        {
            Assert.NotNull(MacAudioCapture.Factory);
            Assert.IsType<PortAudioCaptureFactory>(MacAudioCapture.Factory);
        }

        [Fact]
        public void GetMicrophoneDevices_ReturnsNonNull_WithoutThrowing()
        {
            var devices = new PortAudioCaptureFactory().GetMicrophoneDevices();
            Assert.NotNull(devices);
        }

        [Fact]
        public void GetLoopbackDevices_ReturnsNonNull_WithoutThrowing()
        {
            var devices = new PortAudioCaptureFactory().GetLoopbackDevices();
            Assert.NotNull(devices);
        }

        [Fact]
        public void StartDeviceNotifications_ReturnsNull()
        {
            Assert.Null(new PortAudioCaptureFactory().StartDeviceNotifications(() => { }));
        }

        [Fact]
        public void OpenLoopback_UnknownId_ThrowsInvalidOperation()
        {
            var factory = new PortAudioCaptureFactory();
            Assert.Throws<InvalidOperationException>(() => factory.OpenLoopback(
                "no-such-device", 48000, 2, _ => { }, _ => { }));
        }

        [Theory]
        [InlineData("BlackHole 2ch", true)]
        [InlineData("blackhole 16ch", true)]
        [InlineData("Soundflower (2ch)", true)]
        [InlineData("Loopback Audio", true)]
        [InlineData("MacBook Pro Microphone", false)]
        [InlineData("WH-1000XM4", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsVirtualLoopbackDevice_ClassifiesNames(string? name, bool expected)
        {
            Assert.Equal(expected, PortAudioCaptureFactory.IsVirtualLoopbackDevice(name));
        }

        [Fact]
        public void PreferVirtualDevices_PutsVirtualFirst_AndFlagsDefault()
        {
            var inputs = new[]
            {
                new AudioDeviceInfo("mic", "MacBook Pro Microphone", true),
                new AudioDeviceInfo("bh", "BlackHole 2ch", false),
            };

            var ordered = PortAudioCaptureFactory.PreferVirtualDevices(inputs);

            Assert.Equal(2, ordered.Count);
            Assert.Equal("bh", ordered[0].Id);
            Assert.True(ordered[0].IsDefault);
            Assert.Equal("mic", ordered[1].Id);
            Assert.False(ordered[1].IsDefault);
        }

        [Fact]
        public void PreferVirtualDevices_WithoutVirtual_ClearsDefault()
        {
            var inputs = new[]
            {
                new AudioDeviceInfo("mic", "MacBook Pro Microphone", true),
            };

            var ordered = PortAudioCaptureFactory.PreferVirtualDevices(inputs);

            Assert.Single(ordered);
            Assert.False(ordered[0].IsDefault);
        }
    }
}
