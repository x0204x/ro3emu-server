namespace Ro3Emu.Setup.Tests;

public class SetupTests
{
    [Theory]
    [InlineData("203.0.113.10", "http://203.0.113.10:5380")]
    [InlineData("203.0.113.10:6000", "http://203.0.113.10:6000")]
    [InlineData("play.example.com", "http://play.example.com:5380")]
    [InlineData("http://play.example.com/", "http://play.example.com:5380")]
    [InlineData("https://play.example.com:443", "https://play.example.com:443")]
    [InlineData("[2001:db8::1]:7000", "http://[2001:db8::1]:7000")]
    [InlineData("[2001:db8::1]", "http://[2001:db8::1]:5380")]
    [InlineData(" 10.0.0.2 ", "http://10.0.0.2:5380")]
    public void ServerAddresses(string address, string url) => Assert.Equal(url, Flows.ServerUrl(address));

    [Theory]
    [InlineData("")]
    [InlineData("ftp://x")]
    [InlineData("http://host/path")]
    public void NotServerAddresses(string address) => Assert.Null(Flows.ServerUrl(address));

    [Theory]
    [InlineData("ro3_Data/Managed/*.dll", "ro3_Data/Managed/HUD.dll", true)]
    [InlineData("ro3_Data/Managed/*.dll", "ro3_Data/Managed/sub/HUD.dll", false)]
    [InlineData("ro3_Data/**", "ro3_Data/Managed/sub/HUD.dll", true)]
    [InlineData("ro3.exe", "ro3.exe", true)]
    public void PathFilter(string pattern, string path, bool match) => Assert.Equal(match, InstallFlow.Glob(pattern)(path));

    [Fact]
    public void OptionsParse()
    {
        var o = Options.Parse(["host", "--public", "10.0.0.5", "--gm", "alice", "--gm", "bob", "--http-port", "6000", "--yes"]);
        Assert.Equal(("host", "10.0.0.5", 6000, true), (o.Command, o.PublicHost, o.Ports.Http, o.Yes));
        Assert.Equal(["alice", "bob"], o.GmAccounts);
        Assert.Equal("start", Options.Parse([]).Command);
        Assert.Equal(["1.2.3.4"], Options.Parse(["join", "1.2.3.4"]).Positional);
        Assert.Throws<SetupException>(() => Options.Parse(["--nope"]));
        Assert.Throws<SetupException>(() => Options.Parse(["fly"]));
    }

    [Fact]
    public void VoiceIsOffUnlessAVoicePortIsGiven()
    {
        Assert.Equal(0, Options.Parse(["host"]).Ports.Voice);
        Assert.Equal(33101, Options.Parse(["host", "--voice-port", "33101"]).Ports.Voice);
        Assert.Throws<SetupException>(() => Options.Parse(["host", "--voice-port", "0"]));
    }

    [Fact]
    public void TheVoicePortReachesTheServerOnlyWhenSet()
    {
        var off = ServerProcess.ServerArgs("data", new ServerProcess.Ports());
        Assert.DoesNotContain(off, a => a.StartsWith("--Ro3Emu:Voice:Port"));
        var on = ServerProcess.ServerArgs("data", new ServerProcess.Ports(Voice: 33101));
        Assert.Contains("--Ro3Emu:Voice:Port=33101", on);
        Assert.Contains("--Ro3Emu:GameGatePort=33100", on);
    }

    [Fact]
    public void ThePortsToForwardIncludeVoiceWhenItIsOn()
    {
        Assert.Equal("5380, 20000 and 33100", ServerProcess.PortList(new ServerProcess.Ports()));
        Assert.Equal("5380, 20000, 33100 and 33101", ServerProcess.PortList(new ServerProcess.Ports(Voice: 33101)));
    }

    [Fact]
    public void AnInstallGoesOnToPlayWithTheSameVoicePort()
    {
        var after = new OptionsWithClient(Options.Parse(["install", "--voice-port", "33101"]), @"C:\RO3\ROO_PC").Value;
        Assert.Equal(33101, after.Ports.Voice);
        Assert.Equal(0, new OptionsWithClient(Options.Parse(["install"]), @"C:\RO3\ROO_PC").Value.Ports.Voice);
    }

    [Fact]
    public void TranslationsAreEmbeddedInFileOrder()
    {
        var translations = Flows.Translations();
        Assert.True(translations.Count >= 5);
        Assert.All(translations, t => Assert.Contains('\t', t));
    }
}
