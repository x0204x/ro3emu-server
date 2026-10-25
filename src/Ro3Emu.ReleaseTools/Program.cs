using System.Security.Cryptography;
using System.Text.Json;
using Ro3Emu.Patcher;

if (args.Length == 0)
{
    Usage();
    return 1;
}

try
{
    switch (args[0])
    {
        case "keygen":
            if (args.Length != 3) throw new ArgumentException("keygen 需要 <輸出資料夾> <key-id>");
            GenerateKey(args[1], args[2]);
            return 0;
        case "sign-channel":
            if (args.Length != 5) throw new ArgumentException("sign-channel 需要 <repo> <channel> <private.pem> <key-id>");
            var signed = await Publisher.SignExistingChannelAsync(args[1], args[2],
                new RepositorySigningOptions(args[4], await File.ReadAllTextAsync(args[3])));
            Console.WriteLine($"已簽署頻道 {signed.Channel}，序號 {signed.Sequence}，到期 {signed.Expires:O}");
            return 0;
        case "publish-launcher":
            if (args.Length != 7) throw new ArgumentException("publish-launcher 需要 <repo> <channel> <exe> <version> <private.pem> <key-id>");
            var published = await Publisher.PublishLauncherAsync(args[1], args[2], args[3], args[4],
                new RepositorySigningOptions(args[6], await File.ReadAllTextAsync(args[5])));
            Console.WriteLine($"已發布啟動器 {published.Launcher!.Version}，頻道序號 {published.Sequence}");
            return 0;
        default:
            throw new ArgumentException($"未知命令：{args[0]}");
    }
}
catch (Exception e)
{
    Console.Error.WriteLine($"錯誤：{e.Message}");
    return 2;
}

static void GenerateKey(string directory, string keyId)
{
    directory = Path.GetFullPath(directory);
    if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
        throw new InvalidOperationException("金鑰輸出資料夾必須是空的。");
    Directory.CreateDirectory(directory);
    using var rsa = RSA.Create(3072);
    string privatePath = Path.Combine(directory, "repository-private.pem");
    string publicPath = Path.Combine(directory, "repository-public.pem");
    File.WriteAllText(privatePath, rsa.ExportPkcs8PrivateKeyPem());
    File.WriteAllText(publicPath, rsa.ExportSubjectPublicKeyInfoPem());
    File.WriteAllText(Path.Combine(directory, "launcher-trust.json"), JsonSerializer.Serialize(new
    {
        requireHttpsRepository = true,
        requireSignedChannel = true,
        trustedPublisherKeys = new Dictionary<string, string> { [keyId] = rsa.ExportSubjectPublicKeyInfoPem() },
    }, Json.Indented));
    Console.WriteLine($"私鑰：{privatePath}");
    Console.WriteLine($"公開金鑰：{publicPath}");
    Console.WriteLine("私鑰不得放進 Git、更新站或玩家端電腦。");
}

static void Usage() => Console.Error.WriteLine("""
    RO3Emu 發布信任工具
      keygen <輸出資料夾> <key-id>
      sign-channel <repo> <channel> <private.pem> <key-id>
      publish-launcher <repo> <channel> <exe> <version> <private.pem> <key-id>
    """);
