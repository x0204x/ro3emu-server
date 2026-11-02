using System.Runtime.CompilerServices;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Server.Game;

public sealed partial class GameLoop
{
    /// <summary>Unconverted multi-owner timer families take the explicit conservative realm scope.</summary>
    public void PostLegacy(Func<Task> work, [CallerMemberName] string origin = "legacy")
        => Post(() => RunLegacyAsync(origin, work));

    public void ScheduleLegacy(TimeSpan delay, Func<Task> work, [CallerMemberName] string origin = "legacy")
        => Schedule(delay, () => RunLegacyAsync(origin, work));

    public void TrackLegacyDetached(string name, Func<Task> work)
        => TrackDetached(name, () => RunLegacyAsync(name, work));

    public void ScheduleRuntime(TimeSpan delay, Func<Task> work, [CallerMemberName] string origin = "runtime")
        => Schedule(delay, () => RunRuntimeIntentAsync(origin, work));

    public void PostRuntime(Func<Task> work, [CallerMemberName] string origin = "runtime")
        => Post(() => RunRuntimeIntentAsync(origin, work));

    public void TrackRuntimeDetached(string name, Func<Task> work)
        => TrackDetached(name, () => RunRuntimeIntentAsync(name, work));

    private Task RunRuntimeIntentAsync(string origin, Func<Task> work) => Owners is { } owners
        ? owners.RunRuntimeAsync("runtime/" + origin, work) : work();

    private Task RunLegacyAsync(string origin, Func<Task> work) => Owners is { } owners
        ? owners.RunAsync("timer/" + origin, [ReservationKey.Realm], work) : work();

    /// <summary>A detached role timer captures its attachment before queueing; a role switch cancels its intent.</summary>
    public void PostOwned(GameSession session, Func<Task> work, [CallerMemberName] string origin = "role timer", Action? discarded = null)
        => PostOwned(session, [], work, origin, discarded);

    /// <summary>Event delivery declares its role and natural-source keys together before entering any service gate.</summary>
    public void PostOwned(GameSession session, ReservationKey[] additionalKeys, Func<Task> work,
        [CallerMemberName] string origin = "role timer", Action? discarded = null)
    {
        var role = session.Role;
        var lease = session.Attachment;
        var owner = session.Owner;
        bool Current() => session.Role?.Id == role?.Id && ReferenceEquals(session.Owner, owner) &&
            ReferenceEquals(session.Attachment, lease) && lease?.Cancelled != true &&
            (owner == null ? lease == null : lease != null && ReferenceEquals(owner.Attachment, lease) &&
                owner.State is RoleOwnerState.Loading or RoleOwnerState.Attached);
        Post(async () =>
        {
            if (!Current()) { discarded?.Invoke(); return; }
            if (Owners is not { } owners || role == null) { await work(); return; }
            using var scope = await owners.EnterAsync("timer/" + origin,
                [.. additionalKeys.Append(ReservationKey.Role(role.Id)).Distinct()]);
            if (!Current()) { discarded?.Invoke(); return; }
            if (owner != null) scope.Context.Pin(owner);
            await work();
        });
    }

    private void TickOwned(IGameTicker ticker, GameTime time)
    {
        if (Owners is not { } owners || ticker is Deadlines) { ticker.Tick(time); return; }
        // These families only enqueue intents here; their durable phases declare complete role/event/source keys.
        if (ticker is FestivalService or MvpService or TrainingService or PronteraDefenseService
            or TimedEventService or TimedBattleService or TerritoryWarsService or TerritoryEscortService)
        { ticker.Tick(time); return; }
        // C/D 16 (S3): every other ticker runs under a runtime scope. Ticks enqueue or simulate in memory and durable
        // work admits its own keys. As Realm a tick was deferred behind every reservation (map ticks up to 2.8 s,
        // service ticks up to 16 s). A runtime scope cannot authorize a persistence write, so a write made synchronously
        // inside Tick throws: the owner audit over the full suite with every ticker under runtime found none. Work a tick
        // starts with TrackDetached/Post runs later on its own permit without an owner context; it must admit its own
        // keys (the lazy role parts write through their revision CAS instead).
        owners.RunRuntimeAsync("ticker/" + ticker.GetType().Name, () => { ticker.Tick(time); return Task.CompletedTask; }).GetAwaiter().GetResult();
    }
}
