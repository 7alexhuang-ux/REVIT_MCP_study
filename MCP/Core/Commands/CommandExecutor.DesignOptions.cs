using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCP.Core.Walkthrough;

namespace RevitMCP.Core
{
    public partial class CommandExecutor
    {
        private const string DesignOptionVisibilityNote = "Revit 2024 公開 API 未提供每個 Option Set 的視圖 V/G 選項覆寫存取；VIEWER_OPTION_VISIBILITY 是 Visible In Option，不視為完整 V/G 設定。designOptionVisibility 回傳 null。";

        private object ListDesignOptions(JObject parameters)
        {
            var doc = _uiApp.ActiveUIDocument?.Document ?? throw new InvalidOperationException("目前沒有開啟的 Revit 專案。");
            var sets = BuildDesignOptionSets(doc);
            var views = new JArray();
            if (parameters["includeViews"]?.Value<bool?>() ?? true)
            {
                foreach (var view in new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().Where(v => !v.IsTemplate))
                    views.Add(new JObject
                    {
                        ["viewId"] = view.Id.GetIdValue(), ["uniqueId"] = view.UniqueId, ["name"] = view.Name,
                        ["isPerspective"] = view.IsPerspective, ["designOptionVisibility"] = JValue.CreateNull()
                    });
            }
            // 主模型元件數：與匯出器相同判準（Element.DesignOption == null），不依賴 ElementDesignOptionFilter(InvalidElementId) 語意（API 文件未載明）。
            var mainModelCount = new FilteredElementCollector(doc).WhereElementIsNotElementType().ToElements()
                .Count(e => e.DesignOption == null);
            return new JObject
            {
                ["revitDoc"] = doc.Title, ["mainModelElementCount"] = mainModelCount, ["sets"] = sets, ["views"] = views,
                ["notes"] = new JArray(DesignOptionVisibilityNote,
                    "清單與幾何匯出的設計選項範圍為主文件；連結中的設計選項不在本版支援範圍。")
            };
        }

        private static JArray BuildDesignOptionSets(Document doc)
        {
            var sets = new JArray();
            foreach (var option in new FilteredElementCollector(doc).OfClass(typeof(DesignOption)).Cast<DesignOption>().OrderBy(o => o.Id.GetIdValue()))
            {
                var setId = option.get_Parameter(BuiltInParameter.OPTION_SET_ID)?.AsElementId();
                var set = setId == null ? null : doc.GetElement(setId);
                if (set == null) throw new InvalidOperationException("設計選項缺少有效的 OPTION_SET_ID：" + option.UniqueId);
                var entry = sets.OfType<JObject>().FirstOrDefault(s => s["setKey"]?.Value<string>() == set.UniqueId);
                if (entry == null)
                {
                    entry = new JObject
                    {
                        ["setId"] = set.Id.GetIdValue(), ["setKey"] = set.UniqueId, ["setName"] = set.Name,
                        ["options"] = new JArray()
                    };
                    sets.Add(entry);
                }
                var count = new FilteredElementCollector(doc).WhereElementIsNotElementType()
                    .WherePasses(new ElementDesignOptionFilter(option.Id)).GetElementCount();
                ((JArray)entry["options"]).Add(new JObject
                {
                    ["optionId"] = option.Id.GetIdValue(), ["optionKey"] = option.UniqueId,
                    ["optionName"] = option.Name, ["isPrimary"] = option.IsPrimary, ["elementCount"] = count
                });
            }
            return sets;
        }

