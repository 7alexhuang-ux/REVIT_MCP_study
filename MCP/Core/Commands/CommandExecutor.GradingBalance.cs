#if REVIT2024_OR_GREATER
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitMCP.Core.Grading;

namespace RevitMCP.Core
{
    public partial class CommandExecutor
    {
        /// <summary>
        /// 平衡高程反求：以二分法試算樓板統一升降量，使淨土方逼近目標（預設 0）。
        /// 每次試算在單一交易內完成並回滾，不留任何痕跡；數字一律來自 Revit 內建 CUT/FILL。
        /// apply=true 且收斂時，把解出的偏移寫入樓板並轉呼叫既有整地命令落成方案。
        /// </summary>
        private object SolveBalancedElevation(JObject parameters)
        {
            var stopwatch = Stopwatch.StartNew();
            var timeline = new GradingTimeline();
            var request = new GradingRequest
            {
                ToposolidId = parameters["toposolidId"]?.Value<long>() ?? 0,
                FloorIds = parameters["floorIds"]?.Values<long>().ToArray() ?? new long[0],
                Mode = parameters["mode"]?.Value<string>() ?? "footprint_only",
                TargetFace = parameters["targetFace"]?.Value<string>() ?? "bottom",
                AllowPhaseSetup = parameters["allowPhaseSetup"]?.Value<bool>() ?? true,
                UpdateExisting = false,
                OffsetDistanceMeters = parameters["offsetDistance"]?.Value<double?>(),
                SlopeRatio = parameters["slopeRatio"]?.Value<string>(),
                MaxExtensionMeters = parameters["maxExtension"]?.Value<double?>(),
                LooseFactor = parameters["looseFactor"]?.Value<double?>(),
                CompactionFactor = parameters["compactionFactor"]?.Value<double?>()
            };
            request.Validate();
            var settings = TransitionSettings.FromRequest(request);

            var targetNet = parameters["targetNetCubicMeters"]?.Value<double?>() ?? 0.0;
            var toleranceCubicMeters = parameters["toleranceCubicMeters"]?.Value<double?>() ?? 10.0;
            var maxAdjustMeters = parameters["maxAdjustMeters"]?.Value<double?>() ?? 10.0;
            var maxIterations = parameters["maxIterations"]?.Value<int?>() ?? 10;
            var apply = parameters["apply"]?.Value<bool?>() ?? false;
            if (toleranceCubicMeters <= 0)
            {
                throw new ArgumentException("toleranceCubicMeters 必須大於 0。");
            }

            if (maxAdjustMeters <= 0)
            {
                throw new ArgumentException("maxAdjustMeters 必須大於 0。");
            }

            if (maxIterations <= 0 || maxIterations > 30)
            {
                throw new ArgumentException("maxIterations 必須介於 1 與 30 之間（每次試算需重跑整地）。");
            }

            var doc = _uiApp.ActiveUIDocument.Document;
            var failuresPreprocessor = new GradingFailuresPreprocessor();
            IToposolidGradingAdapter adapter = new RevitToposolidGradingAdapter(timeline);
            Toposolid original;
            IReadOnlyList<Floor> floors;
            using (timeline.Measure("元素驗證"))
            {
                original = adapter.ValidateToposolid(doc, request.ToposolidId);
                floors = adapter.ValidateFloors(doc, request.FloorIds);
            }

            var trialIndex = 0;
            double EvaluateNet(double offsetMeters)
            {
                trialIndex++;
                var offsetFeet = UnitUtils.ConvertToInternalUnits(offsetMeters, UnitTypeId.Meters);
                using (timeline.Measure($"試算#{trialIndex}（offset {offsetMeters:F3} m）"))
                using (var trial = new Transaction(doc, "平衡試算"))
                {
                    GradingFailuresPreprocessor.Attach(trial, failuresPreprocessor);
                    if (trial.Start() != TransactionStatus.Started)
                    {
                        throw new InvalidOperationException("無法啟動平衡試算交易。");
                    }

                    try
                    {
                        adapter.ShiftFloorHeights(doc, floors, offsetFeet);
                        var footprints = adapter.ExtractBottomFootprints(floors);
                        var design = adapter.CreateDesignCopy(doc, original, request.AllowPhaseSetup);
                        var trialWarnings = new List<string>();
                        adapter.ApplyGrading(doc, original, design, footprints, settings, trialWarnings);
                        doc.Regenerate();
                        var (cut, fill) = adapter.ReadCutFill(design);
                        return fill - cut;
                    }
                    catch (Exception exception) when (!(exception is InvalidOperationException
                        && exception.Message.StartsWith("平衡試算於", StringComparison.Ordinal)))
                    {
                        throw new InvalidOperationException(
                            $"平衡試算於 offset {offsetMeters:F2} m 失敗：{exception.Message}"
                            + "（可嘗試縮小 maxAdjustMeters）", exception);
                    }
                    finally
                    {
                        // 試算絕不留痕：無論成敗一律回滾。
                        trial.RollBack();
                    }
                }
            }

