using System;
using System.Diagnostics;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Sends the publisher's recharge / monthly-pass link to the purchase address the server operator configured
    /// (RO3Emu.exe --purchase-url), or opens nothing when there is none: a player must never pay the publisher for an
    /// RO3Emu server (issue #147). OpenUrlPatch replaces the body of UnityEngine.Application.OpenURL(string) (a pure
    /// InternalCall extern in UnityEngine.CoreModule) with a call to <see cref="Open"/>, so every caller is caught at
    /// the one boundary they all pass through: the game, the SDK, and the reflection call Assembly-CSharp makes (its
    /// UTF-16 "OpenURL" literal).
    /// Frida on the live client proved the recharge button opens <c>https://console.playgenesis3.is/</c> through
    /// OpenURL -&gt; ShellExecuteW, that no network call fetches the URL (it is built client-side), and that the button
    /// does not read the Lua <c>publicparamconfig.RechargeExternalUrl</c> table.
    /// Because the extern's original icall binding is gone once the method has a managed body, <see cref="Open"/> opens
    /// the URL itself, through the same ShellExecute path the original used.
    /// </summary>
    public static class UrlRewrite
    {
        /// <summary>
        /// The configured purchase address. The patcher replaces this literal in the installed copy of this DLL
        /// (OpenUrlPatch.SetPurchaseUrl); a method, not a const, so the value is never copied into a caller.
        /// </summary>
        internal static string Target()
        {
            return "ro3emu:purchase-url-unset";
        }

        /// <summary>
        /// A publisher recharge/console URL (any address on a playgenesis host) becomes the configured purchase
        /// address, or null (open nothing) when none is configured; any other URL passes through unchanged. It matches
        /// the host by substring, so a runtime-built URL is caught whatever its exact form.
        /// </summary>
        public static string Fix(string url)
        {
            // IndexOf, not Contains(string, StringComparison): that overload is not on every Unity Mono BCL; IndexOf is.
            if (string.IsNullOrEmpty(url) || url.IndexOf("playgenesis", StringComparison.OrdinalIgnoreCase) < 0) return url;
            string target = Target();
            return target.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? target : null;
        }

        /// <summary>
        /// The managed body OpenUrlPatch gives UnityEngine.Application.OpenURL: rewrite the URL, then open the result in
        /// the user's default browser (ShellExecute, the path the original icall ended in). A failure is logged and
        /// swallowed, as the engine's own OpenURL does nothing visible on failure.
        /// </summary>
        public static void Open(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            string fixedUrl = Fix(url);
            if (fixedUrl == null)
            {
                try { UnityEngine.Debug.Log("[Ro3Emu.UrlRewrite] purchase link not opened (no purchase address on this server): " + url); }
                catch { /* logging must never throw out of OpenURL */ }
                return;
            }
            url = fixedUrl;
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception e)
            {
                try { UnityEngine.Debug.LogWarning("[Ro3Emu.UrlRewrite] could not open " + url + ": " + e.GetType().Name + ": " + e.Message); }
                catch { /* logging must never throw out of OpenURL */ }
            }
        }
    }
}