        /// <summary>主模型只匯出一次，各選項獨立取真實幾何，不展開選項集合的排列組合。</summary>
        private object ExportDesignOptions(JObject parameters)
        {
            var doc = _uiApp.ActiveUIDocument?.Document ?? throw new InvalidOperationException("目前沒有開啟的 Revit 專案。");
            if (doc.IsFamilyDocument || doc.IsReadOnly || doc.IsModifiable)
                throw new InvalidOperationException("設計選項匯出需要可寫、未開啟 Transaction 的專案文件；所有暫存視圖均以 TransactionGroup.RollBack 還原。");
            var outputDir = parameters["outputDir"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(outputDir)) outputDir = WebCompareDefaultOutputDir;
            outputDir = Path.GetFullPath(outputDir);
            Directory.CreateDirectory(outputDir);
            var offset = Vec3.Zero;
            var offsetSource = "default [0,0,0]; no existing camera.json";
            if (parameters["originOffsetMeters"] != null || parameters["cameraPath"] != null || File.Exists(Path.Combine(outputDir, "camera.json")))
                offset = ResolveNamedViewsOrigin(parameters, outputDir, out offsetSource);
            var maxTriangles = parameters["maxTriangles"]?.Value<int?>() ?? 3_000_000;
            var maxElementTriangles = parameters["maxElementTriangles"]?.Value<int?>() ?? 100_000;
            var heavy = new WebCompareHeavyOptions
            {
                OverCapMode = (parameters["overCapMode"]?.Value<string>() ?? "simplify").ToLowerInvariant(),
                SimplifyRatio = parameters["simplifyRatio"]?.Value<double?>() ?? 0.1,
                LevelOfDetail = parameters["levelOfDetail"]?.Value<int?>() ?? -1
            };
            if (maxTriangles < 1 || maxElementTriangles < 0 || !IsFiniteDesignOptionNumber(heavy.SimplifyRatio) ||
                heavy.SimplifyRatio < 0 || heavy.SimplifyRatio > 1 ||
                !new[] { "simplify", "box", "drop" }.Contains(heavy.OverCapMode) || heavy.LevelOfDetail < -1 || heavy.LevelOfDetail > 15)
                throw new ArgumentException("無效的幾何預算、簡化或 levelOfDetail 參數。");
            var excludedCategories = WebCompareStringList(parameters["excludeCategories"]);
            var excludedElements = WebCompareStringList(parameters["excludeElements"]);
            var requestedKeys = WebCompareStringList(parameters["optionKeys"]);
            var sets = BuildDesignOptionSets(doc);
            var allOptions = new FilteredElementCollector(doc).OfClass(typeof(DesignOption)).Cast<DesignOption>().ToList();
            var unknown = requestedKeys.Except(allOptions.Select(o => o.UniqueId)).ToList();
            if (unknown.Count > 0) throw new ArgumentException("找不到 optionKeys：" + string.Join(", ", unknown));
            var options = allOptions.Where(o => requestedKeys.Count == 0 || requestedKeys.Contains(o.UniqueId)).ToList();
            if (requestedKeys.Count > 0)
            {
                foreach (var set in sets.OfType<JObject>())
                    set["options"] = new JArray(((JArray)set["options"]).Where(o => requestedKeys.Contains(o["optionKey"].Value<string>())).Select(o => o.DeepClone()));
                sets = new JArray(sets.OfType<JObject>().Where(s => ((JArray)s["options"]).Count > 0));
            }
            var warnings = new List<string>
            {
                DesignOptionVisibilityNote,
                "主模型使用 CustomExporter；各主文件選項使用 Element.Geometry 實體幾何後備路徑（Solid/Mesh/GeometryInstance），不是目前視圖可見元件的 metadata 標記。",
                "本匯出採全模型範圍的暫存等角視圖；未套用來源視圖的 sectionBox、category overrides、隱藏狀態、相機或 V/G 選項設定。選項後備路徑保留材質顏色/透明度，不含材質外觀貼圖、等高線或視圖裁切。",
                "連結中的設計選項不支援，本次會省略其選項元件；主模型的非選項連結幾何仍會匯出。"
            };
            var parts = new JArray();
            var textureProblems = new JArray();
            var complete = true;
            var timer = Stopwatch.StartNew();
            // 先落一份進度，逾時或意外中止不會把前一輪成功報告當成本輪結果。
            var reportPath = Path.Combine(outputDir, "design_options_export.json");
            var report = new JObject
            {
                ["complete"] = false, ["state"] = "running", ["revitDoc"] = doc.Title,
                ["originOffsetMeters"] = new JArray(offset.X, offset.Y, offset.Z), ["parts"] = parts,
                ["originOffsetSource"] = offsetSource,
                ["geometryScope"] = "host-design-options; full-model; linked-options-excluded",
                ["warnings"] = JArray.FromObject(warnings)
            };
            WriteDesignOptionJson(reportPath, report);
            using (var group = new TransactionGroup(doc, "設計選項：暫存匯出後還原"))
            {
                group.Start();
                try
                {
                    View3D temp;
                    using (var tx = new Transaction(doc, "設計選項：建立全模型暫存等角視圖"))
                    {
                        tx.Start();
                        var type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                            .FirstOrDefault(t => t.ViewFamily == ViewFamily.ThreeDimensional);
                        if (type == null) throw new InvalidOperationException("找不到三維 ViewFamilyType。");
                        temp = View3D.CreateIsometric(doc, type.Id);
                        temp.ViewTemplateId = ElementId.InvalidElementId;
                        temp.IsSectionBoxActive = false;
                        temp.CropBoxActive = false;
                        tx.Commit();
                    }
                    var path = Path.Combine(outputDir, "main_model.glb");
                    try
                    {
                        var links = new JObject();
                        ExportWebCompareGlb(doc, temp, path, offset, maxTriangles, warnings, out var triangles, out var nodes,
                            WebCompareDocuments(doc), new WebCompareSectionOptions { Mode = "off", TerrainMode = "off" }, null,
                            new List<WebCompareTerrainPiece>(), links, parameters["contourIntervalMeters"]?.Value<double?>() ?? 1,
                            excludedCategories, excludedElements, maxElementTriangles, parameters["dumpAppearance"]?.Value<bool?>() ?? false,
                            heavy, (document, element) => element.DesignOption == null);
                        var diag = links["glbTerrain"];
                        // 貼圖找不到清單上提到報告頂層（材質名、原路徑、原因）；不偽造貼圖，該材質退回外觀顏色。
                        if (diag?["textureProblems"] is JObject texProblems)
                        {
                            foreach (var prop in texProblems.Properties())
                            {
                                var split = prop.Name.IndexOf(" :: ", StringComparison.Ordinal);
                                textureProblems.Add(new JObject
                                {
                                    ["material"] = split >= 0 ? prop.Name.Substring(0, split) : prop.Name,
                                    ["file"] = Path.GetFileName(split >= 0 ? prop.Name.Substring(split + 4) : prop.Name),
                                    ["path"] = split >= 0 ? prop.Name.Substring(split + 4) : null,
                                    ["problem"] = prop.Value.ToString()
                                });
                            }
                        }
                        var ok =!(diag?["budgetExceeded"]?.Value<bool>() ?? false) && (diag?["exceptions"]?.Value<int>() ?? 0) == 0;
                        complete &= ok;
                        parts.Add(new JObject
                        {
                            ["kind"] = "main", ["file"] = "main_model.glb", ["path"] = path,
                            ["ok"] = ok, ["geometryMode"] = "customExporter", ["triangleCount"] = triangles,
                            ["elementNodeCount"] = nodes, ["diagnostics"] = links
                        });
                    }
                    catch (Exception ex)
                    {
                        complete = false;
                        parts.Add(new JObject { ["kind"] = "main", ["file"] = "main_model.glb", ["ok"] = false, ["error"] = ex.Message });
                    }
                    foreach (var option in options)
                    {
                        var part = ExportDesignOptionGeometry(doc, option, outputDir, offset, maxTriangles, maxElementTriangles,
                            heavy, excludedCategories, excludedElements);
                        parts.Add(part);
                        complete &= part["ok"].Value<bool>();
                        report["warnings"] = JArray.FromObject(warnings);
                        WriteDesignOptionJson(reportPath, report);
                    }
                }
                finally
                {
                    // 包含所有已 Commit 的暫存視圖交易；不儲存或更動原始選項、主選項與使用者視圖。
                    if (group.GetStatus() == TransactionStatus.Started && group.RollBack() != TransactionStatus.RolledBack)
                        throw new InvalidOperationException("暫存匯出 TransactionGroup 無法確認已完整還原；請檢查 Revit 文件狀態。");
                }
            }
            var manifest = new JObject
            {
                ["sets"] = sets, ["generatedAt"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), ["revitDoc"] = doc.Title
            };
            WriteDesignOptionJson(Path.Combine(outputDir, "design_options.json"), manifest);
            var totalIgnored = parts.OfType<JObject>().Sum(p => p["ignoredNoGeometryCount"]?.Value<int>() ?? 0);
            var totalRecovered = parts.OfType<JObject>().Sum(p => (p["recoveredGeometry"] as JArray)?.Count ?? 0);
            if (totalIgnored > 0)
                warnings.Add("已知非幾何成員 " + totalIgnored.ToString(CultureInfo.InvariantCulture) + " 筆（空嵌板、零尺寸帷幕嵌板、嵌入牆嵌板、帷幕格線、標註、參考平面、草圖線、開口切割、約束、Level／Sketch／SketchPlane、牆輪廓定義）不計入失敗，逐筆列在各分件 ignoredNoGeometry（含 reason 與 basis）；有尺寸的 Glazed、找不到已匯出宿主牆的 Wall 嵌板、牆等實體類無幾何仍算失敗。");
            if (totalRecovered > 0)
                warnings.Add("有 " + totalRecovered.ToString(CultureInfo.InvariantCulture) + " 筆 0 面元素改用實例幾何或較低細緻度取得，列在各分件 recoveredGeometry；需人工比對。");
            report["ignoredNoGeometryCount"] = totalIgnored;
            report["complete"] = complete;
            report["state"] = complete ? "complete" : "incomplete";
            report["elapsedMs"] = timer.ElapsedMilliseconds;
            report["outputDir"] = outputDir;
            report["manifest"] = "design_options.json";
            report["textureProblems"] = textureProblems;
            report["warnings"] = JArray.FromObject(warnings);
            report["parameters"] = parameters.DeepClone();
            WriteDesignOptionJson(reportPath, report);
            return report;
        }

