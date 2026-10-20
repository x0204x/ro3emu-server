using Ro3Emu.ClientHooks;

namespace Ro3Emu.Tests;

/// <summary>
/// When the hint "voice chat is off on this server" shows (src/Ro3Emu.ClientHooks/VoiceOffHint.cs, compiled into this
/// project): only on Talk (mic on) after the server's ticket said the relay is off, until logout.
/// </summary>
public sealed class VoiceOffHintTests
{
    [Fact]
    public void TalkAfterAnOffTicketShowsTheHint()
    {
        var hint = new VoiceOffHint();
        hint.OnTicket(relayOff: true);
        Assert.True(hint.ShowOnMicChange(mute: false));
    }

    [Fact]
    public void EveryTalkClickShowsItAgain()
    {
        var hint = new VoiceOffHint();
        hint.OnTicket(relayOff: true);
        Assert.True(hint.ShowOnMicChange(mute: false));
        Assert.True(hint.ShowOnMicChange(mute: false));
    }

    [Fact]
    public void ListenAndMuteNeverShowIt()
    {
        // Joining the party channel mutes the mic by itself (default Listen): that must stay silent.
        var hint = new VoiceOffHint();
        hint.OnTicket(relayOff: true);
        Assert.False(hint.ShowOnMicChange(mute: true));
    }

    [Fact]
    public void NothingShowsBeforeTheServerAnswered()
    {
        var hint = new VoiceOffHint();
        Assert.False(hint.ShowOnMicChange(mute: false));
    }

    [Fact]
    public void AServedRelayClearsIt()
    {
        var hint = new VoiceOffHint();
        hint.OnTicket(relayOff: true);
        hint.OnTicket(relayOff: false);
        Assert.False(hint.ShowOnMicChange(mute: false));
    }

    [Fact]
    public void LogoutClearsIt()
    {
        // The next login may reach a server with voice on: the old answer must not stick.
        var hint = new VoiceOffHint();
        hint.OnTicket(relayOff: true);
        hint.Reset();
        Assert.False(hint.ShowOnMicChange(mute: false));
    }
}
