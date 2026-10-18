namespace Ro3Emu.Setup.Tests;

/// <summary>A diagnostics zip someone posts in an issue must not carry a live login ticket, sign or gate auth.</summary>
public class DiagnosticsRedactTests
{
    [Theory]
    [InlineData("Micro LoginService.verifySdk {Ticket=\"9f2c4e6a8b0d1f3e5a7c9e1b3d5f7a9c\"}", "9f2c4e6a8b0d1f3e5a7c9e1b3d5f7a9c")]
    [InlineData("token: 9f2c4e6a8b0d1f3e", "9f2c4e6a8b0d1f3e")]
    [InlineData("{\"Sign\":\"abcdef0123456789\",\"Uid\":1}", "abcdef0123456789")]
    [InlineData("Password=hunter22", "hunter22")]
    public void SecretsAreRemoved(string line, string secret)
    {
        string redacted = DiagnosticsZip.Redact(line);
        Assert.DoesNotContain(secret, redacted);
        Assert.Contains("<redacted>", redacted);
    }

    [Theory]
    [InlineData("GameGate: 127.0.0.1:5000 connectServer NewConnection account=stranger1")]
    [InlineData("Sign in with your game account")]
    [InlineData("Micro FriendService.apply {Uid=\"100001\", Target=\"100002\"}")]
    public void OrdinaryLinesStay(string line) => Assert.Equal(line, DiagnosticsZip.Redact(line));
}
