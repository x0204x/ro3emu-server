using System;

namespace Ro3Emu.ClientHooks
{
    /// <summary>沿用原生 app_resource 格式；不將更新發布序號或登入器版本冒充原生 Build。</summary>
    internal static class NativeVersionText
    {
        internal static string Compose(string application, string resource, string current)
        {
            if (string.IsNullOrEmpty(application) || string.IsNullOrEmpty(resource)) return current;
            string native = application + "_" + resource;
            // 原生自行提供的完整標示優先，不疊加或取代其中的 Build。
            if (!string.IsNullOrEmpty(current) && current.StartsWith(native + " Build ", StringComparison.Ordinal)) return current;
            return native;
        }
    }
}
