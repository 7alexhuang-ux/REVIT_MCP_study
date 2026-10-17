using System;
using System.Collections.Generic;

namespace RevitMCP.Core.Walkthrough
{
    /// <summary>Axis-aligned box in model meters (Revit internal coordinates, Z up). No Revit dependency.</summary>
    public readonly struct MeterBox
    {
        public MeterBox(double minX, double minY, double minZ, double maxX, double maxY, double maxZ)
        {
            MinX = minX; MinY = minY; MinZ = minZ; MaxX = maxX; MaxY = maxY; MaxZ = maxZ;
        }

        public double MinX { get; }
        public double MinY { get; }
        public double MinZ { get; }
        public double MaxX { get; }
        public double MaxY { get; }
        public double MaxZ { get; }

        public bool IsValid => MaxX > MinX && MaxY > MinY && MaxZ > MinZ;

        /// <summary>Grows the XY footprint by <paramref name="marginXY"/> on every side and Z by the given amounts.</summary>
        public MeterBox Expand(double marginXY, double below, double above) =>
            new MeterBox(MinX - marginXY, MinY - marginXY, MinZ - below, MaxX + marginXY, MaxY + marginXY, MaxZ + above);

        public MeterBox Union(MeterBox o) =>
            new MeterBox(Math.Min(MinX, o.MinX), Math.Min(MinY, o.MinY), Math.Min(MinZ, o.MinZ),
                Math.Max(MaxX, o.MaxX), Math.Max(MaxY, o.MaxY), Math.Max(MaxZ, o.MaxZ));

        public bool Contains(Vec3 p) =>
            p.X >= MinX && p.X <= MaxX && p.Y >= MinY && p.Y <= MaxY && p.Z >= MinZ && p.Z <= MaxZ;
    }

    public static class WebCompareMath
    {
        /// <summary>Bounding box of a set of points; returns false when the set is empty.</summary>
        public static bool TryBounds(IEnumerable<Vec3> points, out MeterBox box)
        {
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            var any = false;
            foreach (var p in points)
            {
                any = true;
                minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y); minZ = Math.Min(minZ, p.Z);
                maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y); maxZ = Math.Max(maxZ, p.Z);
            }

            box = any ? new MeterBox(minX, minY, minZ, maxX, maxY, maxZ) : default;
            return any;
        }

        /// <summary>
        /// Clips a triangle to the box (Sutherland–Hodgman against the six planes) and returns the result as a
        /// triangle fan list: empty when fully outside, one triangle when fully inside.
        /// </summary>
        public static List<Vec3[]> ClipTriangle(Vec3 a, Vec3 b, Vec3 c, MeterBox box)
        {
            var poly = new List<Vec3> { a, b, c };
            for (var axis = 0; axis < 3 && poly.Count >= 3; axis++)
            {
                poly = ClipAgainst(poly, axis, lower: true, Bound(box, axis, true));
                if (poly.Count >= 3)
                {
                    poly = ClipAgainst(poly, axis, lower: false, Bound(box, axis, false));
                }
            }

            var result = new List<Vec3[]>();
            for (var i = 1; i + 1 < poly.Count; i++)
            {
                result.Add(new[] { poly[0], poly[i], poly[i + 1] });
            }

            return result;
        }

        private static double Bound(MeterBox box, int axis, bool lower) =>
            axis == 0 ? (lower ? box.MinX : box.MaxX) : axis == 1 ? (lower ? box.MinY : box.MaxY) : (lower ? box.MinZ : box.MaxZ);

        private static double Coord(Vec3 p, int axis) => axis == 0 ? p.X : axis == 1 ? p.Y : p.Z;

        private static List<Vec3> ClipAgainst(List<Vec3> poly, int axis, bool lower, double bound)
        {
            bool Inside(Vec3 p) => lower ? Coord(p, axis) >= bound : Coord(p, axis) <= bound;

            var output = new List<Vec3>();
            for (var i = 0; i < poly.Count; i++)
            {
                var cur = poly[i];
                var prev = poly[(i + poly.Count - 1) % poly.Count];
                var curIn = Inside(cur);
                var prevIn = Inside(prev);
                if (curIn != prevIn)
                {
                    var t = (bound - Coord(prev, axis)) / (Coord(cur, axis) - Coord(prev, axis));
                    output.Add(prev + (cur - prev) * t);
                }

                if (curIn)
                {
                    output.Add(cur);
                }
            }

            return output;
        }
    }
}
