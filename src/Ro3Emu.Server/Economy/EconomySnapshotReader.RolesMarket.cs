using System.Globalization;
using System.Text;
using System.Text.Json;
using Google.Protobuf.Reflection;
using Romsg;

namespace Ro3Emu.Server.Economy;

public static partial class EconomySnapshotReader
{
    private sealed partial class State
    {
        internal void ReadRoles()
        {
            if (!Has("role")) return;
            var columns = Columns("role");
            if (!columns.ContainsKey("id")) return;
            string select = string.Join(",", new[] { "id", "deleted_time", "currency" }.Concat(RoleBlobs.Keys)
                .Where(columns.ContainsKey).Select(n => "\"" + n + "\""));
            using var cmd = Command("SELECT " + select + " FROM role ORDER BY id");
            using var r = cmd.ExecuteReader();
            var names = Enumerable.Range(0, r.FieldCount).Select(r.GetName).ToArray();
            long rowIndex = 0;
            while (r.Read())
            {
                Guard("role/row/" + rowIndex++, () =>
                {
                    long role = SqlLong(r, 0);
                    if (role <= 0) throw new FormatException("Invalid role id.");
                    roles.Add(role);
                    unknownRoleDeletion.Add(role); // Quarantine until a valid deletion state has been read.
                    Measure("role", 0);
                    for (int i = 1; i < r.FieldCount; i++)
                    {
                        string name = names[i], key = $"role/{role}/{name}";
                        if (name == "deleted_time")
                        {
                            if (r.IsDBNull(i)) Issue(EconomyIssueKind.Corruption, key, "Persisted deletion time is NULL.");
                            else Guard(key, () =>
                            {
                                long deletedTime = SqlLong(r, i);
                                if (deletedTime < 0) throw new FormatException("Negative role deletion time.");
                                if (deletedTime > 0) deletedRoles.Add(role);
                                unknownRoleDeletion.Remove(role);
                            });
                            continue;
                        }
                        if (r.IsDBNull(i))
                        {
                            if (name == "currency") Issue(EconomyIssueKind.Corruption, key, "Persisted currency purse is NULL.");
                            continue;
                        }
                        if (name == "currency")
                        {
                            Guard(key, () =>
                            {
                                string currencyText = SqlText(r, i);
                                PayloadBytes("role", Encoding.UTF8.GetByteCount(currencyText));
                                ParseCurrency(currencyText, role, key);
                            });
                            continue;
                        }
                        Guard(key, () =>
                        {
                            byte[] value = SqlBlob(r, i);
                            if (value.Length == 0) return;
                            var m = Metrics.GetValueOrDefault("role");
                            Metrics["role"] = (m.Rows, m.Blobs + 1, checked(m.Bytes + value.Length));
                            DecodeRoleBlob(name, value, role, key);
                        });
                    }
                });
            }
        }

