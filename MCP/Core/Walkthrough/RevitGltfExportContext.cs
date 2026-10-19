using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Visual;

namespace RevitMCP.Core.Walkthrough
{
    /// <summary>
    /// CustomExporter context that streams Revit polymeshes into <see cref="GltfBuilder"/>.
    /// Output space: glTF (Y up, meters) with the walkthrough origin offset removed:
    /// gltf = (x_m - ox, z_m - oz, -(y_m - oy)).
    /// Only material color and transparency are kept (no textures).
    /// </summary>
    internal sealed class RevitGltfExportContext : IExportContext
    {
        private readonly Document _hostDocument;
        private readonly GltfBuilder _builder;
        private readonly Vec3 _originOffsetMeters;
        private readonly Stack<Transform> _transforms = new Stack<Transform>();
        private readonly Stack<Document> _documents = new Stack<Document>();
        private int _currentMaterial = -1;
        private bool _cancelled;
        private int _elementDepth;
        private readonly Stack<string> _linkNames = new Stack<string>();
        private readonly Stack<int> _linkStartTriangles = new Stack<int>();
        private readonly Stack<KeyValuePair<int, bool>> _savedElementState = new Stack<KeyValuePair<int, bool>>();
        private bool _holderActive;
        private int _elementStartTriangles;
        private string _elementKey;
        private string _elementCategory;
        private double[] _currentUvScale; // feet per texture repeat (x, y); null = untextured
        private readonly Dictionary<string, MaterialInfo> _materialCache = new Dictionary<string, MaterialInfo>();
        private readonly Dictionary<string, int> _imageIndexByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, string> _textureIndex;
        private long _imageBytes;
        private int _contourMaterial = -1;

        public const string HostName = "(host)";

        /// <summary>Top-level elements exported per "link|category" (link = document title, host = "(host)"). Diagnostics only.</summary>
        public Dictionary<string, int> ElementCounts { get; } = new Dictionary<string, int>();

        /// <summary>Triangles exported inside each link instance, keyed by linked document title.</summary>
        public Dictionary<string, int> LinkTriangles { get; } = new Dictionary<string, int>();

        /// <summary>How many times the exporter entered a link instance (0 = links were never visited).</summary>
        public int LinkBeginCount { get; private set; }

        public RevitGltfExportContext(Document document, GltfBuilder builder, Vec3 originOffsetMeters)
        {
            _hostDocument = document ?? throw new ArgumentNullException(nameof(document));
            _builder = builder ?? throw new ArgumentNullException(nameof(builder));
            _originOffsetMeters = originOffsetMeters;
        }

        public int SkippedPolymeshes { get; private set; }

        /// <summary>When set, every link begin/end and exception is appended here immediately (survives client timeouts).</summary>
        public string DiagPath { get; set; }

        /// <summary>Triangles per "document|category" (top-level elements), diagnostics + budget planning.</summary>
        public Dictionary<string, int> TrianglesByKey { get; } = new Dictionary<string, int>();

