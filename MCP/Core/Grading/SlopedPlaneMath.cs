using System;
using System.Collections.Generic;

namespace RevitMCP.Core.Grading
{
    /// <summary>
    /// 斜板純幾何：由「低側邊＋坡率 1:n」求各頂點抬升量。
    /// 低側邊維持原高程（抬升 0），其餘頂點依「至低側邊線的垂距（朝板內側）/ n」抬升；
    /// 內側方向由頂點群自動判定（垂距總和為正的一側），低側邊外側的頂點取 0 不下切。
    /// </summary>
    public static class SlopedPlaneMath
    {
        public static IReadOnlyList<double> VertexRises(
            IReadOnlyList<Point2D> vertices,
            Point2D edgeStart,
            Point2D edgeEnd,
            double runPerRise)
        {
            if (vertices == null || vertices.Count == 0)
            {
                throw new ArgumentException("頂點清單不可為空。", nameof(vertices));
            }

            if (runPerRise <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(runPerRise), "坡度分母 n 必須大於 0。");
            }

            var edgeX = edgeEnd.X - edgeStart.X;
            var edgeY = edgeEnd.Y - edgeStart.Y;
            var edgeLength = Math.Sqrt((edgeX * edgeX) + (edgeY * edgeY));
            if (edgeLength <= 1e-9)
            {
                throw new ArgumentException("低側邊長度為零。");
            }

            // 邊線法線（未定向）；signed 垂距 = ((p - start) × edgeDir)。
            var signedDistances = new double[vertices.Count];
            var sum = 0.0;
            for (var i = 0; i < vertices.Count; i++)
            {
                var signed = (((vertices[i].X - edgeStart.X) * edgeY)
                    - ((vertices[i].Y - edgeStart.Y) * edgeX)) / edgeLength;
                signedDistances[i] = signed;
                sum += signed;
            }

            // 內側 = 垂距總和為正的一側；總和為零（退化）時取正向。
            var interiorSign = sum >= 0 ? 1.0 : -1.0;

            var rises = new double[vertices.Count];
            for (var i = 0; i < vertices.Count; i++)
            {
                var toward = signedDistances[i] * interiorSign;
                rises[i] = toward > 0 ? toward / runPerRise : 0.0;
            }

            return rises;
        }
    }
}
