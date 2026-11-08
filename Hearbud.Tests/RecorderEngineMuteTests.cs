using System;
using System.Reflection;
using Xunit;

namespace Hearbud.Tests;

public class RecorderEngineMuteTests
{
    private static readonly Type EngineType = typeof(RecorderEngine);

    private static RecorderEngine NewEngine() => new();
    private static void InvokeEmit(RecorderEngine e, string name, float[] a, float[] b, int n)
        => EngineType.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(e, new object[] { a, b, n });
    private static float[] MixBuf(RecorderEngine e)
        => (float[])EngineType.GetField("_mixBufF", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(e)!;

    private static (float[] loop, float[] mic) Frames(int n = 16)
    {
        var loop = new float[n]; var mic = new float[n];
        for (int i = 0; i < n; i++) { loop[i] = 0.25f; mic[i] = 0.125f; }
        return (loop, mic);
    }

    [Fact]
    public void MuteFlags_DefaultFalse_And_RoundTrip()
    {
        var e = NewEngine();
        Assert.False(e.MicMuted); Assert.False(e.SystemMuted);
        e.MicMuted = true; e.SystemMuted = true;
        Assert.True(e.MicMuted); Assert.True(e.SystemMuted);
        e.MicMuted = false; e.SystemMuted = false;
        Assert.False(e.MicMuted); Assert.False(e.SystemMuted);
    }

    [Fact]
    public void EmitFixedMix_MicMuted_EqualsLoopOnly()
    {
        var e = NewEngine();
        e.MicGain = 1.0; e.LoopGain = 1.0; e.MicMuted = true;
        var (loop, mic) = Frames();
        InvokeEmit(e, "EmitFixedMix", loop, mic, loop.Length);
        var mix = MixBuf(e);
        for (int i = 0; i < loop.Length; i++) Assert.Equal(loop[i], mix[i]);
    }

    [Fact]
    public void EmitFixedMix_SystemMuted_EqualsMicOnly()
    {
        var e = NewEngine();
        e.MicGain = 1.0; e.LoopGain = 1.0; e.SystemMuted = true;
        var (loop, mic) = Frames();
        InvokeEmit(e, "EmitFixedMix", loop, mic, loop.Length);
        var mix = MixBuf(e);
        for (int i = 0; i < mic.Length; i++) Assert.Equal(mic[i], mix[i]);
    }

    [Fact]
    public void EmitFixedMix_BothMuted_IsSilence()
    {
        var e = NewEngine();
        e.SetChannelMute(micMuted: true, systemMuted: true);
        var (loop, mic) = Frames();
        InvokeEmit(e, "EmitFixedMix", loop, mic, loop.Length);
        var mix = MixBuf(e);
        for (int i = 0; i < loop.Length; i++) Assert.Equal(0f, mix[i]);
    }

    [Fact]
    public void EmitMicOnlyFixed_MicMuted_IsSilence()
    {
        var e = NewEngine();
        e.MicGain = 1.0; e.MicMuted = true;
        var (_, mic) = Frames();
        EngineType.GetMethod("EmitMicOnlyFixed", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(e, new object[] { mic, mic.Length });
        var mix = MixBuf(e);
        for (int i = 0; i < mic.Length; i++) Assert.Equal(0f, mix[i]);
    }

    [Fact]
    public void ToggleMidStream_IsSafe_SecondFrameIsLoopOnly()
    {
        var e = NewEngine();
        e.MicGain = 1.0; e.LoopGain = 1.0;
        var (loop, mic) = Frames();
        InvokeEmit(e, "EmitFixedMix", loop, mic, loop.Length);
        var first = (float[])MixBuf(e).Clone();
        for (int i = 0; i < loop.Length; i++) Assert.Equal(loop[i] + mic[i], first[i]);

        e.MicMuted = true;
        InvokeEmit(e, "EmitFixedMix", loop, mic, loop.Length);
        var second = (float[])MixBuf(e).Clone();
        for (int i = 0; i < loop.Length; i++) Assert.Equal(loop[i], second[i]);

        e.MicMuted = false;
        InvokeEmit(e, "EmitFixedMix", loop, mic, loop.Length);
        var third = (float[])MixBuf(e).Clone();
        for (int i = 0; i < loop.Length; i++) Assert.Equal(loop[i] + mic[i], third[i]);
    }
}
