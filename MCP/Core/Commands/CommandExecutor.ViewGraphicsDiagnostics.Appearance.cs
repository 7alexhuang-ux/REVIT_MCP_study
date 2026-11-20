using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Visual;
using WpfImaging = System.Windows.Media.Imaging;
using WpfMedia = System.Windows.Media;

#if REVIT2025_OR_GREATER
using IdType = System.Int64;
#else
using IdType = System.Int32;
#endif

namespace RevitMCP.Core
{
    public partial class CommandExecutor
    {
        #region 視圖圖形診斷：外觀資產／油漆／子構件／各視覺型式的顏色來源

        // 擬真類視覺型式畫的是「外觀」頁籤（彩現資產），不是「圖形」頁籤的著色顏色
        private static readonly HashSet<string> DiagAppearanceStyles =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Realistic", "RealisticWithEdges", "Rendering", "Raytrace" };

        private static readonly Dictionary<string, Dictionary<string, object>> DiagBitmapStatsCache =
            new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);

        private static bool DiagIsAppearanceStyle(string displayStyle) =>
            displayStyle != null && DiagAppearanceStyles.Contains(displayStyle);

        // ---------- 外觀資產（擬真 / 光線追蹤用） ----------

        /// <summary>
        /// 讀材料的外觀資產：所有顏色屬性、接在顏色上的貼圖（路徑、實際尺寸、偏移、旋轉、
        /// 貼圖平均色與明暗離散度）、色彩 (Tint)、以及會讓「同材料不同物件」看起來不一樣的
        /// 隨機化設定（風化 / 依物件著色）。
        /// </summary>
        private Dictionary<string, object> DescribeDiagAppearance(Document doc, Material mat)
        {
            var info = new Dictionary<string, object>();
            try
            {
                if (mat.AppearanceAssetId == null || mat.AppearanceAssetId == ElementId.InvalidElementId)
                {
                    info["HasAppearanceAsset"] = false;
                    return info;
                }

                var assetElem = doc.GetElement(mat.AppearanceAssetId) as AppearanceAssetElement;
                Asset asset = assetElem?.GetRenderingAsset();
                if (asset == null)
                {
                    info["HasAppearanceAsset"] = false;
                    return info;
                }

                info["HasAppearanceAsset"] = true;
                info["AppearanceAssetId"] = mat.AppearanceAssetId.GetIdValue();
                info["AppearanceAssetName"] = assetElem.Name;
                info["Schema"] = asset.Name;

                // 共用同一個外觀資產的其他材料：改外觀會一起變
                try
                {
                    info["SharedByMaterials"] = new FilteredElementCollector(doc)
                        .OfClass(typeof(Material)).Cast<Material>()
                        .Where(m => m.Id != mat.Id && m.AppearanceAssetId == mat.AppearanceAssetId)
                        .Select(m => m.Name).Take(20).ToList();
                }
                catch { }

                var colors = new List<Dictionary<string, object>>();
                var flags = new Dictionary<string, object>();

                for (int i = 0; i < asset.Size; i++)
                {
                    AssetProperty prop;
                    try { prop = asset[i]; } catch { continue; }
                    if (prop == null) continue;
                    string name = prop.Name ?? "";
                    string lower = name.ToLowerInvariant();

                    if (prop is AssetPropertyDoubleArray4d colorProp)
                    {
                        var c = new Dictionary<string, object> { ["Property"] = name };
                        try
                        {
                            var rc = colorProp.GetValueAsColor();
                            c["Color"] = DiagRgb(rc);
                        }
                        catch { c["Color"] = null; }

                        var tex = DescribeDiagTexture(doc, prop);
                        if (tex != null) c["Texture"] = tex;
                        colors.Add(c);
                        continue;
                    }

                    // 會造成「每個物件不一樣」或整體偏色的開關，原值照列
                    if (lower.Contains("tint") || lower.Contains("brightmode") || lower.Contains("by_object")
                        || lower.Contains("weather") || lower.Contains("finish") || lower.Contains("bm_"))
                    {
                        object v = DiagPrimitiveValue(prop);
                        if (v != null) flags[name] = v;
                    }
                }

                info["Colors"] = colors;
                info["Flags"] = flags;
            }
            catch (Exception ex)
            {
                info["Error"] = ex.Message;
            }
            return info;
        }

