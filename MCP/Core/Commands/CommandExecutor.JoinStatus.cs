using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

#if REVIT2025_OR_GREATER
using IdType = System.Int64;
#else
using IdType = System.Int32;
#endif

namespace RevitMCP.Core
{
    public partial class CommandExecutor
    {
        /// <summary>兩兩配對數為 n(n-1)/2，超過這個元素數就不做（60 個 = 1770 對）。</summary>
        private const int JoinPairMaxElements = 60;

        /// <summary>BoundingBox 間距在這個範圍內視為「相接」（mm）。</summary>
        private const double JoinTouchToleranceMm = 1.0;

        /// <summary>
        /// 唯讀：回報一組元素兩兩之間的幾何接合狀態，以及會讓「接合了線卻沒消失」的成因
        /// （沒接合、階段不同、材料不同）。不開 Transaction。
        /// </summary>
        private object GetJoinStatus(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            List<Element> elements = ResolveJoinElements(doc, parameters, out string source);

            var elementRows = new List<object>();
            var materialsById = new Dictionary<ElementId, List<string>>();
            foreach (Element e in elements)
            {
                List<string> materials = GetJoinMaterialNames(doc, e);
                materialsById[e.Id] = materials;
                int joinedCount = 0;
                try { joinedCount = JoinGeometryUtils.GetJoinedElements(doc, e).Count; } catch { }
                elementRows.Add(new
                {
                    Id = e.Id.GetIdValue(),
                    Name = e.Name,
                    Category = e.Category?.Name,
                    PhaseCreated = GetJoinPhaseName(doc, e.CreatedPhaseId),
                    PhaseDemolished = GetJoinPhaseName(doc, e.DemolishedPhaseId),
                    Materials = materials,
                    JoinedElementCount = joinedCount
                });
            }

            var pairRows = new List<object>();
            int joined = 0, notJoinedTouching = 0, apart = 0, phaseMismatch = 0, materialMismatch = 0;
            for (int i = 0; i < elements.Count; i++)
            {
                for (int j = i + 1; j < elements.Count; j++)
                {
                    Element a = elements[i], b = elements[j];
                    bool isJoined = false;
                    string error = null;
                    try { isJoined = JoinGeometryUtils.AreElementsJoined(doc, a, b); }
                    catch (Exception ex) { error = ex.Message; }

                    IdType? cuttingId = null;
                    if (isJoined)
                    {
                        try { cuttingId = (JoinGeometryUtils.IsCuttingElementInJoin(doc, a, b) ? a : b).Id.GetIdValue(); } catch { }
                    }

                    double? gapMm = GetJoinBoundingBoxGapMm(a, b);
                    bool touching = gapMm.HasValue && gapMm.Value <= JoinTouchToleranceMm;
                    bool samePhase = a.CreatedPhaseId == b.CreatedPhaseId && a.DemolishedPhaseId == b.DemolishedPhaseId;
                    List<string> sharedMaterials = materialsById[a.Id].Intersect(materialsById[b.Id]).ToList();
                    bool sameMaterial = sharedMaterials.Count > 0;

                    var reasons = new List<string>();
                    string verdict;
                    if (error != null)
                    {
                        verdict = "無法判定";
                        reasons.Add("AreElementsJoined 失敗：" + error);
                    }
                    else if (!isJoined && !touching)
                    {
                        verdict = "未接合（不相鄰）";
                        reasons.Add(gapMm.HasValue ? $"BoundingBox 相距 {gapMm.Value:F0} mm，不相接，本來就不會接合" : "讀不到 BoundingBox");
                        apart++;
                    }
                    else if (!isJoined)
                    {
                        verdict = "未接合（相鄰）";
                        reasons.Add("兩者相接但沒有接合，交界線會保留；可用 join_elements 補接");
                        notJoinedTouching++;
                    }
                    else
                    {
                        verdict = "已接合";
                        joined++;
                    }
                    if (!samePhase && (isJoined || touching))
                    {
                        reasons.Add("階段不同，即使接合交界線也不會消失");
                        phaseMismatch++;
                    }
                    if (!sameMaterial && (isJoined || touching))
                    {
                        reasons.Add("沒有共同材料，即使接合交界線也不會消失");
                        materialMismatch++;
                    }
                    if (isJoined && samePhase && sameMaterial)
                        reasons.Add("接合、階段、材料都一致；若仍看得到線，該處兩個面不共平面（真實的轉角邊），屬正常");

                    pairRows.Add(new
                    {
                        A = a.Id.GetIdValue(),
                        B = b.Id.GetIdValue(),
                        ACategory = a.Category?.Name,
                        BCategory = b.Category?.Name,
                        Joined = isJoined,
                        CuttingElementId = cuttingId,
                        BoundingBoxGapMm = gapMm.HasValue ? Math.Round(gapMm.Value, 1) : (double?)null,
                        SamePhase = samePhase,
                        SharedMaterials = sharedMaterials,
                        Verdict = verdict,
                        Reasons = reasons
                    });
                }
            }

            return new
            {
                Success = true,
                Source = source,
                ElementCount = elements.Count,
                PairCount = pairRows.Count,
                Summary = new
                {
                    Joined = joined,
                    NotJoinedButTouching = notJoinedTouching,
                    NotAdjacent = apart,
                    PhaseMismatch = phaseMismatch,
                    MaterialMismatch = materialMismatch
                },
                Note = "相鄰與否以 BoundingBox 判斷（容差 1 mm），斜向或 L 形元素可能誤判為相鄰；材料比對用元素本體材料（不含塗料）。",
                Elements = elementRows,
                Pairs = pairRows
            };
        }

