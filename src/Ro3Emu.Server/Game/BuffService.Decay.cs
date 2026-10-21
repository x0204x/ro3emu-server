namespace Ro3Emu.Server.Game;

/// <summary>
/// SpecialStatusDecay: crowd-control diminishing returns. Row 1 (Frozen tag 9) and row 2 (Stun tag 13) are decay type
/// 10000, row 3 (control 25 and Silence 26) is 10001, the type 43 battlefields name in DungeonConfig._iDecayType
/// (Battleground of the Gods, Guild League, Mock Battle, War of Emperium, the Stronghold battles). Every other map says 0
/// and no map names 10000, so 10000 is the default set (inferred): stun and freeze decay everywhere, and on those
/// battlefields every hard control and silence decays instead. No client Lua reads the table; the per-landing
/// arithmetic below is (inferred) from its four fields.
/// </summary>
public sealed partial class BuffService
{
    private readonly Dictionary<(long Target, int Rule), BuffRuleTables.DecayTrack> _decay = new();
    private long _nextDecaySweepMs;
    private BuffRuleTables? _rules;
    private BuffRuleTables Rules => _rules ??= BuffRuleTables.For(_data);

    /// <summary>
    /// A hostile control landing on <paramref name="targetUid"/> in <paramref name="sceneEntry"/>: false when the target
    /// is immune, else the duration it lasts (BuffRuleTables.Land). A buff under two rows of the map's type (in no
    /// shipped data) takes the shorter result.
    /// </summary>
    internal bool ApplyDecay(long targetUid, long sceneEntry, int configId, ref int durationMs)
    {
        if (!Rules.DecaysByBuff.TryGetValue(configId, out var rows)) return true;
        int type = Rules.DecayTypeOf(sceneEntry);
        long now = MonsterService.NowMs;
        double factor = 1;
        foreach (var rule in rows)
        {
            if (rule.DecayType != type) continue;
            if (!_decay.TryGetValue((targetUid, rule.Id), out var track)) _decay[(targetUid, rule.Id)] = track = new();
            if (!BuffRuleTables.Land(rule, track, now, out double decayed)) return false;
            factor = Math.Min(factor, decayed);
        }
        durationMs = (int)Math.Max(1, Math.Round(durationMs * factor));
        return true;
    }

    /// <summary>Drop finished histories every 10 s (a quiet window has passed and no immunity runs).</summary>
    private void SweepDecay(long now)
    {
        if (now < _nextDecaySweepMs || _decay.Count == 0) return;
        _nextDecaySweepMs = now + 10_000;
        foreach (var (key, track) in _decay)
            if (now >= track.ImmuneUntilMs && now - track.LastMs > 60_000) _decay.Remove(key);
    }
}
