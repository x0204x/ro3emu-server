using System;

namespace Ro3Emu.ClientHooks
{
    /// <summary>自訂追蹤列的唯讀顯示決策；不產生任務、獎勵或道具使用要求。</summary>
    internal readonly struct TreasureHuntTrackerPlan
    {
        public long PointId { get; }
        public long TargetMapId { get; }
        public bool Show { get; }
        public bool CanResume { get; }

        private TreasureHuntTrackerPlan(long pointId, long mapId, bool show, bool canResume)
        { PointId = pointId; TargetMapId = mapId; Show = show; CanResume = canResume; }

        public static TreasureHuntTrackerPlan Read(long pointId, long targetMapId, long currentMapId, long compatiblePointId)
        {
            bool show = pointId > 0 && targetMapId > 0 && compatiblePointId != pointId;
            return new TreasureHuntTrackerPlan(pointId, targetMapId, show, show && currentMapId > 0 && currentMapId == targetMapId);
        }

        public string Description(string mapName, Func<string, string> label)
        {
            string action = CanResume ? "Click to resume treasure hunting" : "Go to this map to resume treasure hunting";
            return mapName + "\n" + label(action);
        }
    }

    /// <summary>延後建立的原生 widget 只接受目前請求；關閉或取消後不得被舊 callback 復活。</summary>
    internal sealed class TreasureHuntTrackerLease
    {
        private int _generation;
        public bool Pending { get; private set; }
        public bool Ready { get; private set; }

        public int Begin()
        {
            Pending = true;
            Ready = false;
            return ++_generation;
        }

        public bool Complete(int generation)
        {
            if (!Pending || generation != _generation) return false;
            Pending = false;
            Ready = true;
            return true;
        }

        public void Cancel()
        {
            ++_generation;
            Pending = false;
            Ready = false;
        }
    }
}
