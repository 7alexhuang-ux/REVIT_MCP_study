#if REVIT2024_OR_GREATER
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitMCP.Core.Grading;

namespace RevitMCP.Core
{
    public partial class CommandExecutor
    {
        /// <summary>
        /// V3-S2 邊坡率政策：以 (x,y) 公尺座標指定樓板的一條邊界邊用例外坡率（如臨路無障礙 1:12），
        /// 其餘邊沿用整地時的預設坡率。政策存於樓板 ES；clear=true 清空該板全部政策。
        /// </summary>
        private object SetFloorEdgeSlope(JObject parameters)
        {
            var doc = _uiApp.ActiveUIDocument.Document;
            var floorId = parameters["floorId"]?.Value<long>() ?? 0;
            IToposolidGradingAdapter adapter = new RevitToposolidGradingAdapter();
            var floor = adapter.ValidateFloors(doc, new[] { floorId })[0];

            if (parameters["clear"]?.Value<bool?>() == true)
            {
                using (var clearTransaction = new Transaction(doc, "清除板邊坡率政策"))
                {
                    clearTransaction.Start();
                    FloorEdgePolicyStore.Write(floor, Array.Empty<StoredEdgePolicy>());
                    clearTransaction.Commit();
                }

                return new
                {
                    FloorId = floorId,
                    Policies = Array.Empty<object>(),
                    Message = $"樓板 {floorId} 的邊坡率政策已全部清除。"
                };
            }

            var ratio = parameters["slopeRatio"]?.Value<string>();
            TransitionSettings.ParseSlopeRatio(ratio); // 驗證格式 1:n
            var xMeters = parameters["x"]?.Value<double?>()
                ?? throw new ArgumentException("請提供邊參考點 x（公尺，專案內部原點座標）。");
            var yMeters = parameters["y"]?.Value<double?>()
                ?? throw new ArgumentException("請提供邊參考點 y（公尺，專案內部原點座標）。");

            var footprint = adapter.ExtractBottomFootprints(new[] { floor })[0];
            var loop = footprint.OuterLoop;
            var sample = new Point2D(
                UnitUtils.ConvertToInternalUnits(xMeters, UnitTypeId.Meters),
                UnitUtils.ConvertToInternalUnits(yMeters, UnitTypeId.Meters));
            var detailed = Polygon2D.NearestBoundaryPointDetailed(loop, sample);
            var maxSnapFeet = UnitUtils.ConvertToInternalUnits(3.0, UnitTypeId.Meters);
            if (detailed.Distance > maxSnapFeet)
            {
                throw new InvalidOperationException(
                    $"參考點離板邊 {UnitUtils.ConvertFromInternalUnits(detailed.Distance, UnitTypeId.Meters):0.0}m，過遠（上限 3m）；請點在目標邊附近。");
            }

            var edgeStart = loop[detailed.EdgeIndex];
            var edgeEnd = loop[(detailed.EdgeIndex + 1) % loop.Count];
            var fingerprint = EdgeSlopePolicies.FingerprintFromSegment(edgeStart, edgeEnd, 1.0);
            var newPolicy = new StoredEdgePolicy
            {
                MidX = fingerprint.MidX,
                MidY = fingerprint.MidY,
                DirX = fingerprint.DirX,
                DirY = fingerprint.DirY,
                Length = fingerprint.Length,
                SlopeRatio = ratio.Trim()
            };

            // 同一條邊已有政策 → 覆寫（以中點距離判定同邊）。
            var policies = FloorEdgePolicyStore.Read(floor).ToList();
            var sameEdgeTolerance = Math.Min(fingerprint.Length / 2.0, EdgeSlopePolicies.MidToleranceCap);
            policies.RemoveAll(existing =>
                Math.Sqrt(
                    ((existing.MidX - newPolicy.MidX) * (existing.MidX - newPolicy.MidX))
                    + ((existing.MidY - newPolicy.MidY) * (existing.MidY - newPolicy.MidY))) <= sameEdgeTolerance);
            policies.Add(newPolicy);

            using (var transaction = new Transaction(doc, "設定板邊坡率政策"))
            {
                transaction.Start();
                FloorEdgePolicyStore.Write(floor, policies);
                transaction.Commit();
            }

            return new
            {
                FloorId = floorId,
                EdgeIndex = detailed.EdgeIndex,
                EdgeLengthMeters = UnitUtils.ConvertFromInternalUnits(fingerprint.Length, UnitTypeId.Meters),
                Policies = DescribePolicies(policies),
                Message = $"樓板 {floorId} 邊（長 {UnitUtils.ConvertFromInternalUnits(fingerprint.Length, UnitTypeId.Meters):0.0}m）已設坡率 {ratio}；本板共 {policies.Count} 條政策。"
            };
        }

        internal static IReadOnlyList<object> DescribePolicies(IReadOnlyList<StoredEdgePolicy> policies)
        {
            return policies
                .Select((policy, index) => (object)new
                {
                    Index = index + 1,
                    policy.SlopeRatio,
                    LengthMeters = Math.Round(
                        UnitUtils.ConvertFromInternalUnits(policy.Length, UnitTypeId.Meters), 2),
                    MidXMeters = Math.Round(
                        UnitUtils.ConvertFromInternalUnits(policy.MidX, UnitTypeId.Meters), 2),
                    MidYMeters = Math.Round(
                        UnitUtils.ConvertFromInternalUnits(policy.MidY, UnitTypeId.Meters), 2)
                })
                .ToList();
        }
    }
}
#endif
