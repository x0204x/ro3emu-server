using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Ro3Emu.Server.Economy;
using Romsg;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>只授權本次 407 獎勵對應的角色限制快照，不將限制額度變成通用貨幣。</summary>
internal static class EconomyVigorRewardValidation
{
    internal static void ValidateShape(RoleItemPlan child, long role, EconomyVigorRewardEvidence? evidence)
    {
        if (evidence is null) return;
        evidence.Validate();
        var blobs = child.Parts.OfType<RoleBlobWritePlan>().Where(p => p.Column == "life_restrict").ToArray();
        if (blobs.Length != 1 || !blobs[0].RoleWrites.SequenceEqual([RevisionStore.Role(role, "life_restrict")]) ||
            Hash(blobs[0].Value) != evidence.RestrictSha256)
            throw new InvalidOperationException("活力獎勵必須綁定唯一且相同的角色限制寫入。");
    }

    internal static byte[]? ValidateBefore(SqliteConnection db, SqliteTransaction tx, long role,
        EconomyVigorRewardEvidence? evidence, bool allowConsumption = false)
    {
        if (evidence is null) return null;
        evidence.Validate();
        byte[] value = Read(db, tx, role);
        var state = DBRestrictInfo.Parser.ParseFrom(value);
        if (Hash(value) != evidence.BeforeRestrictSha256 ||
            !state.RestrictCount.TryGetValue(EconomyVigorRewardEvidence.Restriction, out var vigor) ||
            vigor.Count != evidence.Before || !allowConsumption && evidence.BeforeReward != evidence.Before)
            throw new InvalidOperationException("活力獎勵的交易前快照已變更。");
        return value;
    }

    internal static void ValidateAfter(SqliteConnection db, SqliteTransaction tx, long role,
        EconomyVigorRewardEvidence? evidence, byte[]? before, bool allowConsumption = false)
    {
        if (evidence is null) return;
        byte[] value = Read(db, tx, role);
        var next = DBRestrictInfo.Parser.ParseFrom(value);
        var prior = DBRestrictInfo.Parser.ParseFrom(before ?? throw new InvalidOperationException("缺少活力交易前快照。"));
        int id = EconomyVigorRewardEvidence.Restriction;
        if (Hash(value) != evidence.RestrictSha256 || !next.RestrictCount.TryGetValue(id, out var vigor) ||
            vigor.Count != evidence.After)
            throw new InvalidOperationException("活力獎勵的實際入帳與宣告證據不符。");
        if (allowConsumption)
        {
            if (vigor.HistoryUseCount - prior.RestrictCount[id].HistoryUseCount != evidence.Before - evidence.BeforeReward)
                throw new InvalidOperationException("同交易活力消耗缺少相符的使用記錄。");
        }
        else
        {
            prior.RestrictCount[id].Count = evidence.After;
            if (!prior.Equals(next)) throw new InvalidOperationException("活力獎勵不能改寫其他限制欄位。");
        }
    }

    private static byte[] Read(SqliteConnection db, SqliteTransaction tx, long role)
    {
        using var cmd = DbSql.Command(db, tx, "SELECT life_restrict FROM role WHERE id=$id");
        cmd.Parameters.AddWithValue("$id", role);
        return cmd.ExecuteScalar() as byte[] ?? throw new InvalidOperationException("角色限制資料不存在。");
    }
    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value));
}
