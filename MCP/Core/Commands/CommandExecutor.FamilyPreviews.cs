using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Core
{
    public partial class CommandExecutor
    {
        #region 族群預覽圖匯出（export_family_previews）

        /// <summary>
        /// 開啟 .rfa 族群文件（不載入目前專案），逐一切換族群類型並匯出平面／立面／3D 預覽 PNG，
        /// 於 outputDir 寫 manifest.json。單一類型或單一族群失敗只記錄，不中止整批。
        /// </summary>
        private object ExportFamilyPreviews(JObject parameters)
        {
            var familyPaths = (parameters?["familyPaths"] as JArray)?
                .Select(t => t.Value<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
            if (familyPaths == null || familyPaths.Count == 0)
                throw new ArgumentException("familyPaths 必須是至少一個 .rfa 絕對路徑的陣列。");

            var outputDirParam = parameters["outputDir"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(outputDirParam))
                throw new ArgumentException("outputDir 為必填。");
            var outputDir = Path.GetFullPath(outputDirParam);
            Directory.CreateDirectory(outputDir);

            int pixelSize = parameters["pixelSize"]?.Value<int?>() ?? 800;
            if (pixelSize < 100 || pixelSize > 8000)
                throw new ArgumentException("pixelSize 必須介於 100 到 8000。");
            var typeFilter = parameters["typeFilter"]?.Value<string>();

            var manifest = new JArray();
            var failures = new JArray();
            int successCount = 0;

            foreach (var rawPath in familyPaths)
            {
                string familyName = Path.GetFileNameWithoutExtension(rawPath ?? "");
                Document famDoc = null;
                try
                {
                    var fullPath = Path.GetFullPath(rawPath);
                    if (!string.Equals(Path.GetExtension(fullPath), ".rfa", StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("路徑必須指向 .rfa 檔案。");
                    if (!File.Exists(fullPath))
                        throw new FileNotFoundException("找不到族群檔案。", fullPath);

                    famDoc = _uiApp.Application.OpenDocumentFile(fullPath);
                    if (famDoc == null || !famDoc.IsFamilyDocument)
                        throw new InvalidOperationException("開啟的文件不是族群文件。");

                    FamilyPreviewViews views = PrepareFamilyPreviewViews(famDoc);
                    var fm = famDoc.FamilyManager;
                    var types = fm.Types.Cast<FamilyType>().ToList();
                    if (types.Count == 0)
                        types.Add(null); // 沒有任何類型：以目前狀態匯出一次

                    foreach (var t in types)
                    {
                        string typeName = t?.Name ?? "(default)";
                        if (!string.IsNullOrEmpty(typeFilter) &&
                            typeName.IndexOf(typeFilter, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        var entry = new JObject
                        {
                            ["family"] = familyName,
                            ["type"] = typeName,
                            ["plan"] = null,
                            ["elev"] = null,
                            ["threed"] = null,
                            ["error"] = null
                        };
                        try
                        {
                            if (t != null)
                            {
                                using (var tx = new Transaction(famDoc, "切換族群類型"))
                                {
                                    tx.Start();
                                    fm.CurrentType = t;
                                    famDoc.Regenerate();
                                    tx.Commit();
                                }
                            }

                            string baseName = SafePreviewFileName(familyName) + "__" + SafePreviewFileName(typeName);
                            var errs = new List<string>();
                            entry["plan"] = TryExportPreview(famDoc, views.Plan, outputDir, baseName + "__plan", pixelSize, errs, "平面");
                            entry["elev"] = TryExportPreview(famDoc, views.Elevation, outputDir, baseName + "__elev", pixelSize, errs, "立面");
                            entry["threed"] = TryExportPreview(famDoc, views.ThreeD, outputDir, baseName + "__3d", pixelSize, errs, "3D");
                            if (errs.Count > 0) entry["error"] = string.Join("；", errs);
                            if (entry["plan"].Type != JTokenType.Null ||
                                entry["elev"].Type != JTokenType.Null ||
                                entry["threed"].Type != JTokenType.Null)
                                successCount++;
                            else
                                failures.Add(new JObject { ["family"] = familyName, ["type"] = typeName, ["error"] = entry["error"] });
                        }
                        catch (Exception ex)
                        {
                            entry["error"] = ex.Message;
                            failures.Add(new JObject { ["family"] = familyName, ["type"] = typeName, ["error"] = ex.Message });
                            Logger.Error($"[族群預覽] {familyName}/{typeName} 失敗：{ex.Message}");
                        }
                        manifest.Add(entry);
                    }
                }
                catch (Exception ex)
                {
                    manifest.Add(new JObject
                    {
                        ["family"] = familyName,
                        ["type"] = null,
                        ["plan"] = null,
                        ["elev"] = null,
                        ["threed"] = null,
                        ["error"] = ex.Message
                    });
                    failures.Add(new JObject { ["family"] = familyName, ["type"] = null, ["error"] = ex.Message });
                    Logger.Error($"[族群預覽] {familyName} 失敗：{ex.Message}");
                }
                finally
                {
                    if (famDoc != null)
                    {
                        try { famDoc.Close(false); }
                        catch (Exception ex) { Logger.Error($"[族群預覽] 關閉 {familyName} 失敗：{ex.Message}"); }
                    }
                }
            }

            var tmpDir = Path.Combine(outputDir, "_fp_tmp");
            try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); } catch { /* 暫存清理失敗不影響結果 */ }

            var manifestPath = Path.Combine(outputDir, "manifest.json");
            File.WriteAllText(manifestPath, manifest.ToString(Newtonsoft.Json.Formatting.Indented),
                new System.Text.UTF8Encoding(false));

            return new
            {
                SuccessTypeCount = successCount,
                FailureCount = failures.Count,
                Failures = failures,
                ManifestPath = manifestPath,
                OutputDir = outputDir
            };
        }

        private sealed class FamilyPreviewViews
        {
            public View Plan;
            public View Elevation;
            public View ThreeD;
        }

        private static FamilyPreviewViews PrepareFamilyPreviewViews(Document famDoc)
        {
            var allViews = new FilteredElementCollector(famDoc).OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate).ToList();

            var plans = allViews.Where(v => v.ViewType == ViewType.FloorPlan).ToList();
            var plan = plans.FirstOrDefault(v => v.Name.IndexOf("Ref", StringComparison.OrdinalIgnoreCase) >= 0
                                                 || v.Name.Contains("參照"))
                       ?? plans.FirstOrDefault();

            var elevs = allViews.Where(v => v.ViewType == ViewType.Elevation).ToList();
            var elev = elevs.FirstOrDefault(v => v.Name.IndexOf("Front", StringComparison.OrdinalIgnoreCase) >= 0
                                                 || v.Name.Contains("前"))
                       ?? elevs.FirstOrDefault();

            var v3d = allViews.OfType<View3D>().FirstOrDefault(v => !v.IsPerspective);

            using (var tx = new Transaction(famDoc, "準備族群預覽 3D 視圖"))
            {
                tx.Start();
                if (v3d == null)
                {
                    var vft = new FilteredElementCollector(famDoc).OfClass(typeof(ViewFamilyType))
                        .Cast<ViewFamilyType>().FirstOrDefault(x => x.ViewFamily == ViewFamily.ThreeDimensional);
                    if (vft != null) v3d = View3D.CreateIsometric(famDoc, vft.Id);
                }
                if (v3d != null)
                {
                    try { v3d.DisplayStyle = DisplayStyle.Realistic; } catch { /* 不支援則維持原樣 */ }
                }
                tx.Commit();
            }

            return new FamilyPreviewViews { Plan = plan, Elevation = elev, ThreeD = v3d };
        }

        private static JToken TryExportPreview(Document doc, View view, string outputDir, string fileBase,
            int pixelSize, List<string> errors, string label)
        {
            if (view == null)
            {
                errors.Add($"{label}：族群內找不到可用視圖");
                return JValue.CreateNull();
            }
            try
            {
                var tmpDir = Path.Combine(outputDir, "_fp_tmp");
                Directory.CreateDirectory(tmpDir);
                foreach (var stale in Directory.GetFiles(tmpDir)) File.Delete(stale);

                var options = new ImageExportOptions
                {
                    ExportRange = ExportRange.SetOfViews,
                    FilePath = Path.Combine(tmpDir, "export"),
                    HLRandWFViewsFileType = ImageFileType.PNG,
                    ShadowViewsFileType = ImageFileType.PNG,
                    ImageResolution = ImageResolution.DPI_150,
                    ZoomType = ZoomFitType.FitToPage,
                    PixelSize = pixelSize
                };
                options.SetViewsAndSheets(new List<ElementId> { view.Id });
                doc.ExportImage(options);

                var produced = Directory.GetFiles(tmpDir, "*.png")
                    .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                if (produced == null)
                {
                    errors.Add($"{label}：ExportImage 未產出 PNG");
                    return JValue.CreateNull();
                }
                var target = Path.Combine(outputDir, fileBase + ".png");
                if (File.Exists(target)) File.Delete(target);
                File.Move(produced, target);
                return new JValue(Path.GetFileName(target));
            }
            catch (Exception ex)
            {
                errors.Add($"{label}：{ex.Message}");
                return JValue.CreateNull();
            }
        }

        private static string SafePreviewFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "_";
            var sb = new System.Text.StringBuilder(name.Length);
            var invalid = Path.GetInvalidFileNameChars();
            foreach (var c in name) sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            return sb.ToString().Trim().TrimEnd('.');
        }

        #endregion
    }
}
