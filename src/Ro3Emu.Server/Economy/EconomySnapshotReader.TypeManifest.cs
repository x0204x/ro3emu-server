using System.Reflection;
using System.Text.Json.Serialization;
using Google.Protobuf.Reflection;

namespace Ro3Emu.Server.Economy;

public static partial class EconomySnapshotReader
{
    /// <summary>
    /// Frozen classification surface, independent of SQL schema. Adding a field to an existing persisted DTO or
    /// protobuf cannot silently classify a new balance as progression. Review the decoder and bump this pin when
    /// the persisted format changes; a matching runtime type alone is not a reviewed economic classification.
    /// </summary>
    // C/D 13e: Dungeon.State.PendingJourney names a captured act/cycle, not a second holding of
    // its dungeon loot. Authored Journey boss acts have no direct goods; later node claims stay separate.
    // Emotes: role.emoji_play is DBEmojiPlayInfo (emote ids: favourites, unlocks, idle emote); no balance, no asset.
    // Achievements: role.achievements is DBRoleAchievementInfo (statuses, score counters, points, claimed reward and
    // hint levels) and role.achievement_runtime is AchievementService.RuntimeState (the last counted login day). Neither
    // holds a balance or an asset: a claim's goods are granted by its own cd13e/achievement-award receipt, and the
    // claimed status / reward level there is only the once-only source marker of that receipt.
    // Escort: RoleState.Cycle and the ledger's Cycle name the Territory War repeat the escort season belongs to
    // (TerritoryWarsClock.EpochOf); a key, not a balance. The decoder reads it into the escort epoch.
    // Special Class Items: role.career_special is DBCareerSpecial (two CartRemouldTable ids: pendant and mount slot); a
    // free lease, no balance, no asset.
    // Adventure Group: role.adventure_group is DBAdventureGroupRelationData (claimed group level rewards, the leave
    // cooldown, the weekly social-money counter, group fields mirrored for 5068). No balance and no asset: a level reward's
    // goods are granted by its own cd13e/advguild-award receipt, and takenGroupRewardLevel is only that receipt's
    // once-only marker. The adventure_group / adventure_group_member tables hold names, rosters, the tree and group EXP
    // (a progression counter, not a currency); they are classified as social tables.
    // Pass play: role.activity_runtime PassPlay (NewActivityService.PassPlay) holds the day's and week's challenge
    // progress, daily EXP caps, paid chapters and EXP counters. No balance and no asset: pass EXP is currency 132, granted
    // by the pass/play receipt that also saves these markers.
    // Titles: role.title is DBTitleInfo (the title rank). Not a balance: a rank's Crests are paid by its own
    // cd13a/character-title receipt that also saves it.
    // Astral Level: role.paragon is DBParagonLevel (points per star and the installed Star Cores as ItemBaseInfo); the
    // level and its EXP are role.paragon_level / paragon_exp, a progression counter. Not a balance: an installed core
    // left the bag through its cd13b/paragon-* receipt (a ParagonCost sink) and comes back through one (ParagonGoods).
    // Valhalla Trials: role.heroic_spirit is DBOdinStatue (Blessing statue EXP and level). Not a balance: its EXP is
    // bought with Einherjar Coins by the cd13b/valhalla-pray receipt that also saves it. Cleared layers are
    // dungeons Records "valhalla:<layer>", written with the layer's reward by the dungeon obligation.
    // Monster Hunt: role.area_concur is DBAreaConcurInfo (kill counters per monster and map, claimed stage markers,
    // the area's claimed-stage count). No balance: kill counters ride the monster-kill receipt, and a claim's goods are
    // granted by its own cd13e/monsterhunt-award receipt whose claimed marker is only that receipt's once-only source.
    // Growth Classroom: role.growth_class is DBGrowthClassInfo (only rankReward, the claimed rank markers; the rest of
    // 5045 is derived from the quests). No balance: a rank's goods are granted by its own cd13e/growthclass-award receipt.
    // Codex monsters: role.library monsters (kill count, unlock flag) ride the monster-kill receipt; the unlock's
    // Collection Points are granted by its own cd13e/library-award receipt whose unlock flag is that receipt's marker.
    // A card collection unlock (82404) with points is a cd13e/library-award receipt the same way.
    // Seven-Day Goal: role.first_heart is DBFirstHeartTaskActivityData (task progress and status, score bar, claimed score
    // rewards). No balance: a task or score claim's goods come with its own cd13e/firstheart-award receipt, whose claimed
    // marker is only that receipt's once-only source.
    // Profile: role.personal is DBPersonalInfo (card presets, current preset, upvote times). No balance and no asset:
    // stickers placed on a card are counted against the owned currency, never spent. role.personal_upvote is
    // PersonalUpvoteList (who liked the card, when) and role.popularity is DBPopularityStar (popularity and affection
    // credited by friend gifts): counters, no balance and no asset; the gift item is spent by the friend-gift receipt.
    // Rift Crisis: role.timed_battle (TimedBattleService.State) OnePieceExtraDay/OnePieceExtraUsed count the day's extra
    // awards (RestrictConfig 1020) and OnePieceWeek/OnePieceWeekItems the week's capped extra items. No balance: the goods
    // come with the timed_battle_obligation the same settlement writes; these are only its limits. The legacy per-day
    // OnePieceGains/OnePieceItemGains are read and dropped on the next Rift payment.
    // Item actions: role.social_outfit is DBSocialOutfit (chat cosmetics unlocked by 349 items, with unlock and expiry
    // times, and the worn choices) and role.emoji_info is DBEmojiInfo (chat emoticon ids unlocked by 212 items). Unlocks,
    // no balance and no asset: the item is spent by the cd13a/character-item-action receipt that writes them.
    // Power: role.emblems EmblemState.Power (PowerService.Record) holds the role's last published power total and its
    // best total (maxHistoryPower), as normal/season parts. Rating counters, no balance and no asset: they only gate
    // Become Stronger stage claims (whose goods come with their own daily-stage receipt) and rank the Hero Registry.
    // Realm of the Gods: dungeons Honors (Final Trial goal states) and BlessingsSeen (acknowledged blessing levels) are
    // markers; a Received honor is only the once-only source of its event/dungeon/honor-award receipt. server_state
    // multi_dungeon_server (DBMultiDungeonServerInfo) holds the realm's first clears: the hall of fame keeps the clearing
    // roles' equipment as a display snapshot, never a holding.
    // Mock Battle: role.timed_battle CasualMmr/CasualRecords/CasualBest/CasualTotal are a rating and statistics, and
    // CasualSettled the once-only markers of each match's event/casual-battle receipt; no balance, no asset.
    // casual_battle_obligation.payout is CasualBattleService.Payout: a frozen match result (rating delta, record,
    // numbers) and the DropPool ids its payment rolls. No goods: they are granted by the role's event/casual-battle
    // receipt that also writes the timed_battle state and marks the row delivered.
    public const string ExpectedFormatManifestHash = "D5E7E8D429207929DD339BBB976CBDAEA16B4197AD5A969D76BFCE9CD3EC6E54";
    public static string FormatManifestHash => State.FormatManifestHash;

