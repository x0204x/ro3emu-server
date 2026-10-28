using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class MsgCatalogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3-msg-catalog-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void DirectionMarkersAndWireDomainsNeverCreateFakeReplies()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllLines(Path.Combine(_dir, "msgtable.tsv"),
        [
            "1\tCANONICAL_NOTIFY\tromsg.NullArg\tromsg.CommonErrorNtf",
            "2\tROMSG_ALIAS_NOTIFY\tromsg.null\tromsg.CommonErrorNtf",
            "3\tEZMSG_ALIAS_NOTIFY\tezmsg.NullArg\tromsg.CommonErrorNtf",
            "4\tREQUEST_RESPONSE\tromsg.SceneLoadedReq\tromsg.SceneLoadedResp",
            "5\tONEWAY\tromsg.SceneLoadedReq\tromsg.NullArg",
            "6\tEZ_SERVICE_REQUEST\tezmsg.MailGetMailListRequest\tezmsg.MailGetMailListResponse",
            "7\tMISSING_REQUEST_SCHEMA\tromsg.MissingReq\tromsg.SceneLoadedResp",
            "8\tMISSING_RESPONSE_SCHEMA\tromsg.SceneLoadedReq\tromsg.MissingResp",
        ]);
        var catalog = new MsgCatalog(
            Options.Create(new ServerOptions { DataDir = _dir }),
            NullLogger<MsgCatalog>.Instance);

        foreach (var id in new uint[] { 1, 2, 3 })
        {
            Assert.True(catalog.IsServerNotification(id));
            Assert.Null(catalog.EmptyResponse(id));
            Assert.Equal("3 bytes", catalog.Describe(id, new byte[3]));
        }

        Assert.False(catalog.IsServerNotification(4));
        Assert.NotNull(catalog.EmptyResponse(4));
        Assert.False(catalog.IsServerNotification(5));
        Assert.Null(catalog.EmptyResponse(5));
        Assert.NotNull(catalog.Get(6)!.Response); // the descriptor exists, but this is not a Game Gate request
        Assert.False(catalog.Get(6)!.IsGameGate);
        Assert.Null(catalog.EmptyResponse(6));
        Assert.Equal("3 bytes", catalog.Describe(6, new byte[3]));
        Assert.NotNull(catalog.Get(7)!.Response);
        Assert.Null(catalog.Get(7)!.Request);
        Assert.Null(catalog.EmptyResponse(7));
        Assert.NotNull(catalog.Get(8)!.Request);
        Assert.Null(catalog.Get(8)!.Response);
        Assert.Null(catalog.EmptyResponse(8));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
