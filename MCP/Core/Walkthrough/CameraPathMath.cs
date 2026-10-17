using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RevitMCP.Core.Walkthrough
{
    /// <summary>Minimal 3D vector (no Revit dependency, unit-testable).</summary>
    public readonly struct Vec3
    {
        public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }
        public double X { get; }
        public double Y { get; }
        public double Z { get; }

        public static readonly Vec3 Zero = new Vec3(0, 0, 0);
        public static readonly Vec3 UnitZ = new Vec3(0, 0, 1);

        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator *(Vec3 a, double s) => new Vec3(a.X * s, a.Y * s, a.Z * s);
        public static Vec3 operator *(double s, Vec3 a) => a * s;

        public double Dot(Vec3 o) => X * o.X + Y * o.Y + Z * o.Z;
        public Vec3 Cross(Vec3 o) => new Vec3(Y * o.Z - Z * o.Y, Z * o.X - X * o.Z, X * o.Y - Y * o.X);
        public double Length => Math.Sqrt(Dot(this));

        public Vec3 Normalized()
        {
            var len = Length;
            return len < 1e-12 ? Zero : this * (1.0 / len);
        }

        public double[] ToArray(int decimals = 6) => new[]
        {
            Math.Round(X, decimals), Math.Round(Y, decimals), Math.Round(Z, decimals)
        };

        public override string ToString() => string.Format(CultureInfo.InvariantCulture, "({0}, {1}, {2})", X, Y, Z);
    }

    /// <summary>One camera station read from a WALK-NN perspective view (model meters).</summary>
    public sealed class CameraStation
    {
        public string Name { get; set; }
        public int Order { get; set; }
        public Vec3 Eye { get; set; }
        public Vec3 Forward { get; set; }
        public Vec3 Up { get; set; }
    }

    /// <summary>One resampled pose on the walkthrough path.</summary>
    public sealed class PathSample
    {
        public double S { get; set; }
        public double T { get; set; }
        public Vec3 P { get; set; }
        public Vec3 Fwd { get; set; }
        public Vec3 Up { get; set; }
    }

    /// <summary>Resampled walkthrough path.</summary>
    public sealed class WalkPath
    {
        public IReadOnlyList<PathSample> Samples { get; set; }
        public double TotalLength { get; set; }
        /// <summary>Sample index closest to each station (same order as input stations).</summary>
        public IReadOnlyList<int> StationSampleIndices { get; set; }
    }

    /// <summary>Field of view derived from a perspective crop box.</summary>
    public readonly struct FieldOfView
    {
        public FieldOfView(double fovXDeg, double fovYDeg, bool isSymmetric, bool isValid)
        { FovXDeg = fovXDeg; FovYDeg = fovYDeg; IsSymmetric = isSymmetric; IsValid = isValid; }
        public double FovXDeg { get; }
        public double FovYDeg { get; }
        /// <summary>Crop region centered on the view direction (three.js camera can reproduce it exactly).</summary>
        public bool IsSymmetric { get; }
        public bool IsValid { get; }
        public double Aspect => Math.Tan(FovXDeg * Math.PI / 360.0) / Math.Tan(FovYDeg * Math.PI / 360.0);
    }

    /// <summary>
    /// Camera path math for walkthrough export: station name parsing, centripetal Catmull-Rom
    /// interpolation, uniform arc-length resampling, camera frame orthonormalization and
    /// perspective crop box to field-of-view conversion.
    /// </summary>
    public static class CameraPathMath
    {
        public const double FeetToMeters = 0.3048;
        private const int SubdivisionsPerSegment = 64;

        /// <summary>Parses "WALK-07" style names. Only prefix + digits (case-insensitive, trimmed) is accepted.</summary>
        public static bool TryParseStationOrder(string viewName, string prefix, out int order)
        {
            order = 0;
            if (string.IsNullOrWhiteSpace(viewName) || prefix == null)
            {
                return false;
            }

            var match = Regex.Match(viewName.Trim(), "^" + Regex.Escape(prefix.Trim()) + "(\\d{1,6})$", RegexOptions.IgnoreCase);
            return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out order);
        }

        /// <summary>
        /// Builds a uniformly spaced (by arc length) camera path through the stations.
        /// Sample count = (stations - 1) * framesPerSegment + 1, so the average density matches the request
        /// while walking speed stays constant across short and long segments.
        /// </summary>
        public static WalkPath BuildPath(IReadOnlyList<CameraStation> stations, int framesPerSegment)
        {
            if (stations == null || stations.Count < 2)
            {
                throw new ArgumentException("At least two camera stations are required.");
            }

            if (framesPerSegment < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(framesPerSegment));
            }

            var points = new Vec3[stations.Count];
            var fwds = new Vec3[stations.Count];
            var ups = new Vec3[stations.Count];
            for (var i = 0; i < stations.Count; i++)
            {
                points[i] = stations[i].Eye;
                var frame = OrthonormalizeCamera(stations[i].Forward, stations[i].Up);
                fwds[i] = frame.Forward;
                ups[i] = frame.Up;
            }

            var segmentCount = stations.Count - 1;

            // Arc length table: (segment, u, cumulative s).
            var tableSeg = new List<int>();
            var tableU = new List<double>();
            var tableS = new List<double>();
            var stationS = new double[stations.Count];
            var cumulative = 0.0;
            var previous = points[0];
            tableSeg.Add(0); tableU.Add(0); tableS.Add(0);
            for (var seg = 0; seg < segmentCount; seg++)
            {
                stationS[seg] = cumulative;
                for (var k = 1; k <= SubdivisionsPerSegment; k++)
                {
                    var u = (double)k / SubdivisionsPerSegment;
                    var point = EvaluatePosition(points, seg, u);
                    cumulative += (point - previous).Length;
                    previous = point;
                    tableSeg.Add(seg); tableU.Add(u); tableS.Add(cumulative);
                }
            }

            stationS[stations.Count - 1] = cumulative;
            if (cumulative < 1e-9)
            {
                throw new ArgumentException("Camera stations coincide; the path has zero length.");
            }

            var sampleCount = segmentCount * framesPerSegment + 1;
            var samples = new PathSample[sampleCount];
            var cursor = 0;
            for (var i = 0; i < sampleCount; i++)
            {
                var t = sampleCount == 1 ? 0.0 : (double)i / (sampleCount - 1);
                var s = t * cumulative;
                while (cursor < tableS.Count - 2 && tableS[cursor + 1] < s)
                {
                    cursor++;
                }

                int segIndex;
                double u;
                var s0 = tableS[cursor];
                var s1 = tableS[cursor + 1];
                var ratio = s1 - s0 < 1e-12 ? 0.0 : Math.Min(1.0, Math.Max(0.0, (s - s0) / (s1 - s0)));
                var seg1 = tableSeg[cursor + 1];
                var u1 = tableU[cursor + 1];
                var seg0 = tableSeg[cursor];
                var u0 = tableU[cursor];
                if (seg0 != seg1)
                {
                    // Table entry at a segment boundary: treat the start as u = 0 of the next segment.
                    seg0 = seg1;
                    u0 = 0.0;
                }

                segIndex = seg1;
                u = u0 + (u1 - u0) * ratio;
                if (i == sampleCount - 1)
                {
                    segIndex = segmentCount - 1;
                    u = 1.0;
                }

                var position = EvaluatePosition(points, segIndex, u);
                var fwd = EvaluateDirection(fwds, segIndex, u);
                var up = EvaluateDirection(ups, segIndex, u);
                var orientation = OrthonormalizeCamera(fwd, up);
                samples[i] = new PathSample { S = s, T = t, P = position, Fwd = orientation.Forward, Up = orientation.Up };
            }

            var stationIndices = new int[stations.Count];
            for (var i = 0; i < stations.Count; i++)
            {
                stationIndices[i] = (int)Math.Round(stationS[i] / cumulative * (sampleCount - 1));
            }

            return new WalkPath { Samples = samples, TotalLength = cumulative, StationSampleIndices = stationIndices };
        }

        /// <summary>Returns an orthonormal (forward, up) pair; up is made perpendicular to forward.</summary>
        public static (Vec3 Forward, Vec3 Up) OrthonormalizeCamera(Vec3 forward, Vec3 up)
        {
            var f = forward.Normalized();
            if (f.Length < 0.5)
            {
                throw new ArgumentException("Camera forward direction is zero.");
            }

            var u = up - f * up.Dot(f);
            if (u.Length < 1e-6)
            {
                // Looking straight up/down or degenerate up: fall back to world Z, then world Y.
                var fallback = Math.Abs(f.Z) < 0.99 ? Vec3.UnitZ : new Vec3(0, 1, 0);
                u = fallback - f * fallback.Dot(f);
            }

            u = u.Normalized();
            // Remove residual numeric error so |dot| is ~1e-16.
            u = (u - f * u.Dot(f)).Normalized();
            return (f, u);
        }

        /// <summary>
        /// Field of view from a perspective view crop box expressed in view coordinates
        /// (X right, Y up, Z toward the viewer). <paramref name="eyeInCropFrame"/> is the eye position
        /// expressed in the same crop frame (usually (0,0,0) because the crop transform origin is the eye).
        /// The Max corner lies on the front clip plane (Autodesk Revit API Developer's Guide, View3D).
        /// </summary>
        public static FieldOfView FieldOfViewFromCropBox(Vec3 cropMin, Vec3 cropMax, Vec3 eyeInCropFrame)
        {
            var near = cropMax - eyeInCropFrame;
            var far = cropMin - eyeInCropFrame;
            var depth = -near.Z;
            if (depth <= 1e-9 || near.X <= 0 || near.Y <= 0)
            {
                return new FieldOfView(0, 0, false, false);
            }

            // Both corners describe the same frustum when the crop is centered: -Min.X/|Min.Z| == Max.X/|Max.Z|.
            var fovX = 2.0 * Math.Atan(near.X / depth) * 180.0 / Math.PI;
            var fovY = 2.0 * Math.Atan(near.Y / depth) * 180.0 / Math.PI;
            var symmetric = true;
            if (-far.Z > 1e-9)
            {
                var farTanX = -far.X / -far.Z;
                var farTanY = -far.Y / -far.Z;
                var unscaledX = -far.X / depth;
                var unscaledY = -far.Y / depth;
                var tanX = near.X / depth;
                var tanY = near.Y / depth;
                symmetric = (RelClose(farTanX, tanX) && RelClose(farTanY, tanY))
                            || (RelClose(unscaledX, tanX) && RelClose(unscaledY, tanY))
                            || (RelClose(-far.X, near.X) && RelClose(-far.Y, near.Y));
            }

            return new FieldOfView(fovX, fovY, symmetric, true);
        }

        private static bool RelClose(double a, double b)
        {
            return Math.Abs(a - b) <= 0.01 * Math.Max(Math.Abs(a), Math.Abs(b)) + 1e-9;
        }

        /// <summary>
        /// Inverse of <see cref="FieldOfViewFromCropBox"/>: builds a symmetric perspective crop box (feet, eye-frame,
        /// eye at the origin) that reproduces the given vertical field of view and aspect ratio. The Max corner sits
        /// on the near clip plane and Min on the far clip plane (Autodesk Revit API Developer's Guide, View3D: the
        /// perspective crop region is a pyramid with the eye at its apex, X right / Y up / Z toward the viewer).
        /// </summary>
        public static (Vec3 Min, Vec3 Max) CropBoxFromFieldOfView(double fovYDeg, double aspect, double nearFeet = 1.0, double farFeet = 1000.0)
        {
            if (fovYDeg <= 0 || fovYDeg >= 180)
            {
                throw new ArgumentOutOfRangeException(nameof(fovYDeg), fovYDeg, "fovYDeg must be in (0, 180).");
            }

            if (aspect <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(aspect), aspect, "aspect must be positive.");
            }

            if (nearFeet <= 0 || farFeet <= nearFeet)
            {
                throw new ArgumentOutOfRangeException(nameof(farFeet), farFeet, "farFeet must be greater than nearFeet, and nearFeet must be positive.");
            }

            var tanY = Math.Tan(fovYDeg * Math.PI / 360.0);
            var tanX = tanY * aspect;

            var max = new Vec3(nearFeet * tanX, nearFeet * tanY, -nearFeet);
            var min = new Vec3(-farFeet * tanX, -farFeet * tanY, -farFeet);
            return (min, max);
        }

        /// <summary>Centripetal Catmull-Rom (alpha = 0.5) on segment [p1, p2], u in [0, 1].</summary>
        public static Vec3 CatmullRomCentripetal(Vec3 p0, Vec3 p1, Vec3 p2, Vec3 p3, double u)
        {
            const double epsilon = 1e-6;
            var t0 = 0.0;
            var t1 = t0 + Math.Max(Math.Sqrt((p1 - p0).Length), epsilon);
            var t2 = t1 + Math.Max(Math.Sqrt((p2 - p1).Length), epsilon);
            var t3 = t2 + Math.Max(Math.Sqrt((p3 - p2).Length), epsilon);
            var t = t1 + (t2 - t1) * u;

            var a1 = p0 * ((t1 - t) / (t1 - t0)) + p1 * ((t - t0) / (t1 - t0));
            var a2 = p1 * ((t2 - t) / (t2 - t1)) + p2 * ((t - t1) / (t2 - t1));
            var a3 = p2 * ((t3 - t) / (t3 - t2)) + p3 * ((t - t2) / (t3 - t2));
            var b1 = a1 * ((t2 - t) / (t2 - t0)) + a2 * ((t - t0) / (t2 - t0));
            var b2 = a2 * ((t3 - t) / (t3 - t1)) + a3 * ((t - t1) / (t3 - t1));
            return b1 * ((t2 - t) / (t2 - t1)) + b2 * ((t - t1) / (t2 - t1));
        }

        private static Vec3 EvaluatePosition(Vec3[] points, int segment, double u)
        {
            var p1 = points[segment];
            var p2 = points[segment + 1];
            var p0 = segment > 0 ? points[segment - 1] : p1 * 2.0 - p2;
            var p3 = segment + 2 < points.Length ? points[segment + 2] : p2 * 2.0 - p1;
            if ((p2 - p1).Length < 1e-9)
            {
                return p1;
            }

            return CatmullRomCentripetal(p0, p1, p2, p3, u);
        }

        private static Vec3 EvaluateDirection(Vec3[] dirs, int segment, double u)
        {
            var d1 = dirs[segment];
            var d2 = dirs[segment + 1];
            var d0 = segment > 0 ? dirs[segment - 1] : d1;
            var d3 = segment + 2 < dirs.Length ? dirs[segment + 2] : d2;

            // Uniform Catmull-Rom on unit vectors (C1 across stations), then normalize.
            var u2 = u * u;
            var u3 = u2 * u;
            var blended = (d1 * 2.0
                           + (d2 - d0) * u
                           + (d0 * 2.0 - d1 * 5.0 + d2 * 4.0 - d3) * u2
                           + (d1 * 3.0 - d0 - d2 * 3.0 + d3) * u3) * 0.5;
            if (blended.Length < 1e-6)
            {
                blended = d1 * (1.0 - u) + d2 * u;
            }

            if (blended.Length < 1e-6)
            {
                // Opposite directions: rotate through a perpendicular to avoid a zero vector.
                var perp = d1.Cross(Vec3.UnitZ);
                if (perp.Length < 1e-6)
                {
                    perp = d1.Cross(new Vec3(0, 1, 0));
                }

                blended = u < 0.5 ? d1 * (1 - 2 * u) + perp.Normalized() * (2 * u) : perp.Normalized() * (2 - 2 * u) + d2 * (2 * u - 1);
            }

            return blended.Normalized();
        }
    }
}
