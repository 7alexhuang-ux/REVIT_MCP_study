using System;
using System.Collections.Generic;

namespace RevitMCP.Core.Grading
{
    public static class Polygon2D
    {
        public static bool Contains(
            IReadOnlyList<Point2D> polygon,
            Point2D point,
            double tolerance)
        {
            if (polygon == null || polygon.Count < 3)
            {
                return false;
            }

            tolerance = Math.Abs(tolerance);
            if (IsOnBoundary(polygon, point, tolerance))
            {
                return true;
            }

            var inside = false;
            for (var i = 0; i < polygon.Count; i++)
            {
                var start = polygon[i];
                var end = polygon[(i + 1) % polygon.Count];
                var crossesRay = (start.Y > point.Y) != (end.Y > point.Y);
                if (!crossesRay)
                {
                    continue;
                }

                var intersectionX = start.X
                    + ((point.Y - start.Y) * (end.X - start.X) / (end.Y - start.Y));
                if (point.X < intersectionX)
                {
                    inside = !inside;
                }
            }

            return inside;
        }

        /// <summary>多邊形絕對面積（與頂點繞向無關）。</summary>
        public static double Area(IReadOnlyList<Point2D> polygon)
        {
            if (polygon == null || polygon.Count < 3)
            {
                throw new ArgumentException("多邊形至少需要三個頂點。", nameof(polygon));
            }

            return Math.Abs(SignedArea(polygon));
        }

        public static double DistanceToBoundary(IReadOnlyList<Point2D> polygon, Point2D point)
        {
            return NearestBoundaryPoint(polygon, point).Distance;
        }

        public static (Point2D Point, double Distance) NearestBoundaryPoint(
            IReadOnlyList<Point2D> polygon,
            Point2D point)
        {
            if (polygon == null || polygon.Count < 2)
            {
                throw new ArgumentException("多邊形至少需要兩個頂點。", nameof(polygon));
            }

            var best = polygon[0];
            var bestDistanceSquared = double.MaxValue;
            for (var index = 0; index < polygon.Count; index++)
            {
                var start = polygon[index];
                var end = polygon[(index + 1) % polygon.Count];
                var edgeX = end.X - start.X;
                var edgeY = end.Y - start.Y;
                var lengthSquared = (edgeX * edgeX) + (edgeY * edgeY);
                double t = 0;
                if (lengthSquared > 0)
                {
                    t = (((point.X - start.X) * edgeX) + ((point.Y - start.Y) * edgeY)) / lengthSquared;
                    t = Math.Max(0, Math.Min(1, t));
                }

                var candidate = new Point2D(start.X + (t * edgeX), start.Y + (t * edgeY));
                var distanceSquared = DistanceSquared(candidate, point);
                if (distanceSquared < bestDistanceSquared)
                {
                    bestDistanceSquared = distanceSquared;
                    best = candidate;
                }
            }

            return (best, Math.Sqrt(bestDistanceSquared));
        }

        public static IReadOnlyList<Point2D> OutwardDirections(IReadOnlyList<Point2D> polygon)
        {
            if (polygon == null || polygon.Count < 3)
            {
                throw new ArgumentException("多邊形至少需要三個頂點。", nameof(polygon));
            }

            // CCW（SignedArea>0）時邊 (dx,dy) 的外法線為 (dy,-dx)；CW 則相反。
            var orientation = SignedArea(polygon) >= 0 ? 1.0 : -1.0;
            var directions = new List<Point2D>(polygon.Count);
            for (var index = 0; index < polygon.Count; index++)
            {
                var previous = polygon[(index - 1 + polygon.Count) % polygon.Count];
                var current = polygon[index];
                var next = polygon[(index + 1) % polygon.Count];
                var incoming = EdgeOutwardNormal(previous, current, orientation);
                var outgoing = EdgeOutwardNormal(current, next, orientation);
                var sumX = incoming.X + outgoing.X;
                var sumY = incoming.Y + outgoing.Y;
                var length = Math.Sqrt((sumX * sumX) + (sumY * sumY));
                directions.Add(length > 1e-12
                    ? new Point2D(sumX / length, sumY / length)
                    : outgoing);
            }

            return directions;
        }

