using System;
using System.Numerics;
using AcDream.App.Audio;
using AcDream.Core.Audio;
using Silk.NET.OpenAL;

namespace AcDream.App.Tests.Audio;

// PR #107 review (Erik): three claims settled against the retail decompile.
// These use the real engine — every gate under test lives behind "_al is not
// null", which the RecordingApi fakes elsewhere in this folder leave null on
// purpose to skip the native device. Where a device is genuinely unavailable
// (a CI runner with no OpenAL backend) each test is a no-op.
public sealed class OpenAlAudioEnginePlaybackGatesTests
{
    // SoundManager::GetAttenuation (0x550020) multiplies by effect_sound_volume
    // whenever it is not the ambient case — the same path PlaySoundFromCenter
    // (0x550950, 0x5509e0) takes for interface sounds. SoundManager::
    // interface_sound_volume (0x81f078) is written by the preference and read
    // nowhere.
    [Fact]
    public void InterfaceSounds_FollowTheEffectVolume()
    {
        using var engine = new OpenAlAudioEngine();
        if (!engine.IsAvailable) return;

        engine.SfxVolume = 0f;
        Assert.False(engine.PlayUiWave(1, Tone(), volume: 1f, priority: 1f));

        engine.SfxVolume = 1f;
        Assert.True(engine.PlayUiWave(2, Tone(), volume: 1f, priority: 1f));
    }

    // SoundManager::PlaySoundInternal (0x54fec0, 0x550170) is the only place
    // s_bPlaySoundOnlyWhenActive is read, ahead of the source being told to
    // play. Nothing there touches listener gain.
    [Fact]
    public void FocusMuted_RefusesNewSounds_WithoutTouchingListenerGain()
    {
        using var engine = new OpenAlAudioEngine();
        if (!engine.IsAvailable) return;

        engine.FocusMuted = true;
        Assert.False(engine.Play3DWave(1, 1, Tone(), Vector3.Zero, volume: 1f, priority: 1f));
        Assert.False(engine.PlayUiWave(2, Tone(), volume: 1f, priority: 1f));
        Assert.False(engine.PlayAmbientFromCenter(3, Tone(), volume: 1f, priority: 1f));
        Assert.Equal(1f, ListenerGain());

        engine.FocusMuted = false;
        Assert.True(engine.Play3DWave(4, 4, Tone(), Vector3.Zero, volume: 1f, priority: 1f));
        Assert.Equal(1f, ListenerGain());
    }

    // Shane's own divergence from #107: losing focus doesn't just refuse new
    // sounds, it cuts whatever world sound is already playing, the same way
    // SuspendWorldAudio does. The per-sound voice cap forces this across
    // thirty-two distinct waves so the pool is genuinely full of sounds still
    // sounding, not one sound holding several voices.
    [Fact]
    public void FocusMuted_CutsSoundsAlreadyPlaying()
    {
        using var engine = new OpenAlAudioEngine(new AudioMixerOptions
        {
            UseAuthoredPriority = false,
            MaxVoicesPerWave = AudioMixerOptions.NoPerWaveCap,
        });
        if (!engine.IsAvailable) return;

        WaveData longTone = LongTone();
        for (uint i = 0; i < 32; i++)
            Assert.True(engine.Play3DWave(i + 1, i + 1, longTone, Vector3.Zero, volume: 1f, priority: 1f));

        // Every voice is genuinely still playing: a thirty-third distinct wave
        // is dropped rather than stealing one of them.
        Assert.False(engine.Play3DWave(900, 900, longTone, Vector3.Zero, volume: 1f, priority: 1f));

        engine.FocusMuted = true;
        engine.FocusMuted = true;   // twice in a row does the work once
        engine.FocusMuted = false;  // starts nothing on its own

        for (uint i = 0; i < 32; i++)
            Assert.True(engine.Play3DWave(1000 + i, 1000 + i, longTone, Vector3.Zero, volume: 1f, priority: 1f));
    }

    // AudioHookSink routes the portal tunnel's own animation cues through the
    // same PlayUiWave as a genuine interface sound (UiPresentationHookSink);
    // only the latter is meant to hear "Disable Interface Sound".
    [Fact]
    public void DisablingInterfaceSound_LeavesTheNonInterfaceCallerAlone()
    {
        using var engine = new OpenAlAudioEngine();
        if (!engine.IsAvailable) return;

        engine.InterfaceEnabled = false;

        Assert.False(engine.PlayUiWave(1, Tone(), volume: 1f, priority: 1f));
        Assert.True(engine.PlayUiWave(2, Tone(), volume: 1f, priority: 1f, isInterfaceSound: false));
    }

    private static float ListenerGain()
    {
        AL al = AL.GetApi(soft: true);
        al.GetListenerProperty(ListenerFloat.Gain, out float gain);
        return gain;
    }

    private static WaveData Tone() => new()
    {
        ChannelCount = 1,
        SampleRate = 8000,
        BitsPerSample = 8,
        PcmBytes = new byte[] { 128, 160, 192, 160, 128, 96, 64, 96 },
    };

    // Ten seconds at 8 kHz: far longer than the test takes to run, so the
    // voice is genuinely still playing at every point it is checked.
    private static WaveData LongTone()
    {
        var pcm = new byte[80_000];
        for (int i = 0; i < pcm.Length; i++)
            pcm[i] = (byte)(128 + 64 * Math.Sin(i * 0.05));

        return new WaveData
        {
            ChannelCount = 1,
            SampleRate = 8000,
            BitsPerSample = 8,
            PcmBytes = pcm,
        };
    }
}