    private sealed partial class State
    {
        private static readonly Lazy<string> FormatManifest = new(ComputeFormatManifest);
        internal static string FormatManifestHash => FormatManifest.Value;

        private static string ComputeFormatManifest()
        {
            var entries = new SortedSet<string>(StringComparer.Ordinal);
            var seenTypes = new HashSet<Type>();
            var seenMessages = new HashSet<string>(StringComparer.Ordinal);
            void Message(MessageDescriptor descriptor)
            {
                if (!seenMessages.Add(descriptor.FullName)) return;
                foreach (var field in descriptor.Fields.InFieldNumberOrder())
                {
                    entries.Add($"proto:{descriptor.FullName}:{field.FieldNumber}:{field.Name}:{field.FieldType}:{field.IsRepeated}:{field.IsMap}:{field.IsRequired}");
                    if (field.FieldType == FieldType.Message) Message(field.MessageType);
                    if (field.FieldType == FieldType.Enum)
                        foreach (var value in field.EnumType.Values) entries.Add($"enum:{field.EnumType.FullName}:{value.Number}:{value.Name}");
                }
            }
            void TypeShape(Type type)
            {
                if (!seenTypes.Add(type)) return;
                if (type.IsArray) { TypeShape(type.GetElementType()!); return; }
                if (type.IsGenericType)
                    foreach (var argument in type.GetGenericArguments()) TypeShape(argument);
                if (type.IsEnum)
                {
                    foreach (string name in Enum.GetNames(type)) entries.Add($"enum:{type.FullName}:{name}:{Enum.Format(type, Enum.Parse(type, name), "D")}");
                    return;
                }
                if (type.Assembly != typeof(EconomySnapshotReader).Assembly) return;
                foreach (var p in type.GetProperties(BindingFlags.Instance | BindingFlags.Public).OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    if (p.GetIndexParameters().Length != 0 || p.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition == JsonIgnoreCondition.Always) continue;
                    entries.Add($"json:{type.FullName}:{p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? p.Name}:{p.PropertyType.FullName}:{p.GetCustomAttribute<JsonConverterAttribute>()?.ConverterType?.FullName}");
                    TypeShape(p.PropertyType);
                }
                foreach (var f in type.GetFields(BindingFlags.Instance | BindingFlags.Public).Where(x => x.IsDefined(typeof(JsonIncludeAttribute))))
                {
                    entries.Add($"field:{type.FullName}:{f.Name}:{f.FieldType.FullName}");
                    TypeShape(f.FieldType);
                }
            }
            foreach (var pair in ProtoDescriptors.OrderBy(x => x.Key, StringComparer.Ordinal))
            { entries.Add($"role-proto:{pair.Key}:{pair.Value.FullName}"); Message(pair.Value); }
            foreach (var pair in JsonRoleTypes.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                entries.Add($"role-json:{pair.Key}:{pair.Value}");
                var type = typeof(EconomySnapshotReader).Assembly.GetType("Ro3Emu.Server.Game." + pair.Value);
                if (type == null) entries.Add("missing:" + pair.Value); else TypeShape(type);
            }
            entries.Add("role-string-set:task_followup:System.String");
            entries.Add("dungeon_reward_obligation:Ro3Emu.Server.Accounts.DungeonRewardObligation");
            TypeShape(typeof(Ro3Emu.Server.Accounts.DungeonRewardObligation));
            entries.Add("casual_battle_obligation:Ro3Emu.Server.Game.CasualBattleService+Payout");
            TypeShape(typeof(Ro3Emu.Server.Game.CasualBattleService.Payout));
            // These JSON wrappers carry embedded protobuf bytes in addition to their explicit DTO graph.
            Message(Romsg.DBMountsInfo.Descriptor);
            Message(Romsg.DBLotteryData.Descriptor);
            Message(Romsg.DBDailyActivityInfo.Descriptor);
            entries.Add("activity_eve_global:romsg.DBSeasonEveInfo");
            Message(Romsg.DBSeasonEveInfo.Descriptor);
            entries.Add("festival_round:Ro3Emu.Server.Game.FestivalService.Round");
            TypeShape(typeof(Ro3Emu.Server.Game.FestivalService.Round));
            entries.Add("server_state/season_history:Ro3Emu.Server.Game.SeasonService.Season");
            TypeShape(typeof(Ro3Emu.Server.Game.SeasonService.Season));
            entries.Add("server_state/multi_dungeon_server:romsg.DBMultiDungeonServerInfo");
            Message(Romsg.DBMultiDungeonServerInfo.Descriptor);
            return Hash(string.Join("\n", entries));
        }
    }
}