        /// <summary>
        /// 把一組元素中「相鄰但尚未接合」的配對逐對 JoinGeometry。單一 Transaction（可用 Revit 復原）。
        /// 每對各自一個 SubTransaction：一對失敗只回滾那一對。
        /// </summary>
        private object JoinElements(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            bool dryRun = parameters["dryRun"]?.Value<bool>() ?? false;
            List<Element> elements = ResolveJoinElements(doc, parameters, out string source);

            var candidates = new List<Tuple<Element, Element>>();
            int alreadyJoined = 0, notAdjacent = 0;
            for (int i = 0; i < elements.Count; i++)
            {
                for (int j = i + 1; j < elements.Count; j++)
                {
                    Element a = elements[i], b = elements[j];
                    bool isJoined = false;
                    try { isJoined = JoinGeometryUtils.AreElementsJoined(doc, a, b); } catch { }
                    if (isJoined) { alreadyJoined++; continue; }
                    double? gapMm = GetJoinBoundingBoxGapMm(a, b);
                    if (!gapMm.HasValue || gapMm.Value > JoinTouchToleranceMm) { notAdjacent++; continue; }
                    candidates.Add(new Tuple<Element, Element>(a, b));
                }
            }

            var joinedPairs = new List<object>();
            var failedPairs = new List<object>();
            if (!dryRun && candidates.Count > 0)
            {
                using (Transaction trans = TransactionHelper.Begin(doc, "Join Elements"))
                {
                    trans.Start();
                    foreach (var pair in candidates)
                    {
                        using (SubTransaction st = new SubTransaction(doc))
                        {
                            try
                            {
                                st.Start();
                                JoinGeometryUtils.JoinGeometry(doc, pair.Item1, pair.Item2);
                                st.Commit();
                                joinedPairs.Add(new { A = pair.Item1.Id.GetIdValue(), B = pair.Item2.Id.GetIdValue() });
                            }
                            catch (Exception ex)
                            {
                                try { if (st.HasStarted() && !st.HasEnded()) st.RollBack(); } catch { }
                                failedPairs.Add(new { A = pair.Item1.Id.GetIdValue(), B = pair.Item2.Id.GetIdValue(), Error = ex.Message });
                            }
                        }
                    }
                    trans.Commit();
                }
            }

            return new
            {
                Success = true,
                DryRun = dryRun,
                Source = source,
                ElementCount = elements.Count,
                AlreadyJoined = alreadyJoined,
                NotAdjacent = notAdjacent,
                Candidates = candidates.Select(p => new { A = p.Item1.Id.GetIdValue(), B = p.Item2.Id.GetIdValue() }).ToList(),
                JoinedCount = joinedPairs.Count,
                Joined = joinedPairs,
                FailedCount = failedPairs.Count,
                Failed = failedPairs,
                Message = dryRun
                    ? $"預覽：{candidates.Count} 對相鄰但未接合，{alreadyJoined} 對已接合，{notAdjacent} 對不相鄰（未寫入）"
                    : $"已接合 {joinedPairs.Count} 對，失敗 {failedPairs.Count} 對；原本已接合 {alreadyJoined} 對，不相鄰 {notAdjacent} 對"
            };
        }

        /// <summary>elementIds 有給就用，沒給用目前選取。少於 2 個或超過上限直接報錯。</summary>
        private List<Element> ResolveJoinElements(Document doc, JObject parameters, out string source)
        {
            var ids = new List<ElementId>();
            if (parameters["elementIds"] is JArray idsArray && idsArray.Count > 0)
            {
                ids.AddRange(idsArray.Select(id => new ElementId(id.Value<IdType>())));
                source = "elementIds";
            }
            else
            {
                ids.AddRange(_uiApp.ActiveUIDocument.Selection.GetElementIds());
                source = "selection";
            }

            List<Element> elements = ids.Distinct()
                .Select(id => doc.GetElement(id))
                .Where(e => e != null && !(e is ElementType))
                .ToList();
            if (elements.Count < 2)
                throw new Exception($"至少需要 2 個元素（{(source == "selection" ? "目前選取" : "elementIds")}只有 {elements.Count} 個）");
            if (elements.Count > JoinPairMaxElements)
                throw new Exception($"元素數 {elements.Count} 超過上限 {JoinPairMaxElements}（兩兩配對數會過大），請分批");
            return elements;
        }

        private static string GetJoinPhaseName(Document doc, ElementId phaseId)
        {
            if (phaseId == null || phaseId == ElementId.InvalidElementId) return null;
            return doc.GetElement(phaseId)?.Name;
        }

        private static List<string> GetJoinMaterialNames(Document doc, Element e)
        {
            try
            {
                return e.GetMaterialIds(false)
                    .Select(id => doc.GetElement(id)?.Name)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Distinct()
                    .OrderBy(n => n)
                    .ToList();
            }
            catch { return new List<string>(); }
        }

        /// <summary>兩個 BoundingBox 之間的最短距離（mm）；重疊或相貼 = 0；讀不到 = null。</summary>
        private static double? GetJoinBoundingBoxGapMm(Element a, Element b)
        {
            BoundingBoxXYZ ba = a.get_BoundingBox(null), bb = b.get_BoundingBox(null);
            if (ba == null || bb == null) return null;
            double dx = Math.Max(0, Math.Max(ba.Min.X - bb.Max.X, bb.Min.X - ba.Max.X));
            double dy = Math.Max(0, Math.Max(ba.Min.Y - bb.Max.Y, bb.Min.Y - ba.Max.Y));
            double dz = Math.Max(0, Math.Max(ba.Min.Z - bb.Max.Z, bb.Min.Z - ba.Max.Z));
            return Math.Sqrt(dx * dx + dy * dy + dz * dz) * 304.8;
        }
    }
}