        private Dictionary<string, object> DescribeDiagTexture(Document doc, AssetProperty prop)
        {
            try
            {
                if (prop.NumberOfConnectedProperties == 0) return null;
                Asset bmp = prop.GetSingleConnectedAsset();
                if (bmp == null) return null;

                var tex = new Dictionary<string, object> { ["Schema"] = bmp.Name };
                for (int i = 0; i < bmp.Size; i++)
                {
                    AssetProperty p;
                    try { p = bmp[i]; } catch { continue; }
                    if (p == null) continue;
                    string n = p.Name ?? "";

                    if (n == UnifiedBitmap.UnifiedbitmapBitmap && p is AssetPropertyString s)
                    {
                        tex["BitmapPath"] = s.Value;
                        string resolved = DiagResolveBitmapPath(doc, s.Value);
                        tex["ResolvedPath"] = resolved;
                        if (resolved != null) tex["BitmapStats"] = DiagBitmapStats(resolved);
                        continue;
                    }

                    if (n.StartsWith("texture_", StringComparison.OrdinalIgnoreCase)
                        || n.StartsWith("unifiedbitmap_", StringComparison.OrdinalIgnoreCase))
                    {
                        object v = DiagPrimitiveValue(p);
                        if (v != null) tex[n] = v;
                    }
                }
                return tex;
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { ["Error"] = ex.Message };
            }
        }

        private object DiagPrimitiveValue(AssetProperty p)
        {
            try
            {
                switch (p)
                {
                    case AssetPropertyDistance d:
                        string unit = null;
                        try { unit = d.GetUnitTypeId()?.TypeId; } catch { }
                        return unit == null ? (object)Math.Round(d.Value, 4) : $"{Math.Round(d.Value, 4)} ({unit})";
                    case AssetPropertyDouble d2: return Math.Round(d2.Value, 4);
                    case AssetPropertyFloat f: return Math.Round(f.Value, 4);
                    case AssetPropertyInteger i: return i.Value;
                    case AssetPropertyBoolean b: return b.Value;
                    case AssetPropertyString s: return s.Value;
                    case AssetPropertyEnum e: return e.Value;
                    case AssetPropertyDoubleArray4d c:
                        try { return DiagRgb(c.GetValueAsColor()); } catch { return null; }
                    default: return null;
                }
            }
            catch { return null; }
        }

