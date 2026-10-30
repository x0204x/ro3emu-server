using System;

namespace Ro3Emu.ClientHooks
{
    /// <summary>客製 PC 頭銜視窗的純幾何；原生內容等比縮小，保留關閉列及畫面安全邊界。</summary>
    internal readonly struct TitleModalGeometry
    {
        public const float Margin = 24, Padding = 12, Header = 44;
        public float Scale { get; }
        public float Width { get; }
        public float Height { get; }

        private TitleModalGeometry(float scale, float width, float height)
        {
            Scale = scale; Width = width; Height = height;
        }

        public static bool TryFit(float canvasWidth, float canvasHeight, float nativeWidth, float nativeHeight,
            out TitleModalGeometry layout)
        {
            layout = default;
            if (!Positive(canvasWidth) || !Positive(canvasHeight) || !Positive(nativeWidth) || !Positive(nativeHeight)) return false;
            float availableWidth = canvasWidth - 2 * (Margin + Padding);
            float availableHeight = canvasHeight - 2 * (Margin + Padding) - Header;
            if (availableWidth <= 0 || availableHeight <= 0) return false;
            float scale = Math.Min(1, Math.Min(availableWidth / nativeWidth, availableHeight / nativeHeight));
            if (!Positive(scale)) return false;
            layout = new TitleModalGeometry(scale, nativeWidth * scale + 2 * Padding, nativeHeight * scale + 2 * Padding + Header);
            return true;
        }

        private static bool Positive(float value) => value > 0 && !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
