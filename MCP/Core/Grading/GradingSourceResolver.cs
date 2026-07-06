using System.Collections.Generic;

namespace RevitMCP.Core.Grading
{
    /// <summary>整地來源解析所需的方案摘要（純資料，無 Revit 相依）。</summary>
    public readonly struct SchemeSourceSummary
    {
        public SchemeSourceSummary(long designId, long originalId, string schemeName)
        {
            DesignId = designId;
            OriginalId = originalId;
            SchemeName = schemeName;
        }

        /// <summary>方案設計地形（成果）ID。</summary>
        public long DesignId { get; }

        /// <summary>方案登記的原地形 ID（多方案共用同一份）。</summary>
        public long OriginalId { get; }

        public string SchemeName { get; }
    }

    /// <summary>整地來源解析結果。</summary>
    public readonly struct GradingSourceResolution
    {
        public GradingSourceResolution(long sourceId, bool redirected, string redirectedFromScheme)
        {
            SourceId = sourceId;
            Redirected = redirected;
            RedirectedFromScheme = redirectedFromScheme;
        }

        /// <summary>實際應作為整地來源的地形 ID。</summary>
        public long SourceId { get; }

        /// <summary>是否從「方案成果地形」自動改用了原地形。</summary>
        public bool Redirected { get; }

        /// <summary>被改用來源的方案名稱（Redirected 為 true 時有值）。</summary>
        public string RedirectedFromScheme { get; }
    }

    /// <summary>
    /// 整地來源解析（純函式）：多方案須共用同一份原地形。若使用者選到的地形本身是
    /// 某方案的「設計成果」（因原地形被拆除隱藏而誤選），自動改用該方案登記的原地形，
    /// 避免方案二疊在方案一挖過的地形上。選到原地形或未知地形則原樣回傳。
    /// </summary>
    public static class GradingSourceResolver
    {
        public static GradingSourceResolution Resolve(
            IEnumerable<SchemeSourceSummary> schemes, long selectedToposolidId)
        {
            if (schemes != null)
            {
                foreach (var scheme in schemes)
                {
                    if (scheme.DesignId == selectedToposolidId)
                    {
                        return new GradingSourceResolution(scheme.OriginalId, true, scheme.SchemeName);
                    }
                }
            }

            return new GradingSourceResolution(selectedToposolidId, false, null);
        }
    }
}
