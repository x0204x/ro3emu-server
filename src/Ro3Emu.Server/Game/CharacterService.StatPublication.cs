using System.Runtime.CompilerServices;

namespace Ro3Emu.Server.Game;

public sealed partial class CharacterService
{
    private readonly ConditionalWeakTable<GameSession, StatPublication> _statPublications = new();

    private sealed class StatPublication
    {
        public int Depth;
        public bool Pending;
        public bool ShowChange;
        public int ChangeType;
    }

    /// <summary>已提交卡片異動的短同步區段：背包、卡片被動都更新後，僅送一次最終屬性。
    /// 不包住資料庫提交；一般增益仍使用原本即時同步。作用域釋放時先清除抑制，再送封包。</summary>
    internal IAsyncDisposable BatchStatPublication(GameSession session, int changeType = AttrModTypeBase)
    {
        var state = _statPublications.GetOrCreateValue(session);
        if (state.Depth == 0) state.ChangeType = changeType;
        state.Depth++;
        return new StatPublicationScope(this, session, state);
    }

    private bool DeferStatPublication(GameSession session, bool showChange)
    {
        if (!_statPublications.TryGetValue(session, out var state)) return false;
        state.Pending = true;
        state.ShowChange |= showChange;
        return true;
    }

    private sealed class StatPublicationScope(CharacterService owner, GameSession session, StatPublication state) : IAsyncDisposable
    {
        private bool _disposed;

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            if (--state.Depth != 0) return;
            owner._statPublications.Remove(session);
            if (state.Pending) await owner.SendStatsAsync(session, state.ShowChange, state.ChangeType);
        }
    }
}
