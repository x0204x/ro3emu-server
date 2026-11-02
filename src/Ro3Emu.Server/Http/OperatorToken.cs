using System.Security.Cryptography;
using System.Text;
using Ro3Emu.Server.Accounts;

namespace Ro3Emu.Server.Http;

/// <summary>
/// The operator's secret for the loopback tools (<see cref="OpsEndpoints"/>): a request to /ro3emu/gm, /shutdown,
/// /online, /reports or /metrics must carry it in <see cref="Header"/>. Loopback alone is not enough, because a web page
/// open in a browser on the host can send a simple POST to 127.0.0.1 too; a page cannot read a file on the disk. It lives
/// in DataDir/<see cref="FileName"/>, created at the first start (32 random bytes as hex, readable only by the server's
/// user), or comes from Ro3Emu:OperatorToken, which then is written to that file for the tools.
/// </summary>
public sealed class OperatorToken
{
    public const string Header = "X-Ro3Emu-Operator";
    public const string FileName = "operator.token";

    /// <summary>The shortest token the server accepts from the configuration or the file.</summary>
    public const int MinLength = 32;

    private readonly byte[] _value;

    private OperatorToken(string value) => _value = Encoding.UTF8.GetBytes(value);

    /// <summary>The token file of a data dir.</summary>
    public static string PathIn(string dataDir) => Path.Combine(dataDir, FileName);

    /// <summary>The token a tool sends to the server of <paramref name="dataDir"/>, or null while it has none.</summary>
    public static string? Read(string dataDir)
    {
        string path = PathIn(dataDir);
        if (!File.Exists(path)) return null;
        string text = File.ReadAllText(path).Trim();
        return text.Length > 0 ? text : null;
    }

    /// <summary>
    /// The configured token (written to the file when the file differs), else the file's, else a new random one in a
    /// new file. A configured or stored token shorter than <see cref="MinLength"/> or with white space stops the start:
    /// a short secret would be guessed.
    /// </summary>
    public static OperatorToken LoadOrCreate(string dataDir, string? configured)
    {
        string path = PathIn(dataDir);
        if (!string.IsNullOrEmpty(configured))
        {
            Check(configured, "Ro3Emu:OperatorToken");
            if (Read(dataDir) != configured) Write(dataDir, path, configured);
            PrivateFiles.Files(path);
            return new OperatorToken(configured);
        }
        if (Read(dataDir) is { } stored)
        {
            Check(stored, path);
            PrivateFiles.Files(path); // also a file an older start or the operator left readable
            return new OperatorToken(stored);
        }
        string fresh = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        Write(dataDir, path, fresh);
        return new OperatorToken(fresh);
    }

    /// <summary>True when <paramref name="presented"/> is the token; compared in constant time.</summary>
    public bool Matches(string? presented) =>
        presented != null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), _value);

    private static void Check(string token, string source)
    {
        if (token.Length < MinLength || token.Any(char.IsWhiteSpace) || token.Any(char.IsControl))
            throw new InvalidOperationException(
                $"The operator token from {source} needs at least {MinLength} characters and no white space.");
    }

    private static void Write(string dataDir, string path, string token)
    {
        PrivateFiles.Directory(dataDir);
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var file = new StreamWriter(path, new UTF8Encoding(false), options)) file.Write(token);
        PrivateFiles.Files(path);
    }
}
