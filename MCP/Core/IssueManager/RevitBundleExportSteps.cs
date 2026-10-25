#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCP.Models;

namespace RevitMCP.Core.IssueManager
{
    /// <summary>
    /// Revit 端的匯出步驟：全部經既有的指令分派器（CommandExecutor.ExecuteCommand）執行，不另寫 Revit API 匯出碼、
    /// 也不經 WebSocket（所以 MCP 服務關閉時照樣可用，CLAUDE.md「Do Not Bypass MCP」指的是臨時 WebSocket 腳本）。
    /// 必須在 Revit API 執行緒呼叫（由 RevitApiThreadRunner 保證）。
    /// </summary>
    internal sealed class RevitBundleExportSteps : IBundleExportSteps
    {
        private readonly UIApplication _uiApp;
        private readonly string _expectedTitle;
        private readonly string _expectedPath;

        public RevitBundleExportSteps(UIApplication uiApp, ProjectSnapshot snapshot)
        {
            _uiApp = uiApp ?? throw new ArgumentNullException("uiApp");
            _expectedTitle = snapshot.DocumentTitle;
            _expectedPath = snapshot.DocumentPath;
        }

        public RevitDocInfo GetDocInfo()
        {
            var doc = RequireSameDocument();
            return new RevitDocInfo
            {
                Title = doc.Title,
                VersionNumber = _uiApp.Application.VersionNumber + " (" + _uiApp.Application.VersionBuild + ")"
            };
        }

        public void ExportPdf(IReadOnlyList<SheetItem> sheetsInOrder, string outputPath)
        {
            RequireSameDocument();
            var ids = new JArray(sheetsInOrder.Select(s => (object)s.ElementId).ToArray());
            Dispatch("export_sheets_pdf", new JObject { ["sheetIds"] = ids, ["combine"] = true, ["outputPath"] = outputPath });
            if (!File.Exists(outputPath))
                throw new InvalidOperationException("PDF 匯出完成但找不到輸出檔；請確認 Revit 的 PDF 匯出可用（Revit 2022 以上）。");
        }

        public ModelExportResult ExportModel(bool includeDesignOptions, string workDirectory, string outputGlbPath)
        {
            RequireSameDocument();
            var exportDir = Path.Combine(workDirectory, "model");
            Directory.CreateDirectory(exportDir);

            var parameters = new JObject { ["outputDir"] = exportDir, ["originOffsetMeters"] = new JArray(0, 0, 0) };
            if (!includeDesignOptions)
            {
                var listed = Dispatch("list_design_options", new JObject { ["includeViews"] = false });
                var primaryKeys = new JArray();
                foreach (var set in listed["sets"] as JArray ?? new JArray())
                {
                    foreach (var option in set["options"] as JArray ?? new JArray())
                    {
                        if (option["isPrimary"] != null && (bool)option["isPrimary"]) primaryKeys.Add((string)option["optionKey"]);
                    }
                }
                if (primaryKeys.Count > 0) parameters["optionKeys"] = primaryKeys;
            }

            var report = Dispatch("export_design_options", parameters);
            if (!(report["complete"] != null && (bool)report["complete"]))
            {
                var failed = (report["parts"] as JArray ?? new JArray()).Count(p => p["ok"] == null || !(bool)p["ok"]);
                throw new InvalidOperationException(
                    "3D 模型匯出不完整（" + failed + " 個分件未成功），為避免上傳缺漏的模型已中止。"
                    + "細節請看 " + Path.Combine(exportDir, "design_options_export.json")
                    + "；也可以取消勾選「匯出 3D 模型」先上傳圖紙。");
            }

            var origin = ReadOrigin(report);
            var parts = report["parts"] as JArray ?? new JArray();
            var hasOptionParts = parts.Any(p => (string)p["kind"] == "option");
            if (!hasOptionParts)
            {
                File.Copy(Path.Combine(exportDir, "main_model.glb"), outputGlbPath, true);
            }
            else
            {
                var mergedDir = Path.Combine(exportDir, "merged");
                Directory.CreateDirectory(mergedDir);
                var merged = Path.Combine(mergedDir, "model.glb");
                GlbMergeRunner.Merge(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
                    Path.Combine(exportDir, "design_options_export.json"), merged);
                File.Copy(merged, outputGlbPath, true);
            }

            var result = new ModelExportResult { OriginOffsetMeters = origin };
            var textureProblems = report["textureProblems"] as JArray;
            if (textureProblems != null && textureProblems.Count > 0)
                result.Warnings.Add(textureProblems.Count + " 個材質的貼圖檔找不到，已退回材質顏色。");
            if (report["ignoredNoGeometryCount"] != null && (int)report["ignoredNoGeometryCount"] > 0)
                result.Warnings.Add((int)report["ignoredNoGeometryCount"] + " 個已知非幾何成員（空嵌板、格線、標註等）未納入模型。");
            return result;
        }

