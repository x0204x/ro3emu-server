using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;

namespace Ro3Emu.Server.Game;

/// <summary>Recommendation and sharing operations of MG_Genre; every record is bound to both role IDs and response ID.</summary>
public sealed partial class GenreService
{
    private const long CodeLifetime = 24 * 60 * 60; // (inferred) one-day share window; no original expiration policy recovered.

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private string NewShareCode(long now)
    {
        for (int attempt = 0; attempt < 32; attempt++)
        {
            string candidate = Random.Shared.Next(100000, 1000000).ToString();
            if (roles.FindGenreCode(candidate, now) == null) return candidate;
        }
        throw new InvalidOperationException("Unable to reserve a unique Build share code");
    }
    private GameSession? Online(long roleId) => sessions.All().FirstOrDefault(s => s.Role?.Id == roleId);
    private DBGenre? Stored(long roleId) => roles.Get(roleId) == null ? null
        : Online(roleId)?.Genre?.Clone() ?? (roles.LoadGenre(roleId) is { } bytes ? DBGenre.Parser.ParseFrom(bytes) : null);

    private static PlanGenreConcreteAndInfo? FindMy(DBGenre state, long seeker, long response) =>
        state.MyRecommend?.MyRecommendPlans.TryGetValue(seeker, out var group) == true
        && group.GenrePlans.TryGetValue(response, out var plan) ? plan : null;

    private static PlanGenreConcreteAndInfo? FindOther(DBGenre state, long responder, long response) =>
        state.OtherRecommend?.Recommend.TryGetValue(responder, out var group) == true
        && group.GenrePlans.TryGetValue(response, out var plan) ? plan : null;

    private static void Put(DBGenre seeker, DBGenre responder, long seekerId, long responderId, long responseId,
        PlanGenreConcreteAndInfo record)
    {
        seeker.OtherRecommend ??= new DBGenreOtherRecommend();
        responder.MyRecommend ??= new DBGenreMyRecommendPlan();
        if (!seeker.OtherRecommend.Recommend.TryGetValue(responderId, out var from))
            seeker.OtherRecommend.Recommend[responderId] = from = new DBGenrePlanRecommends();
        if (!responder.MyRecommend.MyRecommendPlans.TryGetValue(seekerId, out var to))
            responder.MyRecommend.MyRecommendPlans[seekerId] = to = new DBGenrePlanRecommends();
        from.GenrePlans[responseId] = record.Clone();
        to.GenrePlans[responseId] = record.Clone();
    }

    private async Task PublishPairAsync(GameSession sender, DBGenre self, long otherId, DBGenre other)
    {
        var target = Online(otherId);
        var tx = roles.NewTransaction();
        tx.RoleBlob(sender.Role!.Id, "genre", self.ToByteArray());
        tx.RoleBlob(otherId, "genre", other.ToByteArray());
        await roles.CommitAsync(tx, "cd13b/genre-pair", _ =>
        {
            sender.Genre = self;
            if (target != null) target.Genre = other;
        });
        await SyncAsync(sender);
        if (target != null)
        {
            await SyncAsync(target);
            await target.SendGameAsync(82822, new GenreUpdateNtf());
        }
    }