        /// <summary>Categories (localized names as returned by Element.Category.Name) whose elements are skipped entirely.</summary>
        public HashSet<string> ExcludedCategories { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Contour interval in meters for Toposolid/Topography top faces; 0 disables contour generation.</summary>
        public double ContourIntervalMeters { get; set; } = 0;

        public int ContourSegments { get; private set; }

        /// <summary>Elements to skip before tessellation, as "source:elementId" (source = "(host)" or the link document title).</summary>
        public HashSet<string> ExcludedElementKeys { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Elements whose triangle count exceeds this are handled by <see cref="OverCapMode"/> after tessellation (0 = no cap).</summary>
        public int MaxElementTriangles { get; set; }

        /// <summary>What happens to an element over <see cref="MaxElementTriangles"/>: "simplify" (quadric decimation), "box" (bounding-box placeholder) or "drop".</summary>
        public string OverCapMode { get; set; } = "simplify";

        /// <summary>For "simplify": target = this ratio of the element's triangles when &gt; 0, otherwise the cap itself.</summary>
        public double SimplifyRatio { get; set; }

        /// <summary>Revit tessellation level of detail set on the view node (0 coarse .. 15 fine); negative keeps Revit's default.</summary>
        public int LevelOfDetail { get; set; } = -1;

        /// <summary>Time spent simplifying over-cap elements.</summary>
        public System.Diagnostics.Stopwatch SimplifyTime { get; } = new System.Diagnostics.Stopwatch();

        /// <summary>Per-element triangle counts (before any cap handling): label, category, family, triangles, kept triangles.</summary>
        public List<ElementTriangleStat> ElementTriangleStats { get; } = new List<ElementTriangleStat>();

        /// <summary>Elements dropped by <see cref="MaxElementTriangles"/> or <see cref="ExcludedElementKeys"/>: "source:id | category | name | triangles".</summary>
        public List<string> DroppedElements { get; } = new List<string>();

        /// <summary>Appearance asset dump (once per material name) for diagnosing missing colours/textures.</summary>
        public bool DumpAppearance { get; set; }
        private readonly HashSet<string> _appearanceDumped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Texture files that could not be found / loaded (name -> reason). Diagnostics only.</summary>
        public Dictionary<string, string> TextureProblems { get; } = new Dictionary<string, string>();

        /// <summary>Materials whose transparency was non-zero: name -> "node=..;material=..;alpha=..". Diagnostics only.</summary>
        public Dictionary<string, string> TransparentMaterials { get; } = new Dictionary<string, string>();

        public int TexturedMaterials { get; private set; }

        /// <summary>Hard cap for all embedded texture bytes (default 80 MB).</summary>
        public long MaxImageBytes { get; set; } = 80L * 1024 * 1024;

        /// <summary>Number of exceptions swallowed by the context itself.</summary>
        public int ExceptionCount { get; private set; }

        /// <summary>Total elements seen per document (host or link title), including nested ones. Diagnostics only.</summary>
        public Dictionary<string, int> ElementsSeenByDoc { get; } = new Dictionary<string, int>();

        public void Diag(string line)
        {
            if (string.IsNullOrEmpty(DiagPath))
            {
                return;
            }

            try
            {
                System.IO.File.AppendAllText(DiagPath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + line + Environment.NewLine);
            }
            catch (System.IO.IOException)
            {
            }
        }

        /// <summary>
        /// When true each top-level element becomes its own glTF node carrying elementId, uniqueId, category and
        /// family in node.extras. Default false keeps the walkthrough behaviour (geometry merged per material).
        /// </summary>
        public bool TrackElements { get; set; }

        /// <summary>Stops the export once the triangle budget is exceeded (protects Revit from runaway memory).</summary>
        public int TriangleBudget { get; set; } = 2_000_000;

        public bool BudgetExceeded => _cancelled;

        public bool Start()
        {
            _transforms.Clear();
            _transforms.Push(Transform.Identity);
            _documents.Clear();
            _documents.Push(_hostDocument);
            _linkNames.Clear();
            _linkNames.Push(HostName);
            _linkStartTriangles.Clear();
            ElementCounts.Clear();
            LinkTriangles.Clear();
            ElementsSeenByDoc.Clear();
            TrianglesByKey.Clear();
            _savedElementState.Clear();
            _holderActive = false;
            _elementDepth = 0;
            _contourMaterial = -1;
            LinkBeginCount = 0;
            _currentMaterial = _builder.AddMaterial("Default", 0.75, 0.75, 0.75, 1.0);
            return true;
        }

        public void Finish()
        {
            Flush();
        }

        /// <summary>Closes an element node left open by a cancelled export so its geometry is not lost.</summary>
        public void Flush()
        {
            if (_elementDepth > 0 && !_holderActive)
            {
                CloseElement();
            }

            _elementDepth = 0;
            _holderActive = false;
        }

        public bool IsCanceled() => _cancelled;

        public RenderNodeAction OnViewBegin(ViewNode node)
        {
            if (LevelOfDetail >= 0)
            {
                node.LevelOfDetail = Math.Min(15, LevelOfDetail);
            }

            return RenderNodeAction.Proceed;
        }

        public void OnViewEnd(ElementId elementId)
        {
        }

        public RenderNodeAction OnElementBegin(ElementId elementId)
        {
            var seenKey = _linkNames.Peek();
            ElementsSeenByDoc[seenKey] = ElementsSeenByDoc.TryGetValue(seenKey, out var seen) ? seen + 1 : 1;
            if (TrackElements)
            {
                if (_elementDepth == 0)
                {
                    var info = ReadElementInfo(elementId);
                    info.Source = _linkNames.Peek();
                    _elementCategory = info.Category ?? "(none)";
                    _elementKey = info.Source + "|" + _elementCategory;
                    ElementCounts[_elementKey] = ElementCounts.TryGetValue(_elementKey, out var n) ? n + 1 : 1;

                    if (ExcludedCategories.Contains(_elementCategory))
                    {
                        return RenderNodeAction.Skip;
                    }

                    var elementKey = info.Source + ":" + info.ElementId.ToString(CultureInfo.InvariantCulture);
                    if (ExcludedElementKeys.Contains(elementKey))
                    {
                        DroppedElements.Add($"{elementKey} | {_elementCategory} | {info.Name} | excluded-by-id");
                        return RenderNodeAction.Skip;
                    }

                    // A link instance encloses the link's elements; they get their own nodes, the instance gets none.
                    if (_elementCategory == "RVT Links" || _elementCategory == "Revit Links")
                    {
                        _holderActive = true;
                    }
                    else
                    {
                        _builder.BeginElement(info);
                        _currentElementInfo = info;
                        _elementStartTriangles = _builder.TriangleCount;
                        _currentElementLabel = info.Source + ":" + info.ElementId.ToString(CultureInfo.InvariantCulture) + " | " + info.Name;
                    }
                }

                _elementDepth++;
            }

            return RenderNodeAction.Proceed;
        }

        public void OnElementEnd(ElementId elementId)
        {
            if (TrackElements && _elementDepth > 0)
            {
                _elementDepth--;
                if (_elementDepth == 0)
                {
                    if (_holderActive)
                    {
                        _holderActive = false;
                    }
                    else
                    {
                        CloseElement();
                    }
                }
            }
        }

        private string _currentElementLabel;
        private GltfElementInfo _currentElementInfo;

        private void CloseElement()
        {
            var triangles = _builder.CurrentElementTriangles;
            var stat = new ElementTriangleStat
            {
                Key = _currentElementLabel?.Split('|')[0].Trim(),
                Name = _currentElementInfo?.Name,
                Category = _elementCategory,
                Family = _currentElementInfo?.Family,
                Triangles = triangles,
                Kept = triangles,
                Action = "kept"
            };
            if (triangles > 0)
            {
                ElementTriangleStats.Add(stat);
            }

            if (MaxElementTriangles > 0 && triangles > MaxElementTriangles)
            {
                if (OverCapMode == "simplify")
                {
                    var target = SimplifyRatio > 0 ? (int)Math.Round(triangles * SimplifyRatio) : MaxElementTriangles;
                    SimplifyTime.Start();
                    stat.Kept = _builder.SimplifyCurrentElement(target);
                    SimplifyTime.Stop();
                    stat.Action = "simplified";
                    if (_currentElementInfo != null)
                    {
                        _currentElementInfo.SourceTriangles = triangles;
                    }

                    DroppedElements.Add($"{_currentElementLabel} | {_elementCategory} | triangles={triangles} | simplified to {stat.Kept}");
                }
                else if (OverCapMode == "box")
                {
                    _builder.ReplaceCurrentElementWithBox();
                    stat.Kept = 12;
                    stat.Action = "box";
                    if (_currentElementInfo != null)
                    {
                        _currentElementInfo.SourceTriangles = triangles;
                    }

                    DroppedElements.Add($"{_currentElementLabel} | {_elementCategory} | triangles={triangles} | bounding-box placeholder");
                }
                else
                {
                    DroppedElements.Add($"{_currentElementLabel} | {_elementCategory} | triangles={triangles} | over-cap {MaxElementTriangles}");
                    stat.Kept = 0;
                    stat.Action = "dropped";
                    _builder.DiscardElement();
                    _elementKey = null;
                    _currentElementInfo = null;
                    return;
                }
            }

            RecordElementStats();
            _builder.EndElement();
            _currentElementInfo = null;
        }

        private void RecordElementStats()
        {
            if (_elementKey == null)
            {
                return;
            }

            var added = _builder.TriangleCount - _elementStartTriangles;
            TrianglesByKey[_elementKey] = (TrianglesByKey.TryGetValue(_elementKey, out var t) ? t : 0) + added;
            _elementKey = null;
        }

        private GltfElementInfo ReadElementInfo(ElementId elementId)
        {
            var info = new GltfElementInfo { ElementId = elementId.GetIdValue() };
            try
            {
                var document = _documents.Peek();
                var element = document.GetElement(elementId);
                if (element == null)
                {
                    return info;
                }

                info.UniqueId = element.UniqueId;
                info.Category = element.Category?.Name;
                info.Name = element.Name;
                var type = document.GetElement(element.GetTypeId()) as ElementType;
                info.Family = type?.FamilyName ?? (element as ElementType)?.FamilyName;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // Metadata is best-effort; geometry export must not fail because of it.
            }

            return info;
        }

        public RenderNodeAction OnLinkBegin(LinkNode node)
        {
            LinkBeginCount++;
            Document linkDocument = null;
            try
            {
                linkDocument = node.GetDocument();
                _transforms.Push(_transforms.Peek().Multiply(node.GetTransform()));
            }
            catch (Exception ex)
            {
                ExceptionCount++;
                Diag("OnLinkBegin exception: " + ex);
                _transforms.Push(_transforms.Peek());
            }

            _documents.Push(linkDocument ?? _documents.Peek());
            _linkNames.Push(linkDocument?.Title ?? "(link)");
            _linkStartTriangles.Push(_builder.TriangleCount);
            _savedElementState.Push(new KeyValuePair<int, bool>(_elementDepth, _holderActive));
            _elementDepth = 0;
            _holderActive = false;
            Diag($"OnLinkBegin #{LinkBeginCount} doc={linkDocument?.Title ?? "(null)"} triangles={_builder.TriangleCount}");
            return RenderNodeAction.Proceed;
        }

        public void OnLinkEnd(LinkNode node)
        {
            Flush();
            if (_savedElementState.Count > 0)
            {
                var saved = _savedElementState.Pop();
                _elementDepth = saved.Key;
                _holderActive = saved.Value;
            }

            if (_transforms.Count > 1)
            {
                _transforms.Pop();
            }

            if (_documents.Count > 1)
            {
                _documents.Pop();
            }

            if (_linkNames.Count > 1)
            {
                var name = _linkNames.Pop();
                var start = _linkStartTriangles.Count > 0 ? _linkStartTriangles.Pop() : 0;
                LinkTriangles[name] = (LinkTriangles.TryGetValue(name, out var t) ? t : 0) + (_builder.TriangleCount - start);
                Diag($"OnLinkEnd doc={name} addedTriangles={_builder.TriangleCount - start}");
            }
        }

        public RenderNodeAction OnInstanceBegin(InstanceNode node)
        {
            _transforms.Push(_transforms.Peek().Multiply(node.GetTransform()));
            return RenderNodeAction.Proceed;
        }

        public void OnInstanceEnd(InstanceNode node)
        {
            if (_transforms.Count > 1)
            {
                _transforms.Pop();
            }
        }

        public RenderNodeAction OnFaceBegin(FaceNode node) => RenderNodeAction.Proceed;

        public void OnFaceEnd(FaceNode node)
        {
        }

        public void OnRPC(RPCNode node)
        {
        }

        public void OnLight(LightNode node)
        {
        }

        private sealed class MaterialInfo
        {
            public int Index;
            public double[] UvScale;
        }

        public void OnMaterial(MaterialNode node)
        {
            var document = _documents.Peek();
            var cacheKey = document.Title + "|" + node.MaterialId.GetIdValue() + "|" + node.Color?.Red + "," + node.Color?.Green + "," + node.Color?.Blue + "|" + node.Transparency.ToString("R", CultureInfo.InvariantCulture);
            if (_materialCache.TryGetValue(cacheKey, out var cached))
            {
                _currentMaterial = cached.Index;
                _currentUvScale = cached.UvScale;
                return;
            }

            double r = 0.75, g = 0.75, b = 0.75;
            var color = node.Color;
            if (color != null && color.IsValid)
            {
                r = color.Red / 255.0;
                g = color.Green / 255.0;
                b = color.Blue / 255.0;
            }

            var transparency = Math.Max(0.0, Math.Min(1.0, node.Transparency));
            var name = "Material";
            Material material = null;
            try
            {
                material = node.MaterialId != null && node.MaterialId != ElementId.InvalidElementId
                    ? document.GetElement(node.MaterialId) as Material
                    : null;
                if (material != null)
                {
                    name = material.Name;
                }
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // Name is cosmetic; keep the default.
            }

            // Revit reports a node transparency of 1.0 for glazing whose real see-through amount lives in the
            // appearance asset; a fully invisible mesh is useless on the web, so clamp to a light tint.
            var alpha = 1.0 - transparency;
            if (transparency > 0)
            {
                var materialTransparency = material != null ? material.Transparency / 100.0 : -1.0;
                TransparentMaterials[name] = string.Format(CultureInfo.InvariantCulture, "node={0:0.###};material={1:0.###}", transparency, materialTransparency);
                if (alpha < 0.12)
                {
                    alpha = materialTransparency >= 0 && materialTransparency < 0.95 ? 1.0 - materialTransparency : 0.30;
                }
            }

            double appR = -1, appG = -1, appB = -1;
            var textureIndex = -1;
            double[] uvScale = null;
            try
            {
                ReadAppearance(node, name, out appR, out appG, out appB, out textureIndex, out uvScale);
            }
            catch (Exception ex)
            {
                ExceptionCount++;
                Diag("ReadAppearance(" + name + ") exception: " + ex.Message);
            }

            _currentMaterial = _builder.AddMaterial(name, r, g, b, alpha, textureIndex, appR, appG, appB);
            _currentUvScale = textureIndex >= 0 ? uvScale : null;
            _materialCache[cacheKey] = new MaterialInfo { Index = _currentMaterial, UvScale = _currentUvScale };
        }

        private void ReadAppearance(MaterialNode node, string materialName, out double appR, out double appG, out double appB,
            out int textureIndex, out double[] uvScale)
        {
            appR = appG = appB = -1;
            textureIndex = -1;
            uvScale = null;
            var asset = node.GetAppearance();
            if (asset == null)
            {
                return;
            }

            if (DumpAppearance && _appearanceDumped.Add(materialName))
            {
                var names = new List<string>();
                for (var i = 0; i < asset.Size; i++)
                {
                    var q = asset[i];
                    names.Add(q.Name + ":" + q.Type + (q.NumberOfConnectedProperties > 0 ? "*" : string.Empty));
                }

                Diag("appearance " + materialName + " | asset=" + asset.Name + " | " + string.Join(",", names));
            }

            // Colour: generic_diffuse first, then any *_color / *diffuse* RGBA property (Hardwood, Concrete, Metal ... schemas
            // name it differently), finally the tint. Texture: the first property that has a connected UnifiedBitmap.
            AssetProperty diffuse = null;
            AssetProperty tint = null;
            AssetProperty anyColor = null;
            AssetProperty bitmapOwner = null;
            for (var i = 0; i < asset.Size; i++)
            {
                var prop = asset[i];
                var lower = prop.Name.ToLowerInvariant();
                if (prop is AssetPropertyDoubleArray4d)
                {
                    if (lower == "generic_diffuse" || lower == "opaque_albedo")
                    {
                        diffuse = diffuse ?? prop;
                    }
                    else if (lower == "common_tint_color")
                    {
                        tint = prop;
                    }
                    else if (anyColor == null && (lower.EndsWith("_color") || lower.Contains("diffuse") || lower.Contains("albedo")) &&
                             !lower.Contains("tint") && !lower.Contains("emission") && !lower.Contains("reflect") && !lower.Contains("transmit"))
                    {
                        anyColor = prop;
                    }
                }

                if (bitmapOwner == null && prop.NumberOfConnectedProperties > 0 && (lower == "generic_diffuse" || lower.EndsWith("_color") || lower.Contains("diffuse") || lower.Contains("albedo")))
                {
                    bitmapOwner = prop;
                }
            }

            var colorProp = diffuse ?? anyColor ?? tint;
            if (colorProp is AssetPropertyDoubleArray4d arr)
            {
                var c = arr.GetValueAsDoubles();
                if (c != null && c.Count >= 3)
                {
                    appR = Math.Max(0, Math.Min(1, c[0]));
                    appG = Math.Max(0, Math.Min(1, c[1]));
                    appB = Math.Max(0, Math.Min(1, c[2]));
                }
            }

            if (bitmapOwner == null)
            {
                return;
            }

            var bitmapAsset = bitmapOwner.GetSingleConnectedAsset();
            if (bitmapAsset == null)
            {
                return;
            }

            string path = null;
            double sx = 0, sy = 0;
            for (var i = 0; i < bitmapAsset.Size; i++)
            {
                var prop = bitmapAsset[i];
                if (prop.Name == "unifiedbitmap_Bitmap" && prop is AssetPropertyString str)
                {
                    path = str.Value;
                }
                else if (prop.Name == "texture_RealWorldScaleX" && prop is AssetPropertyDistance dx)
                {
                    sx = ToFeet(dx);
                }
                else if (prop.Name == "texture_RealWorldScaleY" && prop is AssetPropertyDistance dy)
                {
                    sy = ToFeet(dy);
                }
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var index = LoadImage(path, out var problem);
            if (index < 0)
            {
                TextureProblems[materialName + " :: " + path] = problem;
                return;
            }

            textureIndex = index;
            if (sx <= 1e-6) { sx = 1.0; }
            if (sy <= 1e-6) { sy = sx; }
            uvScale = new[] { sx, sy };
            TexturedMaterials++;
        }

        private static double ToFeet(AssetPropertyDistance d)
        {
            try
            {
                return UnitUtils.ConvertToInternalUnits(d.Value, d.GetUnitTypeId());
            }
            catch (Exception)
            {
                return d.Value; // assume feet
            }
        }

        private static string SafeExt(string path)
        {
            var dot = path.LastIndexOf('.');
            return dot < 0 ? string.Empty : path.Substring(dot).ToLowerInvariant();
        }

        private static string SafeName(string path)
        {
            var cut = Math.Max(path.LastIndexOf((char)92), path.LastIndexOf('/'));
            return cut < 0 ? path : path.Substring(cut + 1);
        }

        private int LoadImage(string path, out string problem)
        {
            problem = null;
            var key = path.Trim();
            if (_imageIndexByPath.TryGetValue(key, out var existing))
            {
                return existing;
            }

            // Revit may store several candidate paths in one string separated by '|'.
            string resolved = null;
            foreach (var candidate in key.Split('|'))
            {
                var c = candidate.Trim();
                if (c.Length == 0)
                {
                    continue;
                }

                resolved = ResolveTexturePath(c);
                if (resolved != null)
                {
                    break;
                }
            }

            Diag("bitmap " + key + " -> " + (resolved ?? "(not found)"));
            if (resolved == null)
            {
                problem = "file not found";
                _imageIndexByPath[key] = -1;
                return -1;
            }

            try
            {
                var bytes = File.ReadAllBytes(resolved);
                var ext = SafeExt(resolved);
                var mime = ext == ".png" ? "image/png" : (ext == ".jpg" || ext == ".jpeg") ? "image/jpeg" : null;
                if (mime == null || bytes.Length > 1_500_000)
                {
                    using (var ms = new MemoryStream(bytes))
                    using (var img = System.Drawing.Image.FromStream(ms))
                    {
                        var max = 1024;
                        var w = img.Width; var h = img.Height;
                        var scale = Math.Min(1.0, (double)max / Math.Max(w, h));
                        var nw = Math.Max(1, (int)(w * scale)); var nh = Math.Max(1, (int)(h * scale));
                        using (var bmp = new System.Drawing.Bitmap(nw, nh))
                        using (var gfx = System.Drawing.Graphics.FromImage(bmp))
                        using (var outMs = new MemoryStream())
                        {
                            gfx.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            gfx.DrawImage(img, 0, 0, nw, nh);
                            bmp.Save(outMs, System.Drawing.Imaging.ImageFormat.Jpeg);
                            bytes = outMs.ToArray();
                            mime = "image/jpeg";
                        }
                    }
                }

                if (_imageBytes + bytes.Length > MaxImageBytes)
                {
                    problem = "texture byte budget exceeded";
                    _imageIndexByPath[key] = -1;
                    return -1;
                }

                _imageBytes += bytes.Length;
                var index = _builder.AddImage(new GltfImage { Bytes = bytes, MimeType = mime, Name = SafeName(resolved) });
                _imageIndexByPath[key] = index;
                return index;
            }
            catch (Exception ex)
            {
                problem = ex.Message;
                _imageIndexByPath[key] = -1;
                return -1;
            }
        }

        private static string ResolveTexturePath(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }
            catch (Exception)
            {
                // Illegal characters: fall through to the index lookup by file name.
            }

            if (_textureIndex == null)
            {
                _textureIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var roots = new[]
                {
                    @"C:\Program Files (x86)\Common Files\Autodesk Shared\Materials\Textures",
                    @"C:\Program Files\Common Files\Autodesk Shared\Materials\Textures",
                    @"C:\Program Files\Autodesk\Revit 2024\Textures"
                };
                foreach (var root in roots)
                {
                    if (!Directory.Exists(root))
                    {
                        continue;
                    }

                    try
                    {
                        foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
                        {
                            var ext = SafeExt(file);
                            if (ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".bmp" || ext == ".tif" || ext == ".tiff")
                            {
                                var rel = file.Substring(root.Length).TrimStart('\\');
                                _textureIndex[rel] = file;
                                var name = SafeName(file);
                                if (!_textureIndex.ContainsKey(name))
                                {
                                    _textureIndex[name] = file;
                                }
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // Unreadable folder: resolve what we can.
                    }
                }
            }

            var normalized = path.Replace('/', '\\').TrimStart('\\');
            if (_textureIndex.TryGetValue(normalized, out var found))
            {
                return found;
            }

            return _textureIndex.TryGetValue(SafeName(normalized), out found) ? found : null;
        }

        public void OnPolymesh(PolymeshTopology node)
        {
            if (_cancelled)
            {
                return;
            }

            var pointCount = node.NumberOfPoints;
            var facetCount = node.NumberOfFacets;
            if (pointCount < 3 || facetCount < 1)
            {
                SkippedPolymeshes++;
                return;
            }

            var transform = _transforms.Peek();
            var points = node.GetPoints();
            var facets = node.GetFacets();
            var distribution = node.DistributionOfNormals;
            var uvScale = _currentUvScale;
            IList<UV> sourceUvs = null;
            if (uvScale != null)
            {
                try
                {
                    sourceUvs = node.NumberOfUVs > 0 ? node.GetUVs() : null;
                }
                catch (Exception)
                {
                    sourceUvs = null;
                }
            }

            if (distribution == DistributionOfNormals.OnEachFacet)
            {
                // Flat facets: duplicate vertices so each triangle keeps its own normal.
                var positions = new List<Vec3>(facetCount * 3);
                var normals = new List<Vec3>(facetCount * 3);
                var indices = new List<int>(facetCount * 3);
                var uvs = sourceUvs != null ? new List<double>(facetCount * 6) : null;
                for (var f = 0; f < facetCount; f++)
                {
                    var facet = facets[f];
                    var normal = ToGltfVector(transform.OfVector(node.GetNormal(f)));
                    foreach (var v in new[] { facet.V1, facet.V2, facet.V3 })
                    {
                        indices.Add(positions.Count);
                        positions.Add(ToGltfPoint(transform.OfPoint(points[v])));
                        normals.Add(normal);
                        if (uvs != null && v < sourceUvs.Count)
                        {
                            uvs.Add(sourceUvs[v].U / uvScale[0]);
                            uvs.Add(1.0 - sourceUvs[v].V / uvScale[1]);
                        }
                        else if (uvs != null)
                        {
                            uvs.Add(0); uvs.Add(0);
                        }
                    }
                }

                _builder.AddTriangles(_currentMaterial, positions, normals, indices, uvs);
                AddContours(positions, normals, indices);
            }
            else
            {
                var positions = new Vec3[pointCount];
                var normals = new Vec3[pointCount];
                var faceNormal = distribution == DistributionOfNormals.OnePerFace
                    ? ToGltfVector(transform.OfVector(node.GetNormal(0)))
                    : Vec3.Zero;
                for (var i = 0; i < pointCount; i++)
                {
                    positions[i] = ToGltfPoint(transform.OfPoint(points[i]));
                    normals[i] = distribution == DistributionOfNormals.AtEachPoint
                        ? ToGltfVector(transform.OfVector(node.GetNormal(i)))
                        : faceNormal;
                }

                var indices = new int[facetCount * 3];
                for (var f = 0; f < facetCount; f++)
                {
                    var facet = facets[f];
                    indices[f * 3] = facet.V1;
                    indices[f * 3 + 1] = facet.V2;
                    indices[f * 3 + 2] = facet.V3;
                }

                List<double> smoothUvs = null;
                if (sourceUvs != null)
                {
                    smoothUvs = new List<double>(pointCount * 2);
                    for (var i = 0; i < pointCount; i++)
                    {
                        if (i < sourceUvs.Count)
                        {
                            smoothUvs.Add(sourceUvs[i].U / uvScale[0]);
                            smoothUvs.Add(1.0 - sourceUvs[i].V / uvScale[1]);
                        }
                        else
                        {
                            smoothUvs.Add(0); smoothUvs.Add(0);
                        }
                    }
                }

                _builder.AddTriangles(_currentMaterial, positions, normals, indices, smoothUvs);
                AddContours(positions, normals, indices);
            }

            if (_builder.TriangleCount > TriangleBudget)
            {
                _cancelled = true;
            }
        }

        // IExportContextBase members (Revit 2024+): curves, lines and text are not exported.
        public void OnCurve(CurveNode node)
        {
        }

        public void OnPolyline(PolylineNode node)
        {
        }

        public void OnLineSegment(LineSegment segment)
        {
        }

        public void OnPolylineSegments(PolylineSegments segments)
        {
        }

        public void OnText(TextNode node)
        {
        }

        /// <summary>Slices upward-facing terrain triangles at multiples of <see cref="ContourIntervalMeters"/> (absolute elevation).</summary>
        private void AddContours(IReadOnlyList<Vec3> positions, IReadOnlyList<Vec3> normals, IReadOnlyList<int> indices)
        {
            if (ContourIntervalMeters <= 0 || !TrackElements ||
                (_elementCategory != "Toposolid" && _elementCategory != "Topography" && _elementCategory != "Toposolids"))
            {
                return;
            }

            var interval = ContourIntervalMeters;
            var lift = 0.03; // meters, keeps the line above the surface
            var segments = new List<Vec3>();
            for (var t = 0; t + 2 < indices.Count; t += 3)
            {
                var a = positions[indices[t]];
                var b = positions[indices[t + 1]];
                var c = positions[indices[t + 2]];
                var n = (b - a).Cross(c - a);
                if (n.Length < 1e-12 || n.Y / n.Length < 0.5)
                {
                    continue; // not an upward-facing surface triangle
                }

                var lo = Math.Min(a.Y, Math.Min(b.Y, c.Y)) + _originOffsetMeters.Z;
                var hi = Math.Max(a.Y, Math.Max(b.Y, c.Y)) + _originOffsetMeters.Z;
                var k0 = (int)Math.Ceiling(lo / interval);
                var k1 = (int)Math.Floor(hi / interval);
                for (var k = k0; k <= k1; k++)
                {
                    var level = k * interval - _originOffsetMeters.Z;
                    var pts = new List<Vec3>(2);
                    CrossEdge(a, b, level, pts);
                    CrossEdge(b, c, level, pts);
                    CrossEdge(c, a, level, pts);
                    if (pts.Count >= 2 && (pts[0] - pts[1]).Length > 1e-6)
                    {
                        segments.Add(new Vec3(pts[0].X, pts[0].Y + lift, pts[0].Z));
                        segments.Add(new Vec3(pts[1].X, pts[1].Y + lift, pts[1].Z));
                    }
                }
            }

            if (segments.Count == 0)
            {
                return;
            }

            if (_contourMaterial < 0)
            {
                _contourMaterial = _builder.AddMaterial("Contour", 0.35, 0.28, 0.18, 1.0);
            }

            _builder.AddLineSegments(_contourMaterial, segments);
            ContourSegments += segments.Count / 2;
        }

        private static void CrossEdge(Vec3 p, Vec3 q, double level, List<Vec3> output)
        {
            var dp = p.Y - level;
            var dq = q.Y - level;
            if ((dp < 0 && dq >= 0) || (dp >= 0 && dq < 0))
            {
                var f = dp / (dp - dq);
                output.Add(new Vec3(p.X + (q.X - p.X) * f, level, p.Z + (q.Z - p.Z) * f));
            }
        }

        private Vec3 ToGltfPoint(XYZ internalPoint)
        {
            var x = internalPoint.X * CameraPathMath.FeetToMeters - _originOffsetMeters.X;
            var y = internalPoint.Y * CameraPathMath.FeetToMeters - _originOffsetMeters.Y;
            var z = internalPoint.Z * CameraPathMath.FeetToMeters - _originOffsetMeters.Z;
            return new Vec3(x, z, -y);
        }

        private static Vec3 ToGltfVector(XYZ v) => new Vec3(v.X, v.Z, -v.Y);
    }

    /// <summary>One exported element's triangle count before/after over-cap handling (diagnostics, triangle study).</summary>
    internal sealed class ElementTriangleStat
    {
        public string Key { get; set; }
        public string Name { get; set; }
        public string Category { get; set; }
        public string Family { get; set; }
        public int Triangles { get; set; }
        public int Kept { get; set; }
        public string Action { get; set; }
    }
}
