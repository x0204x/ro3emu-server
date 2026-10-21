using System.Globalization;
using Romsg;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>Pending 31134 decision. The server preserves one HP until the player confirms or declines.</summary>
public sealed record ImmuneDeathOffer(long BuffGuid, int BuffConfigId, long ExpiresMs);

public sealed partial class BuffService
{
    public const uint ImmuneDeathNotify = 31134, ImmuneDeathConfirm = 31135;

    /// <summary>CombatService supplies its ordinary death transition for decline/timeout.</summary>
    public Func<GameSession, Task>? ImmuneDeathDeclined { get; set; }

    /// <summary>
    /// Apply an active buff's lethal-hit protection before HP reaches zero. BuffConfig 1430501 and 16340401
    /// explicitly grant immediate survival; 20520/20521 explicitly offer a revive choice (Localization_en
    /// 10220300143-145). The identity and exact ordering of hit/life-state packets are inferred.
    /// </summary>
    public async Task<bool> TryPreventLethalAsync(GameSession p)
    {
        if (p.Role == null || p.Dead) return false;
        long now = MonsterService.NowMs;
        if (p.ImmuneDeathOffer is { } pending)
        {
            if (pending.ExpiresMs > now) return true;
            p.ImmuneDeathOffer = null;
        }
        if (p.ImmuneRecoveryUntilMs > now) return true;
        var live = p.Buffs.Where(b => b.ExpireMs > now).ToList();
        var automatic = live.FirstOrDefault(b => b.ConfigId is 1430501 or 16340401 or 15400401);
        if (automatic != null)
        {
            int percent = automatic.ConfigId == 16340401 ? 100 : 30;
            await RemoveAsync(p, automatic); // one fatal hit uses one charge (inferred from descriptions).
            var stats = _character.Stats(p);
            await _character.SetHpSpAsync(p, Math.Max(1, stats[101] * percent / 100), null);
            return true;
        }

        var choice = live.FirstOrDefault(b => _data.Row("BuffConfig", b.ConfigId) is { } row
            && GameData.Int(row, "_iBuffID") is 20520 or 20521
            && p.BuffEffectCds.GetValueOrDefault(b.Guid) <= now);
        if (choice == null) return false;
        long window = _data.Table("PublicParamConfig").TryGetValue("ImmuneDeadConfirmTimeMs", out var value)
            ? (long)(GameData.Number(value) ?? 5000) : 5000;
        window = Math.Clamp(window, 1000, 30000);
        p.ImmuneDeathOffer = new ImmuneDeathOffer(choice.Guid, choice.ConfigId, now + window);
        long cd = InternalCooldownMs(choice.ConfigId);
        if (cd > 0)
        {
            p.BuffEffectCds[choice.Guid] = now + cd;
            await p.SendGameAsync(BuffEffectCd, new BuffEffectCdNotify
            {
                BuffGuid = choice.Guid, StartTimestampMs = now, EndTimestampMs = now + cd,
            });
        }
        await _character.SetHpSpAsync(p, 1, null);
        await RefreshControlAsync(p);
        await p.SendGameAsync(ImmuneDeathNotify, new PlayerOnEnterImmuneDeadNotify
        {
            BuffConfigId = choice.ConfigId, ExpireTimestampMs = p.ImmuneDeathOffer.ExpiresMs,
        });
        return true;
    }

    /// <summary>31135 accepts a live 31134 offer; false or timeout resumes ordinary death.</summary>
    public async Task ConfirmImmuneDeathAsync(GameSession p, PlayerConfirmImmuneDeadReq req)
    {
        var offer = p.ImmuneDeathOffer;
        bool valid = offer != null && offer.ExpiresMs >= MonsterService.NowMs && !p.Dead;
        await p.SendGameAsync(ImmuneDeathConfirm, new PlayerConfirmImmuneDeadRsp
        {
            Result = valid ? 0 : ErrorCodes.Parameter,
        });
        if (!valid) return;
        p.ImmuneDeathOffer = null;
        if (!req.IsConfirm)
        {
            await DeclineImmuneDeathAsync(p);
            return;
        }
        var stats = _character.Stats(p);
        if (_data.Row("BuffConfig", offer!.BuffConfigId) is not { } row)
        {
            await DeclineImmuneDeathAsync(p);
            return;
        }
        int group = (int)GameData.Int(row, "_iBuffID");
        var desc = row.GetProperty("_iDescription");
        int percent = (int)Math.Clamp(DescriptionNumber(desc, 1, 100), 1, 100);
        if (group == 20520)
        {
            p.ImmuneRecoveryUntilMs = MonsterService.NowMs + 3000;
            p.ImmuneRecoveryPercent = (int)Math.Clamp(DescriptionNumber(desc, 4, 20), 1, 100);
            await _character.SetHpSpAsync(p, 1, null);
        }
        else
        {
            await _character.SetHpSpAsync(p, Math.Max(1, stats[101] * percent / 100),
                Math.Max(1, stats[102] * percent / 100));
        }
        await RefreshControlAsync(p);
    }

    private static double DescriptionNumber(System.Text.Json.JsonElement desc, int index, double fallback) =>
        desc.ValueKind == System.Text.Json.JsonValueKind.Array && desc.GetArrayLength() > index
        && double.TryParse(desc[index].ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value : fallback;

    private long InternalCooldownMs(int configId)
    {
        if (_data.Row("BuffConfig", configId) is not { } row) return 0;
        var desc = row.GetProperty("_iDescription");
        int group = (int)GameData.Int(row, "_iBuffID");
        double minutes = DescriptionNumber(desc, group == 20520 ? 5 : 2, 0);
        return (long)(minutes * 60000);
    }

    private async Task DeclineImmuneDeathAsync(GameSession p)
    {
        await RefreshControlAsync(p);
        await _character.SetHpSpAsync(p, 0, null);
        if (ImmuneDeathDeclined != null) await ImmuneDeathDeclined(p);
        else _log.LogError("Immune-death decline has no CombatService death callback for {Name}", p.Role?.Name);
    }

    private void TickImmuneDeath(GameSession p, long now)
    {
        if (p.ImmuneDeathOffer is { } offer && offer.ExpiresMs < now)
        {
            p.ImmuneDeathOffer = null;
            _loop.TrackLegacyDetached("immune death decline", () => DeclineImmuneDeathAsync(p));
        }
        if (p.ImmuneRecoveryUntilMs > 0 && p.ImmuneRecoveryUntilMs <= now)
        {
            p.ImmuneRecoveryUntilMs = 0;
            int percent = p.ImmuneRecoveryPercent;
            p.ImmuneRecoveryPercent = 0;
            _loop.TrackLegacyDetached("immune recovery end", () => EndImmuneRecoveryAsync(p, percent));
        }
    }

    private async Task EndImmuneRecoveryAsync(GameSession p, int percent)
    {
        if (p.Role == null || p.Dead) return;
        var stats = _character.Stats(p);
        await _character.SetHpSpAsync(p, Math.Max(1, stats[101] * percent / 100),
            Math.Max(1, stats[102] * percent / 100));
        await RefreshControlAsync(p);
    }
}
