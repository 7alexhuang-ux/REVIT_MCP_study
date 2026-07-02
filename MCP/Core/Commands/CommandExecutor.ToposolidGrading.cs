#if REVIT2024_OR_GREATER
using System;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitMCP.Core.Grading;

namespace RevitMCP.Core
{
    public partial class CommandExecutor
    {
        /// <summary>
        /// 將樓板底面投影套用至 Toposolid 設計副本，並計算挖填方。
        /// </summary>
        private object GradeToposolidToFloors(JObject parameters)
        {
            var request = new GradingRequest
            {
                ToposolidId = parameters["toposolidId"]?.Value<long>() ?? 0,
                FloorIds = parameters["floorIds"]?.Values<long>().ToArray() ?? new long[0],
                Mode = parameters["mode"]?.Value<string>() ?? "footprint_only",
                TargetFace = parameters["targetFace"]?.Value<string>() ?? "bottom",
                AllowPhaseSetup = parameters["allowPhaseSetup"]?.Value<bool>() ?? false,
                UpdateExisting = parameters["updateExisting"]?.Value<bool>() ?? false
            };
            request.Validate();

            var doc = _uiApp.ActiveUIDocument.Document;
            IToposolidGradingAdapter adapter = new RevitToposolidGradingAdapter();
            var original = adapter.ValidateToposolid(doc, request.ToposolidId);
            var floors = adapter.ValidateFloors(doc, request.FloorIds);
            var footprints = adapter.ExtractBottomFootprints(floors);

            Toposolid design = null!;
            string associationId = null!;
            var modifiedPointCount = 0;
            var cutCubicMeters = 0.0;
            var fillCubicMeters = 0.0;

            using (var group = new TransactionGroup(doc, "樓板投影整地"))
            {
                try
                {
                    if (group.Start() != TransactionStatus.Started)
                    {
                        throw new InvalidOperationException("無法啟動樓板投影整地交易群組。");
                    }

                    using (var setupTransaction = new Transaction(doc, "建立整地設計副本"))
                    {
                        if (setupTransaction.Start() != TransactionStatus.Started)
                        {
                            throw new InvalidOperationException("無法啟動建立整地設計副本交易。");
                        }

                        design = adapter.CreateDesignCopy(doc, original, request.AllowPhaseSetup);
                        associationId = adapter.WriteAssociation(
                            doc,
                            design,
                            request.ToposolidId,
                            request.FloorIds);

                        if (setupTransaction.Commit() != TransactionStatus.Committed)
                        {
                            throw new InvalidOperationException("建立整地設計副本交易未能提交。");
                        }
                    }

                    using (var gradingTransaction = new Transaction(doc, "套用樓板投影並計算挖填方"))
                    {
                        if (gradingTransaction.Start() != TransactionStatus.Started)
                        {
                            throw new InvalidOperationException("無法啟動套用樓板投影交易。");
                        }

                        modifiedPointCount = adapter.ApplyFootprintOnly(doc, design, footprints);
                        doc.Regenerate();
                        (cutCubicMeters, fillCubicMeters) = adapter.ReadCutFill(design);

                        if (gradingTransaction.Commit() != TransactionStatus.Committed)
                        {
                            throw new InvalidOperationException("套用樓板投影交易未能提交。");
                        }
                    }

                    if (group.Assimilate() != TransactionStatus.Committed)
                    {
                        throw new InvalidOperationException("樓板投影整地交易群組未能完整提交。");
                    }
                }
                catch
                {
                    if (group.GetStatus() == TransactionStatus.Started)
                    {
                        group.RollBack();
                    }

                    throw;
                }
            }

            var result = new GradingResult
            {
                OriginalToposolidId = request.ToposolidId,
                DesignToposolidId = design.Id.GetIdValue(),
                FloorIds = request.FloorIds,
                CutCubicMeters = cutCubicMeters,
                FillCubicMeters = fillCubicMeters,
                ModifiedPointCount = modifiedPointCount,
                AssociationId = associationId,
                Warnings = new string[0]
            };

            return new
            {
                result.OriginalToposolidId,
                result.DesignToposolidId,
                result.FloorIds,
                result.CutCubicMeters,
                result.FillCubicMeters,
                result.NetCubicMeters,
                result.ModifiedPointCount,
                result.AssociationId,
                result.Warnings,
                Message = "樓板投影整地完成。"
            };
        }
    }
}
#endif
