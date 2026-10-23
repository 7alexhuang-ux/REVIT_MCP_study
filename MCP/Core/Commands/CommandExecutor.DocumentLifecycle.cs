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
        // Detached 文件的 PathName/Title 可能為空；只追蹤本次 Revit session 實際開啟的來源，勿猜路徑。
        private sealed class OpenedDocumentSource
        {
            public string Path;
            public bool DetachApplied;
            public bool PreserveWorksets;
        }

        private static readonly Dictionary<Document, OpenedDocumentSource> OpenedDocumentSources =
            new Dictionary<Document, OpenedDocumentSource>();

        private static void PruneOpenedDocumentSources()
        {
            foreach (var doc in OpenedDocumentSources.Keys.Where(d => !d.IsValidObject).ToList())
                OpenedDocumentSources.Remove(doc);
        }

        private static string DocumentLifecycleTitle(Document doc)
        {
            if (!string.IsNullOrEmpty(doc.Title)) return doc.Title;
            return OpenedDocumentSources.TryGetValue(doc, out var source)
                ? Path.GetFileNameWithoutExtension(source.Path) : "";
        }

        private JObject DocumentLifecycleInfo(Document doc)
        {
            OpenedDocumentSources.TryGetValue(doc, out var source);
            return new JObject
            {
                ["title"] = DocumentLifecycleTitle(doc),
                ["revitTitle"] = doc.Title,
                ["path"] = doc.PathName,
                ["sourcePath"] = source == null ? JValue.CreateNull() : new JValue(source.Path),
                ["isActive"] = ReferenceEquals(_uiApp.ActiveUIDocument?.Document, doc),
                ["isLinked"] = doc.IsLinked,
                ["isWorkshared"] = doc.IsWorkshared,
                ["isDetached"] = doc.IsDetached,
                ["detachApplied"] = source == null ? JValue.CreateNull() : new JValue(source.DetachApplied),
                ["isModified"] = doc.IsModified,
                ["isReadOnly"] = doc.IsReadOnly
            };
        }

        private static string RequireExistingRvtPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) ||
                !(path.StartsWith(@"\\", StringComparison.Ordinal) ||
                  (path.Length >= 3 && path[1] == ':' && (path[2] == '\\' || path[2] == '/'))))
                throw new ArgumentException("path 必須是既存 .rvt 檔案的完整絕對路徑（支援本機/UNC；不下載或猜測來源）。");
            var fullPath = Path.GetFullPath(path);
            if (!string.Equals(Path.GetExtension(fullPath), ".rvt", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("path 必須指向 .rvt 檔案。");
            if (!File.Exists(fullPath)) throw new FileNotFoundException("找不到 Revit 檔案。", fullPath);
            return fullPath;
        }

        private object OpenDocument(JObject parameters)
        {
            var path = RequireExistingRvtPath(parameters.Value<string>("path"));
            bool detach = parameters.Value<bool?>("detach") ?? true;
            bool preserveWorksets = parameters.Value<bool?>("preserveWorksets") ?? true;
            bool activate = parameters.Value<bool?>("activate") ?? true;
            bool audit = parameters.Value<bool?>("audit") ?? false;
            PruneOpenedDocumentSources();

            var matches = _uiApp.Application.Documents.Cast<Document>().Where(d => !d.IsLinked &&
                (string.Equals(d.PathName, path, StringComparison.OrdinalIgnoreCase) ||
                 (OpenedDocumentSources.TryGetValue(d, out var s) &&
                  string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase)))).ToList();
            if (matches.Count > 1)
                throw new InvalidOperationException("同一來源有多份文件已開啟，請先用 list_open_documents 檢查並消除歧義。");

            var warnings = new JArray();
            if (matches.Count == 1)
            {
                var existing = matches[0];
                OpenedDocumentSources.TryGetValue(existing, out var source);
                // 不能以「已開啟」掩蓋 detach/worksets 的要求；不偷偷重開另一份模型。
                bool actuallyDetached = source?.DetachApplied ?? existing.IsDetached;
                if ((existing.IsWorkshared || actuallyDetached) && detach != actuallyDetached)
                    throw new InvalidOperationException("來源已開啟，但 detach 狀態與請求不同；請保留目前文件，另行明確決定關閉後再開啟。");
                if (actuallyDetached && source != null && source.PreserveWorksets != preserveWorksets)
                    throw new InvalidOperationException("來源已開啟，但 preserveWorksets 與首次開啟不同；不會改動既有文件。");
                if (activate && !ReferenceEquals(_uiApp.ActiveUIDocument?.Document, existing))
                {
                    if (string.IsNullOrEmpty(existing.PathName))
                        throw new InvalidOperationException("此 detached 背景文件沒有可供 API 啟用的 PathName；請用 activate=true 重新規劃開啟流程，或在 Revit UI 啟用它。既有文件未關閉。");
                    EnsureNoLifecycleTransaction();
                    // 不傳 true：第三參數 true 會忽略 DoNotDetach 並跳出 detach 選擇對話框。
                    _uiApp.OpenAndActivateDocument(existing.PathName);
                }
                if (audit) warnings.Add("文件已開啟，未重新執行 audit。");
                return new JObject { ["alreadyOpen"] = true, ["document"] = DocumentLifecycleInfo(existing), ["warnings"] = warnings };
            }

            EnsureNoLifecycleTransaction();
            bool workshared, central;
            using (var info = BasicFileInfo.Extract(path)) { workshared = info.IsWorkshared; central = info.IsWorkshared && info.IsCentral; }
            // 安全規則：不以 detach=false 開啟工作共享中央檔（會鎖定中央、可能與他人衝突或改動中央）。
            if (central && !detach)
                throw new InvalidOperationException("拒絕開啟：此檔是工作共享中央檔（IsCentral=true），detach=false 一律不允許。請用 detach=true（預設），或改開自己的本機副本。");
            if (!workshared && detach) warnings.Add("來源不是工作共享模型；detach/preserveWorksets 不適用。");
            using (var options = new OpenOptions())
            using (var modelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(path))
            {
                options.Audit = audit;
                options.DetachFromCentralOption = workshared && detach
                    ? (preserveWorksets ? DetachFromCentralOption.DetachAndPreserveWorksets : DetachFromCentralOption.DetachAndDiscardWorksets)
                    : DetachFromCentralOption.DoNotDetach;
                // ExternalEventManager 於 UI 執行緒呼叫；不自行排程 Idling 開檔，不開 Transaction。
                var opened = activate
                    ? _uiApp.OpenAndActivateDocument(modelPath, options, false).Document
                    : _uiApp.Application.OpenDocumentFile(modelPath, options);
                OpenedDocumentSources[opened] = new OpenedDocumentSource
                {
                    Path = path, DetachApplied = workshared && detach, PreserveWorksets = preserveWorksets
                };
                return new JObject
                {
                    ["alreadyOpen"] = false, ["document"] = DocumentLifecycleInfo(opened),
                    ["requested"] = new JObject { ["detach"] = detach, ["preserveWorksets"] = preserveWorksets, ["activate"] = activate, ["audit"] = audit },
                    ["warnings"] = warnings
                };
            }
        }

        private object ListOpenDocuments(JObject parameters)
        {
            PruneOpenedDocumentSources();
            var documents = new JArray(_uiApp.Application.Documents.Cast<Document>()
                .OrderBy(DocumentLifecycleTitle, StringComparer.OrdinalIgnoreCase).Select(DocumentLifecycleInfo));
            return new JObject { ["documents"] = documents, ["count"] = documents.Count };
        }

        private object CloseDocument(JObject parameters)
        {
            string title = parameters.Value<string>("title");
            if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("title 為必填，請先呼叫 list_open_documents。");
            bool discard = parameters.Value<bool?>("discard") ?? false;
            PruneOpenedDocumentSources();
            var matches = _uiApp.Application.Documents.Cast<Document>()
                .Where(d => string.Equals(DocumentLifecycleTitle(d), title, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count != 1)
                throw new InvalidOperationException(matches.Count == 0 ? "找不到此 title 的已開啟文件。" : "title 對應多份文件；請先在 Revit UI 消除同名歧義。");
            var doc = matches[0];
            if (doc.IsLinked) throw new InvalidOperationException("不可用 close_document 關閉連結文件。");
            if (ReferenceEquals(_uiApp.ActiveUIDocument?.Document, doc))
                throw new InvalidOperationException("Revit API 不允許關閉目前作用中文件；請先用 open_document 啟用另一份文件，或在 Revit UI 關閉。目前文件未變動。");
            EnsureNoLifecycleTransaction();
            if (doc.IsModifiable) throw new InvalidOperationException("文件仍有開啟中的 Transaction，無法關閉。");
            bool modified = doc.IsModified;
            if (modified && !discard)
                throw new InvalidOperationException("文件有未儲存變更，discard=false 不會丟棄或覆寫原檔。請先在 Revit UI 儲存，或明確指定 discard=true。");
            // 未授權自動儲存；只有未修改的文件或 discard=true 才能走此支線。
            bool closed = doc.Close(false);
            if (closed) OpenedDocumentSources.Remove(doc);
            return new JObject { ["title"] = title, ["closed"] = closed, ["discardedChanges"] = closed && modified && discard, ["saved"] = false };
        }

        private void EnsureNoLifecycleTransaction()
        {
            if (_uiApp.ActiveUIDocument?.Document.IsModifiable == true)
                throw new InvalidOperationException("目前文件有開啟中的 Transaction，請先完成後再開啟/關閉/重新載入文件。");
        }

        private Document RequireLinkHostDocument()
        {
            var doc = _uiApp.ActiveUIDocument?.Document;
            if (doc == null) throw new InvalidOperationException("目前沒有作用中的 Revit 文件，請先 open_document。");
            return doc;
        }

        private JObject LinkLifecycleInfo(Document doc, RevitLinkType type)
        {
            var notes = new JArray();
            string path = null, storedPath = null, pathType = null, status = null;
            try
            {
                var reference = type.GetExternalFileReference();
                if (reference != null)
                {
                    status = reference.GetLinkedFileStatus().ToString();
                    pathType = reference.PathType.ToString();
                    using (var modelPath = reference.GetAbsolutePath())
                        if (modelPath != null) path = ModelPathUtils.ConvertModelPathToUserVisiblePath(modelPath);
                    using (var modelPath = reference.GetPath())
                        if (modelPath != null) storedPath = ModelPathUtils.ConvertModelPathToUserVisiblePath(modelPath);
                }
            }
            catch (Exception ex) { notes.Add("外部檔案參照不可讀（可能為雲端/外部資源連結）：" + ex.Message); }
            bool loaded = RevitLinkType.IsLoaded(doc, type.Id);
            if (loaded) status = "Loaded";
            var instanceCount = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>().Count(i => i.GetTypeId() == type.Id);
            return new JObject
            {
                ["elementId"] = type.Id.GetIdValue(), ["uniqueId"] = type.UniqueId, ["name"] = type.Name,
                ["referenceType"] = type.AttachmentType.ToString(), ["instanceCount"] = instanceCount,
                ["status"] = status == null ? JValue.CreateNull() : new JValue(status), ["isLoaded"] = loaded,
                ["isNested"] = type.IsNestedLink,
                ["path"] = path == null ? JValue.CreateNull() : new JValue(path),
                ["storedPath"] = storedPath == null ? JValue.CreateNull() : new JValue(storedPath),
                ["pathType"] = pathType == null ? JValue.CreateNull() : new JValue(pathType), ["notes"] = notes
            };
        }

        private object ListLinks(JObject parameters)
        {
            var doc = RequireLinkHostDocument();
            var links = new JArray(new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType))
                .Cast<RevitLinkType>().OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Select(t => LinkLifecycleInfo(doc, t)));
            return new JObject { ["revitDoc"] = DocumentLifecycleTitle(doc), ["links"] = links, ["count"] = links.Count };
        }

        private object ReloadLinks(JObject parameters)
        {
            var doc = RequireLinkHostDocument();
            EnsureNoLifecycleTransaction();
            if (doc.IsReadOnly || doc.IsFamilyDocument)
                throw new InvalidOperationException("目前文件唯讀或為族群文件，無法重新載入 Revit 連結。");
            var types = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>().ToList();
            var namesToken = parameters["names"];
            if (namesToken != null && namesToken.Type != JTokenType.Array)
                throw new ArgumentException("names 必須是連結名稱字串陣列。");
            // names 省略或空陣列＝全部頂層連結；此時只重載「未載入／找不到」者（已載入者略過，避免無謂清除 Undo）。
            // 明確列名的連結照做（使用者要求重載）。
            bool allMode = namesToken == null || !((JArray)namesToken).Any();
            var names = allMode ? types.Where(t => !t.IsNestedLink).Select(t => t.Name).ToList()
                : ((JArray)namesToken).Select(n => n.Type == JTokenType.String ? n.Value<string>() : throw new ArgumentException("names 僅接受字串。")).ToList();
            if (names.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("names 不可含空白名稱。");
            var overridesToken = parameters["pathOverrides"];
            if (overridesToken != null && overridesToken.Type != JTokenType.Object)
                throw new ArgumentException("pathOverrides 必須是 {連結名稱: 完整 .rvt 路徑} 物件。");
            var overrides = overridesToken as JObject ?? new JObject();
            // 先整批驗證；拼錯名稱/路徑不會導致前半批已 reload、後半批才發現錯字。
            var validatedPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in overrides.Properties())
            {
                if (!names.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                    throw new ArgumentException("pathOverrides 的名稱未列於這次 reload 目標：" + property.Name);
                if (property.Value.Type != JTokenType.String) throw new ArgumentException("pathOverrides 的值必須是字串路徑。");
                validatedPaths.Add(property.Name, RequireExistingRvtPath(property.Value.Value<string>()));
            }
            // 在任何會清除 Undo 的 Load 前，先驗證所有目標，拼錯名稱不能造成前半批已執行。
            foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var matches = types.Where(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count != 1)
                    throw new ArgumentException("reload 目標不存在或有同名歧義，整批未執行：" + name);
                if (matches[0].IsNestedLink)
                    throw new ArgumentException("reload 目標是巢狀連結，請在擁有文件處理；整批未執行：" + name);
            }
            var results = new JArray();
            foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var matching = types.Where(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
                var row = new JObject { ["name"] = name, ["success"] = false };
                results.Add(row);
                if (matching.Count != 1)
                {
                    row["error"] = matching.Count == 0 ? "找不到既有連結；不會新建連結或猜路徑。" : "名稱對應多個連結型別，無法安全選取。";
                    continue;
                }
                var type = matching[0];
                row["before"] = LinkLifecycleInfo(doc, type);
                if (type.IsNestedLink) { row["error"] = "Revit API 只允許在其擁有文件重新載入頂層連結；巢狀連結已略過。"; continue; }
                if (allMode && !validatedPaths.ContainsKey(name) && RevitLinkType.IsLoaded(doc, type.Id))
                {
                    row["success"] = true; row["skipped"] = "已載入；names 省略／空陣列時只重載未載入或找不到的連結。";
                    continue;
                }
                try
                {
                    // Load/LoadFrom 會清除 Undo history，API 明定不得包 Transaction/TransactionGroup。
                    // null workset config 會保留連結先前開啟的工作集；不碰原模型、下載或建立連結。
                    LinkLoadResult loadResult;
                    if (validatedPaths.TryGetValue(name, out var overridePath))
                    {
                        using (var modelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(overridePath))
                            loadResult = type.LoadFrom(modelPath, null);
                        row["requestedPath"] = overridePath;
                    }
                    else loadResult = type.Load();
                    using (loadResult)
                    {
                        row["loadResult"] = loadResult?.LoadResult.ToString();
                        row["success"] = loadResult != null && loadResult.LoadResult == LinkLoadResultType.LinkLoaded;
                        if (!(row.Value<bool>("success"))) row["error"] = "Revit 回報連結未載入，請檢查 loadResult 與 after。";
                    }
                }
                catch (Exception ex) { row["error"] = ex.Message; row["exceptionType"] = ex.GetType().FullName; }
                row["after"] = LinkLifecycleInfo(doc, type);
            }
            return new JObject
            {
                ["revitDoc"] = DocumentLifecycleTitle(doc), ["results"] = results,
                ["succeeded"] = results.Count(r => r.Value<bool>("success") && r["skipped"] == null),
                ["skipped"] = results.Count(r => r["skipped"] != null),
                ["failed"] = results.Count(r => !r.Value<bool>("success")),
                ["warnings"] = new JArray("Revit 的 Load/LoadFrom 會清除文件 Undo history，不能由 Transaction 還原；也會丟棄連結在記憶體中尚未儲存的共享座標變更。此命令不會儲存主檔或連結原檔。")
            };
        }
    }
}