        /// <summary>
        /// 貼圖路徑可能是絕對路徑、相對於 Autodesk 材料庫，或只有檔名；可能以 '|' 串多個候選。
        /// </summary>
        private string DiagResolveBitmapPath(Document doc, string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            var bases = new List<string>();
            foreach (var env in new[] { "CommonProgramFiles(x86)", "CommonProgramFiles" })
            {
                string root = Environment.GetEnvironmentVariable(env);
                if (!string.IsNullOrEmpty(root))
                    bases.Add(Path.Combine(root, "Autodesk Shared", "Materials", "Textures"));
            }
            try
            {
                if (!string.IsNullOrEmpty(doc.PathName))
                    bases.Add(Path.GetDirectoryName(doc.PathName));
            }
            catch { }

            foreach (var candidateRaw in raw.Split('|'))
            {
                string candidate = candidateRaw.Trim();
                if (candidate.Length == 0) continue;
                try
                {
                    if (Path.IsPathRooted(candidate) && File.Exists(candidate)) return candidate;
                    foreach (var b in bases)
                    {
                        string p = Path.Combine(b, candidate);
                        if (File.Exists(p)) return p;
                    }
                    string fileName = Path.GetFileName(candidate);
                    foreach (var b in bases.Where(Directory.Exists))
                    {
                        var hit = Directory.EnumerateFiles(b, fileName, SearchOption.AllDirectories).FirstOrDefault();
                        if (hit != null) return hit;
                    }
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// 貼圖本身的平均色、明暗離散度與暖色偏移（R-B）。
        /// 平均色明顯偏暖 = 擬真下整體偏黃；離散度高 = 不同物件取到貼圖不同區塊時落差大。
        /// </summary>
        private Dictionary<string, object> DiagBitmapStats(string path)
        {
            lock (DiagBitmapStatsCache)
            {
                if (DiagBitmapStatsCache.TryGetValue(path, out var cached)) return cached;
            }

            var stats = new Dictionary<string, object>();
            try
            {
                var decoder = WpfImaging.BitmapDecoder.Create(new Uri(path),
                    WpfImaging.BitmapCreateOptions.IgnoreColorProfile, WpfImaging.BitmapCacheOption.OnLoad);
                WpfImaging.BitmapSource src = decoder.Frames[0];
                stats["Pixels"] = $"{src.PixelWidth}x{src.PixelHeight}";

                double scale = Math.Min(1.0, 256.0 / Math.Max(src.PixelWidth, src.PixelHeight));
                WpfImaging.BitmapSource scaled = scale < 1.0
                    ? new WpfImaging.TransformedBitmap(src, new WpfMedia.ScaleTransform(scale, scale))
                    : src;
                var bgra = new WpfImaging.FormatConvertedBitmap(scaled, WpfMedia.PixelFormats.Bgra32, null, 0);

                int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
                var px = new byte[stride * h];
                bgra.CopyPixels(px, stride, 0);

                double sr = 0, sg = 0, sb = 0, sl = 0, sl2 = 0;
                int n = w * h;
                for (int i = 0; i < px.Length; i += 4)
                {
                    double b = px[i], g = px[i + 1], r = px[i + 2];
                    double l = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                    sr += r; sg += g; sb += b; sl += l; sl2 += l * l;
                }
                double mr = sr / n, mg = sg / n, mb = sb / n, ml = sl / n;
                stats["AverageColor"] = $"RGB({(int)mr},{(int)mg},{(int)mb})";
                stats["Warmth_RminusB"] = (int)(mr - mb);
                stats["LuminanceMean"] = (int)ml;
                stats["LuminanceStdDev"] = (int)Math.Sqrt(Math.Max(0, sl2 / n - ml * ml));
            }
            catch (Exception ex)
            {
                stats["Error"] = ex.Message;
            }

            lock (DiagBitmapStatsCache) { DiagBitmapStatsCache[path] = stats; }
            return stats;
        }

        // ---------- 油漆（Paint 工具塗在面上的材料） ----------

        private List<Dictionary<string, object>> DescribeDiagPaintedFaces(Document doc, Element element)
        {
            var list = new List<Dictionary<string, object>>();
            try
            {
                var opt = new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine };
                GeometryElement ge = element.get_Geometry(opt);
                if (ge == null) return list;

                int faceIndex = 0;
                foreach (var solid in DiagSolids(ge))
                {
                    foreach (Face face in solid.Faces)
                    {
                        faceIndex++;
                        bool painted = false;
                        try { painted = doc.IsPainted(element.Id, face); } catch { }
                        if (!painted) continue;

                        var entry = new Dictionary<string, object>
                        {
                            ["ElementId"] = element.Id.GetIdValue(),
                            ["FaceIndex"] = faceIndex
                        };
                        try
                        {
                            var pmId = doc.GetPaintedMaterial(element.Id, face);
                            var pm = doc.GetElement(pmId) as Material;
                            entry["PaintedMaterialId"] = pmId.GetIdValue();
                            entry["PaintedMaterialName"] = pm?.Name;
                        }
                        catch { }
                        try
                        {
                            var n = face.ComputeNormal(new UV(0.5, 0.5));
                            entry["Normal"] = $"({n.X:F2},{n.Y:F2},{n.Z:F2})";
                        }
                        catch { }
                        list.Add(entry);
                    }
                }
            }
            catch { }
            return list;
        }

        private IEnumerable<Solid> DiagSolids(GeometryElement ge)
        {
            foreach (GeometryObject go in ge)
            {
                if (go is Solid s && s.Faces.Size > 0) yield return s;
                else if (go is GeometryInstance gi)
                {
                    GeometryElement inner = null;
                    try { inner = gi.GetInstanceGeometry(); } catch { }
                    if (inner == null) continue;
                    foreach (var x in DiagSolids(inner)) yield return x;
                }
            }
        }

        // ---------- 子構件（樓梯的梯段／平台／支撐各有自己的材料與油漆） ----------

        private List<Element> DiagSubComponents(Document doc, Element element)
        {
            var subs = new List<Element>();
            if (element is Stairs stairs)
            {
                try { subs.AddRange(stairs.GetStairsRuns().Select(doc.GetElement)); } catch { }
                try { subs.AddRange(stairs.GetStairsLandings().Select(doc.GetElement)); } catch { }
                try { subs.AddRange(stairs.GetStairsSupports().Select(doc.GetElement)); } catch { }
            }
            return subs.Where(e => e != null).ToList();
        }

        private List<Dictionary<string, object>> DescribeDiagSubComponents(Document doc, Element element)
        {
            var list = new List<Dictionary<string, object>>();
            foreach (var sub in DiagSubComponents(doc, element))
            {
                list.Add(new Dictionary<string, object>
                {
                    ["ElementId"] = sub.Id.GetIdValue(),
                    ["Kind"] = sub.GetType().Name,
                    ["Name"] = sub.Name,
                    ["Materials"] = DescribeDiagMaterials(doc, sub),
                    ["PaintedFaces"] = DescribeDiagPaintedFaces(doc, sub)
                });
            }
            return list;
        }

        // ---------- 每種視覺型式下，表面顏色由誰決定 ----------

        /// <summary>
        /// 同一個材料在不同視覺型式下取色來源不同；把每種模式的來源與（可得時）實際顏色列出。
        /// </summary>
        private Dictionary<string, object> BuildDiagModeResolution(List<Dictionary<string, object>> materials)
        {
            var perMaterial = new List<Dictionary<string, object>>();
            foreach (var m in materials ?? new List<Dictionary<string, object>>())
            {
                bool useRender = m.TryGetValue("UseRenderAppearanceForShading", out var ur) && ur is bool b && b;
                string shading = m.TryGetValue("ShadingColor", out var sc) ? sc as string : null;
                string appearanceColor = DiagPrimaryAppearanceColor(m);

                string shadedSource = useRender
                    ? $"外觀資產（材料勾了『使用彩現外觀』）≈ {appearanceColor ?? "未知"}"
                    : $"圖形頁籤著色顏色 {shading}";

                perMaterial.Add(new Dictionary<string, object>
                {
                    ["MaterialName"] = m.TryGetValue("MaterialName", out var mn) ? mn : null,
                    ["Wireframe/HLR"] = "不填色；只畫表面填充樣式（若有）與線",
                    ["Shading / ShadingWithEdges"] = shadedSource + "，再乘上面向光源的明暗",
                    ["FlatColors (一致的顏色)"] = (useRender ? "外觀資產 ≈ " + (appearanceColor ?? "未知") : "著色顏色 " + shading) + "，不受光源影響",
                    ["Realistic / RealisticWithEdges"] = "外觀資產（顏色＋貼圖＋色彩 Tint＋風化），完全不看圖形頁籤著色顏色"
                                                         + (appearanceColor != null ? $"；主要顏色 {appearanceColor}" : ""),
                    ["Raytrace"] = "同 Realistic，另加日光／人工光與全域照明"
                });
            }
            return new Dictionary<string, object> { ["PerMaterial"] = perMaterial };
        }

        /// <summary>
        /// 主要外觀顏色：有貼圖時取貼圖平均色，否則取第一個顏色屬性。
        /// </summary>
        private string DiagPrimaryAppearanceColor(Dictionary<string, object> mat)
        {
            if (!(mat.TryGetValue("Appearance", out var a) && a is Dictionary<string, object> app)) return null;
            if (!(app.TryGetValue("Colors", out var c) && c is List<Dictionary<string, object>> colors) || colors.Count == 0) return null;

            foreach (var col in colors)
            {
                if (col.TryGetValue("Texture", out var t) && t is Dictionary<string, object> tex
                    && tex.TryGetValue("BitmapStats", out var bs) && bs is Dictionary<string, object> stats
                    && stats.TryGetValue("AverageColor", out var avg) && avg != null)
                    return $"{avg}（貼圖平均）";
            }
            return colors[0].TryGetValue("Color", out var first) ? first as string : null;
        }

        /// <summary>
        /// 視覺型式為擬真類時，把外觀資產上「會讓畫面偏色或物件間落差大」的證據轉成成因。
        /// </summary>
        private List<Dictionary<string, object>> BuildDiagAppearanceCauses(string displayStyle, List<Dictionary<string, object>> materials)
        {
            var causes = new List<Dictionary<string, object>>();
            if (!DiagIsAppearanceStyle(displayStyle) || materials == null) return causes;

            foreach (var m in materials)
            {
                if (!(m.TryGetValue("Appearance", out var a) && a is Dictionary<string, object> app)) continue;
                string matName = m.TryGetValue("MaterialName", out var mn) ? mn as string : "?";
                var details = new List<string>();

                if (app.TryGetValue("Colors", out var c) && c is List<Dictionary<string, object>> colors)
                {
                    foreach (var col in colors)
                    {
                        if (!(col.TryGetValue("Texture", out var t) && t is Dictionary<string, object> tex)) continue;
                        string path = tex.TryGetValue("BitmapPath", out var bp) ? bp as string : null;
                        string part = $"{col["Property"]} 接了貼圖 '{Path.GetFileName(path ?? "")}'";

                        if (tex.TryGetValue("BitmapStats", out var bs) && bs is Dictionary<string, object> stats
                            && stats.ContainsKey("AverageColor"))
                        {
                            part += $"，貼圖平均色 {stats["AverageColor"]}、暖色偏移 R-B={stats["Warmth_RminusB"]}、明暗離散度 σ={stats["LuminanceStdDev"]}";
                            if (stats["Warmth_RminusB"] is int warm && warm >= 12) part += "（偏暖／偏黃）";
                            if (stats["LuminanceStdDev"] is int sd && sd >= 25) part += "（斑駁，不同物件取到不同區塊時落差會很明顯）";
                        }
                        else if (tex.ContainsKey("BitmapPath"))
                        {
                            part += "（找不到貼圖檔，無法計算平均色）";
                        }

                        var scaleKeys = tex.Keys.Where(k => k.IndexOf("RealWorldScale", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                        if (scaleKeys.Count > 0)
                            part += "，貼圖實際尺寸 " + string.Join(" × ", scaleKeys.Select(k => tex[k]));
                        details.Add(part);
                    }
                }

                if (app.TryGetValue("Flags", out var f) && f is Dictionary<string, object> flags && flags.Count > 0)
                {
                    foreach (var kv in flags)
                    {
                        string k = kv.Key.ToLowerInvariant();
                        if (k.Contains("brightmode") || k.Contains("weather"))
                            details.Add($"風化設定 {kv.Key}={kv.Value}（非 0 時 Revit 會依物件隨機改變亮度，同材料各物件深淺不同）");
                        else if (k.Contains("by_object"))
                            details.Add($"{kv.Key}={kv.Value}（依物件變化顏色的開關）");
                        else if (k.Contains("tint"))
                            details.Add($"{kv.Key}={kv.Value}");
                    }
                }

                if (details.Count == 0) continue;
                causes.Add(MakeDiagCause(
                    1,
                    $"外觀資產 '{app["AppearanceAssetName"]}'（材料 '{matName}'）",
                    "擬真／光線追蹤模式下的全專案",
                    $"視覺型式 {displayStyle} 畫的是外觀資產，不是圖形頁籤著色顏色：{string.Join("；", details)}。",
                    "材料瀏覽器 > 外觀頁籤：換乾淨貼圖、調大實際尺寸、關閉色彩 (Tint) 或風化；只要看形體就改用著色／一致的顏色。"));
            }
            return causes;
        }

        // ---------- 多元素比對（同材料為何長得不一樣） ----------

        private Dictionary<string, object> BuildDiagComparison(Document doc, View view, List<Element> elements)
        {
            string displayStyle = null;
            try { displayStyle = view.DisplayStyle.ToString(); } catch { }

            var rows = new List<Dictionary<string, object>>();
            var differences = new List<string>();

            foreach (var el in elements)
            {
                var mats = DescribeDiagMaterials(doc, el);
                var subs = DescribeDiagSubComponents(doc, el);
                var painted = DescribeDiagPaintedFaces(doc, el);
                foreach (var s in subs)
                    if (s["PaintedFaces"] is List<Dictionary<string, object>> sp) painted.AddRange(sp);

                var allMats = new List<string>();
                allMats.AddRange(mats.Select(m => m["MaterialName"] as string));
                foreach (var s in subs)
                    if (s["Materials"] is List<Dictionary<string, object>> sm)
                        allMats.AddRange(sm.Select(m => m["MaterialName"] as string));

                var elemOgs = DescribeDiagOverride(doc, SafeGetElementOverrides(view, el.Id));
                rows.Add(new Dictionary<string, object>
                {
                    ["ElementId"] = el.Id.GetIdValue(),
                    ["Name"] = el.Name,
                    ["Category"] = el.Category?.Name,
                    ["Materials"] = allMats.Where(x => x != null).Distinct().ToList(),
                    ["PaintedFaceCount"] = painted.Count,
                    ["PaintedMaterials"] = painted.Select(p => p.TryGetValue("PaintedMaterialName", out var n) ? n : null)
                                                  .Where(n => n != null).Distinct().ToList(),
                    ["HasElementOverride"] = elemOgs != null && elemOgs["HasAnyOverride"] is bool ho && ho
                });
            }

            var materialSets = rows.Select(r => string.Join(" + ", (List<string>)r["Materials"])).Distinct().ToList();
            if (materialSets.Count > 1)
                differences.Add("元素實際使用的材料組合不同（含樓梯子構件）：" + string.Join(" / ", materialSets));

            var paintedRows = rows.Where(r => (int)r["PaintedFaceCount"] > 0).ToList();
            if (paintedRows.Count > 0)
                differences.Add("有面被『油漆』塗了其他材料：" + string.Join("；", paintedRows.Select(r =>
                    $"{r["ElementId"]} 共 {r["PaintedFaceCount"]} 面 → {string.Join(", ", (List<object>)r["PaintedMaterials"])}")));

            var overridden = rows.Where(r => r["HasElementOverride"] is bool h && h).ToList();
            if (overridden.Count > 0)
                differences.Add("有元素個別圖形覆寫：" + string.Join(", ", overridden.Select(r => r["ElementId"])));

            string conclusion;
            if (differences.Count > 0)
                conclusion = "找到實際差異：" + string.Join("。", differences) + "。";
            else if (DiagIsAppearanceStyle(displayStyle))
                conclusion = $"材料、油漆、覆寫完全相同。視覺型式 {displayStyle} 下的差異只可能來自外觀資產本身"
                           + "（貼圖在各物件的取樣位置／比例不同、風化隨機亮度、色彩 Tint）——詳見各材料的 Appearance 與 Verdict。";
            else
                conclusion = "材料、油漆、覆寫完全相同，模型層面沒有差異。";

            return new Dictionary<string, object>
            {
                ["DisplayStyle"] = displayStyle,
                ["Elements"] = rows,
                ["Differences"] = differences,
                ["Conclusion"] = conclusion
            };
        }

        private static string DiagRgb(Color c) =>
            (c != null && c.IsValid) ? $"RGB({c.Red},{c.Green},{c.Blue})" : null;

        #endregion
    }
}
