using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

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
            LinkBeginCount = 0;
            _currentMaterial = _builder.AddMaterial("Default", 0.75, 0.75, 0.75, 1.0);
            return true;
        }

        public void Finish()
        {
        }

        public bool IsCanceled() => _cancelled;

        public RenderNodeAction OnViewBegin(ViewNode node) => RenderNodeAction.Proceed;

        public void OnViewEnd(ElementId elementId)
        {
        }

        public RenderNodeAction OnElementBegin(ElementId elementId)
        {
            if (TrackElements)
            {
                if (_elementDepth == 0)
                {
                    var info = ReadElementInfo(elementId);
                    _builder.BeginElement(info);
                    var key = _linkNames.Peek() + "|" + (info.Category ?? "(none)");
                    ElementCounts[key] = ElementCounts.TryGetValue(key, out var n) ? n + 1 : 1;
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
                    _builder.EndElement();
                }
            }
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
            _transforms.Push(_transforms.Peek().Multiply(node.GetTransform()));
            var linkDocument = node.GetDocument();
            _documents.Push(linkDocument ?? _documents.Peek());
            _linkNames.Push(linkDocument?.Title ?? "(link)");
            _linkStartTriangles.Push(_builder.TriangleCount);
            LinkBeginCount++;
            return RenderNodeAction.Proceed;
        }

        public void OnLinkEnd(LinkNode node)
        {
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

        public void OnMaterial(MaterialNode node)
        {
            double r = 0.75, g = 0.75, b = 0.75;
            var color = node.Color;
            if (color != null && color.IsValid)
            {
                r = color.Red / 255.0;
                g = color.Green / 255.0;
                b = color.Blue / 255.0;
            }

            var alpha = 1.0 - Math.Max(0.0, Math.Min(1.0, node.Transparency));
            var name = "Material";
            try
            {
                var material = node.MaterialId != null && node.MaterialId != ElementId.InvalidElementId
                    ? _documents.Peek().GetElement(node.MaterialId) as Material
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

            _currentMaterial = _builder.AddMaterial(name, r, g, b, alpha);
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

            if (distribution == DistributionOfNormals.OnEachFacet)
            {
                // Flat facets: duplicate vertices so each triangle keeps its own normal.
                var positions = new List<Vec3>(facetCount * 3);
                var normals = new List<Vec3>(facetCount * 3);
                var indices = new List<int>(facetCount * 3);
                for (var f = 0; f < facetCount; f++)
                {
                    var facet = facets[f];
                    var normal = ToGltfVector(transform.OfVector(node.GetNormal(f)));
                    foreach (var v in new[] { facet.V1, facet.V2, facet.V3 })
                    {
                        indices.Add(positions.Count);
                        positions.Add(ToGltfPoint(transform.OfPoint(points[v])));
                        normals.Add(normal);
                    }
                }

                _builder.AddTriangles(_currentMaterial, positions, normals, indices);
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

                _builder.AddTriangles(_currentMaterial, positions, normals, indices);
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

        private Vec3 ToGltfPoint(XYZ internalPoint)
        {
            var x = internalPoint.X * CameraPathMath.FeetToMeters - _originOffsetMeters.X;
            var y = internalPoint.Y * CameraPathMath.FeetToMeters - _originOffsetMeters.Y;
            var z = internalPoint.Z * CameraPathMath.FeetToMeters - _originOffsetMeters.Z;
            return new Vec3(x, z, -y);
        }

        private static Vec3 ToGltfVector(XYZ v) => new Vec3(v.X, v.Z, -v.Y);
    }
}
