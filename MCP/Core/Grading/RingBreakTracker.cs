using System.Collections.Generic;

namespace RevitMCP.Core.Grading
{
    /// <summary>
    /// 放坡外圈摺線的斷段追蹤（純函式層，無 Revit API 相依）。
    /// 實機教訓（2026-07-08）：外圈錨點被跳過（落入鄰板投影、超出地形、地形已達板底）時，
    /// 若封圈摺線直接把缺口兩端連弦，弦會從高處斜切樓板上空，SlabShape 沿線抬升成垂直細片鰭。
    /// 規則：任何跳過都讓摺線斷段、永不跨缺口連弦。
    /// 邊索引 i 代表點 i → 點 i+1 的摺線段；封圈邊索引 = 點數-1（最後點 → 第 0 點）。
    /// </summary>
    public sealed class RingBreakTracker
    {
        private readonly HashSet<int> _breaks = new HashSet<int>();
        private bool _pendingBreak;
        private bool _closingBreak;
        private int _pointCount;

        /// <summary>錨點被跳過（未產生圈點）。</summary>
        public void MarkSkipped()
        {
            _pendingBreak = true;
        }

        /// <summary>圈點已加入。</summary>
        public void MarkAdded()
        {
            if (_pendingBreak)
            {
                if (_pointCount == 0)
                {
                    // 首點之前有跳過：缺口落在封圈邊（最後點 → 第 0 點）。
                    _closingBreak = true;
                }
                else
                {
                    _breaks.Add(_pointCount - 1);
                }

                _pendingBreak = false;
            }

            _pointCount++;
        }

        /// <summary>回傳不可畫摺線的邊索引集合。少於 2 點時沒有任何邊。</summary>
        public IReadOnlyCollection<int> Finalize()
        {
            if (_pointCount >= 2 && (_pendingBreak || _closingBreak))
            {
                _breaks.Add(_pointCount - 1);
            }

            return _breaks;
        }
    }
}
