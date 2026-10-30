namespace Ro3Emu.Setup.Tests;

public sealed partial class PreparedGameDataTests
{
    [Fact]
    public void ValidImportedDataCanAddPicturesWithoutExportingOrChangingCoreData()
    {
        Prepare();
        var before = Snapshot();
        int exports = 0, pictures = 0;
        void ExportPictures(string client, string data)
        {
            pictures++;
            Assert.Equal(Client, client);
            Assert.Equal(Data, data);
            Assert.True(PreparedGameData.UseIfPresent(data, client, false));
            Write(data, "icons/index.json", "{}");
            Write(data, "icons/items/fixture.png", "自造圖片資料，不啟動圖片解碼器");
        }

        // fixture 的 Lua／VFS 都是無效 bytes；只准呼叫自造圖片 delegate。
        Flows.EnsureGameData(Client, false, Data, (_, _) => exports++, ExportPictures);
        Flows.EnsureGameData(Client, false, Data, (_, _) => exports++, ExportPictures);

        Assert.Equal(0, exports);
        Assert.Equal(1, pictures);
        foreach (var (relative, hash) in before) Assert.Equal(hash, Hash(Path.Combine(Data, relative)));
        Assert.True(PreparedGameData.UseIfPresent(Data, Client, false));
    }

    [Fact]
    public void DisabledPicturesKeepTheImportedDataAndNeverCallTheCoreExporter()
    {
        Prepare();
        var before = Snapshot();
        int exports = 0;
        Flows.EnsureGameData(Client, false, Data, (_, _) => exports++, exportIcons: null);
        Assert.Equal(0, exports);
        Assert.False(Directory.Exists(Path.Combine(Data, "icons")));
        Assert.Equal(before.OrderBy(p => p.Key), Snapshot().OrderBy(p => p.Key));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnInvalidImportOrForcedExportCannotReachThePictureExporter(bool force)
    {
        Prepare();
        if (!force) Write(Data, "config/MapResource.json", "來源不同，不准覆寫");
        var before = Snapshot();
        int exports = 0, pictures = 0;
        Assert.Throws<SetupException>(() => Flows.EnsureGameData(Client, force, Data,
            (_, _) => exports++, (_, _) => pictures++));
        Assert.Equal(0, exports);
        Assert.Equal(0, pictures);
        Assert.Equal(before.OrderBy(p => p.Key), Snapshot().OrderBy(p => p.Key));
    }

    [Fact]
    public void AFullExportOwnsItsPicturesWithoutASecondPicturePass()
    {
        int exports = 0, pictures = 0;
        Flows.EnsureGameData(Client, false, Data, (_, data) =>
        {
            exports++;
            Write(data, "icons/index.json", "{}");
        }, (_, _) => pictures++);
        Assert.Equal(1, exports);
        Assert.Equal(0, pictures);
    }
}
