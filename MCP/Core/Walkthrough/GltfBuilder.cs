using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace RevitMCP.Core.Walkthrough
{
    /// <summary>Per-element metadata written to glTF node.extras by <see cref="GltfBuilder.BeginElement"/>.</summary>
    public sealed class GltfElementInfo
    {
        public long ElementId;
        public string UniqueId;
        public string Category;
        public string Family;
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
            public readonly List<uint> Indices = new List<uint>();
        }

        private sealed class MaterialBucket : Geo
        {
            public string Name;
            public double R, G, B, A;
        }

        private sealed class ElementNode
        {
            public GltfElementInfo Info;
            public readonly SortedDictionary<int, Geo> ByMaterial = new SortedDictionary<int, Geo>();
        }

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
            if (_currentElementNode != null && _currentElementNode.ByMaterial.Count > 0)
            {
                _elementNodes.Add(_currentElementNode);
            }

            _currentElementNode = null;
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
            }

            return used;
        }

        /// <summary>Returns a material index; identical colors (after 8-bit quantization) share one material.</summary>
        public int AddMaterial(string name, double r, double g, double b, double alpha)
        {
            r = Clamp01(r); g = Clamp01(g); b = Clamp01(b); alpha = Clamp01(alpha);
            var key = string.Format(CultureInfo.InvariantCulture, "{0}-{1}-{2}-{3}",
                (int)Math.Round(r * 255), (int)Math.Round(g * 255), (int)Math.Round(b * 255), (int)Math.Round(alpha * 255));
            if (_materialKeys.TryGetValue(key, out var existing))
            {
                return existing;
            }

            _buckets.Add(new MaterialBucket { Name = string.IsNullOrWhiteSpace(name) ? "material_" + key : name, R = r, G = g, B = b, A = alpha });
            _materialKeys[key] = _buckets.Count - 1;
            return _buckets.Count - 1;
        }

        /// <summary>
        /// Adds an indexed triangle list. When <paramref name="normals"/> is null, area-weighted vertex normals
        /// are computed from the triangles.
        /// </summary>
        public void AddTriangles(int material, IReadOnlyList<Vec3> positions, IReadOnlyList<Vec3> normals, IReadOnlyList<int> indices)
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
            }

            foreach (var index in indices)
            {
                bucket.Indices.Add(baseIndex + (uint)index);
            }

            TriangleCount += indices.Count / 3;
            VertexCount += positions.Count;
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
                materials.Add(string.Format(CultureInfo.InvariantCulture,
                    "{{\"name\":{0},\"pbrMetallicRoughness\":{{\"baseColorFactor\":[{1},{2},{3},{4}],\"metallicFactor\":0,\"roughnessFactor\":0.9}},\"doubleSided\":true{5}}}",
                    JsonString(bucket.Name), Num(bucket.R), Num(bucket.G), Num(bucket.B), Num(bucket.A),
                    bucket.A < 0.999 ? ",\"alphaMode\":\"BLEND\"" : string.Empty));
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

                return string.Format(CultureInfo.InvariantCulture,
                    "{{\"attributes\":{{\"POSITION\":{0},\"NORMAL\":{1}}},\"indices\":{2},\"material\":{3},\"mode\":4}}",
                    positionAccessor, normalAccessor, indexAccessor, materialIndex);
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

                var name = JsonString(element.Info.Name ?? ("Element " + element.Info.ElementId.ToString(CultureInfo.InvariantCulture)));
                nodes.Add(string.Format(CultureInfo.InvariantCulture,
                    "{{\"name\":{0},\"mesh\":{1},\"extras\":{{\"elementId\":{2},\"uniqueId\":{3},\"category\":{4},\"family\":{5}}}}}",
                    name, meshes.Count, element.Info.ElementId,
                    JsonString(element.Info.UniqueId ?? string.Empty),
                    JsonString(element.Info.Category ?? string.Empty),
                    JsonString(element.Info.Family ?? string.Empty)));
                meshes.Add("{\"name\":" + name + ",\"primitives\":[" + string.Join(",", elementPrimitives) + "]}");
            }

            json.Append("{\"asset\":{\"version\":\"2.0\",\"generator\":\"RevitMCP walkthrough exporter\"},\"scene\":0");
            if (nodes.Count == 0)
            {
                json.Append(",\"scenes\":[{\"nodes\":[]}]}");
            }
            else
            {
                var nodeIndices = new List<string>();
                for (var i = 0; i < nodes.Count; i++)
                {
                    nodeIndices.Add(i.ToString(CultureInfo.InvariantCulture));
                }

                json.Append(",\"scenes\":[{\"nodes\":[").Append(string.Join(",", nodeIndices)).Append("]}],\"nodes\":[").Append(string.Join(",", nodes)).Append(']');
                json.Append(",\"meshes\":[").Append(string.Join(",", meshes)).Append(']');
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

        private static double Clamp01(double v) => double.IsNaN(v) ? 1.0 : Math.Max(0.0, Math.Min(1.0, v));

        private static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        private static string Num(float v) => v.ToString("R", CultureInfo.InvariantCulture);

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
