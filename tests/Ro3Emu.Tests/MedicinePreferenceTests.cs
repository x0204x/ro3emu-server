using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class MedicinePreferenceTests
{
    [Fact]
    public void FillingMissingMedicineSlotsDoesNotMutateSavedPreferences()
    {
        var saved = new MedicinesAutoUseSet
        {
            HpConfig = new MedicinesAutoUse { IsOpen = false, Condition = 42, ItemEntrys = { 1000000 } },
        };
        var before = saved.ToByteArray();
        var normalized = CharacterService.WithMedicineDefaults(saved);
        Assert.Equal(before, saved.ToByteArray());
        Assert.False(normalized.HpConfig.IsOpen);
        Assert.Equal(42, normalized.HpConfig.Condition);
        Assert.Equal([1000000, 0, 0], normalized.HpConfig.ItemEntrys);
        Assert.NotNull(normalized.SpConfig);
        normalized.HpConfig.ItemEntrys[0] = 0;
        Assert.Equal(1000000, saved.HpConfig.ItemEntrys[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoginAndRestartProvideMedicineSlotsWithoutOverwritingSavedChoices(bool savedHp)
    {
        await using var host = await SyntheticHost.StartAsync();
        var preferences = new DBItemsInfo { CurrentArrowID = 123456 };
        if (savedHp)
            preferences.MedicinesAutoUse = new MedicinesAutoUseSet
            {
                HpConfig = new MedicinesAutoUse { IsOpen = false, Condition = 42, ItemEntrys = { 1000000 } },
            };
        long role;
        await using (var bot = host.NewBot())
        {
            await bot.LoginAsync("alice", "pw");
            await bot.EnterGateAsync();
            role = (await bot.CreateRoleAsync("Alice")).RoleBaseInfo.Id;
            host.Service<RoleStore>().SaveBagState(role, preferences.ToByteArray());
            await bot.EnterWorldAsync(role);
            AssertMedicineSlots(await Next(bot), savedHp);
        }
        await host.RestartAsync();
        await using var again = await host.InWorldAsync("Alice", create: false);
        AssertMedicineSlots(await Next(again), savedHp);
        var stored = DBItemsInfo.Parser.ParseFrom(host.Service<RoleStore>().LoadBagState(role)!);
        Assert.Equal(preferences.MedicinesAutoUse, stored.MedicinesAutoUse);
    }

    private static void AssertMedicineSlots(DBItemsInfoIncrement wire, bool savedHp)
    {
        var medicine = wire.MedicinesAutoUse.All;
        Assert.Equal(savedHp ? 42 : 60, medicine.HpConfig.Condition);
        Assert.False(medicine.HpConfig.IsOpen);
        int[] expected = savedHp ? [1000000, 0, 0] : [0, 0, 0];
        Assert.Equal(expected, medicine.HpConfig.ItemEntrys);
        Assert.False(medicine.SpConfig.IsOpen);
        Assert.Equal(60, medicine.SpConfig.Condition);
        Assert.Equal([0, 0, 0], medicine.SpConfig.ItemEntrys);
    }

    private static async Task<DBItemsInfoIncrement> Next(Ro3Emu.Bot.BotClient bot) => DBItemsInfoIncrement.Parser.ParseFrom(
        (await bot.Inbox.WaitAsync(e => e.IsGame(CharacterService.ItemsSync), TimeSpan.FromSeconds(5))).Body);
}