        /// <summary>
        /// 沿邊界產生密控制錨點：直邊依最大弦長細分（沿邊外法線），凸角補一串圓弧方向
        /// （繞角點自入邊外法線掃至出邊外法線），凹角／直邊僅放單一角平分方向。
        /// 每個錨點為 (基點, 外向單位向量)；放坡帶圈點 = 基點 + 外向 × 放坡距離，
        /// 凸角諸方向共用頂點基點即形成圓角，貼合距離場真實坡腳。
        /// </summary>
        public static IReadOnlyList<(Point2D BasePoint, Point2D OutwardDir)> DensifiedBoundaryWithCornerArcs(
            IReadOnlyList<Point2D> polygon,
            double maxChord,
            double maxArcStepRadians)
        {
            if (polygon == null || polygon.Count < 3)
            {
                throw new ArgumentException("多邊形至少需要三個頂點。", nameof(polygon));
            }

            var orientation = SignedArea(polygon) >= 0 ? 1.0 : -1.0;
            var anchors = new List<(Point2D, Point2D)>();
            var count = polygon.Count;
            for (var index = 0; index < count; index++)
            {
                var previous = polygon[(index - 1 + count) % count];
                var current = polygon[index];
                var next = polygon[(index + 1) % count];
                var incomingNormal = EdgeOutwardNormal(previous, current, orientation);
                var outgoingNormal = EdgeOutwardNormal(current, next, orientation);

                if (IsConvexVertex(previous, current, next, orientation))
                {
                    foreach (var direction in ArcDirections(incomingNormal, outgoingNormal, maxArcStepRadians))
                    {
                        anchors.Add((current, direction));
                    }
                }
                else
                {
                    var sumX = incomingNormal.X + outgoingNormal.X;
                    var sumY = incomingNormal.Y + outgoingNormal.Y;
                    var length = Math.Sqrt((sumX * sumX) + (sumY * sumY));
                    anchors.Add((current, length > 1e-12
                        ? new Point2D(sumX / length, sumY / length)
                        : outgoingNormal));
                }

                var edgeLength = Distance(current, next);
                if (maxChord > 0 && edgeLength > maxChord)
                {
                    var segments = (int)Math.Ceiling(edgeLength / maxChord);
                    for (var step = 1; step < segments; step++)
                    {
                        var t = (double)step / segments;
                        var point = new Point2D(
                            current.X + (t * (next.X - current.X)),
                            current.Y + (t * (next.Y - current.Y)));
                        anchors.Add((point, outgoingNormal));
                    }
                }
            }

            return anchors;
        }

        private static bool IsConvexVertex(Point2D previous, Point2D current, Point2D next, double orientation)
        {
            var incomingX = current.X - previous.X;
            var incomingY = current.Y - previous.Y;
            var outgoingX = next.X - current.X;
            var outgoingY = next.Y - current.Y;
            var cross = (incomingX * outgoingY) - (incomingY * outgoingX);
            return cross * orientation > 1e-12;
        }

        private static IReadOnlyList<Point2D> ArcDirections(Point2D from, Point2D to, double maxArcStepRadians)
        {
            var dot = Math.Max(-1.0, Math.Min(1.0, (from.X * to.X) + (from.Y * to.Y)));
            var cross = (from.X * to.Y) - (from.Y * to.X);
            var angle = Math.Atan2(cross, dot);
            var step = maxArcStepRadians > 0 ? maxArcStepRadians : Math.PI / 12.0;
            var segments = Math.Max(1, (int)Math.Ceiling(Math.Abs(angle) / step));
            var directions = new List<Point2D>(segments + 1);
            for (var k = 0; k <= segments; k++)
            {
                var theta = angle * k / segments;
                var cos = Math.Cos(theta);
                var sin = Math.Sin(theta);
                directions.Add(new Point2D(
                    (from.X * cos) - (from.Y * sin),
                    (from.X * sin) + (from.Y * cos)));
            }

            return directions;
        }

