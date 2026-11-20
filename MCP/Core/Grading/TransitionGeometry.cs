using System;

namespace RevitMCP.Core.Grading
{
    /// <summary>
    /// 邊界銜接帶的純幾何：距離場目標高程計算。
    /// 所有長度與高程使用同一單位（Revit 內部英呎），由呼叫端負責轉換。
    /// </summary>
    public static class TransitionGeometry
    {
        /// <summary>模式 2：offset 帶內由板底高程線性漸變回該點地形高程。</summary>
        public static double OffsetTargetZ(
            double boundaryBottomZ,
            double localTerrainZ,
            double distanceFromBoundary,
            double offsetDistance)
        {
            if (offsetDistance <= 0)
                throw new ArgumentOutOfRangeException(nameof(offsetDistance), "offset 帶寬必須大於 0。");
            var t = distanceFromBoundary / offsetDistance;
            if (t <= 0) return boundaryBottomZ;
            if (t >= 1) return localTerrainZ;
            return boundaryBottomZ + (t * (localTerrainZ - boundaryBottomZ));
        }

        /// <summary>模式 3：以 1:n 固定坡度由板底向地形靠攏，到達該點地形高程即貼合不再變化。</summary>
        public static double SlopeTargetZ(
            double boundaryBottomZ,
            double localTerrainZ,
            double distanceFromBoundary,
            double runPerRise)
        {
            if (runPerRise <= 0)
                throw new ArgumentOutOfRangeException(nameof(runPerRise), "坡度分母 n 必須大於 0。");
            if (distanceFromBoundary <= 0) return boundaryBottomZ;
            var delta = distanceFromBoundary / runPerRise;
            return localTerrainZ >= boundaryBottomZ
                ? Math.Min(boundaryBottomZ + delta, localTerrainZ)
                : Math.Max(boundaryBottomZ - delta, localTerrainZ);
        }

        /// <summary>模式 3：從板底放坡到指定地形高程所需的水平距離。</summary>
        public static double SlopeExtensionNeeded(
            double boundaryBottomZ,
            double terrainZ,
            double runPerRise)
        {
            if (runPerRise <= 0)
                throw new ArgumentOutOfRangeException(nameof(runPerRise), "坡度分母 n 必須大於 0。");
            return Math.Abs(terrainZ - boundaryBottomZ) * runPerRise;
        }
    }
}
