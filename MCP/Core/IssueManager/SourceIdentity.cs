#nullable disable
using System;
using System.Linq;

namespace RevitMCP.Core.IssueManager
{
    /// <summary>模型登記的來源 Revit 檔（伺服器 models 端點的 source；沒有記錄為 null）。</summary>
    public sealed class AttachModelSource
    {
        public string FileName { get; set; }
        public string Path { get; set; }
        public string CentralPath { get; set; }
    }

    public enum SourceVerdict
    {
        /// <summary>檔案路徑或中央模型路徑其中一個相同＝同一個 Revit 檔。</summary>
        Match,
        /// <summary>兩邊都有路徑資訊，但沒有任何一個相同＝不同的 Revit 檔（接上去會整批跑位）。</summary>
        Mismatch,
        /// <summary>任一邊沒有路徑資訊（舊模型、未存檔的新專案）：不能確認，也不能說不一樣。</summary>
        Unknown
    }

    /// <summary>
    /// 只傳圖面接既有模型時，比對「這個模型當初從哪個 Revit 檔來」與「目前開著的 Revit 檔」（IM D-870 補強）。
    /// 為什麼需要：外掛匯出模型的共用原點寫死 [0,0,0]，單看原點分辨不出圖面是不是來自同一個檔。
    /// 規則與網站端 <c>src/lib/attach-source.ts</c> 逐項一致（網站端 finalize 會再比一次，這裡只是提早擋）。
    /// </summary>
    public static class SourceIdentity
    {
        /// <summary>比對用的路徑：去頭尾空白、斜線統一、去結尾斜線、不分大小寫（Windows 路徑不分大小寫）；空值回 null。</summary>
        public static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var value = path.Trim().Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
            return value.Length == 0 ? null : value;
        }

        public static SourceVerdict Compare(AttachModelSource model, string currentPath, string currentCentralPath)
        {
            var known = new[] { model == null ? null : Normalize(model.Path), model == null ? null : Normalize(model.CentralPath) }.Where(p => p != null).ToList();
            var now = new[] { Normalize(currentPath), Normalize(currentCentralPath) }.Where(p => p != null).ToList();
            if (known.Count == 0 || now.Count == 0) return SourceVerdict.Unknown;
            return now.Any(p => known.Contains(p)) ? SourceVerdict.Match : SourceVerdict.Mismatch;
        }
    }
}