        private static Point2D EdgeOutwardNormal(Point2D start, Point2D end, double orientation)
        {
            var edgeX = end.X - start.X;
            var edgeY = end.Y - start.Y;
            var length = Math.Sqrt((edgeX * edgeX) + (edgeY * edgeY));
            if (length <= 1e-12)
            {
                return new Point2D(0, 0);
            }

            return new Point2D(orientation * edgeY / length, orientation * -edgeX / length);
        }

        public static bool Overlaps(
            IReadOnlyList<Point2D> first,
            IReadOnlyList<Point2D> second,
            double tolerance)
        {
            if (first == null || second == null || first.Count < 3 || second.Count < 3)
            {
                return false;
            }

            tolerance = Math.Abs(tolerance);
            var firstWinding = Math.Sign(SignedArea(first));
            var secondWinding = Math.Sign(SignedArea(second));
            for (var firstIndex = 0; firstIndex < first.Count; firstIndex++)
            {
                var firstStart = first[firstIndex];
                var firstEnd = first[(firstIndex + 1) % first.Count];

                for (var secondIndex = 0; secondIndex < second.Count; secondIndex++)
                {
                    var secondStart = second[secondIndex];
                    var secondEnd = second[(secondIndex + 1) % second.Count];
                    if (StrictlyIntersects(
                        firstStart,
                        firstEnd,
                        secondStart,
                        secondEnd,
                        tolerance))
                    {
                        return true;
                    }

                    if (HasCollinearOverlapOnSameInteriorSide(
                        firstStart,
                        firstEnd,
                        secondStart,
                        secondEnd,
                        firstWinding,
                        secondWinding,
                        tolerance))
                    {
                        return true;
                    }
                }
            }

            return HasStrictlyContainedVertex(first, second, tolerance)
                || HasStrictlyContainedVertex(second, first, tolerance);
        }

