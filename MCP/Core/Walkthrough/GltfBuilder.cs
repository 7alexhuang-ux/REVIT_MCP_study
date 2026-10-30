using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace RevitMCP.Core.Walkthrough
{
    /// <summary>設計選項的穩定識別；名稱只供顯示，跨次匯出以 UniqueId 為鍵。</summary>
    public sealed class GltfDesignOptionInfo
    {
        public string SetKey;
        public string SetName;
        public string OptionKey;
        public string OptionName;
        public bool IsPrimary;
    }

    /// <summary>Per-element metadata written to glTF node.extras by <see cref="GltfBuilder.BeginElement"/>.</summary>
    public sealed class GltfElementInfo
    {
        public long ElementId;
        public string UniqueId;
        public string Category;
        /// <summary>Category 的 BuiltInCategory 整數值（與介面語言無關）；0＝未知。不寫進 GLB，只供分檔判斷。</summary>
        public int CategoryBuiltIn;
        public string Family;
        public string Name;
        /// <summary>Document the element lives in: "(host)" or the linked document title.</summary>
        public string Source;
        /// <summary>Triangle count before over-cap simplification (0 = untouched).</summary>
        public int SourceTriangles;
        /// <summary>True when the geometry was replaced by its bounding box.</summary>
        public bool Placeholder;
        /// <summary>主模型保持 null，不輸出 designOption 欄位。</summary>
        public GltfDesignOptionInfo DesignOption;
    }

    /// <summary>One embedded texture image (JPEG/PNG bytes).</summary>
    public sealed class GltfImage
    {
        public byte[] Bytes;
        public string MimeType;
        public string Name;
    }

    /// <summary>
    /// Accumulates triangle meshes grouped by flat color material and writes a glTF 2.0 binary (GLB).
    /// Zero external dependencies. Positions and normals must already be in glTF space (Y up, meters).
    /// </summary>
    public sealed class GltfBuilder
    {
        private class Geo
        {
            public readonly List<float> Positions = new List<float>();
            public readonly List<float> Normals = new List<float>();
            public readonly List<float> Uvs = new List<float>();
            public readonly List<uint> Indices = new List<uint>();
        }

        private sealed class MaterialBucket : Geo
        {
            public string Name;
            public double R, G, B, A;
            public int Texture = -1;
            public double AppR = -1, AppG = -1, AppB = -1;
        }

        private sealed class LineSet
        {
            public int Material;
            public readonly List<float> Positions = new List<float>();
        }

        private sealed class ElementNode
        {
            public GltfElementInfo Info;
            public readonly SortedDictionary<int, Geo> ByMaterial = new SortedDictionary<int, Geo>();
            public readonly List<LineSet> Lines = new List<LineSet>();
            public int Triangles;
            public int Vertices;
        }

        private readonly List<GltfImage> _images = new List<GltfImage>();

        /// <summary>
        /// Revit 內部座標（公尺）中被扣掉的 GLB 原點；有值時寫進 scenes[0].extras.originOffsetMeters（同 camera.json），
        /// 網頁上傳基地範圍時可自動對齊。null＝不輸出。
        /// </summary>
        public Vec3? OriginOffsetMeters { get; set; }

        /// <summary>
        /// 連結模型分檔（splitLinks）的檔案識別：兩者皆非空時寫進 scenes[0].extras.part = { key, label }。
        /// 主檔不設定。
        /// </summary>
        public string PartKey { get; set; } = string.Empty;

        public string PartLabel { get; set; } = string.Empty;

        private readonly List<ElementNode> _elementNodes = new List<ElementNode>();
        private ElementNode _currentElementNode;

        private readonly List<MaterialBucket> _buckets = new List<MaterialBucket>();
        private readonly Dictionary<string, int> _materialKeys = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>
        /// Starts a per-element glTF node: triangles added until <see cref="EndElement"/> go to their own node
        /// carrying <paramref name="info"/> in node.extras. Without it geometry is merged per material (walkthrough).
        /// </summary>
        public void BeginElement(GltfElementInfo info)
        {
            _currentElementNode = info == null ? null : new ElementNode { Info = info };
        }

        public void EndElement()
        {
            if (_currentElementNode != null && (_currentElementNode.ByMaterial.Count > 0 || _currentElementNode.Lines.Count > 0))
            {
                _elementNodes.Add(_currentElementNode);
            }

            _currentElementNode = null;
        }

        /// <summary>Triangles added to the element node that is currently open (0 when none).</summary>
        public int CurrentElementTriangles => _currentElementNode?.Triangles ?? 0;

        /// <summary>Drops the open element node and its geometry (used for over-heavy elements).</summary>
        public void DiscardElement()
        {
            if (_currentElementNode == null)
            {
                return;
            }

            TriangleCount -= _currentElementNode.Triangles;
            VertexCount -= _currentElementNode.Vertices;
            _currentElementNode = null;
        }

        /// <summary>
        /// Simplifies the open element node toward <paramref name="targetTriangles"/> (split across its materials by
        /// share) with quadric edge collapse. Vertices are welded by position (and UV when textured) first; normals are
        /// rebuilt with a 45° crease so hard edges stay hard. Returns the element's new triangle count.
        /// </summary>
        public int SimplifyCurrentElement(int targetTriangles)
        {
            var node = _currentElementNode;
            if (node == null || node.Triangles <= targetTriangles || node.Triangles == 0)
            {
                return node?.Triangles ?? 0;
            }

            var total = node.Triangles;
            int newTriangles = 0, newVertices = 0;
            foreach (var key in node.ByMaterial.Keys.ToList())
            {
                var geo = node.ByMaterial[key];
                var geoTriangles = geo.Indices.Count / 3;
                var geoTarget = Math.Max(4, (int)Math.Round((double)targetTriangles * geoTriangles / total));
                var simplified = geoTriangles > geoTarget ? SimplifyGeo(geo, geoTarget) : geo;
                node.ByMaterial[key] = simplified;
                newTriangles += simplified.Indices.Count / 3;
                newVertices += simplified.Positions.Count / 3;
            }

            TriangleCount += newTriangles - node.Triangles;
            VertexCount += newVertices - node.Vertices;
            node.Triangles = newTriangles;
            node.Vertices = newVertices;
            return newTriangles;
        }

        private static Geo SimplifyGeo(Geo geo, int target)
        {
            var hasUv = geo.Uvs.Count > 0;
            var weld = new Dictionary<(long, long, long, long, long), int>();
            var positions = new List<double>();
            var uvs = new List<float>();
            var indices = new List<int>(geo.Indices.Count);
            var welded = new int[geo.Positions.Count / 3];
            for (var i = 0; i < welded.Length; i++)
            {
                var key = ((long)Math.Round(geo.Positions[i * 3] * 1e5), (long)Math.Round(geo.Positions[i * 3 + 1] * 1e5),
                    (long)Math.Round(geo.Positions[i * 3 + 2] * 1e5),
                    hasUv ? (long)Math.Round(geo.Uvs[i * 2] * 1e4) : 0L, hasUv ? (long)Math.Round(geo.Uvs[i * 2 + 1] * 1e4) : 0L);
                if (!weld.TryGetValue(key, out var w))
                {
                    w = positions.Count / 3;
                    weld[key] = w;
                    positions.Add(geo.Positions[i * 3]);
                    positions.Add(geo.Positions[i * 3 + 1]);
                    positions.Add(geo.Positions[i * 3 + 2]);
                    if (hasUv)
                    {
                        uvs.Add(geo.Uvs[i * 2]);
                        uvs.Add(geo.Uvs[i * 2 + 1]);
                    }
                }

                welded[i] = w;
            }

            foreach (var index in geo.Indices)
            {
                indices.Add(welded[index]);
            }

            MeshSimplifier.Simplify(positions, indices, target, out var outPositions, out var outIndices, out var source);

            // Face normals, then per-corner normals averaged over adjacent faces within the crease angle.
            var faceCount = outIndices.Count / 3;
            var faceNormals = new Vec3[faceCount];
            var facesOfVertex = new List<int>[outPositions.Count / 3];
            for (var f = 0; f < faceCount; f++)
            {
                var a = P(outPositions, outIndices[f * 3]);
                var b = P(outPositions, outIndices[f * 3 + 1]);
                var c = P(outPositions, outIndices[f * 3 + 2]);
                faceNormals[f] = (b - a).Cross(c - a); // area-weighted
                for (var j = 0; j < 3; j++)
                {
                    var v = outIndices[f * 3 + j];
                    (facesOfVertex[v] ?? (facesOfVertex[v] = new List<int>())).Add(f);
                }
            }

            const double crease = 0.7071; // cos 45°
            var result = new Geo();
            var corners = new Dictionary<(int, long, long, long), uint>();
            for (var f = 0; f < faceCount; f++)
            {
                var fn = faceNormals[f].Normalized();
                for (var j = 0; j < 3; j++)
                {
                    var v = outIndices[f * 3 + j];
                    var sum = Vec3.Zero;
                    foreach (var g in facesOfVertex[v])
                    {
                        if (faceNormals[g].Normalized().Dot(fn) >= crease)
                        {
                            sum = sum + faceNormals[g];
                        }
                    }

                    var n = sum.Length < 1e-20 ? fn : sum.Normalized();
                    if (n.Length < 0.5)
                    {
                        n = new Vec3(0, 1, 0);
                    }

                    var key = (v, (long)Math.Round(n.X * 1000), (long)Math.Round(n.Y * 1000), (long)Math.Round(n.Z * 1000));
                    if (!corners.TryGetValue(key, out var idx))
                    {
                        idx = (uint)(result.Positions.Count / 3);
                        corners[key] = idx;
                        result.Positions.Add((float)outPositions[v * 3]);
                        result.Positions.Add((float)outPositions[v * 3 + 1]);
                        result.Positions.Add((float)outPositions[v * 3 + 2]);
                        result.Normals.Add((float)n.X);
                        result.Normals.Add((float)n.Y);
                        result.Normals.Add((float)n.Z);
                        if (hasUv)
                        {
                            result.Uvs.Add(uvs[source[v] * 2]);
                            result.Uvs.Add(uvs[source[v] * 2 + 1]);
                        }
                    }

                    result.Indices.Add(idx);
                }
            }

            return result;
        }

        private static Vec3 P(List<double> p, int i) => new Vec3(p[i * 3], p[i * 3 + 1], p[i * 3 + 2]);

        /// <summary>Replaces the open element node's geometry with its axis-aligned bounding box (dominant material).</summary>
        public void ReplaceCurrentElementWithBox()
        {
            var node = _currentElementNode;
            if (node == null || node.ByMaterial.Count == 0)
            {
                return;
            }

            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
            int dominant = -1, dominantCount = -1;
            foreach (var kv in node.ByMaterial)
            {
                var p = kv.Value.Positions;
                for (var i = 0; i + 2 < p.Count; i += 3)
                {
                    minX = Math.Min(minX, p[i]); maxX = Math.Max(maxX, p[i]);
                    minY = Math.Min(minY, p[i + 1]); maxY = Math.Max(maxY, p[i + 1]);
                    minZ = Math.Min(minZ, p[i + 2]); maxZ = Math.Max(maxZ, p[i + 2]);
                }

                if (kv.Value.Indices.Count > dominantCount)
                {
                    dominantCount = kv.Value.Indices.Count;
                    dominant = kv.Key;
                }
            }

            var box = new Geo();
            var hasUv = _buckets[dominant].Texture >= 0;
            void Face(float nx, float ny, float nz, float[][] quad)
            {
                var b = (uint)(box.Positions.Count / 3);
                foreach (var q in quad)
                {
                    box.Positions.Add(q[0]); box.Positions.Add(q[1]); box.Positions.Add(q[2]);
                    box.Normals.Add(nx); box.Normals.Add(ny); box.Normals.Add(nz);
                    if (hasUv)
                    {
                        box.Uvs.Add(0f); box.Uvs.Add(0f);
                    }
                }

                box.Indices.Add(b); box.Indices.Add(b + 1); box.Indices.Add(b + 2);
                box.Indices.Add(b); box.Indices.Add(b + 2); box.Indices.Add(b + 3);
            }

            float[] V(float x, float y, float z) => new[] { x, y, z };
            Face(1, 0, 0, new[] { V(maxX, minY, minZ), V(maxX, maxY, minZ), V(maxX, maxY, maxZ), V(maxX, minY, maxZ) });
            Face(-1, 0, 0, new[] { V(minX, minY, maxZ), V(minX, maxY, maxZ), V(minX, maxY, minZ), V(minX, minY, minZ) });
            Face(0, 1, 0, new[] { V(minX, maxY, minZ), V(minX, maxY, maxZ), V(maxX, maxY, maxZ), V(maxX, maxY, minZ) });
            Face(0, -1, 0, new[] { V(minX, minY, minZ), V(maxX, minY, minZ), V(maxX, minY, maxZ), V(minX, minY, maxZ) });
            Face(0, 0, 1, new[] { V(minX, minY, maxZ), V(maxX, minY, maxZ), V(maxX, maxY, maxZ), V(minX, maxY, maxZ) });
            Face(0, 0, -1, new[] { V(minX, maxY, minZ), V(maxX, maxY, minZ), V(maxX, minY, minZ), V(minX, minY, minZ) });

            node.ByMaterial.Clear();
            node.ByMaterial[dominant] = box;
            TriangleCount += 12 - node.Triangles;
            VertexCount += 24 - node.Vertices;
            node.Triangles = 12;
            node.Vertices = 24;
            if (node.Info != null)
            {
                node.Info.Placeholder = true;
            }
        }

        public int ElementNodeCount => _elementNodes.Count;

        public int TriangleCount { get; private set; }
        public int VertexCount { get; private set; }

        /// <summary>Number of materials that received at least one triangle.</summary>
        public int MaterialCount
        {
            get
            {
                return UsedMaterials().Count;
            }
        }

        private HashSet<int> UsedMaterials()
        {
            var used = new HashSet<int>();
            for (var i = 0; i < _buckets.Count; i++)
            {
                if (_buckets[i].Indices.Count > 0)
                {
                    used.Add(i);
                }
            }

            foreach (var node in _elementNodes)
            {
                foreach (var key in node.ByMaterial.Keys)
                {
                    used.Add(key);
                }

                foreach (var line in node.Lines)
                {
                    used.Add(line.Material);
                }
            }

            return used;
        }

        /// <summary>Returns a material index; identical colors (after 8-bit quantization) share one material.</summary>
        public int AddMaterial(string name, double r, double g, double b, double alpha)
            => AddMaterial(name, r, g, b, alpha, -1, -1, -1, -1);

        /// <summary>Registers an embedded texture and returns its index.</summary>
        public int AddImage(GltfImage image)
        {
            _images.Add(image);
            return _images.Count - 1;
        }

        public int ImageCount => _images.Count;

        /// <summary>
        /// Material with an optional texture (baseColorTexture) and the Revit appearance (Realistic) colour that the web
        /// viewer applies in realistic mode. Colours are sRGB 0..1; <paramref name="r"/>,<paramref name="g"/>,<paramref name="b"/> is the graphics colour.
        /// </summary>
        public int AddMaterial(string name, double r, double g, double b, double alpha, int texture, double appR, double appG, double appB)
        {
            r = Clamp01(r); g = Clamp01(g); b = Clamp01(b); alpha = Clamp01(alpha);
            string Q(double v) => v < 0 ? "x" : ((int)Math.Round(Clamp01(v) * 255)).ToString(CultureInfo.InvariantCulture);
            var key = string.Format(CultureInfo.InvariantCulture, "{0}-{1}-{2}-{3}|t{4}|a{5}-{6}-{7}",
                Q(r), Q(g), Q(b), Q(alpha), texture, Q(appR), Q(appG), Q(appB));
            if (_materialKeys.TryGetValue(key, out var existing))
            {
                return existing;
            }

            _buckets.Add(new MaterialBucket
            {
                Name = string.IsNullOrWhiteSpace(name) ? "material_" + key : name,
                R = r, G = g, B = b, A = alpha, Texture = texture, AppR = appR, AppG = appG, AppB = appB
            });
            _materialKeys[key] = _buckets.Count - 1;
            return _buckets.Count - 1;
        }

        /// <summary>Adds line segments (pairs of points) to the current element node, drawn with <paramref name="material"/>'s colour.</summary>
        public void AddLineSegments(int material, IReadOnlyList<Vec3> points)
        {
            if (_currentElementNode == null || points == null || points.Count < 2)
            {
                return;
            }

            LineSet set = null;
            foreach (var l in _currentElementNode.Lines)
            {
                if (l.Material == material) { set = l; break; }
            }

            if (set == null)
            {
                set = new LineSet { Material = material };
                _currentElementNode.Lines.Add(set);
            }

            for (var i = 0; i + 1 < points.Count; i += 2)
            {
                foreach (var pt in new[] { points[i], points[i + 1] })
                {
                    set.Positions.Add((float)pt.X);
                    set.Positions.Add((float)pt.Y);
                    set.Positions.Add((float)pt.Z);
                }
            }
        }

        /// <summary>
        /// Adds an indexed triangle list. When <paramref name="normals"/> is null, area-weighted vertex normals
        /// are computed from the triangles.
        /// </summary>
        public void AddTriangles(int material, IReadOnlyList<Vec3> positions, IReadOnlyList<Vec3> normals, IReadOnlyList<int> indices)
            => AddTriangles(material, positions, normals, indices, null);

        /// <summary>Same as above with optional per-vertex UVs (u,v pairs, one per position) for textured materials.</summary>
        public void AddTriangles(int material, IReadOnlyList<Vec3> positions, IReadOnlyList<Vec3> normals, IReadOnlyList<int> indices, IReadOnlyList<double> uvs)
        {
            if (material < 0 || material >= _buckets.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(material));
            }

            if (positions == null || indices == null || indices.Count % 3 != 0)
            {
                throw new ArgumentException("Positions are required and index count must be a multiple of 3.");
            }

            if (normals != null && normals.Count != positions.Count)
            {
                throw new ArgumentException("Normal count must match position count.");
            }

            if (positions.Count == 0 || indices.Count == 0)
            {
                return;
            }

            foreach (var index in indices)
            {
                if (index < 0 || index >= positions.Count)
                {
                    throw new ArgumentOutOfRangeException(nameof(indices), "Triangle index out of range.");
                }
            }

            var computed = normals ?? ComputeVertexNormals(positions, indices);
            Geo bucket = _buckets[material];
            if (_currentElementNode != null)
            {
                if (!_currentElementNode.ByMaterial.TryGetValue(material, out var elementGeo))
                {
                    elementGeo = new Geo();
                    _currentElementNode.ByMaterial[material] = elementGeo;
                }

                bucket = elementGeo;
            }

            var baseIndex = (uint)(bucket.Positions.Count / 3);
            for (var i = 0; i < positions.Count; i++)
            {
                bucket.Positions.Add((float)positions[i].X);
                bucket.Positions.Add((float)positions[i].Y);
                bucket.Positions.Add((float)positions[i].Z);
                var n = computed[i].Normalized();
                if (n.Length < 0.5)
                {
                    n = new Vec3(0, 1, 0);
                }

                bucket.Normals.Add((float)n.X);
                bucket.Normals.Add((float)n.Y);
                bucket.Normals.Add((float)n.Z);
                if (_buckets[material].Texture >= 0)
                {
                    var hasUv = uvs != null && uvs.Count >= (i + 1) * 2;
                    bucket.Uvs.Add(hasUv ? (float)uvs[i * 2] : 0f);
                    bucket.Uvs.Add(hasUv ? (float)uvs[i * 2 + 1] : 0f);
                }
            }

            foreach (var index in indices)
            {
                bucket.Indices.Add(baseIndex + (uint)index);
            }

            TriangleCount += indices.Count / 3;
            VertexCount += positions.Count;
            if (_currentElementNode != null)
            {
                _currentElementNode.Triangles += indices.Count / 3;
                _currentElementNode.Vertices += positions.Count;
            }
        }

        public byte[] BuildGlb()
        {
            var bin = new MemoryStream();
            var json = new StringBuilder();
            var accessors = new List<string>();
            var bufferViews = new List<string>();
            var primitives = new List<string>();
            var materials = new List<string>();

            var usedMaterials = UsedMaterials();
            var materialMap = new Dictionary<int, int>();
            for (var i = 0; i < _buckets.Count; i++)
            {
                if (!usedMaterials.Contains(i))
                {
                    continue;
                }

                var bucket = _buckets[i];
                materialMap[i] = materials.Count;
                // glTF baseColorFactor is linear; Revit colours are sRGB. extras keep the sRGB values for the web viewer.
                var tex = bucket.Texture >= 0
                    ? string.Format(CultureInfo.InvariantCulture, ",\"baseColorTexture\":{{\"index\":{0}}}", bucket.Texture)
                    : string.Empty;
                var app = bucket.AppR >= 0
                    ? string.Format(CultureInfo.InvariantCulture, ",\"appearance\":[{0},{1},{2}]", Num(bucket.AppR), Num(bucket.AppG), Num(bucket.AppB))
                    : string.Empty;
                var extras = string.Format(CultureInfo.InvariantCulture, ",\"extras\":{{\"srgb\":[{0},{1},{2}],\"alpha\":{3}{4}{5}}}",
                    Num(bucket.R), Num(bucket.G), Num(bucket.B), Num(bucket.A), app,
                    bucket.Texture >= 0 ? ",\"textured\":true" : string.Empty);
                materials.Add(string.Format(CultureInfo.InvariantCulture,
                    "{{\"name\":{0},\"pbrMetallicRoughness\":{{\"baseColorFactor\":[{1},{2},{3},{4}]{6},\"metallicFactor\":0,\"roughnessFactor\":0.9}},\"doubleSided\":true{5}{7}}}",
                    JsonString(bucket.Name), Num(SrgbToLinear(bucket.R)), Num(SrgbToLinear(bucket.G)), Num(SrgbToLinear(bucket.B)), Num(bucket.A),
                    bucket.A < 0.999 ? ",\"alphaMode\":\"BLEND\"" : string.Empty, tex, extras));
            }

            string EmitPrimitive(Geo geo, int materialIndex)
            {
                var vertexCount = geo.Positions.Count / 3;
                ComputeBounds(geo.Positions, out var min, out var max);

                var positionView = WriteFloats(bin, geo.Positions, bufferViews, 34962);
                var positionAccessor = accessors.Count;
                accessors.Add(string.Format(CultureInfo.InvariantCulture,
                    "{{\"bufferView\":{0},\"componentType\":5126,\"count\":{1},\"type\":\"VEC3\",\"min\":[{2},{3},{4}],\"max\":[{5},{6},{7}]}}",
                    positionView, vertexCount, Num(min[0]), Num(min[1]), Num(min[2]), Num(max[0]), Num(max[1]), Num(max[2])));

                var normalView = WriteFloats(bin, geo.Normals, bufferViews, 34962);
                var normalAccessor = accessors.Count;
                accessors.Add(string.Format(CultureInfo.InvariantCulture,
                    "{{\"bufferView\":{0},\"componentType\":5126,\"count\":{1},\"type\":\"VEC3\"}}", normalView, vertexCount));

                var indexView = WriteUInts(bin, geo.Indices, bufferViews, 34963);
                var indexAccessor = accessors.Count;
                accessors.Add(string.Format(CultureInfo.InvariantCulture,
                    "{{\"bufferView\":{0},\"componentType\":5125,\"count\":{1},\"type\":\"SCALAR\"}}", indexView, geo.Indices.Count));

                var uvAttr = string.Empty;
                if (geo.Uvs.Count == vertexCount * 2 && vertexCount > 0)
                {
                    var uvView = WriteFloats(bin, geo.Uvs, bufferViews, 34962);
                    accessors.Add(string.Format(CultureInfo.InvariantCulture,
                        "{{\"bufferView\":{0},\"componentType\":5126,\"count\":{1},\"type\":\"VEC2\"}}", uvView, vertexCount));
                    uvAttr = ",\"TEXCOORD_0\":" + (accessors.Count - 1).ToString(CultureInfo.InvariantCulture);
                }

                return string.Format(CultureInfo.InvariantCulture,
                    "{{\"attributes\":{{\"POSITION\":{0},\"NORMAL\":{1}{4}}},\"indices\":{2},\"material\":{3},\"mode\":4}}",
                    positionAccessor, normalAccessor, indexAccessor, materialIndex, uvAttr);
            }

            var nodes = new List<string>();
            var meshes = new List<string>();
            for (var i = 0; i < _buckets.Count; i++)
            {
                if (_buckets[i].Indices.Count > 0)
                {
                    primitives.Add(EmitPrimitive(_buckets[i], materialMap[i]));
                }
            }

            if (primitives.Count > 0)
            {
                nodes.Add("{\"name\":\"RevitModel\",\"mesh\":" + meshes.Count.ToString(CultureInfo.InvariantCulture) + "}");
                meshes.Add("{\"name\":\"RevitModel\",\"primitives\":[" + string.Join(",", primitives) + "]}");
            }

            foreach (var element in _elementNodes)
            {
                var elementPrimitives = new List<string>();
                foreach (var pair in element.ByMaterial)
                {
                    elementPrimitives.Add(EmitPrimitive(pair.Value, materialMap[pair.Key]));
                }

                foreach (var line in element.Lines)
                {
                    var lineView = WriteFloats(bin, line.Positions, bufferViews, 34962);
                    ComputeBounds(line.Positions, out var lmin, out var lmax);
                    accessors.Add(string.Format(CultureInfo.InvariantCulture,
                        "{{\"bufferView\":{0},\"componentType\":5126,\"count\":{1},\"type\":\"VEC3\",\"min\":[{2},{3},{4}],\"max\":[{5},{6},{7}]}}",
                        lineView, line.Positions.Count / 3, Num(lmin[0]), Num(lmin[1]), Num(lmin[2]), Num(lmax[0]), Num(lmax[1]), Num(lmax[2])));
                    elementPrimitives.Add(string.Format(CultureInfo.InvariantCulture,
                        "{{\"attributes\":{{\"POSITION\":{0}}},\"material\":{1},\"mode\":1}}",
                        accessors.Count - 1, materialMap[line.Material]));
                }

                var name = JsonString(element.Info.Name ?? ("Element " + element.Info.ElementId.ToString(CultureInfo.InvariantCulture)));
                nodes.Add(string.Format(CultureInfo.InvariantCulture,
                    "{{\"name\":{0},\"mesh\":{1},\"extras\":{{\"elementId\":{2},\"uniqueId\":{3},\"category\":{4},\"family\":{5},\"source\":{6}{7}}}}}",
                    name, meshes.Count, element.Info.ElementId,
                    JsonString(element.Info.UniqueId ?? string.Empty),
                    JsonString(element.Info.Category ?? string.Empty),
                    JsonString(element.Info.Family ?? string.Empty),
                    JsonString(element.Info.Source ?? "(host)"),
                    (element.Info.SourceTriangles > 0 ? ",\"sourceTriangles\":" + element.Info.SourceTriangles.ToString(CultureInfo.InvariantCulture) : string.Empty) +
                    (element.Info.Placeholder ? ",\"placeholder\":true" : string.Empty) +
                    DesignOptionJson(element.Info.DesignOption)));
                meshes.Add("{\"name\":" + name + ",\"primitives\":[" + string.Join(",", elementPrimitives) + "]}");
            }

            json.Append("{\"asset\":{\"version\":\"2.0\",\"generator\":\"RevitMCP walkthrough exporter\"},\"scene\":0");
            var sceneExtraFields = new List<string>();
            if (OriginOffsetMeters.HasValue)
            {
                sceneExtraFields.Add(string.Format(CultureInfo.InvariantCulture, "\"originOffsetMeters\":[{0},{1},{2}]",
                    Num(OriginOffsetMeters.Value.X), Num(OriginOffsetMeters.Value.Y), Num(OriginOffsetMeters.Value.Z)));
            }

            if (!string.IsNullOrEmpty(PartKey))
            {
                sceneExtraFields.Add("\"part\":{\"key\":" + JsonString(PartKey) + ",\"label\":" + JsonString(string.IsNullOrEmpty(PartLabel) ? PartKey : PartLabel) + "}");
            }

            var sceneExtras = sceneExtraFields.Count > 0
                ? ",\"extras\":{" + string.Join(",", sceneExtraFields) + "}"
                : string.Empty;
            if (nodes.Count == 0)
            {
                json.Append(",\"scenes\":[{\"nodes\":[]").Append(sceneExtras).Append("}]}");
            }
            else
            {
                var nodeIndices = new List<string>();
                for (var i = 0; i < nodes.Count; i++)
                {
                    nodeIndices.Add(i.ToString(CultureInfo.InvariantCulture));
                }

                json.Append(",\"scenes\":[{\"nodes\":[").Append(string.Join(",", nodeIndices)).Append(']').Append(sceneExtras).Append("}],\"nodes\":[").Append(string.Join(",", nodes)).Append(']');
                json.Append(",\"meshes\":[").Append(string.Join(",", meshes)).Append(']');
                if (_images.Count > 0)
                {
                    var images = new List<string>();
                    var textures = new List<string>();
                    for (var i = 0; i < _images.Count; i++)
                    {
                        var view = WriteBytes(bin, _images[i].Bytes, bufferViews);
                        images.Add(string.Format(CultureInfo.InvariantCulture, "{{\"bufferView\":{0},\"mimeType\":{1},\"name\":{2}}}",
                            view, JsonString(_images[i].MimeType), JsonString(_images[i].Name ?? "image" + i)));
                        textures.Add(string.Format(CultureInfo.InvariantCulture, "{{\"source\":{0},\"sampler\":0}}", i));
                    }

                    json.Append(",\"images\":[").Append(string.Join(",", images)).Append(']');
                    json.Append(",\"samplers\":[{\"magFilter\":9729,\"minFilter\":9987,\"wrapS\":10497,\"wrapT\":10497}]");
                    json.Append(",\"textures\":[").Append(string.Join(",", textures)).Append(']');
                }

                json.Append(",\"materials\":[").Append(string.Join(",", materials)).Append(']');
                json.Append(",\"accessors\":[").Append(string.Join(",", accessors)).Append(']');
                json.Append(",\"bufferViews\":[").Append(string.Join(",", bufferViews)).Append(']');
                json.Append(",\"buffers\":[{\"byteLength\":").Append(bin.Length.ToString(CultureInfo.InvariantCulture)).Append("}]}");
            }

            var jsonBytes = Encoding.UTF8.GetBytes(json.ToString());
            var jsonPadded = Pad4(jsonBytes.Length);
            var binBytes = bin.ToArray();
            var binPadded = Pad4(binBytes.Length);
            var hasBin = binBytes.Length > 0;
            var total = 12 + 8 + jsonPadded + (hasBin ? 8 + binPadded : 0);

            using (var output = new MemoryStream(total))
            using (var writer = new BinaryWriter(output))
            {
                writer.Write(0x46546C67u); // "glTF"
                writer.Write(2u);
                writer.Write((uint)total);
                writer.Write((uint)jsonPadded);
                writer.Write(0x4E4F534Au); // "JSON"
                writer.Write(jsonBytes);
                for (var i = jsonBytes.Length; i < jsonPadded; i++)
                {
                    writer.Write((byte)0x20);
                }

                if (hasBin)
                {
                    writer.Write((uint)binPadded);
                    writer.Write(0x004E4942u); // "BIN\0"
                    writer.Write(binBytes);
                    for (var i = binBytes.Length; i < binPadded; i++)
                    {
                        writer.Write((byte)0);
                    }
                }

                writer.Flush();
                return output.ToArray();
            }
        }

        private static Vec3[] ComputeVertexNormals(IReadOnlyList<Vec3> positions, IReadOnlyList<int> indices)
        {
            var normals = new Vec3[positions.Count];
            for (var i = 0; i < normals.Length; i++)
            {
                normals[i] = Vec3.Zero;
            }

            for (var i = 0; i < indices.Count; i += 3)
            {
                var a = positions[indices[i]];
                var b = positions[indices[i + 1]];
                var c = positions[indices[i + 2]];
                var faceNormal = (b - a).Cross(c - a); // length = 2x area (area weighting)
                normals[indices[i]] += faceNormal;
                normals[indices[i + 1]] += faceNormal;
                normals[indices[i + 2]] += faceNormal;
            }

            return normals;
        }

        private static int WriteFloats(MemoryStream bin, List<float> values, List<string> bufferViews, int target)
        {
            var offset = bin.Length;
            var bytes = new byte[values.Count * 4];
            Buffer.BlockCopy(values.ToArray(), 0, bytes, 0, bytes.Length);
            bin.Write(bytes, 0, bytes.Length);
            bufferViews.Add(string.Format(CultureInfo.InvariantCulture,
                "{{\"buffer\":0,\"byteOffset\":{0},\"byteLength\":{1},\"target\":{2}}}", offset, bytes.Length, target));
            return bufferViews.Count - 1;
        }

        private static int WriteBytes(MemoryStream bin, byte[] bytes, List<string> bufferViews)
        {
            while (bin.Length % 4 != 0)
            {
                bin.WriteByte(0);
            }

            var offset = bin.Length;
            bin.Write(bytes, 0, bytes.Length);
            bufferViews.Add(string.Format(CultureInfo.InvariantCulture,
                "{{\"buffer\":0,\"byteOffset\":{0},\"byteLength\":{1}}}", offset, bytes.Length));
            return bufferViews.Count - 1;
        }

        private static int WriteUInts(MemoryStream bin, List<uint> values, List<string> bufferViews, int target)
        {
            var offset = bin.Length;
            var bytes = new byte[values.Count * 4];
            Buffer.BlockCopy(values.ToArray(), 0, bytes, 0, bytes.Length);
            bin.Write(bytes, 0, bytes.Length);
            bufferViews.Add(string.Format(CultureInfo.InvariantCulture,
                "{{\"buffer\":0,\"byteOffset\":{0},\"byteLength\":{1},\"target\":{2}}}", offset, bytes.Length, target));
            return bufferViews.Count - 1;
        }

        private static void ComputeBounds(List<float> positions, out float[] min, out float[] max)
        {
            min = new[] { float.MaxValue, float.MaxValue, float.MaxValue };
            max = new[] { float.MinValue, float.MinValue, float.MinValue };
            for (var i = 0; i < positions.Count; i += 3)
            {
                for (var k = 0; k < 3; k++)
                {
                    var v = positions[i + k];
                    if (v < min[k]) min[k] = v;
                    if (v > max[k]) max[k] = v;
                }
            }
        }

        private static int Pad4(int length) => (length + 3) & ~3;

        /// <summary>sRGB transfer function inverse (0..1 to linear 0..1).</summary>
        public static double SrgbToLinear(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

        private static double Clamp01(double v) => double.IsNaN(v) ? 1.0 : Math.Max(0.0, Math.Min(1.0, v));

        private static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        private static string Num(float v) => v.ToString("R", CultureInfo.InvariantCulture);

        private static string DesignOptionJson(GltfDesignOptionInfo option)
        {
            if (option == null) return string.Empty;
            return ",\"designOption\":{\"setKey\":" + JsonString(option.SetKey) +
                ",\"setName\":" + JsonString(option.SetName) + ",\"optionKey\":" + JsonString(option.OptionKey) +
                ",\"optionName\":" + JsonString(option.OptionName) + ",\"isPrimary\":" +
                (option.IsPrimary ? "true" : "false") + "}";
        }

        private static string JsonString(string value)
        {
            var sb = new StringBuilder("\"");
            foreach (var ch in value)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 0x20)
                        {
                            sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(ch);
                        }

                        break;
                }
            }

            return sb.Append('"').ToString();
        }
    }
}
