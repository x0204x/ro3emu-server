namespace Ro3Emu.Server.Game;

/// <summary>
/// The game-gate dispatcher: admits each romsg message under its reservation and runs its route. The routes come from
/// the message modules (Game/Messages), mapped once in registration order (ServerHost).
/// </summary>
public sealed partial class GameMessageHandler(MsgCatalog catalog, IEnumerable<Messaging.IMessageModule> modules, ILogger<GameMessageHandler> log)
{
    private Messaging.MessageRoutes? _routes;
    /// <summary>Every game message's single owner; built once, duplicates fail.</summary>
    internal Messaging.MessageRoutes Routes => _routes ??= BuildRoutes();
    private Messaging.MessageRoutes BuildRoutes()
    {
        var routes = new Messaging.MessageRoutes();
        foreach (var module in modules) module.Map(routes);
        return routes;
    }

    public Task HandleAsync(GameSession session, uint msgId, ReadOnlyMemory<byte> body)
    {
        if (!Routes.Accepts(session, msgId))
        {
            Refuse(session, msgId);
            return Task.CompletedTask;
        }
        // Cancellation must run before an ordinary reservation that the speculative gate waiter holds.
        if (msgId == MsgId.PreSwitchRole) session.CancelPendingCharacterIntents();
        if (session.Owners is not { } owners) return HandleCoreAsync(session, msgId, body);
        var admission = Admit(session, msgId, body);
        Task Work() => HandleCoreAsync(session, msgId, body);
        return admission.Mode switch
        {
            Messaging.AdmissionMode.Economic => owners.RunEconomicRequestAsync(session, Work),
            Messaging.AdmissionMode.Runtime => owners.RunRuntimeAsync(admission.Origin, Work),
            _ => owners.RunSessionAsync(admission.Origin, admission.Keys, session, Work, admission.RealmLabel),
        };
    }

    /// <summary>
    /// A message sent before the stage it is declared for: the client's own code never sends it then (see the
    /// AllowFrom declarations), so it is dropped unanswered and reserves nothing. The original server had no avatar to
    /// take it before the role was selected (inferred). The first one per connection is logged.
    /// </summary>
    private void Refuse(GameSession session, uint msgId)
    {
        if (session.EarlyMessageRefused) return;
        session.EarlyMessageRefused = true;
        LogEarlyMessage(log, msgId, catalog.Name(msgId), session.Channel.Remote, session.Account ?? "not signed in",
            Messaging.SessionStages.Of(session));
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Game msg {MsgId} {Name} from {Remote} ({Account}) refused at stage {Stage}; later ones not logged")]
    private static partial void LogEarlyMessage(ILogger logger, uint msgId, string name, string remote,
        string account, Messaging.SessionStage stage);

    /// <summary>How a message is admitted before its handler runs.</summary>
    internal Messaging.Admission Admit(GameSession session, uint msgId, ReadOnlyMemory<byte> body) =>
        Routes.Admit(session, msgId, body, IsUnknownMessage);

    // An id the client never sends (no route, absent from the client's message table) only gets the unrouted reply.
    // As the manifest default it took Realm, so a client sending made-up ids serialized all durable work.
    internal bool IsUnknownMessage(uint id) => !Routes.TryGet(id, out _) && catalog.UnknownToClient(id);

    private async Task HandleCoreAsync(GameSession session, uint msgId, ReadOnlyMemory<byte> body)
    {
        Ro3Emu.Server.Diagnostics.MessageCoverage.Received(msgId);
        await Routes.DispatchAsync(session, msgId, body, UnroutedAsync);
    }

    /// <summary>A message no route claims.</summary>
    private async Task UnroutedAsync(GameSession session, uint msgId, ReadOnlyMemory<byte> body)
    {
        Ro3Emu.Server.Diagnostics.MessageCoverage.Unanswered(msgId);
        // Unknown to the emulator: an empty response of the right type keeps the client's 10 s request
        // timeouts (enmu_ForcedReqWhite) and waiting UIs from tripping.
        var reply = catalog.EmptyResponse(msgId);
        log.LogInformation("Game msg {MsgId} {Name} from {Account} not handled{Reply}: {Body}",
            msgId, catalog.Name(msgId), session.Account, reply != null ? " (empty reply)" : "", catalog.Describe(msgId, body));
        if (reply != null) await session.SendGameAsync(msgId, reply);
    }
}