    internal async Task SeekAsync(GameSession s)
    {
        {
            int code = Gate(s);
            int share = 0;
            long now = Now();
            if (code == 0)
            {
                try
                {
                    share = int.Parse(NewShareCode(now));
                    var next = s.Genre!.Clone();
                    next.SeekerId = checked((int)s.Role!.Id);
                    next.CodeInfo = new SeekerCodeInfo { CodeInfo = share.ToString(), ExpireTime = now + CodeLifetime };
                    await SaveGenreAsync(s, next);
                    await SyncAsync(s);
                }
                catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or OverflowException)
                { log.LogWarning(e, "Build seek code failed"); code = ErrorCodes.Unknown; share = 0; }
            }
            await s.SendGameAsync(82800, new GenreSeekerResp
            {
                Result = code, Name = code == 0 ? s.Role!.Name : "", ZoneId = s.Role!.Area,
                SeekerTime = now, SeekerCode = share,
            });
        }
    }

    internal async Task CodeAsync(GameSession s)
    {
        {
            int code = Gate(s);
            string share = "";
            if (code == 0)
            {
                try
                {
                    long now = Now();
                    share = NewShareCode(now);
                    var next = s.Genre!.Clone();
                    next.SeekerId = checked((int)s.Role!.Id);
                    next.CodeInfo = new SeekerCodeInfo { CodeInfo = share, ExpireTime = now + CodeLifetime };
                    await SaveGenreAsync(s, next);
                    await SyncAsync(s);
                }
                catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or OverflowException)
                { log.LogWarning(e, "Build sharing code failed"); code = ErrorCodes.Unknown; share = ""; }
            }
            await s.SendGameAsync(82807, new GenreSeekerCodeResp { Result = code, Code = share });
        }
    }

    internal async Task RespondAsync(GameSession s, GenreResponserReq req)
    {
        int result = Gate(s), response = 0;
        {
            var seeker = Stored(req.SeekUserId);
            if (result == 0 && (req.SeekUserId == s.Role!.Id || seeker == null || seeker.SeekerId != req.SeekUserId
                || req.AreaId != roles.Get(req.SeekUserId)?.Area
                || seeker.CodeInfo?.CodeInfo != req.CodeId.ToString() || seeker.CodeInfo.ExpireTime <= Now()))
                result = ErrorCodes.Parameter;
            if (result == 0)
            {
                response = seeker!.ResponseId + 1;
                seeker.ResponseId = response;
                var mine = s.Genre!.Clone();
                var record = new PlanGenreConcreteAndInfo
                {
                    PlanBeginTime = Now(), RougeInfo = new GenreRougeRecommend
                    {
                        SeekUserId = req.SeekUserId, ResponserUserId = s.Role!.Id,
                        ResponseId = response, ZoneId = s.Role.Area,
                    },
                };
                Put(seeker, mine, req.SeekUserId, s.Role.Id, response, record);
                await PublishPairAsync(s, mine, req.SeekUserId, seeker);
            }
        }
        await s.SendGameAsync(82801, new GenreResponserResp { Result = result, ResponseId = response, SeekUserId = req.SeekUserId });
    }

    private async Task RespondCodeAsync(GameSession s, GenreResponseCodeReq req)
    {
        int result = Gate(s), response = 0;
        long seekerId = 0;
        {
            seekerId = roles.FindGenreCode(req.Code, Now()) ?? 0;
            if (result == 0 && (seekerId == 0 || seekerId == s.Role!.Id)) result = ErrorCodes.Parameter;
            if (result == 0)
            {
                var seeker = Stored(seekerId)!;
                response = seeker.ResponseId + 1;
                seeker.ResponseId = response;
                var mine = s.Genre!.Clone();
                Put(seeker, mine, seekerId, s.Role!.Id, response,
                    new PlanGenreConcreteAndInfo
                    {
                        PlanBeginTime = Now(), RougeInfo = new GenreRougeRecommend
                        { SeekUserId = seekerId, ResponserUserId = s.Role.Id, ResponseId = response, ZoneId = s.Role.Area },
                    });
                await PublishPairAsync(s, mine, seekerId, seeker);
            }
        }
        await s.SendGameAsync(82812, new GenreResponseCodeResp { Result = result, ResponseId = response, SeekUserId = seekerId });
    }

    internal async Task SaveRecommendationAsync(GameSession s, GenreResponserSaveReq req)
    {
        int result = Gate(s);
        {
            using var held = await CharacterService.Lock(s);
            var seeker = Stored(req.SeekUserId);
            var mine = s.Genre!.Clone();
            var plan = FindMy(mine, req.SeekUserId, req.ResponseId);
            if (result == 0 && (seeker == null || plan == null || FindOther(seeker, s.Role!.Id, req.ResponseId) == null
                || req.Plan == null || !OwnsConcrete(s, req.Plan))) result = ErrorCodes.Parameter;
            if (result == 0) result = ValidateOwnConcrete(s, req.Plan!);
            if (result == 0)
            {
                plan!.Plans = req.Plan!.Clone();
                plan.NotWork = false;
                Put(seeker!, mine, req.SeekUserId, s.Role!.Id, req.ResponseId, plan);
                await PublishPairAsync(s, mine, req.SeekUserId, seeker!);
            }
        }
        await s.SendGameAsync(82803, new GenreResponserSaveResp { Result = result });
    }

    /// <summary>Recommendations may describe only the sender's resources; the seeker is revalidated again on apply.</summary>
    private static bool OwnsConcrete(GameSession s, GenrePlanConcrete plan)
    {
        var itemIds = s.Items.Select(i => i.Guid).ToHashSet();
        if (plan.EquipPlan != null && plan.EquipPlan.ItemsEquipGrid.Values.Any(id => id != 0 && !itemIds.Contains(id))) return false;
        if (plan.TotemPlan != null && plan.TotemPlan.SetUpTotem.Values.Any(id => id != 0 && !itemIds.Contains(id))) return false;
        if (plan.EmblePlan != null && plan.EmblePlan.SetUpEmblem.Values.Any(id => id != 0 && !itemIds.Contains(id))) return false;
        if (plan.PetPlan != null && plan.PetPlan.SetUpPets.Values.Concat(plan.PetPlan.SupportPets.Values)
            .Any(id => id != 0 && s.Pets?.ListPet.ContainsKey(id) != true)) return false;
        if (plan.CardPlan != null && plan.CardPlan.CardInfo.Values.SelectMany(g => g.CardSlotInfo.Values)
            .Any(card => card.ItemGuid > 0 && !itemIds.Contains(card.ItemGuid))) return false;
        return true;
    }

    /// <summary>Resolve each recommended socket to a distinct owned copy with its actual star, treasure and binding.</summary>
    private static DBStoragePlanInfo? OwnedCardPlan(DBStoragePack source, IReadOnlyList<InvItem> owned)
    {
        var plan = new DBStoragePlanInfo();
        var used = new Dictionary<long, int>();
        foreach (var (grid, slots) in source.CardInfo.OrderBy(kv => kv.Key))
        {
            var row = new DBInsertCardInfo();
            foreach (var (socket, donor) in slots.CardSlotInfo.OrderBy(kv => kv.Key))
            {
                if (donor.ItemEntry == 0) continue;
                long star = donor.ItemInfos.TryGetValue(502, out var starAttr) ? starAttr.LongVal : 0;
                long treasured = donor.ItemInfos.TryGetValue(530, out var treasureAttr) ? treasureAttr.LongVal : 0;
                int bind = (int)(donor.ItemBindInfo?.ItemBindType ?? 0);
                long expire = donor.ItemBindInfo?.ExpireTimeStamp ?? 0;
                var item = owned.Where(i => i.Entry == donor.ItemEntry && i.Pack == 3 && i.Grid <= 0
                        && i.Stack > used.GetValueOrDefault(i.Guid)
                        && (i.Attrs?.GetValueOrDefault(502) ?? 0) == star
                        && (i.Attrs?.GetValueOrDefault(530) ?? 0) == treasured
                        && i.BindType == bind && i.BindExpire == expire)
                    .OrderBy(i => i.Guid).FirstOrDefault();
                if (item == null) return null;
                used[item.Guid] = used.GetValueOrDefault(item.Guid) + 1;
                row.CardSlotInfo[socket] = new DBSlotItemInfo
                {
                    ItemId = item.Entry, StarLevel = item.Attrs?.GetValueOrDefault(502) ?? 0,
                    Treasured = item.Attrs?.GetValueOrDefault(530) ?? 0,
                    ItemBindInfo = new ItemBindInfo
                    { ItemBindType = (ItemBindType)item.BindType, ExpireTimeStamp = item.BindExpire },
                };
            }
            plan.CardInfo[grid] = row;
        }
        return plan;
    }

    private int ValidateOwnConcrete(GameSession s, GenrePlanConcrete concrete)
    {
        if (concrete.SkillPlan == null && concrete.AttrPointPlan == null && concrete.EquipPlan == null
            && concrete.CardPlan == null && concrete.TotemPlan == null && concrete.EmblePlan == null
            && concrete.PetPlan == null && concrete.SeasonTalentPlan == null) return ErrorCodes.Parameter;
        var next = new Stage(s, skills);
        int code;
        if (concrete.AttrPointPlan != null)
        {
            next.Attrs.AllPlanBaseProperty[0] = concrete.AttrPointPlan.Clone();
            if ((code = character.StageStatPlan(next.Attrs, next.Progress, 0)) != 0) return code;
        }
        if (concrete.SkillPlan != null)
        {
            next.Skills.SkillAssignPlans[0] = concrete.SkillPlan.Clone();
            if ((code = skills.StageSkillPlan(s, next.Skills, next.Progress, 0)) != 0) return code;
        }
        if (concrete.EquipPlan != null)
        {
            int index = next.Equipment.CurrentEquipPlan == 0 ? 1 : 0;
            next.Equipment.EquipPlans[index] = concrete.EquipPlan.Clone();
            foreach (int slot in next.Equipment.EquipPlans[index].ItemsEquipGrid.Where(kv => kv.Value == 0).Select(kv => kv.Key).ToArray())
                next.Equipment.EquipPlans[index].ItemsEquipGrid.Remove(slot);
            if ((code = character.StageEquipmentPlan(next.Equipment, next.Items, index)) != 0) return code;
        }
        if (concrete.CardPlan != null)
        {
            int index = next.Cards.CurrentPlan == 1 ? 2 : 1;
            var copy = OwnedCardPlan(concrete.CardPlan, next.Items);
            if (copy == null) return ErrorCodes.ItemMissing;
            next.Cards.StoragePlans[index] = copy;
            if ((code = cards.StageGenrePlan(s, next.Cards, next.Items, index)) != 0) return code;
        }
        if (concrete.TotemPlan != null && (code = totems.ValidateSelection(s, concrete.TotemPlan)) != 0) return code;
        if (concrete.EmblePlan != null)
        {
            int index = next.Echoes.CurrentPlan == 0 ? 1 : 0;
            next.Echoes.Plans[index] = new EmblemService.Plan
            { Slots = concrete.EmblePlan.SetUpEmblem.Where(kv => kv.Value != 0).ToDictionary(kv => kv.Key, kv => kv.Value) };
            if ((code = emblems.ValidateGenrePlan(s, next.Echoes, index)) != 0) return code;
        }
        if (concrete.PetPlan != null)
        {
            int index = next.Pets.CurrentPlan == 0 ? 1 : 0;
            next.Pets.SetupPlans[index] = concrete.PetPlan.Clone();
            foreach (int slot in next.Pets.SetupPlans[index].SetUpPets.Where(kv => kv.Value == 0).Select(kv => kv.Key).ToArray())
                next.Pets.SetupPlans[index].SetUpPets.Remove(slot);
            foreach (int slot in next.Pets.SetupPlans[index].SupportPets.Where(kv => kv.Value == 0).Select(kv => kv.Key).ToArray())
                next.Pets.SetupPlans[index].SupportPets.Remove(slot);
            if ((code = pets.ValidateGenrePlan(s, next.Pets, index)) != 0) return code;
        }
        if (concrete.SeasonTalentPlan != null && concrete.SeasonTalentPlan.TalentNodes.Any(kv =>
            kv.Value.CurrentLevel < 0 || data.Row("SeasonTalentConfig", kv.Key) == null
            || kv.Value.CurrentLevel > (s.SeasonTalent?.Plan.Values.Select(p => p.TalentNodes.GetValueOrDefault(kv.Key)?.CurrentLevel ?? 0).DefaultIfEmpty().Max() ?? 0)))
            return ErrorCodes.TalentLocked;
        return 0;
    }

    internal async Task RenameRecommendationAsync(GameSession s, GenreResponserNameSaveReq req)
    {
        int result = Gate(s);
        {
            var seeker = Stored(req.SeekUserId);
            var mine = s.Genre!.Clone();
            var plan = FindMy(mine, req.SeekUserId, req.ResponseId);
            if (result == 0 && (seeker == null || plan == null || !ValidName(req.PlanName)
                || req.PlanExplain.Any(char.IsControl) || req.PlanExplain.EnumerateRunes().Count() > 200)) result = ErrorCodes.Parameter;
            if (result == 0)
            {
                plan!.RougeInfo ??= new GenreRougeRecommend();
                plan.RougeInfo.PlanName = req.PlanName;
                plan.RougeInfo.PlanExplain = req.PlanExplain;
                Put(seeker!, mine, req.SeekUserId, s.Role!.Id, req.ResponseId, plan);
                await PublishPairAsync(s, mine, req.SeekUserId, seeker!);
            }
        }
        await s.SendGameAsync(82824, new GenreResponserNameSaveResp { Result = result });
    }

    internal async Task SendRecommendationAsync(GameSession s, GenreResponserSendPlanReq req)
    {
        int result = Gate(s);
        {
            var seeker = Stored(req.SeekUserId);
            var mine = s.Genre!.Clone();
            var plan = FindMy(mine, req.SeekUserId, req.ResponseId);
            if (result == 0 && (seeker == null || plan?.Plans == null || !OwnsConcrete(s, plan.Plans))) result = ErrorCodes.Parameter;
            if (result == 0)
            {
                plan!.HaveCommited = true;
                plan.CommitTime = Now();
                Put(seeker!, mine, req.SeekUserId, s.Role!.Id, req.ResponseId, plan);
                await PublishPairAsync(s, mine, req.SeekUserId, seeker!);
            }
        }
        await s.SendGameAsync(82802, new GenreResponserSendPlanResp { Result = result });
    }

    internal async Task DeleteRecommendationAsync(GameSession s, GenreResponserDeleteReq req)
    {
        int result = Gate(s);
        {
            var seeker = Stored(req.SeekerUserId);
            var mine = s.Genre!.Clone();
            if (result == 0 && (seeker == null || FindMy(mine, req.SeekerUserId, req.ResponseId) == null)) result = ErrorCodes.Parameter;
            if (result == 0)
            {
                mine.MyRecommend!.MyRecommendPlans[req.SeekerUserId].GenrePlans.Remove(req.ResponseId);
                seeker!.OtherRecommend!.Recommend[s.Role!.Id].GenrePlans.Remove(req.ResponseId);
                await PublishPairAsync(s, mine, req.SeekerUserId, seeker);
            }
        }
        await s.SendGameAsync(82804, new GenreResponserDeleteResp { Result = result });
    }

    internal async Task DeleteSeekerAsync(GameSession s, GenreSeekerDeleteReq req)
    {
        int result = Gate(s);
        {
            var responder = Stored(req.ResponseUserId);
            var mine = s.Genre!.Clone();
            if (result == 0 && (responder == null || FindOther(mine, req.ResponseUserId, req.ResponseId) == null)) result = ErrorCodes.Parameter;
            if (result == 0)
            {
                mine.OtherRecommend!.Recommend[req.ResponseUserId].GenrePlans.Remove(req.ResponseId);
                responder!.MyRecommend!.MyRecommendPlans[s.Role!.Id].GenrePlans.Remove(req.ResponseId);
                await PublishPairAsync(s, mine, req.ResponseUserId, responder);
            }
        }
        await s.SendGameAsync(82805, new GenreSeekerDeleteResp { Result = result });
    }

    internal async Task OthersAsync(GameSession s, GenreGetAllOtherRecommendReq req)
    {
        int result = Gate(s);
        if (result == 0 && FindOther(s.Genre!, req.ResponseUserID, req.ResponseId) == null) result = ErrorCodes.Parameter;
        await s.SendGameAsync(82809, new GenreGetAllOtherRecommendResp { Result = result });
        if (result == 0) await SyncAsync(s);
    }

    internal async Task ConcreteAsync(GameSession s, GenreGetMyRecConcretePlanReq req)
    {
        int result = Gate(s);
        var plan = FindMy(s.Genre!, req.SeekerUserId, req.ResponseId);
        var seeker = roles.Get(req.SeekerUserId);
        if (result == 0 && (plan?.Plans == null || seeker == null)) result = ErrorCodes.Parameter;
        var response = new GenreGetMyRecConcretePlanResp { Result = result };
        if (result == 0)
        {
            response.PlanInfo = plan!.Clone();
            response.SeekerInfo = new DBUserGenreData
            {
                UserId = seeker!.Id, Zoneid = seeker.Area, Job = seeker.Career, Lv = seeker.Level, Sex = seeker.Sex,
            };
            foreach (var item in items.Load(seeker.Id))
            {
                var wire = character.ItemInfoOf(item);
                if (EmblemService.IsEmblem(data, item.Entry))
                {
                    response.SeekerInfo.Embleitems[item.Guid] = wire;
                    continue;
                }
                switch (item.Pack)
                {
                    case 2: response.SeekerInfo.Equipitems[item.Guid] = wire; break;
                    case 3: response.SeekerInfo.Carditems[item.Guid] = wire; break;
                    case 4: response.SeekerInfo.Totemitems[item.Guid] = wire; break;
                }
            }
            var seekerSession = Online(seeker.Id);
            var petBytes = seekerSession?.Pets?.ToByteArray() ?? roles.LoadPets(seeker.Id);
            if (petBytes is { Length: > 0 })
                foreach (var (petId, pet) in DBPetInfo.Parser.ParseFrom(petBytes).ListPet)
                    response.SeekerInfo.ListPet[petId] = pet.Clone();
        }
        await s.SendGameAsync(82810, response);
    }

    /// <summary>82813: import only checked child systems, resolving donor instances to owned items before publishing.</summary>
    internal async Task ApplyRecommendationAsync(GameSession s, GenreSeekerApplyPlanReq req)
    {
        bool skillChanged = false;
        {
            using var held = await CharacterService.Lock(s);
            int result = Gate(s);
            var reply = new GenreSeekerApplyPlanResp();
            reply.ChildType.Add(req.ChildType);
            var record = FindOther(s.Genre!, req.ResponseUserId, req.ResponseId);
            if (result == 0 && (!s.Genre!.GenreMap.SystemMap.ContainsKey(req.PlanId) || record?.HaveCommited != true
                || record.Plans == null || record.RougeInfo?.SeekUserId != s.Role!.Id
                || record.RougeInfo.ResponserUserId != req.ResponseUserId)) result = ErrorCodes.Parameter;
            var selected = s.Genre!.GenreMap.SystemMap.TryGetValue(req.PlanId, out var current)
                ? current.GenrePlans.ToDictionary(kv => kv.Key, kv => kv.Value) : new Dictionary<int, int>();
            var replacing = (req.ChildType.Count == 0 ? req.SystemType.Keys : req.ChildType.Where(kv => kv.Value == 1).Select(kv => kv.Key)).ToArray();
            if (result == 0 && (replacing.Length == 0 || replacing.Any(type => type is < Skill or > Talent
                || !req.SystemType.ContainsKey(type)) || req.ChildType.Any(kv => kv.Value is not 0 and not 1)))
                result = ErrorCodes.Parameter;
            var next = new Stage(s, skills);
            // The donor WholeRole reservation includes login reconciliation and item/GM writes through Apply.
            var donorItems = result == 0 ? (Online(req.ResponseUserId)?.Items ?? items.Load(req.ResponseUserId))
                .ToDictionary(i => i.Guid, i => i.Clone()) : new();
            var used = new HashSet<long>();
            long Own(long donorGuid)
            {
                if (!donorItems.TryGetValue(donorGuid, out var from)) return 0;
                var own = next.Items.Where(i => i.Entry == from.Entry && i.Stack > 0 && !used.Contains(i.Guid))
                    .OrderBy(i => i.Guid == donorGuid ? 0 : 1).ThenBy(i => i.Guid).FirstOrDefault();
                if (own == null) return 0;
                used.Add(own.Guid);
                return own.Guid;
            }
            if (result == 0)
            {
                var concrete = record!.Plans!;
                foreach (int type in replacing)
                {
                    int target = req.SystemType[type];
                    selected[type] = target;
                    switch (type)
                    {
                        case Skill when concrete.SkillPlan != null && target >= 0 && target < next.Skills.SkillAssignPlans.Count:
                            next.Skills.SkillAssignPlans[target] = concrete.SkillPlan.Clone(); break;
                        case Attr when concrete.AttrPointPlan != null && next.Attrs.AllPlanBaseProperty.ContainsKey(target):
                            next.Attrs.AllPlanBaseProperty[target] = concrete.AttrPointPlan.Clone(); break;
                        case Equip when concrete.EquipPlan != null && next.Equipment.EquipPlans.ContainsKey(target):
                            {
                                var copy = concrete.EquipPlan.Clone();
                                foreach (int slot in copy.ItemsEquipGrid.Keys.ToArray())
                                {
                                    if (copy.ItemsEquipGrid[slot] == 0) { copy.ItemsEquipGrid.Remove(slot); continue; }
                                    long owned = Own(copy.ItemsEquipGrid[slot]);
                                    if (owned == 0) { result = ErrorCodes.ItemMissing; break; }
                                    copy.ItemsEquipGrid[slot] = owned;
                                }
                                if (result == 0) next.Equipment.EquipPlans[target] = copy;
                                break;
                            }
                        case Card when concrete.CardPlan != null && next.Cards.StoragePlans.ContainsKey(target):
                            {
                                var copy = OwnedCardPlan(concrete.CardPlan, next.Items);
                                if (copy == null) result = ErrorCodes.ItemMissing;
                                else next.Cards.StoragePlans[target] = copy;
                                break;
                            }
                        case Totem when concrete.TotemPlan != null && next.Totems.TotemPlans.ContainsKey(target):
                            {
                                var copy = concrete.TotemPlan.Clone();
                                foreach (int slot in copy.SetUpTotem.Keys.ToArray())
                                {
                                    if (copy.SetUpTotem[slot] == 0) continue;
                                    long owned = Own(copy.SetUpTotem[slot]);
                                    if (owned == 0) { result = ErrorCodes.ItemMissing; break; }
                                    copy.SetUpTotem[slot] = owned;
                                }
                                if (result == 0) next.Totems.TotemPlans[target] = copy;
                                break;
                            }
                        case Emblem when concrete.EmblePlan != null && next.Echoes.Plans.ContainsKey(target):
                            {
                                var copy = new EmblemService.Plan();
                                foreach (var (slot, donorGuid) in concrete.EmblePlan.SetUpEmblem)
                                {
                                    if (donorGuid == 0) continue;
                                    long owned = Own(donorGuid);
                                    if (owned == 0) { result = ErrorCodes.ItemMissing; break; }
                                    copy.Slots[slot] = owned;
                                }
                                if (result == 0) next.Echoes.Plans[target] = copy;
                                break;
                            }
                        case Pet when concrete.PetPlan != null && next.Pets.SetupPlans.ContainsKey(target):
                            next.Pets.SetupPlans[target] = concrete.PetPlan.Clone();
                            foreach (int slot in next.Pets.SetupPlans[target].SetUpPets.Where(kv => kv.Value == 0).Select(kv => kv.Key).ToArray())
                                next.Pets.SetupPlans[target].SetUpPets.Remove(slot);
                            foreach (int slot in next.Pets.SetupPlans[target].SupportPets.Where(kv => kv.Value == 0).Select(kv => kv.Key).ToArray())
                                next.Pets.SetupPlans[target].SupportPets.Remove(slot);
                            break;
                        case Talent when concrete.SeasonTalentPlan != null && next.Talents.Plan.TryGetValue(target, out var purchased):
                            // Talent levels are paid for per plan/node. A recommendation cannot purchase them for the seeker.
                            if (concrete.SeasonTalentPlan.TalentNodes.Any(kv => kv.Value.CurrentLevel < 0
                                || !purchased.TalentNodes.ContainsKey(kv.Key)
                                || kv.Value.CurrentLevel > purchased.TalentNodes[kv.Key].CurrentLevel)) result = ErrorCodes.TalentLocked;
                            else next.Talents.Plan[target] = concrete.SeasonTalentPlan.Clone();
                            break;
                        default: result = ErrorCodes.Parameter; break;
                    }
                    if (result != 0) break;
                }
            }
            if (result == 0) result = StageSelection(s, next, selected, replacing.ToHashSet());
            if (result == 0) result = ApplyCooldownError(s, next);
            if (result == 0)
            {
                var genre = s.Genre!.Clone();
                var plan = genre.GenreMap.SystemMap[req.PlanId];
                plan.GenrePlans.Clear();
                foreach (var (type, index) in selected) plan.GenrePlans[type] = index;
                plan.Property.Clear();
                foreach (var (id, value) in Properties(s, next)) plan.Property[id] = value;
                plan.Power = Power(s, next);
                genre.CurrentPlan = req.PlanId;
                try
                {
                    bool changed = replacing.Contains(Skill) || next.Skills.CurrentPlanIndex != s.SkillPlans!.CurrentPlanIndex;
                    await CommitAsync(s, next, genre);
                    skillChanged = changed;
                }
                catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
                { log.LogWarning(e, "Genre recommendation commit failed"); result = ErrorCodes.Unknown; }
            }
            reply.Result = result;
            await s.SendGameAsync(82813, reply);
            if (result == 0) { await SyncAsync(s); await PublishSyncAsync(s); }
        }
        if (skillChanged) await skills.OnActivePlanChangedAsync(s);
    }
}
