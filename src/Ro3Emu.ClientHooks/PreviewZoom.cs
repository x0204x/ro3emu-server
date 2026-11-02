using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// The wheel over a list no longer zooms the 3D preview. UIModelRenderManager.Update reads Input.mouseScrollDelta whenever
    /// the model camera is on and never looks at the pointer, while the UI sends the same wheel to the list under it, so in
    /// the wardrobe scrolling the mounts also moves the camera. Update only moves it within GetCameraMaxNearOffset and
    /// GetCameraMaxFarOffset, two delegates that LC_UIModelManager sets, so these are wrapped: over anything that takes a scroll
    /// event neither limit allows a step. Everywhere else, the preview itself included, they answer as before.
    /// </summary>
    internal static class PreviewZoom
    {
        // Update tests delta + z <= near and delta + z >= -far; no camera offset gets anywhere near this.
        private const int NoStep = -100000;

        private static UIModelRenderManager _manager;
        private static UIModelRenderManager.GetCameraMaxOffset _near, _far;
        // One instance each: Tick compares against it to see whether Lua has set the delegate anew.
        private static readonly UIModelRenderManager.GetCameraMaxOffset NearWrapper = Near, FarWrapper = Far;
        private static readonly List<RaycastResult> Hits = new List<RaycastResult>();
        private static int _checkedFrame = -1;
        private static bool _overScrollable;

        /// <summary>From UiTweaks.Tick; wraps again when Lua sets the delegates anew (a recreated LuaEnv).</summary>
        internal static void Tick()
        {
            // Not .Instance: SingletonMonoBehaviour creates an empty manager when the game has not made its own yet.
            if (_manager == null) _manager = UnityEngine.Object.FindObjectOfType<UIModelRenderManager>();
            if (_manager == null) return;
            if (_manager.GetCameraMaxNearOffset != null && _manager.GetCameraMaxNearOffset != NearWrapper)
            {
                _near = _manager.GetCameraMaxNearOffset;
                _manager.GetCameraMaxNearOffset = NearWrapper;
            }
            if (_manager.GetCameraMaxFarOffset != null && _manager.GetCameraMaxFarOffset != FarWrapper)
            {
                _far = _manager.GetCameraMaxFarOffset;
                _manager.GetCameraMaxFarOffset = FarWrapper;
            }
        }

        private static int Near() => OverScrollable() ? NoStep : _near?.Invoke() ?? 0;

        private static int Far() => OverScrollable() ? NoStep : _far?.Invoke() ?? 0;

        /// <summary>The same hit the input module sends the scroll event to; only asked on a frame with a wheel step.</summary>
        private static bool OverScrollable()
        {
            if (_checkedFrame == Time.frameCount) return _overScrollable;
            _checkedFrame = Time.frameCount;
            _overScrollable = false;
            try
            {
                var events = EventSystem.current;
                if (events == null) return false;
                Hits.Clear();
                events.RaycastAll(new PointerEventData(events) { position = Input.mousePosition }, Hits);
                _overScrollable = Hits.Count > 0 && ExecuteEvents.GetEventHandler<IScrollHandler>(Hits[0].gameObject) != null;
            }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.PreviewZoom] " + e.GetType().Name + ": " + e.Message); }
            return _overScrollable;
        }
    }
}
