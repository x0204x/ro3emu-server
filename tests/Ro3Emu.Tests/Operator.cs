using Ro3Emu.Server.Http;

namespace Ro3Emu.Tests;

/// <summary>
/// The operator token of every test host (TestHostDefaults sets it as Ro3Emu:OperatorToken), so a test reaches the
/// loopback tools (/ro3emu/gm, /reports, /online, /metrics, /shutdown) without knowing its host's data dir.
/// </summary>
internal static class Operator
{
    internal const string Token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    /// <summary>A GM command line as the body of POST /ro3emu/gm, carrying the token.</summary>
    internal static StringContent Command(string line)
    {
        var content = new StringContent(line);
        content.Headers.Add(OperatorToken.Header, Token);
        return content;
    }

    /// <summary>A client whose every request carries the token.</summary>
    internal static HttpClient Client(string? baseAddress = null, TimeSpan? timeout = null)
    {
        var http = new HttpClient();
        if (baseAddress != null) http.BaseAddress = new Uri(baseAddress);
        if (timeout is { } t) http.Timeout = t;
        Sign(http);
        return http;
    }

    /// <summary>Adds the token to an existing client's requests.</summary>
    internal static HttpClient Sign(HttpClient http)
    {
        http.DefaultRequestHeaders.Remove(OperatorToken.Header);
        http.DefaultRequestHeaders.Add(OperatorToken.Header, Token);
        return http;
    }
}