        private static bool HasStrictlyContainedVertex(
            IReadOnlyList<Point2D> candidateVertices,
            IReadOnlyList<Point2D> polygon,
            double tolerance)
        {
            for (var i = 0; i < candidateVertices.Count; i++)
            {
                var candidate = candidateVertices[i];
                if (!IsOnBoundary(polygon, candidate, tolerance)
                    && Contains(polygon, candidate, tolerance))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasCollinearOverlapOnSameInteriorSide(
            Point2D firstStart,
            Point2D firstEnd,
            Point2D secondStart,
            Point2D secondEnd,
            int firstWinding,
            int secondWinding,
            double tolerance)
        {
            var firstLength = Distance(firstStart, firstEnd);
            var secondLength = Distance(secondStart, secondEnd);
            if (firstLength == 0 || secondLength == 0 || firstWinding == 0 || secondWinding == 0)
            {
                return false;
            }

            if (Math.Abs(Cross(firstStart, firstEnd, secondStart)) > tolerance * firstLength
                || Math.Abs(Cross(firstStart, firstEnd, secondEnd)) > tolerance * firstLength)
            {
                return false;
            }

            var firstDirectionX = (firstEnd.X - firstStart.X) / firstLength;
            var firstDirectionY = (firstEnd.Y - firstStart.Y) / firstLength;
            var secondStartProjection = ((secondStart.X - firstStart.X) * firstDirectionX)
                + ((secondStart.Y - firstStart.Y) * firstDirectionY);
            var secondEndProjection = ((secondEnd.X - firstStart.X) * firstDirectionX)
                + ((secondEnd.Y - firstStart.Y) * firstDirectionY);
            var overlapLength = Math.Min(firstLength, Math.Max(secondStartProjection, secondEndProjection))
                - Math.Max(0, Math.Min(secondStartProjection, secondEndProjection));
            if (overlapLength <= tolerance)
            {
                return false;
            }

            var secondDirectionX = (secondEnd.X - secondStart.X) / secondLength;
            var secondDirectionY = (secondEnd.Y - secondStart.Y) / secondLength;
            var interiorNormalDot = firstWinding * secondWinding
                * ((firstDirectionX * secondDirectionX) + (firstDirectionY * secondDirectionY));
            return interiorNormalDot > 0;
        }

        private static bool IsOnBoundary(
            IReadOnlyList<Point2D> polygon,
            Point2D point,
            double tolerance)
        {
            for (var i = 0; i < polygon.Count; i++)
            {
                if (IsOnSegment(
                    polygon[i],
                    polygon[(i + 1) % polygon.Count],
                    point,
                    tolerance))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsOnSegment(
            Point2D start,
            Point2D end,
            Point2D point,
            double tolerance)
        {
            var deltaX = end.X - start.X;
            var deltaY = end.Y - start.Y;
            var lengthSquared = (deltaX * deltaX) + (deltaY * deltaY);
            if (lengthSquared == 0)
            {
                return DistanceSquared(start, point) <= tolerance * tolerance;
            }

            var projection = ((point.X - start.X) * deltaX + (point.Y - start.Y) * deltaY)
                / lengthSquared;
            var projectionTolerance = tolerance / Math.Sqrt(lengthSquared);
            if (projection < -projectionTolerance || projection > 1 + projectionTolerance)
            {
                return false;
            }

            var cross = Cross(start, end, point);
            return Math.Abs(cross) <= tolerance * Math.Sqrt(lengthSquared);
        }

        private static bool StrictlyIntersects(
            Point2D firstStart,
            Point2D firstEnd,
            Point2D secondStart,
            Point2D secondEnd,
            double tolerance)
        {
            var firstLength = Distance(firstStart, firstEnd);
            var secondLength = Distance(secondStart, secondEnd);
            if (firstLength == 0 || secondLength == 0)
            {
                return false;
            }

            var secondStartSide = Cross(firstStart, firstEnd, secondStart);
            var secondEndSide = Cross(firstStart, firstEnd, secondEnd);
            var firstStartSide = Cross(secondStart, secondEnd, firstStart);
            var firstEndSide = Cross(secondStart, secondEnd, firstEnd);

            return AreOnStrictOppositeSides(secondStartSide, secondEndSide, tolerance * firstLength)
                && AreOnStrictOppositeSides(firstStartSide, firstEndSide, tolerance * secondLength);
        }

        private static bool AreOnStrictOppositeSides(double first, double second, double threshold)
        {
            return (first > threshold && second < -threshold)
                || (first < -threshold && second > threshold);
        }

        private static double Cross(Point2D start, Point2D end, Point2D point)
        {
            return ((end.X - start.X) * (point.Y - start.Y))
                - ((end.Y - start.Y) * (point.X - start.X));
        }

        private static double Distance(Point2D first, Point2D second)
        {
            return Math.Sqrt(DistanceSquared(first, second));
        }

        private static double DistanceSquared(Point2D first, Point2D second)
        {
            var deltaX = second.X - first.X;
            var deltaY = second.Y - first.Y;
            return (deltaX * deltaX) + (deltaY * deltaY);
        }

        private static double SignedArea(IReadOnlyList<Point2D> polygon)
        {
            var doubledArea = 0.0;
            for (var i = 0; i < polygon.Count; i++)
            {
                var current = polygon[i];
                var next = polygon[(i + 1) % polygon.Count];
                doubledArea += (current.X * next.Y) - (next.X * current.Y);
            }

            return doubledArea / 2.0;
        }
    }
}
