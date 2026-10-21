using Ro3Emu.Server.Accounts;

namespace Ro3Emu.Server.Game.Persistence;

/// <summary>
/// C/D 18: the public world position is a lazy part (see <see cref="LazyRolePart"/>). A walk only marks it dirty; a
/// stop is written on the next tick, continuous walking at most every 5 s from the first unpersisted change - the
/// durability window of the synchronous code this replaced. Private-floor positions are never written here (entry
/// saves the outside return point); barrier saves (leave world, switch) stay synchronous.
/// </summary>
public sealed class PositionPersistence : LazyRolePart
{
    private readonly RoleStore _roles;

    public PositionPersistence(RoleStore roles, GameLoop loop, ILogger<PositionPersistence> log) : base(loop, roles, log)
    {
        this._roles = roles;
        Interval = TimeSpan.FromSeconds(5);
    }

    protected override string RevisionPart => Accounts.Persistence.RevisionStore.Position;
    protected override State? Get(GameSession s) => s.PositionPart;
    protected override void Set(GameSession s, State state) => s.PositionPart = state;
    protected override bool Eligible(GameSession s) => s.Role != null && s.InstanceId == 0;

    protected override Func<long, long, long, Task<bool>> Capture(GameSession s)
    {
        var p = s.Position;
        long scene = s.SceneEntry;
        float face = s.Face;
        return (role, epoch, revision) => _roles.SavePositionLazyAsync(role, epoch, revision, scene, p.X, p.Y, p.Z, face);
    }
}