        private void ParseCurrency(string value, long role, string key)
        {
            if (value.Length == 0) return;
            var seen = new HashSet<long>();
            foreach (string pair in value.Split(','))
            {
                var parts = pair.Split(':');
                if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long id)
                    || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long quantity)
                    || id <= 0 || !seen.Add(id)) throw new FormatException("Invalid or duplicate currency pair.");
                Currency(id, quantity, Role(role, key), EconomyCompartment.Currency, key + "/" + id);
            }
        }

        private void DecodeRoleBlob(string name, byte[] value, long role, string key)
        {
            if (!RoleBlobs.TryGetValue(name, out var format))
            { Issue(EconomyIssueKind.UnknownSchema, key, "Unclassified role BLOB."); return; }
            if (format == BlobFormat.RawEpoch)
            {
                string epoch = new UTF8Encoding(false, true).GetString(value);
                string[] parts = epoch.Split(':');
                if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long book)
                    || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks)
                    || book <= 0 || ticks <= 0) throw new FormatException("Invalid raw totem refinement epoch.");
                return;
            }
            if (format == BlobFormat.Protobuf)
            {
                if (name == "skill_plans" && value[0] == (byte)'{')
                { Issue(EconomyIssueKind.Coverage, key, "Legacy JSON skill plans require migration classification."); return; }
                if (!ProtoDescriptors.TryGetValue(name, out var descriptor))
                { Issue(EconomyIssueKind.UnknownSchema, key, "No pinned protobuf descriptor."); return; }
                ValidateProto(value, descriptor);
                return;
            }
            using var doc = Json(value);
            if (format == BlobFormat.StringSet)
            {
                // RoleStore persists only source IDs for required task-condition reevaluation.
                // The bag/award is already committed; these IDs are neither another grant nor a balance.
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    throw new JsonException("Expected task follow-up source array.");
                var sources = new HashSet<string>(StringComparer.Ordinal);
                foreach (var source in doc.RootElement.EnumerateArray())
                    if (source.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(source.GetString())
                        || !sources.Add(source.GetString()!))
                        throw new JsonException("Invalid task follow-up source ID.");
                if (sources.Count > 0)
                    Issue(EconomyIssueKind.RecoveryDebt, key, "Committed award has required task follow-up pending.");
                return;
            }
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Expected JSON object.");
            if (format != BlobFormat.Mailbox) ValidateRoleJson(name, doc.RootElement, key);
            switch (format)
            {
                case BlobFormat.Mailbox: ReadMailbox(doc.RootElement, role, key); break;
                case BlobFormat.TerritoryRole: ReadTerritoryRole(doc.RootElement, role, key); break;
                case BlobFormat.Json:
                    if (name == "guild_challenge") Fields(doc.RootElement, "Granted");
                    if (name == "activity_runtime") ClassifyActivityRuntime(doc.RootElement, key);
                    break;
            }
        }

        private static readonly Dictionary<string, MessageDescriptor> ProtoDescriptors = new(StringComparer.Ordinal)
        {
            ["settings"] = DBSettingInfo.Descriptor, ["skill_plans"] = DBSkillData.Descriptor,
            ["tasks"] = DBTaskInfo.Descriptor, ["guides"] = DBGuideInfo.Descriptor, ["emoji_play"] = DBEmojiPlayInfo.Descriptor,
            ["achievements"] = DBRoleAchievementInfo.Descriptor, ["adventure_group"] = DBAdventureGroupRelationData.Descriptor,
            ["library"] = DBLibraryInfo.Descriptor, ["card_plans"] = DBStorageHoleInfo.Descriptor,
            ["trade_info"] = DBUserTradeInfo.Descriptor, ["stalls_info"] = DBStallsInfo.Descriptor,
            ["auction"] = DBAuction.Descriptor, ["guild_info"] = DBRoleGuildInfo.Descriptor,
            ["tower"] = DBClimTower.Descriptor, ["lifejob"] = DBLifeJobInfo.Descriptor,
            ["life_restrict"] = DBRestrictInfo.Descriptor, ["season_talent"] = DBSeasonTalent.Descriptor,
            ["season_page"] = DBSeasonPage.Descriptor, ["season_refine"] = DBSeasonRefine.Descriptor,
            ["equip_plans"] = DBEquipInfo.Descriptor, ["auto_decompose"] = AutoDecomposeSetting.Descriptor,
            ["bag_state"] = DBItemsInfo.Descriptor, ["stat_plans"] = BaseAttrInfo.Descriptor,
            ["pets"] = DBPetInfo.Descriptor, ["wardrobe"] = DBWardRobeInfo.Descriptor,
            ["totems"] = DBTotemInfo.Descriptor, ["genre"] = DBGenre.Descriptor, ["career_special"] = DBCareerSpecial.Descriptor, ["heroic_spirit"] = DBOdinStatue.Descriptor, ["area_concur"] = DBAreaConcurInfo.Descriptor, ["growth_class"] = DBGrowthClassInfo.Descriptor, ["first_heart"] = DBFirstHeartTaskActivityData.Descriptor, ["personal"] = DBPersonalInfo.Descriptor, ["personal_upvote"] = PersonalUpvoteList.Descriptor, ["popularity"] = DBPopularityStar.Descriptor, ["social_outfit"] = DBSocialOutfit.Descriptor, ["emoji_info"] = DBEmojiInfo.Descriptor,
            ["new_activities"] = DBNewActivityInfo.Descriptor, ["training"] = DBTrainingGroundInfo.Descriptor
        };

        // Decode with the pinned generated descriptor rather than accepting protobuf's UnknownFieldSet.
        private static void ValidateProto(ReadOnlySpan<byte> bytes, MessageDescriptor descriptor, int depth = 0)
        {
            if (depth > 32) throw new FormatException("Protobuf nesting exceeds decoder bound.");
            int pos = 0;
            while (pos < bytes.Length)
            {
                if (!Varint(bytes, ref pos, out ulong tag) || tag >> 3 == 0 || tag >> 3 > int.MaxValue)
                    throw new FormatException("Invalid protobuf tag.");
                var field = descriptor.Fields.InFieldNumberOrder().FirstOrDefault(f => f.FieldNumber == (int)(tag >> 3));
                if (field == null) throw new UnknownPersistedFieldException($"Unknown protobuf field {tag >> 3} in {descriptor.FullName}.");
                int wire = (int)(tag & 7);
                int expected = field.FieldType switch
                {
                    FieldType.Double or FieldType.Fixed64 or FieldType.SFixed64 => 1,
                    FieldType.Float or FieldType.Fixed32 or FieldType.SFixed32 => 5,
                    FieldType.String or FieldType.Bytes or FieldType.Message => 2,
                    _ => 0
                };
                if (wire != expected && !(wire == 2 && field.IsRepeated && expected != 2))
                    throw new FormatException($"Wrong protobuf wire type for {descriptor.FullName}.{field.Name}.");
                switch (wire)
                {
                    case 0:
                        if (!Varint(bytes, ref pos, out ulong number)) throw new FormatException("Truncated protobuf varint.");
                        ValidateEnum(field, number);
                        break;
                    case 1: if (bytes.Length - pos < 8) throw new FormatException("Truncated protobuf fixed64."); pos += 8; break;
                    case 2:
                        if (!Varint(bytes, ref pos, out ulong length) || length > (ulong)(bytes.Length - pos))
                            throw new FormatException("Truncated protobuf payload.");
                        if (field.FieldType == FieldType.Message)
                            ValidateProto(bytes.Slice(pos, (int)length), field.MessageType, depth + 1);
                        else if (expected != 2)
                            ValidatePacked(bytes.Slice(pos, (int)length), field, expected);
                        pos += (int)length; break;
                    case 5: if (bytes.Length - pos < 4) throw new FormatException("Truncated protobuf fixed32."); pos += 4; break;
                    default: throw new FormatException("Unsupported protobuf group wire type.");
                }
            }
            if (depth == 0)
            {
                try { descriptor.Parser.ParseFrom(bytes.ToArray()); }
                catch (Google.Protobuf.InvalidProtocolBufferException e)
                { throw new FormatException("Malformed protobuf payload.", e); }
            }
        }
        private static void ValidateEnum(FieldDescriptor field, ulong number)
        {
            if (field.FieldType == FieldType.Enum && !field.EnumType.Values.Any(v => v.Number == unchecked((int)number)))
                throw new UnknownPersistedFieldException($"Unknown protobuf enum value {number} in {field.ContainingType.FullName}.{field.Name}.");
        }
        private static void ValidatePacked(ReadOnlySpan<byte> bytes, FieldDescriptor field, int expected)
        {
            if (expected is 1 or 5)
            {
                int width = expected == 1 ? 8 : 4;
                if (bytes.Length % width != 0) throw new FormatException("Truncated packed protobuf fixed-width value.");
                return;
            }
            int position = 0;
            while (position < bytes.Length)
            {
                if (!Varint(bytes, ref position, out ulong number)) throw new FormatException("Truncated packed protobuf varint.");
                ValidateEnum(field, number);
            }
        }
        private static bool Varint(ReadOnlySpan<byte> bytes, ref int pos, out ulong value)
        {
            value = 0;
            for (int i = 0; i < 10 && pos < bytes.Length; i++)
            {
                byte b = bytes[pos++];
                if (i == 9 && b > 1) return false;
                value |= (ulong)(b & 127) << (7 * i);
                if ((b & 128) == 0) return true;
            }
            return false;
        }

        private void ClassifyActivityRuntime(JsonElement value, string key)
        {
            Fields(value, "PassProgress MonthCards AdventureFragmentAsks EveSettledCycleStart AdventureTreasure PassPlay");
            if (!Try(value, "AdventureTreasure", out var cursor)) return;
            Fields(cursor, "PointId GroupId ItemId EventId Began PendingMonsterUid PendingMonsterAward PendingMonsterId");
            // Active hunt/monster cursor is a restorable paid opportunity. The award is not earned until its kill.
            if (Long(cursor, "PendingMonsterAward") > 0 && Long(cursor, "PendingMonsterId") <= 0)
                Issue(EconomyIssueKind.Corruption, key + "/AdventureTreasure", "Pending monster award has no monster identity.");
        }

        private void ReadMailbox(JsonElement box, long role, string key)
        {
            Fields(box, "NextId Letters");
            if (!Try(box, "Letters", out var letters)) throw new JsonException("Mailbox Letters are absent.");
            int index = 0;
            foreach (var letter in Elements(letters))
            {
                Fields(letter, "Id Category CreatedTime RemoveTime Title Content Abstract Sender SenderId TemplateId ImportantType DeliveryKey SourceKey ContentParams Read Claimed Attachments Items Currency");
                string id = String(letter, "Id", "index-" + index++);
                string path = key + "/" + id;
                mailDeliveryKeys.Add(role + "/" + id); // Legacy migration preserves row id, even after claim.
                string delivery = String(letter, "DeliveryKey", String(letter, "SourceKey"));
                if (delivery.Length > 0) mailDeliveryKeys.Add(role + "/" + delivery);
                if (Bool(letter, "Claimed")) continue;
                var owner = Role(role, key);
                if (Try(letter, "Attachments", out var attachments))
                {
                    int n = 0;
                    foreach (var a in Elements(attachments))
                    {
                        Fields(a, "Entry Count Bind");
                        Entry(Long(a, "Entry"), Long(a, "Count"), owner, EconomyCompartment.Mail,
                            path + "/attachment-" + n++, GrantFingerprint(Long(a, "Bind")));
                    }
                }
                if (Try(letter, "Items", out var items))
                {
                    int n = 0;
                    foreach (var item in Elements(items)) Item(item, owner, EconomyCompartment.Mail, path + "/item-" + n++);
                }
                if (Try(letter, "Currency", out var currency))
                    foreach (var p in Properties(currency))
                        Currency(long.Parse(p.Name, CultureInfo.InvariantCulture), p.Value.GetInt64(), owner,
                            EconomyCompartment.Mail, path + "/currency-" + p.Name);
            }
        }

        internal void ReadItems()
        {
            if (!Has("item") || !new[] { "guid", "role_id", "entry", "stack", "pack", "sort", "grid", "attrs", "enchantments", "str_attrs", "bind_type", "bind_expire", "is_locked" }
                .All(Columns("item").ContainsKey)) return;
            using var cmd = Command("SELECT guid,role_id,entry,stack,pack,sort,grid,attrs,enchantments,str_attrs,bind_type,bind_expire,is_locked FROM item ORDER BY guid");
            using var r = cmd.ExecuteReader();
            long rowIndex = 0;
            while (r.Read())
            {
                Guard("item/row/" + rowIndex++, () =>
                {
                    long guid = SqlLong(r, 0), role = SqlLong(r, 1);
                    string key = $"item/{guid}";
                    Guard(key, () =>
                    {
                        long entry = SqlLong(r, 2), stack = SqlLong(r, 3), pack = SqlLong(r, 4), sort = SqlLong(r, 5), grid = SqlLong(r, 6);
                        long bindType = SqlLong(r, 10), bindExpire = SqlLong(r, 11), locked = SqlLong(r, 12);
                        if (guid <= 0 || role <= 0 || entry <= 0 || pack < 0 || sort < 0 || bindType < 0 || bindExpire < 0 || locked is not (0 or 1))
                            throw new FormatException("Invalid inventory identity/location or binding.");
                        var extras = new Dictionary<string, JsonElement>();
                        foreach (var pair in new[] { (7, "Attrs"), (8, "Enchantments"), (9, "StringAttrs") })
                        {
                            if (r.IsDBNull(pair.Item1)) continue;
                            using var doc = Json(SqlText(r, pair.Item1));
                            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Invalid item metadata.");
                            extras.Add(pair.Item2, doc.RootElement.Clone());
                        }
                        string Extra(string name) => extras.TryGetValue(name, out var value) ? Canon(value) : "{}";
                        string fingerprint = Hash($"Attrs={Extra("Attrs")}|StringAttrs={Extra("StringAttrs")}" +
                            $"|Enchantments={Extra("Enchantments")}|BindType={bindType}|BindExpire={bindExpire}|IsLocked={locked}");
                        Entry(entry, stack, Role(role, key), EconomyCompartment.Inventory,
                            key + $"/pack-{pack}/grid-{grid}/sort-{sort}", fingerprint, instance: Id(guid));
                    });
                    Measure("item", Enumerable.Range(7, 3).Where(i => !r.IsDBNull(i)).Sum(i => Encoding.UTF8.GetByteCount(SqlText(r, i))));
                });
            }
        }

        internal void ReadMarkets()
        {
            if (!Has("market_record") || !new[] { "kind", "id", "data" }.All(Columns("market_record").ContainsKey)) return;
            using var cmd = Command("SELECT kind,id,data FROM market_record ORDER BY kind,id");
            using var r = cmd.ExecuteReader();
            long rowIndex = 0;
            while (r.Read())
            {
                Guard("market/row/" + rowIndex++, () =>
                {
                    string kind = SqlText(r, 0), id = SqlText(r, 1), data = SqlText(r, 2), key = "market/" + kind + "/" + id;
                    Measure("market_record", Encoding.UTF8.GetByteCount(data), true);
                    Guard(key, () =>
                    {
                        using var doc = Json(data);
                        var row = doc.RootElement;
                        if (row.ValueKind != JsonValueKind.Object && kind != "trade_sequence") throw new JsonException("Expected market row object.");
                        switch (kind)
                        {
                            case "trade_listing":
                                Fields(row, "Sku Spu SellerId Entry Currency UnitPrice Quantity SaleTimeMs PublicUntilMs ExpiresAtMs SpecialType Item");
                                long seller = Long(row, "SellerId"), quantity = Long(row, "Quantity");
                                if (!Try(row, "Item", out var item) || Long(item, "Stack") != quantity || Long(item, "Entry") != Long(row, "Entry"))
                                    throw new FormatException("Trade listing item/quantity mismatch.");
                                Item(item, Role(seller, key), EconomyCompartment.TradeListing, key); break;
                            case "trade_preorder":
                                Fields(row, "Id Sku Spu BuyerId Entry Quantity Price Currency TimeMs Sequence Status Hidden Item");
                                long status = Long(row, "Status"), count = Long(row, "Quantity"), price = Long(row, "Price");
                                if (status is < 0 or > 5 || count <= 0 || price <= 0) throw new FormatException("Invalid preorder status or price.");
                                if (status == 0) Currency(Long(row, "Currency"), checked(count * price), Role(Long(row, "BuyerId"), key),
                                    EconomyCompartment.TradePreorder, key); break;
                            case "stall":
                                Fields(row, "Key Owner SystemId Scene LineNumber Title OwnerName Career Sex Level Area Appearance X Y Z OpenTime Timeout RestockTime MaxGrid NextItemId Ended EndMailId Stock Records Gains TaxRate");
                                long ownerId = Long(row, "Owner"), system = Long(row, "SystemId");
                                if (ownerId <= 0 && system <= 0) throw new FormatException("Stall has no owner identity.");
                                var owner = ownerId > 0 ? Role(ownerId, key) : new EconomyOwner(EconomyOwnerKind.System, Id(system));
                                if (!Try(row, "Stock", out var stock)) throw new JsonException("Stall stock missing.");
                                int n = 0;
                                foreach (var slot in Elements(stock))
                                {
                                    Fields(slot, "Id Item Price Currency SystemConfig Bought");
                                    if (!Try(slot, "Item", out var held)) throw new JsonException("Stall stock item missing.");
                                    long stockCount = Long(held, "Stack");
                                    if (stockCount < 0 || stockCount == 0 && ownerId > 0)
                                        throw new FormatException("Invalid player or negative stall stock.");
                                    string slotKey = key + "/" + Long(slot, "Id", n++);
                                    if (stockCount > 0) Item(held, owner, EconomyCompartment.Stall, slotKey);
                                    if (ownerId > 0 && Bool(row, "Ended")) Issue(EconomyIssueKind.Corruption, key, "Ended player stall retains stock.");
                                }
                                break;
                            case "mail":
                                Fields(row, "Id RoleId TemplateId CreatedTime RemoveTime Read Claimed Items Currency ContentParams");
                                if (Bool(row, "Claimed")) break;
                                long recipient = Long(row, "RoleId");
                                if (recipient <= 0) throw new FormatException("Legacy mail recipient is invalid.");
                                string effectiveId = String(row, "Id");
                                if (effectiveId.Length == 0) effectiveId = id;
                                var receiver = Role(recipient, key);
                                if (Try(row, "Items", out var legacyItems))
                                {
                                    int i = 0; foreach (var held in Elements(legacyItems)) Item(held, receiver, EconomyCompartment.LegacyMail, key + "/item-" + i++);
                                }
                                if (Try(row, "Currency", out var legacyCurrency))
                                    foreach (var p in Properties(legacyCurrency))
                                        Currency(long.Parse(p.Name, CultureInfo.InvariantCulture), p.Value.GetInt64(), receiver,
                                            EconomyCompartment.LegacyMail, key + "/currency-" + p.Name);
                                if (mailDeliveryKeys.Contains(recipient + "/" + effectiveId))
                                    Issue(EconomyIssueKind.Corruption, key, "Legacy row appears already migrated to canonical mailbox.");
                                break;
                            case "trade_history":
                                Fields(row, "Id Sku Spu SellerId BuyerId Entry Quantity UnitPrice TimeMs Item");
                                if (Try(row, "Item", out var historyItem))
                                    Fields(historyItem, "Guid Entry Stack Pack Sort Grid IsLocked Attrs StringAttrs BindType BindExpire ExpireTimeStamp Enchantments");
                                break;
                            case "trade_sequence": break; // Allocator, never a purse.
                            default: Issue(EconomyIssueKind.UnknownSchema, key, "Unclassified market_record.kind."); break;
                        }
                    });
                });
            }
        }
    }
}