        public void ExportNamedViews(IReadOnlyList<string> viewUniqueIds, double[] originOffsetMeters, string workDirectory, string outputPath)
        {
            RequireSameDocument();
            var dir = Path.Combine(workDirectory, "named_views");
            Directory.CreateDirectory(dir);
            Dispatch("export_named_views", new JObject
            {
                ["outputDir"] = dir,
                ["originOffsetMeters"] = new JArray(originOffsetMeters[0], originOffsetMeters[1], originOffsetMeters[2]),
                ["uniqueIds"] = new JArray(viewUniqueIds.ToArray())
            });
            var produced = Path.Combine(dir, "named_views.json");
            if (!File.Exists(produced)) throw new InvalidOperationException("3D 視角匯出完成但找不到輸出檔。");
            File.Copy(produced, outputPath, true);
        }

        private static double[] ReadOrigin(JObject report)
        {
            var array = report["originOffsetMeters"] as JArray;
            if (array == null || array.Count != 3) throw new InvalidOperationException("模型匯出報告沒有共用原點。");
            return new[] { (double)array[0], (double)array[1], (double)array[2] };
        }

        private Autodesk.Revit.DB.Document RequireSameDocument()
        {
            var doc = _uiApp.ActiveUIDocument == null ? null : _uiApp.ActiveUIDocument.Document;
            if (doc == null) throw new InvalidOperationException("目前沒有開啟的 Revit 專案。");
            if (!string.Equals(doc.Title, _expectedTitle, StringComparison.Ordinal) || !string.Equals(doc.PathName, _expectedPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("作用中的文件已經不是開啟這個視窗時的文件，請關閉視窗後重新開啟。");
            return doc;
        }

        private JObject Dispatch(string command, JObject parameters)
        {
            var response = new CommandExecutor(_uiApp).ExecuteCommand(new RevitCommandRequest
            {
                CommandName = command,
                Parameters = parameters,
                RequestId = "issue-manager-" + Guid.NewGuid().ToString("N")
            });
            if (!response.Success)
                throw new InvalidOperationException("「" + command + "」失敗：" + response.Error);
            var data = response.Data as JToken ?? (response.Data == null ? new JObject() : JToken.FromObject(response.Data));
            return data as JObject ?? new JObject();
        }
    }

    /// <summary>把工作排進 ExternalEventManager 佇列，在 Revit API 執行緒執行（與 MCP 指令共用同一條佇列，不會互相打斷）。</summary>
    internal sealed class RevitApiThreadRunner : IApiThreadRunner
    {
        public Task<T> RunAsync<T>(Func<T> work)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            ExternalEventManager.Instance.ExecuteCommand(app =>
            {
                try
                {
                    completion.SetResult(work());
                }
                catch (OperationCanceledException)
                {
                    completion.SetCanceled();
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            });
            return completion.Task;
        }
    }
}
