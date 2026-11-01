using Microsoft.Data.Sqlite;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>既有無經濟 envelope 的角色原子交易仍必須檢核相同的額度及快照邊界。</summary>
internal sealed class VigorRewardBoundaryPlan(long role, EconomyVigorRewardEvidence evidence, bool after, byte[] before) : DbPlan
{
    internal override int Bytes => 512;
    internal override string Label => "life/vigor-reward-boundary";
    internal override IReadOnlyList<long> RolePins => [role];
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        if (!after) EconomyVigorRewardValidation.ValidateBefore(db, tx, role, evidence);
        else EconomyVigorRewardValidation.ValidateAfter(db, tx, role, evidence, before);
        return 1;
    }
}
