using Romsg;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class SceneService
{
    public List<Func<GameSession, int>> LineSwitchChecks { get; } = [];
    /// <summary>LC_DAL_MapLine:782-790 uses zero map/area for the authenticated current map.</summary>
    public Task LineInfoAsync(GameSession s, GetSceneLineInfoReq req)
    {
        long map = req.SceneEntry == 0 ? s.SceneEntry : req.SceneEntry;
        bool valid = s.Role != null && s.InstanceId == 0 && map == s.SceneEntry && req.Area == 0
            && (req.PlayerUUID == 0 || req.PlayerUUID == (ulong)s.Role.Id);
        var response = new GetSceneLineInfoResp
        { Result = valid ? 0 : ErrorCodes.Parameter, PlayerUUID = (ulong)(s.Role?.Id ?? 0), SceneEntry = map };
        if (valid)
            foreach (var line in lines.Snapshot(map)) response.LineInfo.Add(new SceneLineInfo
            { SceneID = line.Map.WorldScene, SceneEntry = map, Linenum = line.Map.LineNumber, Currolenum = line.Load });
        return s.SendGameAsync(70009, response);
    }

    /// <summary>70010 has an explicit owner. A source enum is presentation metadata, never authority to bypass capacity.</summary>
    public async Task SwitchLineAsync(GameSession s, SwitchSceneLineReq req)
    {
        int error = s.Role == null || s.InstanceId != 0 || req.Area != 0 || req.Linenum <= 0
            || req.SceneEntry != 0 && req.SceneEntry != s.SceneEntry
            || req.PlayerUUID != 0 && req.PlayerUUID != (ulong)s.Role!.Id
            || (int)req.ESource is < 0 or > 2 ? ErrorCodes.Parameter
            : s.SceneLoading || s.Handoff != null || s.SceneRecoveryRequired || s.SceneTransitionGate.CurrentCount == 0
                ? ErrorCodes.SwitchingScenes
            : (s.StatusBits & (1L << 15)) != 0 ? ErrorCodes.ActionInBattle
            : s.Dead || req.Linenum == s.LineNumber ? ErrorCodes.Parameter : 0;
        if (error == 0) error = LineSwitchChecks.Select(check => check(s)).FirstOrDefault(code => code != 0);
        if (error == 0)
        {
            var point = new MapPoint(s.Position.X, s.Position.Y, s.Position.Z, s.Face);
            if (await EnterSceneAsync(s, s.SceneEntry, point, reason: 1, lineNumber: req.Linenum, source: req.ESource)) return;
            error = ErrorCodes.SceneMissing;
        }
        await s.SendGameAsync(MsgId.SwitchSceneLine, new SwitchSceneLineResp { Result = error, ESource = req.ESource });
    }

    private void RequireReconnect(GameSession s, SceneHandoff handoff)
    {
        if (handoff.Reservation is { } reservation) lines.Release(s, reservation);
        if (s.Handoff != handoff) return;
        s.SceneRecoveryRequired = true;
        s.SceneLoading = true;
        s.Handoff = null;
        ++s.SceneGeneration;
        s.AbortForRecovery();
    }

    public void DisconnectLine(GameSession s)
    {
        s.SceneRecoveryRequired = true;
        s.SceneLoading = true;
        s.Handoff = null;
        ++s.SceneGeneration;
        lines.Release(s);
    }

    /// <summary>(inferred) Bounded 30-second load timeout; reconnect chooses a new valid line.
    /// Never reuse a cancelled same-config handshake: 30005 cannot identify its old line.</summary>
    public void ExpireLineLoad(GameSession s)
    {
        // A completing handoff (the client acknowledged the load; its hooks are still running) does not expire: forcing a
        // reconnect then would drop a player who did load, whenever the hooks outlast the deadline (C/D 16).
        if (s.Handoff is { Completing: false } pending && clock.GetUtcNow() >= pending.Deadline) RequireReconnect(s, pending);
    }

    public async Task LineCommandAsync(GameSession s, string[] args)
    {
        if (args.Length == 0 || args[0].Equals("info", StringComparison.OrdinalIgnoreCase))
        {
            await GmCommands.ReplyAsync(s, "TEST AID !line: " + string.Join("; ", lines.Snapshot(s.SceneEntry)
                .Select(l => $"line {l.Map.Id}: {l.Players} players + {l.Reservations} loading / {l.SoftLimit} soft / {l.HardLimit} hard")));
            return;
        }
        if (args[0].Equals("new", StringComparison.OrdinalIgnoreCase))
        {
            if (s.InstanceId != 0 || s.SceneLoading || s.Handoff != null || !await EnterSceneAsync(s, s.SceneEntry,
                new MapPoint(s.Position.X, s.Position.Y, s.Position.Z, s.Face), reason: 1, createLine: true))
                await GmCommands.ReplyAsync(s, "TEST AID: line creation refused by map limits or an active handoff.");
            return;
        }
        await SwitchLineAsync(s, new SwitchSceneLineReq { Linenum = int.Parse(args[0]), ESource = SwitchSceneSourceType.SwitchSceneSourceManual });
    }
}
