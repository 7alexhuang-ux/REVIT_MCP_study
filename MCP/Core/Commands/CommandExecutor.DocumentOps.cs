using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
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
        private object SaveDocument(JObject parameters)
        {
            string documentTitle = parameters["documentTitle"]?.Value<string>();
            var documents = _uiApp.Application.Documents
                .Cast<Document>()
                .Where(doc => !doc.IsLinked)
                .ToList();

            Document document;
            if (string.IsNullOrWhiteSpace(documentTitle))
            {
                document = _uiApp.ActiveUIDocument?.Document;
                if (document == null)
                    throw new Exception("目前沒有作用中的 Revit 文件可儲存。");
            }
            else
            {
                document = documents.FirstOrDefault(doc => doc.Title == documentTitle);
                if (document == null)
                {
                    string openTitles = documents.Count == 0
                        ? "（無）"
                        : string.Join("、", documents.Select(doc => doc.Title));
                    throw new Exception($"找不到標題為「{documentTitle}」的已開啟文件。已開啟文件：{openTitles}");
                }
            }

            if (document.IsReadOnly)
                throw new Exception($"文件「{document.Title}」為唯讀，無法儲存。");

            document.Save();

            return new
            {
                Success = true,
                Title = document.Title,
                PathName = document.PathName,
                Message = "文件已儲存。"
            };
        }
    }
}
