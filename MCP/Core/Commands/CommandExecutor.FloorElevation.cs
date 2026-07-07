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
        /// V3-S1 板高程/斜板：批次設定控制樓板的平板偏移、斜板（低側邊＋坡率 1:n）或還原平板。
        /// 斜板規則：低側邊維持目前高程，其餘頂點依垂距/n 抬升（SlopedPlaneMath，SlabShape 逐頂點寫入）。
        /// 逐板容錯；單一交易。
        /// </summary>
        private object SetFloorElevations(JObject parameters)
        {
            var doc = _uiApp.ActiveUIDocument.Document;
            var entries = parameters["floors"] as JArray;
            if (entries == null || entries.Count == 0)
            {
                throw new ArgumentException("請提供 floors 陣列（每項含 floorId 與動作）。");
            }

            IToposolidGradingAdapter adapter = new RevitToposolidGradingAdapter();
            var results = new List<object>();
            using (var transaction = new Transaction(doc, "板高程/斜板設定"))
            {
                GradingFailuresPreprocessor.Attach(transaction, new GradingFailuresPreprocessor());
                transaction.Start();
                foreach (var entry in entries.OfType<JObject>())
                {
                    var floorId = entry["floorId"]?.Value<long>() ?? 0;
                    var actions = new List<string>();
                    try
                    {
                        var floor = adapter.ValidateFloors(doc, new[] { floorId })[0];

                        if (entry["reset"]?.Value<bool?>() == true)
                        {
                            var resetEditor = floor.GetSlabShapeEditor();
                            resetEditor.ResetSlabShape();
                            actions.Add("還原平板");
                        }

                        var offsetMeters = entry["offsetMeters"]?.Value<double?>();
                        if (offsetMeters.HasValue)
                        {
                            var parameter = floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)
                                ?? throw new InvalidOperationException("樓板沒有「自標高偏移」參數。");
                            parameter.Set(UnitUtils.ConvertToInternalUnits(offsetMeters.Value, UnitTypeId.Meters));
                            actions.Add($"偏移 {offsetMeters.Value:0.###}m");
                        }

                        if (entry["slope"] is JObject slope)
                        {
                            actions.Add(ApplyFloorSlope(doc, adapter, floor, slope));
                        }

                        if (actions.Count == 0)
                        {
                            actions.Add("（未指定動作，略過）");
                        }

                        doc.Regenerate();
                        var currentOffset = floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.AsDouble() ?? 0;
                        results.Add(new
                        {
                            FloorId = floorId,
                            Success = true,
                            Actions = actions,
                            OffsetMeters = UnitUtils.ConvertFromInternalUnits(currentOffset, UnitTypeId.Meters)
                        });
                    }
                    catch (Exception ex)
                    {
                        results.Add(new { FloorId = floorId, Success = false, Actions = actions, Error = ex.Message });
                    }
                }

                transaction.Commit();
            }

            var successCount = results.Count(result => (bool)result.GetType().GetProperty("Success").GetValue(result));
            return new
            {
                Results = results,
                Message = $"板高程設定完成：{successCount}/{results.Count} 片成功。"
            };
        }

        /// <summary>斜板：以 (x,y) 公尺座標找該板最近邊界邊當「低側邊」，依 1:n 把各 SlabShape 頂點抬到斜平面。</summary>
        private static string ApplyFloorSlope(
            Document doc, IToposolidGradingAdapter adapter, Autodesk.Revit.DB.Floor floor, JObject slope)
        {
            var ratio = slope["slopeRatio"]?.Value<string>();
            var runPerRise = TransitionSettings.ParseSlopeRatio(ratio);
            var xMeters = slope["x"]?.Value<double?>()
                ?? throw new ArgumentException("slope 需要低側邊參考點 x（公尺）。");
            var yMeters = slope["y"]?.Value<double?>()
                ?? throw new ArgumentException("slope 需要低側邊參考點 y（公尺）。");

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
                    $"低側邊參考點離板邊 {UnitUtils.ConvertFromInternalUnits(detailed.Distance, UnitTypeId.Meters):0.0}m，過遠（上限 3m）。");
            }

            var edgeStart = loop[detailed.EdgeIndex];
            var edgeEnd = loop[(detailed.EdgeIndex + 1) % loop.Count];

            var editor = floor.GetSlabShapeEditor();
            editor.ResetSlabShape(); // 先回平板，確保寫入語意為「相對平板抬升」。
            editor.Enable();
            doc.Regenerate();

            var vertices = new List<SlabShapeVertex>();
            foreach (SlabShapeVertex vertex in editor.SlabShapeVertices)
            {
                vertices.Add(vertex);
            }

            if (vertices.Count == 0)
            {
                throw new InvalidOperationException("樓板沒有可編輯的 SlabShape 頂點。");
            }

            var positions = vertices
                .Select(vertex => new Point2D(vertex.Position.X, vertex.Position.Y))
                .ToList();
            var rises = SlopedPlaneMath.VertexRises(positions, edgeStart, edgeEnd, runPerRise);
            for (var index = 0; index < vertices.Count; index++)
            {
                if (rises[index] > 1e-9)
                {
                    editor.ModifySubElement(vertices[index], rises[index]);
                }
            }

            doc.Regenerate();
            var edgeLengthMeters = UnitUtils.ConvertFromInternalUnits(
                Math.Sqrt(
                    ((edgeEnd.X - edgeStart.X) * (edgeEnd.X - edgeStart.X))
                    + ((edgeEnd.Y - edgeStart.Y) * (edgeEnd.Y - edgeStart.Y))),
                UnitTypeId.Meters);
            return $"斜板 {ratio}（低側邊長 {edgeLengthMeters:0.0}m、頂點 {vertices.Count}）";
        }
    }
}
#endif
