using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace RevitMCP.Core.Walkthrough
{
    /// <summary>某類別的三角面數。BuiltInCategory＝Revit BuiltInCategory 列舉的整數值（與介面語言無關），0＝未知（例如自訂類別）。</summary>
    public sealed class CategoryTriangles
    {
        public string Name { get; set; } = string.Empty;
        public int BuiltInCategory { get; set; }
        public int Triangles { get; set; }
    }

    /// <summary>
    /// splitLinks 分檔的純邏輯（不依賴 Revit API，可單元測試）：檔名 slug、defaultVisible 判定、前幾大類別。
    /// 規格：issue-manager docs/spec/viewer3d.md §22.2。
    /// </summary>
    public static class LinkPartsManifest
    {
        /// <summary>結構／機電類別（Revit 英文類別名，不分大小寫）。分檔中這些類別的面數占 ≥ 50% → 預設不顯示。</summary>
        public static readonly HashSet<string> StructuralMepCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Structural Framing", "Structural Columns", "Structural Foundations", "Structural Foundation", "Structural Rebar",
            "Pipes", "Pipe Fittings", "Pipe Accessories",
            "Ducts", "Duct Fittings", "Duct Accessories", "Air Terminals",
            "Conduits", "Conduit Fittings",
            "Cable Trays", "Cable Tray Fittings",
            "Mechanical Equipment", "Electrical Equipment",
            "Lighting Fixtures", "Plumbing Fixtures", "Sprinklers", "Electrical Fixtures",
            "Flex Pipes", "Flex Ducts", "Pipe Insulations", "Duct Insulations", "Duct Linings",
            "Fire Alarm Devices", "Communication Devices", "Data Devices", "Security Devices", "Nurse Call Devices", "Telephone Devices"
        };

        public const double HiddenRatio = 0.5;

        public static bool IsStructuralOrMep(string category) =>
            !string.IsNullOrEmpty(category) && StructuralMepCategories.Contains(category.Trim());

        /// <summary>
        /// 一個類別是否算結構／機電。有 BuiltInCategory 整數值（非 0）時只看它是否在 <paramref name="hiddenBuiltIns"/>
        /// （與 Revit 介面語言無關）；拿不到（0，例如自訂類別）才退回英文類別名比對。
        /// </summary>
        public static bool IsStructuralOrMep(CategoryTriangles category, ISet<int> hiddenBuiltIns)
        {
            if (category.BuiltInCategory != 0) return hiddenBuiltIns != null && hiddenBuiltIns.Contains(category.BuiltInCategory);
            return IsStructuralOrMep(category.Name);
        }

        /// <summary>結構／機電類別面數占比 ≥ 50% → false（預設不顯示）；沒有任何面數（空分檔）→ true。</summary>
        public static bool ComputeDefaultVisible(IEnumerable<CategoryTriangles> categories, ISet<int> hiddenBuiltIns)
        {
            long total = 0, hidden = 0;
            foreach (var c in categories)
            {
                if (c.Triangles <= 0) continue;
                total += c.Triangles;
                if (IsStructuralOrMep(c, hiddenBuiltIns)) hidden += c.Triangles;
            }

            if (total == 0) return true;
            return hidden * 1.0 / total < HiddenRatio;
        }

        /// <summary>面數前 N 大的類別（同一 BuiltInCategory 或同名者合併），面數相同時依類別名排序（結果穩定）。</summary>
        public static List<CategoryTriangles> TopCategories(IEnumerable<CategoryTriangles> categories, int count)
        {
            return categories
                .Where(c => c.Triangles > 0)
                .GroupBy(c => c.BuiltInCategory != 0 ? "b" + c.BuiltInCategory.ToString(CultureInfo.InvariantCulture) : "n" + c.Name, StringComparer.Ordinal)
                .Select(g => new CategoryTriangles { Name = g.First().Name, BuiltInCategory = g.First().BuiltInCategory, Triangles = g.Sum(c => c.Triangles) })
                .OrderByDescending(c => c.Triangles)
                .ThenBy(c => c.Name, StringComparer.Ordinal)
                .Take(count)
                .ToList();
        }

        /// <summary>
        /// 連結文件標題 → ASCII 檔名鍵（小寫字母、數字、底線）。全是非 ASCII（例如中文標題）時用 "link_" + 標題的穩定雜湊；
        /// 與 <paramref name="used"/> 重複時加 _2、_3…；結果會加入 <paramref name="used"/>。
        /// </summary>
        public static string Slug(string title, ISet<string> used)
        {
            var text = (title ?? string.Empty).Trim();
            if (text.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)) text = text.Substring(0, text.Length - 4);
            var sb = new StringBuilder();
            var lastUnderscore = true;
            foreach (var ch in text)
            {
                if ((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9'))
                {
                    sb.Append(char.ToLowerInvariant(ch));
                    lastUnderscore = false;
                }
                else if (!lastUnderscore)
                {
                    sb.Append('_');
                    lastUnderscore = true;
                }
            }

            var slug = sb.ToString().Trim('_');
            if (slug.Length == 0)
            {
                slug = "link_" + StableHash(text).ToString("x8", CultureInfo.InvariantCulture);
            }
            else if (slug.Length > 60)
            {
                slug = slug.Substring(0, 60).TrimEnd('_');
            }

            var candidate = slug;
            for (var n = 2; used.Contains(candidate); n++)
            {
                candidate = slug + "_" + n.ToString(CultureInfo.InvariantCulture);
            }

            used.Add(candidate);
            return candidate;
        }

        public static string PartFileName(string key) => "part_" + key + ".glb";

        /// <summary>FNV-1a 32 位元，跨執行穩定（不用 string.GetHashCode）。</summary>
        private static uint StableHash(string text)
        {
            unchecked
            {
                var hash = 2166136261u;
                foreach (var ch in text)
                {
                    hash = (hash ^ ch) * 16777619u;
                }

                return hash;
            }
        }
    }
}
