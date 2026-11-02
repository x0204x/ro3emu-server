using System.Collections.Concurrent;
using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataFact]
    public async Task CardPassivePublishesOneFinalAttributeChangeOnInsertAndRemove()
    {
        await using var alice = await InWorld("Cardstats");
        await Gm("cardstats", "!lvl 80");
        await Gm("cardstats", "!item 1440604 1");
        var player = Session("cardstats");
        await Loop.InvokeAsync(async () =>
        {
            EnsureCardFixtureGear(player, 6);
            // 非零基值隔離百分比變化；原卡 2024001 的 1335 仍由真實被動路徑讀取。
            Character.ExtraAttributes.Add(s => s == player ? [(202L, 1000.0)] : []);
            await Character.RefreshStatsAsync(player);
        });
        await Next(alice, CharacterService.OnePropertyChange, OnePropertyChange.Parser,
            m => m.Property.Propertys.Any(p => p.PropertyId == 202 && p.Data == 1000));
        var observed = new ConcurrentQueue<OnePropertyChange>();
        var inserted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        alice.EnableLoadMode(512, e =>
        {
            if (!e.IsGame(CharacterService.OnePropertyChange)) return;
            var message = OnePropertyChange.Parser.ParseFrom(e.Body);
            observed.Enqueue(message);
            if (message.Property.Propertys.Any(p => p.PropertyId == 202 && p.Data == 1250)) inserted.TrySetResult();
            if (message.Property.Propertys.Any(p => p.PropertyId == 202 && p.Data == 1000)) removed.TrySetResult();
        });
        long guid = player.Items.Single(i => i.Entry == 1440604).Guid;
        using var insertReply = alice.Inbox.Expect(e => e.IsGame(CardService.SlotInsert));
        await alice.SendGameAsync(CardService.SlotInsert, new EquipmentCardInsertReq
        { EquipPos = (EquipGridType)6, SlotIndex = 1, CardGuid = guid });
        Assert.Equal(0, EquipmentCardInsertResp.Parser.ParseFrom((await insertReply.WaitAsync(Wait)).Body).Result);
        await inserted.Task.WaitAsync(Wait);
        var first = Assert.Single(observed);
        Assert.Contains(28, first.AttrModTypes);
        Assert.Equal(1250, Character.Stats(player)[202]);
        Assert.Contains(player.Buffs, b => b.ConfigId == 2024001 && b.IsPassive);
        observed.Clear();
        using var removeReply = alice.Inbox.Expect(e => e.IsGame(CardService.SlotRemove));
        await alice.SendGameAsync(CardService.SlotRemove, new EquipmentCardRemoveReq { EquipPos = (EquipGridType)6, SlotIndex = 1 });
        Assert.Equal(0, EquipmentCardRemoveResp.Parser.ParseFrom((await removeReply.WaitAsync(Wait)).Body).Result);
        await removed.Task.WaitAsync(Wait);
        var last = Assert.Single(observed);
        Assert.Contains(28, last.AttrModTypes);
        Assert.Equal(1000, Character.Stats(player)[202]);
        Assert.DoesNotContain(player.Buffs, b => b.ConfigId == 2024001 && b.IsPassive);
    }

    [ClientDataFact]
    public async Task CardStatPublicationScopeReleasesAfterException()
    {
        await using var alice = await InWorld("Cardbatch");
        var player = Session("cardbatch");
        int snapshots = 0;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        alice.EnableLoadMode(512, e =>
        {
            if (e.IsGame(CharacterService.OnePropertyChange) && Interlocked.Increment(ref snapshots) == 2)
                completed.TrySetResult();
        });
        await Loop.InvokeAsync(async () =>
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await using var batch = Character.BatchStatPublication(player);
                await Character.RefreshStatsAsync(player);
                await Character.RefreshStatsAsync(player);
                throw new InvalidOperationException("LAB scope failure");
            });
            await Character.RefreshStatsAsync(player);
        });
        await completed.Task.WaitAsync(Wait);
        Assert.Equal(2, Volatile.Read(ref snapshots));
    }
}
