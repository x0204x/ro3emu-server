using Ro3Emu.Server.Game;

namespace Ro3Emu.Server.Maps;

public sealed partial class World
{
    /// <summary>Directed gameplay visibility, in addition to distance/cap. A null policy preserves ordinary AOI.</summary>
    internal Func<GameSession, GameSession, bool>? CanObserve { get; set; }
    private bool Observable(GameSession viewer, GameSession subject) => CanObserve?.Invoke(viewer, subject) != false;

    internal async Task RefreshVisibilityAsync(GameSession changed)
    {
        await _aoiDispatch.WaitAsync();
        try
        {
            var leaves = new List<(GameSession Viewer, GameSession Subject)>();
            var enters = new List<(GameSession Viewer, GameSession Subject)>();
            lock (_sync)
            {
                if (!_cells.ContainsKey(changed)) return;
                var viewers = NearLocked(changed.WorldScene, changed.Position.X, changed.Position.Z)
                    .Concat(changed.Observers).Append(changed).Distinct().ToArray();
                foreach (var viewer in viewers)
                {
                    foreach (var subject in viewer.Visible.Where(p => !Observable(viewer, p)).ToArray())
                    { Unsee(viewer, subject); leaves.Add((viewer, subject)); }
                    foreach (var subject in RefillLocked(viewer)) enters.Add((viewer, subject));
                }
            }
            foreach (var (viewer, subject) in leaves) await LeavePlayerAsync(viewer, subject);
            foreach (var (viewer, subject) in enters) await EnterPlayerAsync(viewer, subject);
        }
        finally { _aoiDispatch.Release(); }
    }
}
