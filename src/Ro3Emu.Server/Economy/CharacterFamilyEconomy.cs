namespace Ro3Emu.Server.Economy;

/// <summary>E05a's bounded command/part/reason policy. Existing E01, E04 and E05b receipts retain their schemas.</summary>
internal static class CharacterFamilyEconomy
{
    internal static bool IsKind(string kind) => kind.StartsWith("cd13a/", StringComparison.Ordinal) &&
        new[] { "character", "equipment", "appraisal", "enchant", "card", "shop" }
            .Any(family => kind[6..].StartsWith(family + "-", StringComparison.Ordinal));

    /// <summary>A Fashion-tab purchase (ShopCommodityConfig._iAppearanceId): one payment, the counter, the 5034
    /// unlock and the _iAppearanceAwardId bonus goods commit together; only this shop command may write "wardrobe".
    /// A mount look also writes "mounts": its 5012 system-mount ownership (MountService.PlanShopGrant).</summary>
    internal const string ShopAppearanceKind = "cd13a/shop-appearance";

    /// <summary>A used item and the role state its use action unlocks (CharacterService.ItemActions): the item is the only
    /// cost and there are no goods. 207 a Codex page (library), 212 a chat emoticon set (emoji_info), 620 a profile
    /// theme or background (personal), 349 a chat cosmetic (social_outfit), 112 / 105 / 2 nothing but the item;
    /// bag_state carries the item's cooldown.</summary>
    internal const string ItemActionKind = "cd13a/character-item-action";

    internal static IReadOnlySet<string> AllowedBlobs(string kind)
    {
        string[] parts = kind == ShopAppearanceKind ? ["shop", "wardrobe", "mounts", "equip_plans"]
            : kind == ItemActionKind ? ["bag_state", "emoji_info", "personal", "social_outfit"]
            : kind[6..].Split('-')[0] switch
            {
                "character" => ["bag_state", "equip_plans", "stat_plans", "activity_runtime", "title"],
                "equipment" => ["equip_levels", "equip_plans", "auto_decompose"],
                "appraisal" or "enchant" => ["equip_plans"],
                "card" => ["card_plans", "library", "equip_plans"],
                "shop" => ["shop", "equip_plans"],
                _ => throw new InvalidOperationException("Unknown E05a family.")
            };
        return new HashSet<string>(parts.Concat(["library", "tasks", "task_runtime", "task_followup"]), StringComparer.Ordinal);
    }

    internal static (EconomyReason Cost, EconomyReason Goods) Reasons(string kind) => kind switch
    {
        "cd13a/equipment-refine" => (EconomyReason.RefineCost, EconomyReason.CraftOutput),
        "cd13a/equipment-craft" or "cd13a/equipment-combine" => (EconomyReason.CraftInput, EconomyReason.CraftOutput),
        "cd13a/equipment-decompose" => (EconomyReason.DecomposeInput, EconomyReason.DecomposeOutput),
        "cd13a/character-use" or "cd13a/character-treasure-use" => (EconomyReason.ItemUse, EconomyReason.CraftOutput),
        "cd13a/character-box" => (EconomyReason.ItemUse, EconomyReason.ItemBoxGoods),
        "cd13a/character-gm-grant" or "cd13a/character-gm-remove" or "cd13a/character-gm-clear" =>
            (EconomyReason.GmRemove, EconomyReason.GmGrant),
        _ => kind[6..].Split('-')[0] switch
        {
            "equipment" => (EconomyReason.EquipmentCost, EconomyReason.CraftOutput),
            "appraisal" => (EconomyReason.AppraisalCost, EconomyReason.CraftOutput),
            "enchant" => (EconomyReason.EnchantCost, EconomyReason.EnchantRefund),
            "card" => (EconomyReason.CardInput, EconomyReason.CardOutput),
            "shop" => (EconomyReason.ShopPayment, EconomyReason.ShopGoods),
            // State-only character commands authorize no costs/goods; the semantic amounts must remain zero.
            "character" => (EconomyReason.ItemUse, EconomyReason.CraftOutput),
            _ => throw new InvalidOperationException("Unknown E05a reason namespace.")
        }
    };
}
