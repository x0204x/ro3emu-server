using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class TerritoryPersistenceCaptureTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ro3-territory-capture-" + Guid.NewGuid().ToString("N"));
    private readonly AccountStore _accounts;
    private readonly RoleStore _roles;
    private readonly GuildStore _guilds;

    public TerritoryPersistenceCaptureTests()
    {
        _accounts = new AccountStore(Options.Create(new ServerOptions { DataDir = _directory }));
        _roles = new RoleStore(_accounts);
        _guilds = new GuildStore(_accounts);
        _accounts.WriteSync<int>(new TerritorySchemaPlan(RealmStateKind.TerritoryWars));
        _accounts.WriteSync<int>(new TerritorySchemaPlan(RealmStateKind.TerritoryEscort));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingGuildRejectsRealmAndRoleUpdatesTogether(bool wars)
    {
        var kind = wars ? RealmStateKind.TerritoryWars : RealmStateKind.TerritoryEscort;
        string column = wars ? "territory_wars" : "territory_escort";
        long role = _roles.Create("territory", 1, wars ? "WarsRole" : "EscortRole", 1, 1000, 1)!.Id;
        byte[] initialRealm = [1, 2], initialRole = [3, 4];
        _accounts.WriteLegacySync<int>(new RealmStatePlan(kind, initialRealm));
        if (wars) _roles.SaveTerritoryWars(role, initialRole);
        else _roles.SaveTerritoryEscort(role, initialRole);

        byte[] candidateRealm = [5, 6], candidateRole = [7, 8];
        var transaction = new RoleItemPlanBuilder(_accounts);
        transaction.Add(new RealmStatePlan(kind, candidateRealm));
        transaction.RoleBlob(role, column, candidateRole);
        _guilds.EnlistSave(new GuildStore.State { Id = long.MaxValue, Name = "VanishedGuild" }, transaction,
            "Territory guild disappeared");
        candidateRealm[0] = 99;
        candidateRole[0] = 99;

        var gone = Assert.Throws<RowMissingException>(
            () => transaction.Commit(_accounts, "test/territory-missing-guild"));
        Assert.True(gone.Is(GuildStore.Table, long.MaxValue));
        Assert.Equal(initialRealm, _accounts.ReadSync<byte[]?>(new TerritoryRealmReadPlan(kind)));
        Assert.Equal(initialRole, wars ? _roles.LoadTerritoryWars(role) : _roles.LoadTerritoryEscort(role));
    }

    [Fact]
    public void MalformedTargetSeasonCannotCommitOldArchiveOrReplaceCurrentRealm()
    {
        byte[] current = JsonSerializer.SerializeToUtf8Bytes(new TerritoryWarsService.WorldState { Epoch = "current" });
        byte[] fallback = JsonSerializer.SerializeToUtf8Bytes(new TerritoryWarsService.WorldState { Epoch = "fallback" });
        _accounts.WriteLegacySync<int>(new RealmStatePlan(RealmStateKind.TerritoryWars, current));
        _accounts.WriteLegacySync<byte[]>(new TerritoryWarsSeasonTransitionPlan("corrupt", Encoding.UTF8.GetBytes("{broken"),
            "absent", fallback));
        _accounts.WriteLegacySync<int>(new RealmStatePlan(RealmStateKind.TerritoryWars, current));

        Assert.Throws<JsonException>(() => _accounts.WriteLegacySync<byte[]>(
            new TerritoryWarsSeasonTransitionPlan("attempt", current, "corrupt", fallback)));

        Assert.Equal(current, _accounts.ReadSync<byte[]?>(new TerritoryRealmReadPlan(RealmStateKind.TerritoryWars)));
        // If the failed transaction had archived "attempt", this would find it and restore current instead of fallback.
        var next = _accounts.WriteLegacySync<byte[]>(new TerritoryWarsSeasonTransitionPlan("probe", current, "attempt", fallback));
        Assert.Equal("fallback", JsonSerializer.Deserialize<TerritoryWarsService.WorldState>(next)!.Epoch);
    }

    [Fact]
    public void ReturningToArchivedSeasonMovesHoldingsBackToActiveRowOnce()
    {
        var first = new TerritoryWarsService.WorldState { Epoch = "first" };
        first.WorldDrops[7] = new TerritoryWarsService.WorldDrop { Id = 7, Scene = 30311, Amount = 42 };
        byte[] firstBytes = JsonSerializer.SerializeToUtf8Bytes(first);
        byte[] secondBytes = JsonSerializer.SerializeToUtf8Bytes(new TerritoryWarsService.WorldState { Epoch = "second" });
        _accounts.WriteLegacySync<int>(new RealmStatePlan(RealmStateKind.TerritoryWars, firstBytes));

        _accounts.WriteLegacySync<byte[]>(new TerritoryWarsSeasonTransitionPlan("first", firstBytes, "second", secondBytes));
        Assert.NotNull(_accounts.ReadSync<byte[]?>(new TerritoryWarsSeasonArchiveReadPlan("first")));
        byte[] second = _accounts.ReadSync<byte[]?>(new TerritoryRealmReadPlan(RealmStateKind.TerritoryWars))!;
        var restored = _accounts.WriteLegacySync<byte[]>(new TerritoryWarsSeasonTransitionPlan("second", second, "first", secondBytes));

        Assert.Null(_accounts.ReadSync<byte[]?>(new TerritoryWarsSeasonArchiveReadPlan("first")));
        Assert.NotNull(_accounts.ReadSync<byte[]?>(new TerritoryWarsSeasonArchiveReadPlan("second")));
        Assert.Equal(42, JsonSerializer.Deserialize<TerritoryWarsService.WorldState>(restored)!.WorldDrops[7].Amount);
    }

    public void Dispose()
    {
        _accounts.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }
}