        private static JObject ExportDesignOptionGeometry(Document doc, DesignOption option, string outputDir, Vec3 offset,
            int maxTriangles, int maxElementTriangles, WebCompareHeavyOptions heavy, List<string> excludedCategories, List<string> excludedElements)
        {
            var builder = new GltfBuilder();
            var members = new FilteredElementCollector(doc).WhereElementIsNotElementType().WherePasses(new ElementDesignOptionFilter(option.Id)).ToList();
            var exported = new JArray();
            var excluded = new JArray();
            var noGeometry = new JArray();
            var ignored = new JArray();
            var recovered = new JArray();
            // 嵌入牆嵌板需等宿主牆匯出結果，延後到迴圈結束再判定。
            var pendingHostPanels = new List<PendingHostPanel>();
            var exportedTriangles = new Dictionary<long, int>();
            var containers = new JArray();
            var failed = new JArray();
            var diagnostics = new JArray();
            var materials = new Dictionary<long, int>();
            bool budgetExceeded = false;
            var memberIds = new HashSet<long>(members.Select(e => (long)e.Id.GetIdValue()));
            foreach (var element in members)
            {
                var key = RevitGltfExportContext.HostName + ":" + element.Id.GetIdValue().ToString(CultureInfo.InvariantCulture);
                if (excludedElements.Contains(key, StringComparer.OrdinalIgnoreCase) || excludedCategories.Contains(element.Category?.Name ?? "", StringComparer.OrdinalIgnoreCase))
                {
                    excluded.Add(element.UniqueId);
                    continue;
                }
                try
                {
                    // 群組容器與其成員不能各匯出一次；只在成員已完整列入本次篩選時略過容器。
                    if (element is Group group && group.GetMemberIds().Count > 0 && group.GetMemberIds().All(id => memberIds.Contains(id.GetIdValue())))
                    {
                        containers.Add(new JObject { ["uniqueId"] = element.UniqueId, ["reason"] = "group container; members exported individually" });
                        continue;
                    }
                    // 共享巢狀族群可能同時出現在父族群幾何和獨立成員中；未經模型實測不宣稱無重複。
                    if (element is FamilyInstance family && family.GetSubComponentIds().Any(id => memberIds.Contains(id.GetIdValue())))
                        diagnostics.Add("共享巢狀族群的父/子幾何覆蓋需人工驗證，選項不標為完整：" + element.UniqueId);
                    var info = new GltfElementInfo
                    {
                        ElementId = element.Id.GetIdValue(), UniqueId = element.UniqueId, Name = element.Name,
                        Category = element.Category?.Name, Family = (doc.GetElement(element.GetTypeId()) as ElementType)?.FamilyName,
                        Source = RevitGltfExportContext.HostName, DesignOption = RevitGltfExportContext.ReadDesignOptionInfo(element)
                    };
                    builder.BeginElement(info);
                    var geometry = element.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine, IncludeNonVisibleObjects = false });
                    if (geometry != null) AddDesignOptionGeometry(doc, geometry, Transform.Identity, builder, materials, offset, maxTriangles, diagnostics);
                    var sourceTriangles = builder.CurrentElementTriangles;
                    if (sourceTriangles == 0)
                    {
                        var builtInName = DesignOptionBuiltInCategoryName(element);
                        var className = element.GetType().Name;
                        var entry = new JObject
                        {
                            ["elementId"] = info.ElementId, ["uniqueId"] = info.UniqueId, ["name"] = info.Name, ["category"] = info.Category,
                            ["builtInCategory"] = builtInName, ["class"] = className
                        };
                        // 1) 已知非幾何白名單：放行，但逐筆留在 ignoredNoGeometry（不消失）。實體類（Glazed／Wall／Aluminium…）不在名單內。
                        var facts = BuildNoGeometryFacts(doc, element, info.Category, builtInName, className, info.Name, memberIds);
                        var decision = DesignOptionNoGeometryPolicy.Evaluate(facts);
                        if (decision != null)
                        {
                            entry["reason"] = decision.Reason;
                            entry["basis"] = decision.Basis;
                            ignored.Add(entry);
                            builder.DiscardElement();
                            continue;
                        }
                        // 1b) 嵌入牆嵌板：宿主牆可能還沒處理，先記下，迴圈結束後依宿主牆實際匯出三角面判定。
                        if (facts.HostWallId.HasValue)
                        {
                            pendingHostPanels.Add(new PendingHostPanel { Entry = entry, Facts = facts });
                            builder.DiscardElement();
                            continue;
                        }
                        // 2) 帷幕牆本體自身無幾何、全部面板與豎框都在本選項內（各自獨立匯出）：與 Group 容器同概念，不重複匯出。
                        //    面板自己的 0 面仍會各自計為 noGeometry，所以不會掩蓋缺漏。
                        if (IsCurtainWallHostWithAllMembers(element, memberIds, out var gridMemberCount))
                        {
                            containers.Add(new JObject
                            {
                                ["uniqueId"] = element.UniqueId, ["name"] = info.Name,
                                ["reason"] = "curtain wall host; grid panels and mullions exported individually",
                                ["gridMemberCount"] = gridMemberCount
                            });
                            builder.DiscardElement();
                            continue;
                        }
                        // 3) 其他 0 面元素：換 Options／實例幾何重試；有取得才算數，並標示取得方式，仍為 0 則照舊算失敗。
                        var retryDiagnostics = new JArray();
                        var via = RetryDesignOptionGeometry(doc, element, builder, materials, offset, maxTriangles, retryDiagnostics);
                        if (via == null)
                        {
                            noGeometry.Add(entry);
                            builder.DiscardElement();
                            continue;
                        }
                        foreach (var d in retryDiagnostics) diagnostics.Add(d);
                        sourceTriangles = builder.CurrentElementTriangles;
                        entry["recoveredBy"] = via;
                        entry["triangles"] = sourceTriangles;
                        recovered.Add(entry);
                    }
                    if (maxElementTriangles > 0 && sourceTriangles > maxElementTriangles)
                    {
                        info.SourceTriangles = sourceTriangles;
                        if (heavy.OverCapMode == "simplify")
                            builder.SimplifyCurrentElement(heavy.SimplifyRatio > 0 ? (int)Math.Round(sourceTriangles * heavy.SimplifyRatio) : maxElementTriangles);
                        else if (heavy.OverCapMode == "box") builder.ReplaceCurrentElementWithBox();
                        else
                        {
                            excluded.Add(new JObject { ["uniqueId"] = info.UniqueId, ["reason"] = "overCapMode=drop", ["sourceTriangles"] = sourceTriangles });
                            builder.DiscardElement();
                            continue;
                        }
                    }
                    exported.Add(new JObject { ["uniqueId"] = info.UniqueId, ["elementId"] = info.ElementId,
                        ["sourceTriangles"] = sourceTriangles, ["triangles"] = builder.CurrentElementTriangles });
                    exportedTriangles[(long)info.ElementId] = builder.CurrentElementTriangles;
                    builder.EndElement();
                }
                catch (Exception ex)
                {
                    builder.DiscardElement();
                    failed.Add(new JObject { ["elementId"] = element.Id.GetIdValue(), ["uniqueId"] = element.UniqueId, ["error"] = ex.Message });
                    if (ex is DesignOptionBudgetException) { budgetExceeded = true; break; }
                }
            }
            // 嵌入牆嵌板：宿主牆已匯出（triangles>0）才放行；否則算失敗，維持原本的 noGeometry 揭露。
            foreach (var pending in pendingHostPanels)
            {
                var hostId = pending.Facts.HostWallId.Value;
                pending.Facts.HostWallExported = exportedTriangles.TryGetValue(hostId, out var hostTriangles) && hostTriangles > 0;
                pending.Facts.HostWallTriangles = pending.Facts.HostWallExported ? hostTriangles : 0;
                var decision = DesignOptionNoGeometryPolicy.Evaluate(pending.Facts);
                if (decision != null)
                {
                    pending.Entry["reason"] = decision.Reason;
                    pending.Entry["basis"] = decision.Basis;
                    ignored.Add(pending.Entry);
                }
                else
                {
                    pending.Entry["hostWallId"] = hostId;
                    pending.Entry["hostWallExported"] = false;
                    noGeometry.Add(pending.Entry);
                }
            }
            var file = "option_" + option.UniqueId + ".glb";
            var path = Path.Combine(outputDir, file);
            File.WriteAllBytes(path, builder.BuildGlb());
            // 沒有輸出幾何的成員逐筆揭露，不能把只有 metadata 的選項判成完整成功。
            // 只有白名單（ignoredNoGeometry）與已證實的帷幕牆容器不計入失敗；其餘 noGeometry 仍使整份選項 ok=false。
            var ok = !budgetExceeded && failed.Count == 0 && diagnostics.Count == 0 && noGeometry.Count == 0;
            var ignoredSummary = new JObject();
            foreach (var reasonGroup in ignored.OfType<JObject>().GroupBy(i => i["reason"].Value<string>()).OrderBy(g => g.Key, StringComparer.Ordinal))
                ignoredSummary[reasonGroup.Key] = reasonGroup.Count();
            return new JObject
            {
                ["kind"] = "option", ["file"] = file, ["path"] = path, ["optionKey"] = option.UniqueId,
                ["setKey"] = doc.GetElement(option.get_Parameter(BuiltInParameter.OPTION_SET_ID).AsElementId()).UniqueId,
                ["ok"] = ok, ["geometryMode"] = "elementGeometry", ["candidateCount"] = members.Count,
                ["elementNodeCount"] = builder.ElementNodeCount, ["triangleCount"] = builder.TriangleCount,
                ["exported"] = exported, ["excluded"] = excluded, ["noGeometry"] = noGeometry, ["containers"] = containers,
                ["ignoredNoGeometry"] = ignored, ["ignoredNoGeometryCount"] = ignored.Count, ["ignoredNoGeometrySummary"] = ignoredSummary,
                ["recoveredGeometry"] = recovered,
                ["failed"] = failed, ["diagnostics"] = diagnostics, ["budgetExceeded"] = budgetExceeded
            };
        }

        private sealed class PendingHostPanel
        {
            public JObject Entry;
            public NoGeometryFacts Facts;
        }

        /// <summary>
        /// 收集無幾何判斷所需的事實（純資料）：系統嵌板的寬高（先讀 CURTAIN_WALL_PANELS_WIDTH／HEIGHT 實例，再讀類型；
        /// 都讀不到才以 bounding box 為空視為 0×0）與 Panel.FindHostPanel() 指向的、屬於本選項成員的 Wall。
        /// 注意：FindHostPanel 是 Panel 的方法（回傳嵌入牆 ElementId），不是 Wall 的方法。
        /// </summary>
        private static NoGeometryFacts BuildNoGeometryFacts(Document doc, Element element, string category, string builtIn,
            string className, string name, HashSet<long> memberIds)
        {
            var facts = new NoGeometryFacts { CategoryName = category, BuiltInCategory = builtIn, ClassName = className, Name = name };
            var panel = element as Panel;
            if (panel == null) return facts;
            try
            {
                double? ReadFeet(BuiltInParameter bip)
                {
                    var p = element.get_Parameter(bip);
                    if (p == null || !p.HasValue || p.StorageType != StorageType.Double)
                        p = (doc.GetElement(element.GetTypeId()) as ElementType)?.get_Parameter(bip);
                    return p != null && p.HasValue && p.StorageType == StorageType.Double ? p.AsDouble() : (double?)null;
                }
                facts.PanelWidthFeet = ReadFeet(BuiltInParameter.CURTAIN_WALL_PANELS_WIDTH);
                facts.PanelHeightFeet = ReadFeet(BuiltInParameter.CURTAIN_WALL_PANELS_HEIGHT);
                facts.PanelSizeSource = "parameter";
                if (!facts.PanelWidthFeet.HasValue || !facts.PanelHeightFeet.HasValue)
                {
                    facts.PanelWidthFeet = null;
                    facts.PanelHeightFeet = null;
                    if (element.get_BoundingBox(null) == null)
                    {
                        facts.PanelWidthFeet = 0;
                        facts.PanelHeightFeet = 0;
                        facts.PanelSizeSource = "boundingBox-empty";
                    }
                }
            }
            catch (Exception) { facts.PanelWidthFeet = null; facts.PanelHeightFeet = null; }
            try
            {
                var hostId = panel.FindHostPanel();
                if (hostId != null && hostId != ElementId.InvalidElementId && memberIds.Contains((long)hostId.GetIdValue())
                    && doc.GetElement(hostId) is Wall)
                    facts.HostWallId = (long)hostId.GetIdValue();
            }
            catch (Exception) { facts.HostWallId = null; }
            return facts;
        }

        private sealed class DesignOptionBudgetException : Exception
        {
            public DesignOptionBudgetException() : base("選項三角面超過 maxTriangles；目前元件已捨棄，未處理的後續成員未匯出。") { }
        }

        /// <summary>BuiltInCategory 的 Enum 名稱（語系無關）；無分類或非內建分類回 null。</summary>
        private static string DesignOptionBuiltInCategoryName(Element element)
        {
            try
            {
                var category = element.Category;
                if (category == null) return null;
                var value = (long)category.Id.GetIdValue();
                if (value < int.MinValue || value > int.MaxValue || !Enum.IsDefined(typeof(BuiltInCategory), (int)value)) return null;
                return ((BuiltInCategory)(int)value).ToString();
            }
            catch (Exception) { return null; }
        }

        /// <summary>帷幕牆本體：有 CurtainGrid、面板＋豎框至少一個，且全部都在本選項成員內（各自獨立匯出）。</summary>
        private static bool IsCurtainWallHostWithAllMembers(Element element, HashSet<long> memberIds, out int gridMemberCount)
        {
            gridMemberCount = 0;
            try
            {
                var wall = element as Wall;
                var grid = wall?.CurtainGrid;
                if (grid == null) return false;
                var ids = new HashSet<long>();
                foreach (var id in grid.GetPanelIds()) ids.Add((long)id.GetIdValue());
                foreach (var id in grid.GetMullionIds()) ids.Add((long)id.GetIdValue());
                gridMemberCount = ids.Count;
                return ids.Count > 0 && ids.All(memberIds.Contains);
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// 0 面元素的後備取幾何：依序改用實例幾何（GetInstanceGeometry，含實例參數）、Medium、Coarse 細緻度。
        /// 不開 IncludeNonVisibleObjects，避免把隱藏子類別（空腔、參考線）混進模型。回傳取得幾何的方式；仍為 0 回 null。
        /// 沒有 Revit 實機驗證前，不得宣稱這些後備能救回 Glazed／Wall 嵌板。
        /// </summary>
        private static string RetryDesignOptionGeometry(Document doc, Element element, GltfBuilder builder,
            Dictionary<long, int> materials, Vec3 offset, int budget, JArray diagnostics)
        {
            var names = new[] { "fine-instanceGeometry", "medium-symbolGeometry", "coarse-symbolGeometry" };
            var levels = new[] { ViewDetailLevel.Fine, ViewDetailLevel.Medium, ViewDetailLevel.Coarse };
            for (var i = 0; i < names.Length; i++)
            {
                try
                {
                    var geometry = element.get_Geometry(new Options { DetailLevel = levels[i], IncludeNonVisibleObjects = false });
                    if (geometry != null) AddDesignOptionGeometry(doc, geometry, Transform.Identity, builder, materials, offset, budget, diagnostics, i == 0);
                }
                catch (DesignOptionBudgetException) { throw; }
                catch (Exception) { continue; }
                if (builder.CurrentElementTriangles > 0) return names[i];
            }
            return null;
        }

        private static void AddDesignOptionGeometry(Document doc, GeometryElement geometry, Transform transform, GltfBuilder builder,
            Dictionary<long, int> materials, Vec3 offset, int budget, JArray diagnostics, bool instanceGeometry = false)
        {
            foreach (var obj in geometry)
            {
                if (obj is GeometryInstance instance)
                {
                    // GetInstanceGeometry 已含實例變換；GetSymbolGeometry 是類型座標，需再乘 instance.Transform。
                    if (instanceGeometry)
                        AddDesignOptionGeometry(doc, instance.GetInstanceGeometry(), transform, builder, materials, offset, budget, diagnostics, true);
                    else
                        AddDesignOptionGeometry(doc, instance.GetSymbolGeometry(), transform.Multiply(instance.Transform), builder, materials, offset, budget, diagnostics);
                }
                else if (obj is Solid solid)
                {
                    foreach (Face face in solid.Faces)
                        AddDesignOptionMesh(doc, face.Triangulate(), face.MaterialElementId, transform, builder, materials, offset, budget);
                }
                else if (obj is Mesh mesh)
                    AddDesignOptionMesh(doc, mesh, mesh.MaterialElementId, transform, builder, materials, offset, budget);
                else if (!(obj is Curve) && !(obj is PolyLine) && !(obj is Point))
                    diagnostics.Add("未支援的幾何型別：" + obj.GetType().FullName);
            }
        }

        private static void AddDesignOptionMesh(Document doc, Mesh mesh, ElementId materialId, Transform transform, GltfBuilder builder,
            Dictionary<long, int> materials, Vec3 offset, int budget)
        {
            if (builder.TriangleCount + mesh.NumTriangles > budget) throw new DesignOptionBudgetException();
            var key = materialId.GetIdValue();
            if (!materials.TryGetValue(key, out var materialIndex))
            {
                var material = doc.GetElement(materialId) as Material;
                var color = material?.Color;
                var alpha = 1 - (material?.Transparency ?? 0) / 100.0;
                if (alpha < 0.12) alpha = 0.30;
                materialIndex = builder.AddMaterial(material?.Name ?? "Default", (color?.Red ?? 191) / 255.0,
                    (color?.Green ?? 191) / 255.0, (color?.Blue ?? 191) / 255.0, alpha);
                materials[key] = materialIndex;
            }
            var positions = new List<Vec3>();
            var normals = new List<Vec3>();
            var indices = new List<int>();
            for (var i = 0; i < mesh.NumTriangles; i++)
            {
                var triangle = mesh.get_Triangle(i);
                var p = new Vec3[3];
                for (var j = 0; j < 3; j++)
                {
                    var v = transform.OfPoint(triangle.get_Vertex(j));
                    p[j] = new Vec3(v.X * CameraPathMath.FeetToMeters - offset.X,
                        v.Z * CameraPathMath.FeetToMeters - offset.Z, -(v.Y * CameraPathMath.FeetToMeters - offset.Y));
                }
                // 鏡射實例需反轉繞序，法向量依變換後三角形重新計算。
                if (transform.HasReflection) { var swap = p[1]; p[1] = p[2]; p[2] = swap; }
                var normal = (p[1] - p[0]).Cross(p[2] - p[0]).Normalized();
                for (var j = 0; j < 3; j++) { indices.Add(positions.Count); positions.Add(p[j]); normals.Add(normal); }
            }
            builder.AddTriangles(materialIndex, positions, normals, indices);
        }

        private static bool IsFiniteDesignOptionNumber(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        private static void WriteDesignOptionJson(string path, JObject value) =>
            File.WriteAllText(path, value.ToString(Formatting.Indented), new UTF8Encoding(false));
    }
}
