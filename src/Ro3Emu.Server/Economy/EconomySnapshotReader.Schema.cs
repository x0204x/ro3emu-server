namespace Ro3Emu.Server.Economy;

public static partial class EconomySnapshotReader
{
    // Asserted against CoreSchemaPlans and every CREATE TABLE in Accounts/Persistence and Game (E00).
    // An added table/column, including non-BLOB assets, is an inventory change requiring review.
    private static readonly Dictionary<string, string> KnownColumns = new(StringComparer.Ordinal)
    {
        ["account"] = "name password_hash salt created_utc",
        ["auth_token"] = "token kind account expires_utc",
        ["role_lifecycle"] = "role_id epoch deleted",
        ["owner_revision"] = "kind id part revision",
        ["owner_operation_receipt"] = "operation_id family result",
        ["economy_meta"] = "singleton realm_id next_sequence retained_floor",
        ["operation_receipt"] = "operation_id realm_id commit_seq actor_account actor_role command_kind request_fingerprint intent_hash intent_payload source_key recorded_utc_ms effective_utc_ms outcome child_receipts before_revisions after_revisions leg_count",
        ["operation_source"] = "realm_id source_key operation_id",
        ["economy_entry"] = "entry_id commit_seq operation_id leg_no recorded_utc_ms effective_utc_ms owner_kind owner_id compartment_kind compartment_key instance_key asset_kind asset_id asset_epoch delta reason_code leg_kind edge_no counterparty_owner_kind counterparty_owner_id counterparty_compartment_kind counterparty_compartment_key counterparty_instance_key before_fingerprint after_fingerprint source_key",
        ["dungeon_reward_obligation"] = "source_key role_id room_id activated completed state",
        ["role"] = "id account area name sex career level hair create_time last_login_time scene_entry pos_x pos_y pos_z face settings job_level exp job_exp stats bonus_points currency starter_kit skills skill_bar skill_plans tasks task_runtime task_followup guides emoji_play achievements achievement_runtime adventure_group library card_plans shop trade_info stalls_info mailbox auction guild_info guild_challenge pet_books daily new_activities activity_runtime timed_activity timed_battle training lottery tower wild_afk dungeons territory_wars territory_escort lifejob life_restrict life_runtime season_talent season_page season_page_meta season_refine equip_levels equip_plans auto_decompose bag_state stat_plans world_state emblems pets mounts wardrobe totems totem_refine_epoch genre career_special heroic_spirit area_concur growth_class first_heart personal personal_upvote popularity social_outfit emoji_info deleted_time",
        ["server_state"] = "key value", ["tower_first_pass"] = "floor record",
        ["item"] = "guid role_id entry stack pack sort grid attrs enchantments str_attrs is_locked bind_type bind_expire bind expire_time",
        ["friend"] = "owner other since intimacy", ["friend_apply"] = "target applicant time",
        ["blacklist"] = "owner other", ["friend_metadata"] = "role data",
        ["friend_chat_group"] = "id owner name created", ["friend_chat_member"] = "group_id role dnd",
        ["recent_team_peer"] = "owner other last_seen",
        ["adventure_group"] = "id name state", ["adventure_group_member"] = "role_id group_id",
        ["market_record"] = "kind id data",
        ["auction_item"] = "id source_key state", ["auction_run"] = "id state",
        ["auction_bonus"] = "role_id group_key amount claimed guild_id", ["auction_source"] = "source_key",
        ["auction_prebuy"] = "id role_id auction_type src_type item_id quantity unit_price currency revision processed requested_id paused",
        ["guild"] = "id name state", ["guild_member"] = "role_id guild_id", ["guild_market"] = "id state",
        ["guild_recovery"] = "guild_id state created_ms", ["guild_event"] = "guild_id idx data",
        ["guild_caravan_member"] = "role_id state revision", ["guild_caravan_version"] = "guild_id day revision",
        ["guild_campfire"] = "guild_id state", ["guild_hunting"] = "cycle_start data runtime", ["guild_challenge"] = "guild_id state",
        ["guild_challenge_clear"] = "id guild_id season cycle difficulty pending state",
        ["territory_wars"] = "id state", ["territory_wars_seasons"] = "epoch state",
        ["territory_escort"] = "id state", ["guild_league"] = "id state", ["festival_round"] = "cycle state",
        ["festival_reward"] = "cycle role_id source award claimed grants",
        ["mvp_award_obligation"] = "source_key cfg_id death_ms player_id awards result delivered",
        ["mvp_settlement"] = "source_key cfg_id death_ms",
        ["timed_battle_obligation"] = "source_key match_id entry role_id grants costs next_state result delivered",
        ["casual_battle_obligation"] = "source_key match_id role_id payout delivered",
        ["activity_pre_progress"] = "activity_id cycle_start area score",
        ["activity_pre_donation_area"] = "activity_id cycle_start area day role_id amount",
        ["activity_eve_global"] = "activity_id cycle_start blob",
        ["mvp_state"] = "cfg_id state state_time dead_ms", ["mvp_line_cooldown"] = "cfg_id death_ms",
        ["mvp_player"] = "player_id first_num end_num join_num team_num last_time",
        ["prontera_defense_world"] = "season_open map_id defense_point",
        ["prontera_defense_station"] = "season_open guild_id map_id"
    };

    private sealed partial class State
    {
        private void CheckAllColumns()
        {
            foreach (string table in tables)
            {
                if (!KnownColumns.TryGetValue(table, out string? declared))
                { Issue(EconomyIssueKind.UnknownSchema, "schema/" + table, "Unclassified persisted table."); continue; }
                var allowed = declared.Split(' ').ToHashSet(StringComparer.Ordinal);
                foreach (string col in Columns(table).Keys)
                    if (!allowed.Contains(col))
                        Issue(EconomyIssueKind.UnknownSchema, "schema/" + table + "." + col,
                            "Unclassified persisted column; it may hold a new asset or obligation.");
            }
        }
    }
}
