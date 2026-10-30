using Romsg;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class SkillService
{
    private const int QueueCarrierSystemType = 6;
    private const string QueueCarrierScript = "jinengduilie1";
    private const string AdvancedQueueCarrierScript = "jinengduilie2";
    private const string MageQueueCarrierScript = "jinengduilie2_5";

    /// <summary>
    /// LC_System_Skill._onJoystickSkillListChange 複製 whellSkills；PC 的 queue widget 固定讀第七格。
    /// JobConfig._iQueueSkillId 是完整 SkillConfig row ID，不是隊列內第一個普通技能的 group ID。
    /// 這裡僅投射職業載體，不授予戰鬥施放權限，也不代替 client 的隊列 BT 執行子技能。
    /// </summary>
    private void ProjectQueueCarrier(GameSession session, SkillWheel wheel)
    {
        int slot = (int)SkillWheelSlotType.SkillWheelSkillQueue;
        wheel.WhellSkills.Remove(slot);
        if (session.Progress is not { } progress
            || JobConfig.Get(_data, progress.Career) is not { QueueSkillId: > 0 } job
            || job.QueueSkillId > int.MaxValue
            || SkillConfig.Get(_data, job.QueueSkillId) is not
            { SystemType: QueueCarrierSystemType, BasicType: 1, InitialLevel: > 0 } carrier
            || carrier.Id != job.QueueSkillId
            || carrier.ScriptLogic is not (QueueCarrierScript or AdvancedQueueCarrierScript or MageQueueCarrierScript))
            return;

        // 不繼承父職的非零值：目前職業明確配置 0 就不應憑空增加按鈕。
        // (inferred) 安全門：client 的 BT 只驗證可用子技能，成功後仍會發載體 31102；
        // 載體另有 CD／演出，伺服器隊列排程契約尚未閉合前，顯示但不提供已知無法完成的點擊。
        var projected = new SkillWheelSlot { SkillConfigId = (int)carrier.Id, Enable = false };
        wheel.WhellSkills[slot] = projected;
        wheel.SkillQueue = projected.Clone();
    }

    /// <summary>
    /// LC_SkillGrowthHelper.GetHasSkillQueue 以 skillList 的 group 辨識隊列頁入口；
    /// LC_DAL_SkillDevelop._refreshLocalSkill 先給目錄項目 level0，再讀 skillAssigns。
    /// 只補目錄，不改 Tree／Progress／Granted／技能等級或戰鬥施放授權。
    /// </summary>
    private void ProjectQueueCatalogue(GameSession session, DBSkillData state)
    {
        if (session.Progress is not { } progress
            || !state.SkillWheel.WhellSkills.TryGetValue((int)SkillWheelSlotType.SkillWheelSkillQueue, out var slot)
            || JobConfig.Get(_data, progress.Career) is not { } job
            || SkillConfig.Get(_data, slot.SkillConfigId) is not { SkillId: > 0 and <= int.MaxValue } carrier
            || carrier.Id != job.QueueSkillId || carrier.SystemType != QueueCarrierSystemType)
            return;
        int group = (int)carrier.SkillId;
        if (!state.SkillList.Contains(group)) state.SkillList.Add(group);
    }
}
