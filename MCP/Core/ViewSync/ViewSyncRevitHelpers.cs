using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using RevitMCP.Core.Walkthrough;

namespace RevitMCP.Core.ViewSync
{
    /// <summary>目前 Revit 文件 → GLB／status 用的模型識別。</summary>
    internal static class ViewSyncModelInfo
    {
        public static RevitModelInfo FromDocument(Document doc)
        {
            return new RevitModelInfo
            {
                Title = doc.Title,
                Path = doc.PathName ?? string.Empty,
                Guid = doc.CreationGUID.ToString(),
            };
        }

        /// <summary>工作共用檔的中央檔路徑；非工作共用或讀不到回 null。</summary>
        public static string CentralPath(Document doc)
        {
            try
            {
                if (!doc.IsWorkshared) return null;
                var path = doc.GetWorksharingCentralModelPath();
                return path == null ? null : ModelPathUtils.ConvertModelPathToUserVisiblePath(path);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// 用 Extensible Storage 在視圖上做「這是 IM 臨時對位視圖」的標記（不靠名稱，使用者改名也找得到）。
    /// Schema 為 Public 讀寫，GUID 固定。
    /// </summary>
    internal static class ViewSyncMarker
    {
        private static readonly Guid SchemaGuid = new Guid("5b7d0c3e-1d6a-4f0e-9a52-6a1f3c0b7d11");
        private const string FieldName = "kind";
        public const string TempKind = "imviewsync-temp";

        private static Schema GetOrCreateSchema()
        {
            var schema = Schema.Lookup(SchemaGuid);
            if (schema != null) return schema;
            var builder = new SchemaBuilder(SchemaGuid);
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.SetVendorId("RMCP"); // SchemaBuilder 只接受英數字；專案其他 Schema 同用 RMCP
            builder.SetSchemaName("IMViewSyncMarker");
            builder.SetDocumentation("issue-manager 網頁聯動專用視圖標記");
            builder.AddSimpleField(FieldName, typeof(string));
            return builder.Finish();
        }

        /// <summary>需在交易內呼叫。</summary>
        public static void Mark(Element element, string kind)
        {
            var schema = GetOrCreateSchema();
            var entity = new Entity(schema);
            entity.Set<string>(FieldName, kind);
            element.SetEntity(entity);
        }

        public static bool IsMarked(Element element, string kind)
        {
            try
            {
                var schema = Schema.Lookup(SchemaGuid);
                if (schema == null) return false;
                var entity = element.GetEntity(schema);
                return entity != null && entity.IsValid() && entity.Get<string>(FieldName) == kind;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
