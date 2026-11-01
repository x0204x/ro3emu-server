namespace Ro3Emu.Server.Game;

public sealed partial class CharacterService
{
    /// <summary>治療屬性由角色完整來源合計，包含裝備、卡片、圖騰及仍有效的普通／寵物增益。
    /// AttributeConfig 207/208/1207/1208 沒有只限寵物來源的條件；已到期未清除的增益不再生效。</summary>
    internal StatSheet HealingStats(GameSession session) =>
        Stats(session, static buff => buff.ExpireMs > MonsterService.NowMs);
}