            try
            {
                var solve = BalanceSolver.Solve(
                    EvaluateNet,
                    targetNet,
                    toleranceCubicMeters,
                    -maxAdjustMeters,
                    maxAdjustMeters,
                    maxIterations);

                object gradeResult = null;
                var warnings = new List<string>();
                if (apply)
                {
                    if (!solve.Converged)
                    {
                        warnings.Add(
                            $"二分法在 {maxIterations} 次迭代內未達容差 {toleranceCubicMeters} m³"
                            + $"（最佳淨值 {solve.AchievedNet:F2} m³），未套用樓板調整。");
                    }
                    else
                    {
                        using (timeline.Measure("套用平衡高程"))
                        using (var applyTransaction = new Transaction(doc, "套用平衡高程"))
                        {
                            GradingFailuresPreprocessor.Attach(applyTransaction, failuresPreprocessor);
                            if (applyTransaction.Start() != TransactionStatus.Started)
                            {
                                throw new InvalidOperationException("無法啟動套用平衡高程交易。");
                            }

                            adapter.ShiftFloorHeights(
                                doc,
                                floors,
                                UnitUtils.ConvertToInternalUnits(solve.SolvedOffset, UnitTypeId.Meters));
                            if (applyTransaction.Commit() != TransactionStatus.Committed)
                            {
                                throw new InvalidOperationException("套用平衡高程交易未能提交。");
                            }
                        }

                        var gradeParameters = new JObject
                        {
                            ["toposolidId"] = request.ToposolidId,
                            ["floorIds"] = new JArray(request.FloorIds.ToArray()),
                            ["mode"] = request.Mode,
                            ["targetFace"] = request.TargetFace,
                            ["allowPhaseSetup"] = request.AllowPhaseSetup,
                            ["schemeName"] = parameters["schemeName"]?.Value<string>()
                                ?? $"平衡方案（{solve.SolvedOffset:+0.00;-0.00} m）"
                        };
                        if (request.OffsetDistanceMeters.HasValue)
                        {
                            gradeParameters["offsetDistance"] = request.OffsetDistanceMeters.Value;
                        }

                        if (!string.IsNullOrWhiteSpace(request.SlopeRatio))
                        {
                            gradeParameters["slopeRatio"] = request.SlopeRatio;
                        }

                        if (request.MaxExtensionMeters.HasValue)
                        {
                            gradeParameters["maxExtension"] = request.MaxExtensionMeters.Value;
                        }

                        if (request.LooseFactor.HasValue)
                        {
                            gradeParameters["looseFactor"] = request.LooseFactor.Value;
                            gradeParameters["compactionFactor"] = request.CompactionFactor.Value;
                        }

                        gradeResult = GradeToposolidToFloors(gradeParameters);
                    }
                }

                var timing = new
                {
                    TotalMilliseconds = stopwatch.ElapsedMilliseconds,
                    Stages = timeline.Stages
                        .Select(stage => new { stage.Name, stage.ElapsedMilliseconds })
                        .ToArray()
                };

                WriteGradingPerformanceRecord(new
                {
                    Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    Document = doc.Title,
                    Command = "solve_balanced_elevation",
                    Success = true,
                    Error = (string)null,
                    request.Mode,
                    request.ToposolidId,
                    FloorIds = request.FloorIds,
                    SolvedOffsetMeters = solve.SolvedOffset,
                    AchievedNetCubicMeters = solve.AchievedNet,
                    solve.Converged,
                    Applied = apply && solve.Converged,
                    Timing = timing
                });

                return new
                {
                    SolvedOffsetMeters = solve.SolvedOffset,
                    AchievedNetCubicMeters = solve.AchievedNet,
                    solve.Converged,
                    TargetNetCubicMeters = targetNet,
                    ToleranceCubicMeters = toleranceCubicMeters,
                    Iterations = solve.Iterations
                        .Select(iteration => new
                        {
                            OffsetMeters = iteration.Offset,
                            NetCubicMeters = iteration.Net
                        })
                        .ToArray(),
                    Applied = apply && solve.Converged,
                    Warnings = warnings,
                    GradeResult = gradeResult,
                    Timing = timing,
                    Message = solve.Converged
                        ? $"平衡高程反求完成：樓板統一{(solve.SolvedOffset >= 0 ? "抬升" : "下降")} "
                          + $"{Math.Abs(solve.SolvedOffset):F2} m 時淨土方 {solve.AchievedNet:F2} m³"
                          + (apply ? "，已套用並落成方案。" : "（僅試算，未修改模型）。")
                        : $"二分法未收斂：最佳解 offset {solve.SolvedOffset:F2} m、淨值 {solve.AchievedNet:F2} m³；"
                          + "可增加 maxIterations 或放寬 toleranceCubicMeters。"
                };
            }
            catch (Exception exception)
            {
                WriteGradingPerformanceRecord(new
                {
                    Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    Document = doc.Title,
                    Command = "solve_balanced_elevation",
                    Success = false,
                    Error = exception.Message,
                    request.Mode,
                    request.ToposolidId,
                    FloorIds = request.FloorIds,
                    TotalMilliseconds = stopwatch.ElapsedMilliseconds,
                    Stages = timeline.Stages
                        .Select(stage => new { stage.Name, stage.ElapsedMilliseconds })
                        .ToArray()
                });
                throw;
            }
        }
    }
}
#endif
