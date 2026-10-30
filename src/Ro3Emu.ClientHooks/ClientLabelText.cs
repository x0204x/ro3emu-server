using System;
using System.Reflection;

#nullable disable

namespace Ro3Emu.ClientHooks
{
    /// <summary>只接已安裝 Loc 的已知 Map(string)；沒有補譯器時保留原字串，不另選玩家語系。</summary>
    internal sealed class ClientLabelText
    {
        private readonly Func<string, string> _map;

        internal ClientLabelText(Func<string, string> map) { _map = map; }

        public string Map(string source)
        {
            try { return _map?.Invoke(source) ?? source; }
            catch { return source; }
        }

        public static ClientLabelText Installed()
        {
            try
            {
                var type = Type.GetType("Ro3Emu.Loc.Ro3Loc, Ro3Emu.Loc", false);
                var method = type?.GetMethod("Map", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                if (method != null && method.ReturnType == typeof(string))
                    return new ClientLabelText((Func<string, string>)Delegate.CreateDelegate(typeof(Func<string, string>), method));
            }
            catch { /* 未安裝或版本不相容時保留原文，不掃描任意 assembly。 */ }
            return new ClientLabelText(null);
        }
    }

    /// <summary>保留既有成功／錯誤 handler；僅補原生已存在但未接線的 TitleLevel 失敗 handler。</summary>
    internal static class TitleReplyBinding
    {
        public const string Dataset = "TitleLevel";

        public static bool Ensure<T>(T success, T existingError, T nativeError, bool showErrors,
            Action<string, T, T, bool> register) where T : class
        {
            if (existingError != null) return true;
            if (nativeError == null || register == null) return false;
            register(Dataset, success, nativeError, showErrors);
            return true;
        }
    }
}
