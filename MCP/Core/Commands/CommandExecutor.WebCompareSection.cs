using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitMCP.Core.Walkthrough;

namespace RevitMCP.Core
{
    /// <summary>
    /// export_web_compare, part 2: site-extent section box, link diagnostics and the linked-terrain fallback.
    /// Method notes: docs/WEB_COMPARE_EXPORT.md.
    /// </summary>
    public partial class CommandExecutor
    {
        private const double WebCompareFarMeters = 1.0e6;

        private sealed class WebCompareDoc
        {
            public Document Doc;
            public Transform Transform;
            public string Name;
            public long InstanceId;
        }

        private sealed class WebCompareTerrainPiece
        {
            public GltfElementInfo Info;
            public string MaterialName;
            public double R = 0.45, G = 0.55, B = 0.35;
            public List<Vec3[]> Triangles = new List<Vec3[]>();
        }

        private sealed class WebCompareSectionOptions
        {
            public string Mode = "auto";           // auto | manual | off
            public double MarginMeters = 10.0;
            public double BelowMeters = 2.0;
            public double AboveMeters = 5.0;
            public bool Apply = true;
            public string TerrainMode = "auto";    // auto | always | off
            public double[] ManualMin;             // Revit internal meters
            public double[] ManualMax;
        }

        private static WebCompareSectionOptions ParseWebCompareSectionOptions(JObject parameters)
        {
            var o = new WebCompareSectionOptions();
            if (parameters["sectionBox"] is JObject sb)
            {
                o.Mode = (sb["mode"]?.Value<string>() ?? o.Mode).ToLowerInvariant();
                o.MarginMeters = sb["marginMeters"]?.Value<double?>() ?? o.MarginMeters;
                o.BelowMeters = sb["belowMeters"]?.Value<double?>() ?? o.BelowMeters;
                o.AboveMeters = sb["aboveMeters"]?.Value<double?>() ?? o.AboveMeters;
                o.Apply = sb["apply"]?.Value<bool?>() ?? true;
                o.ManualMin = (sb["min"] as JArray)?.Select(t => t.Value<double>()).ToArray();
                o.ManualMax = (sb["max"] as JArray)?.Select(t => t.Value<double>()).ToArray();
            }
            else if (parameters["sectionBox"]?.Type == JTokenType.Boolean && parameters["sectionBox"].Value<bool>() == false)
            {
                o.Mode = "off";
            }

            o.TerrainMode = (parameters["linkedTerrain"]?.Value<string>() ?? o.TerrainMode).ToLowerInvariant();
            if (o.Mode != "auto" && o.Mode != "manual" && o.Mode != "off")
            {
                throw new ArgumentException("sectionBox.mode 只能是 auto、manual 或 off。");
            }

            if (o.TerrainMode != "auto" && o.TerrainMode != "always" && o.TerrainMode != "off")
            {
                throw new ArgumentException("linkedTerrain 只能是 auto、always 或 off。");
            }

            if (o.Mode == "manual" && (o.ManualMin == null || o.ManualMax == null || o.ManualMin.Length < 2 || o.ManualMax.Length < 2))
            {
                throw new ArgumentException("sectionBox.mode=manual 需要 min 與 max（Revit 內部座標公尺，至少 [x, y]，可加 z）。");
            }

            return o;
        }

        private static List<WebCompareDoc> WebCompareDocuments(Document host)
        {
            var list = new List<WebCompareDoc>
            {
                new WebCompareDoc { Doc = host, Transform = Transform.Identity, Name = RevitGltfExportContext.HostName, InstanceId = 0 }
            };
            foreach (var inst in new FilteredElementCollector(host).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                var linked = inst.GetLinkDocument();
                if (linked == null)
                {
                    continue;
                }

                list.Add(new WebCompareDoc { Doc = linked, Transform = inst.GetTotalTransform(), Name = linked.Title, InstanceId = inst.Id.GetIdValue() });
            }

            return list;
        }

        private static IEnumerable<Element> WebCompareElements(Document doc, params BuiltInCategory[] categories)
        {
            var filter = new ElementMulticategoryFilter(categories.ToList());
            return new FilteredElementCollector(doc).WherePasses(filter).WhereElementIsNotElementType().ToList();
        }

        private static IEnumerable<Vec3> WebCompareElementPointsMeters(Element e, Transform t)
        {
            const double toM = CameraPathMath.FeetToMeters;
            var pts = new List<XYZ>();
            try
            {
                var bb = e.get_BoundingBox(null);
                if (bb != null)
                {
                    foreach (var x in new[] { bb.Min.X, bb.Max.X })
                    {
                        foreach (var y in new[] { bb.Min.Y, bb.Max.Y })
                        {
                            foreach (var z in new[] { bb.Min.Z, bb.Max.Z })
                            {
                                pts.Add(bb.Transform.OfPoint(new XYZ(x, y, z)));
                            }
                        }
                    }
                }
                else
                {
                    var curve = (e as CurveElement)?.GeometryCurve ?? (e.Location as LocationCurve)?.Curve;
                    if (curve != null)
                    {
                        pts.AddRange(curve.Tessellate());
                    }
                }
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // Element without usable geometry: skip.
            }

            return pts.Select(p =>
            {
                var q = t.OfPoint(p);
                return new Vec3(q.X * toM, q.Y * toM, q.Z * toM);
            }).ToList();
        }

        private static bool WebCompareBounds(IEnumerable<Element> elements, Transform t, out MeterBox box) =>
            WebCompareMath.TryBounds(elements.SelectMany(e => WebCompareElementPointsMeters(e, t)), out box);

        private static JArray BoxArray(double x, double y, double z) =>
            new JArray(Math.Round(x, 3), Math.Round(y, 3), Math.Round(z, 3));

        private static JObject BoxJson(MeterBox b) => new JObject
        {
            ["min"] = BoxArray(b.MinX, b.MinY, b.MinZ),
            ["max"] = BoxArray(b.MaxX, b.MaxY, b.MaxZ)
        };

        /// <summary>
        /// Resolves the section box (Revit internal meters) from property lines (preferred) or the building footprint,
        /// then Z from building and terrain. <paramref name="report"/> documents every candidate so the choice is auditable.
        /// </summary>
        private static MeterBox? ResolveWebCompareSectionBox(List<WebCompareDoc> docs, WebCompareSectionOptions opt,
            List<WebCompareTerrainPiece> terrain, List<string> warnings, JObject report)
        {
            if (opt.Mode == "off")
            {
                report["source"] = "off";
                return null;
            }

            MeterBox? property = null, building = null;
            var propertyCount = 0;
            foreach (var d in docs)
            {
                var lines = WebCompareElements(d.Doc, BuiltInCategory.OST_SiteProperty, BuiltInCategory.OST_SitePropertyLineSegment).ToList();
                propertyCount += lines.Count;
                if (WebCompareBounds(lines, d.Transform, out var pb))
                {
                    property = property.HasValue ? property.Value.Union(pb) : pb;
                }

                var bldg = WebCompareElements(d.Doc, BuiltInCategory.OST_Walls, BuiltInCategory.OST_Floors, BuiltInCategory.OST_Roofs,
                    BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_Columns, BuiltInCategory.OST_StructuralFraming,
                    BuiltInCategory.OST_StructuralFoundation, BuiltInCategory.OST_Stairs).ToList();
                if (WebCompareBounds(bldg, d.Transform, out var bb))
                {
                    building = building.HasValue ? building.Value.Union(bb) : bb;
                }
            }

            report["propertyLineElements"] = propertyCount;
            if (property.HasValue)
            {
                report["propertyLineBounds"] = BoxJson(property.Value);
            }

            if (building.HasValue)
            {
                report["buildingBounds"] = BoxJson(building.Value);
            }

            double minX, minY, maxX, maxY;
            if (opt.Mode == "manual")
            {
                minX = opt.ManualMin[0]; minY = opt.ManualMin[1]; maxX = opt.ManualMax[0]; maxY = opt.ManualMax[1];
                report["source"] = "manual";
            }
            else if (property.HasValue && property.Value.MaxX > property.Value.MinX && property.Value.MaxY > property.Value.MinY)
            {
                var e = property.Value.Expand(opt.MarginMeters, 0, 0);
                minX = e.MinX; minY = e.MinY; maxX = e.MaxX; maxY = e.MaxY;
                report["source"] = "propertyLines";
            }
            else if (building.HasValue)
            {
                var e = building.Value.Expand(opt.MarginMeters, 0, 0);
                minX = e.MinX; minY = e.MinY; maxX = e.MaxX; maxY = e.MaxY;
                report["source"] = "buildingFootprint";
                warnings.Add("找不到基地線（property line），section box 的 XY 範圍改由建築範圍推定。");
            }
            else
            {
                report["source"] = "none";
                warnings.Add("找不到基地線或建築範圍，未設定 section box。");
                return null;
            }

            // Terrain surface Z inside the XY window (top faces only) + building Z → box Z.
            var xy = new MeterBox(minX, minY, -WebCompareFarMeters, maxX, maxY, WebCompareFarMeters);
            var terrainInside = CollectLinkedTerrain(docs, xy, int.MaxValue, warnings, out _);
            terrain.AddRange(terrainInside);
            double? tMin = null, tMax = null;
            foreach (var piece in terrainInside)
            {
                foreach (var tri in piece.Triangles)
                {
                    if ((tri[1] - tri[0]).Cross(tri[2] - tri[0]).Normalized().Z < 0.3)
                    {
                        continue;
                    }

                    foreach (var p in tri)
                    {
                        tMin = tMin.HasValue ? Math.Min(tMin.Value, p.Z) : p.Z;
                        tMax = tMax.HasValue ? Math.Max(tMax.Value, p.Z) : p.Z;
                    }
                }
            }

            var zLo = new List<double>();
            var zHi = new List<double>();
            if (building.HasValue) { zLo.Add(building.Value.MinZ); zHi.Add(building.Value.MaxZ); }
            if (tMin.HasValue) { zLo.Add(tMin.Value); zHi.Add(tMax.Value); }
            report["terrainSurfaceZ"] = tMin.HasValue ? (JToken)new JArray(Math.Round(tMin.Value, 3), Math.Round(tMax.Value, 3)) : JValue.CreateNull();
            double minZ, maxZ;
            if (opt.Mode == "manual" && opt.ManualMin.Length >= 3 && opt.ManualMax.Length >= 3)
            {
                minZ = opt.ManualMin[2]; maxZ = opt.ManualMax[2];
            }
            else if (zLo.Count > 0)
            {
                minZ = zLo.Min() - opt.BelowMeters;
                maxZ = zHi.Max() + opt.AboveMeters;
            }
            else
            {
                warnings.Add("無法由建築或地形算出 Z 範圍，section box 的 Z 由 XY 範圍高度估計，請檢查。");
                minZ = -50; maxZ = 150;
            }

            var box = new MeterBox(minX, minY, minZ, maxX, maxY, maxZ);
            if (!box.IsValid)
            {
                warnings.Add("算出的 section box 無效（範圍為零或反向），未套用。");
                return null;
            }

            report["marginMeters"] = opt.MarginMeters;
            report["belowMeters"] = opt.BelowMeters;
            report["aboveMeters"] = opt.AboveMeters;
            report["box"] = BoxJson(box);
            report["sizeMeters"] = BoxArray(box.MaxX - box.MinX, box.MaxY - box.MinY, box.MaxZ - box.MinZ);
            return box;
        }

        /// <summary>Puts the view camera at eye/forward/up (Revit internal meters, Z up) with a symmetric perspective frustum.</summary>
        private static void ApplyWebCompareCamera(Document doc, View3D view, JObject cam)
        {
            const double toFt = 1.0 / CameraPathMath.FeetToMeters;
            if (!view.IsPerspective)
            {
                throw new InvalidOperationException("camera 參數只支援透視視圖。");
            }

            Vec3 V(string key)
            {
                var a = cam[key] as JArray ?? throw new ArgumentException($"camera.{key} 需要 [x, y, z]（Revit 內部座標公尺）。");
                return new Vec3(a[0].Value<double>(), a[1].Value<double>(), a[2].Value<double>());
            }

            var eye = V("eye");
            var forward = V("forward").Normalized();
            // Revit rejects a slightly non-orthogonal basis ("view basis is corrupted"), e.g. from 4-decimal rounded vectors.
            var up = V("up");
            up = (up - forward * up.Dot(forward)).Normalized();
            var fov = cam["fovVerticalDeg"]?.Value<double?>() ?? 40.0;
            var aspect = cam["aspect"]?.Value<double?>() ?? 1600.0 / 1095.0;
            var nearFeet = (cam["nearMeters"]?.Value<double?>() ?? 0.3048) * toFt;
            var farFeet = (cam["farMeters"]?.Value<double?>() ?? 6096.0) * toFt;
            var crop = CameraPathMath.CropBoxFromFieldOfView(fov, aspect, nearFeet, farFeet);
            using (var tx = new Transaction(doc, "WEB_COMPARE：設定相機"))
            {
                tx.Start();
                if (view.IsLocked)
                {
                    view.Unlock();
                }

                view.SetOrientation(new ViewOrientation3D(
                    new XYZ(eye.X * toFt, eye.Y * toFt, eye.Z * toFt), new XYZ(up.X, up.Y, up.Z), new XYZ(forward.X, forward.Y, forward.Z)));
                // Setting a crop box from a field of view (setCrop=true) made Revit render an all-white image in this
                // project (the pyramid values are not what Revit expects), so the default only moves the eye/target.
                if (cam["setCrop"]?.Value<bool?>() ?? false)
                {
                    var cropBox = view.CropBox;
                    cropBox.Min = new XYZ(crop.Min.X, crop.Min.Y, crop.Min.Z);
                    cropBox.Max = new XYZ(crop.Max.X, crop.Max.Y, crop.Max.Z);
                    view.CropBox = cropBox;
                }
                tx.Commit();
            }
        }

        private static void ApplyWebCompareSectionBox(Document doc, View3D view, MeterBox box)
        {
            const double toFt = 1.0 / CameraPathMath.FeetToMeters;
            using (var tx = new Transaction(doc, "WEB_COMPARE：設定 section box"))
            {
                tx.Start();
                var bb = new BoundingBoxXYZ
                {
                    Transform = Transform.Identity,
                    Min = new XYZ(box.MinX * toFt, box.MinY * toFt, box.MinZ * toFt),
                    Max = new XYZ(box.MaxX * toFt, box.MaxY * toFt, box.MaxZ * toFt)
                };
                bb.set_MinEnabled(0, true); bb.set_MinEnabled(1, true); bb.set_MinEnabled(2, true);
                bb.set_MaxEnabled(0, true); bb.set_MaxEnabled(1, true); bb.set_MaxEnabled(2, true);
                view.IsSectionBoxActive = true;
                view.SetSectionBox(bb);
                tx.Commit();
            }
        }

        private static JObject DescribeWebCompareLinks(Document host, View3D view, List<WebCompareDoc> docs, JObject report)
        {
            var links = new JArray();
            foreach (var inst in new FilteredElementCollector(host).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                var entry = new JObject
                {
                    ["instanceId"] = inst.Id.GetIdValue(),
                    ["name"] = inst.Name,
                    ["loaded"] = inst.GetLinkDocument() != null
                };
                try
                {
                    // GetLinkOverrides returns null when the link has no per-view override (= follows the host view).
                    entry["visibilityInView"] = view.GetLinkOverrides(inst.Id)?.LinkVisibilityType.ToString() ?? "ByHostView (no override)";
                }
                catch (Exception ex)
                {
                    entry["visibilityInView"] = "unknown: " + ex.Message;
                }

                links.Add(entry);
            }

            report["links"] = links;
            var hidden = new JObject();
            foreach (var cat in new[] { BuiltInCategory.OST_RvtLinks, BuiltInCategory.OST_Toposolid, BuiltInCategory.OST_Topography })
            {
                try
                {
                    hidden[cat.ToString()] = view.GetCategoryHidden(new ElementId(cat));
                }
                catch (Exception)
                {
                    hidden[cat.ToString()] = JValue.CreateNull();
                }
            }

            report["categoryHiddenInView"] = hidden;
            return report;
        }

        // ── linked terrain fallback ────────────────────

        /// <summary>
        /// Triangulates Toposolid / Topography of every loaded link (host terrain is exported by CustomExporter itself),
        /// transforms to host model meters and clips to <paramref name="clip"/>. Bottom faces are skipped.
        /// </summary>
        private static List<WebCompareTerrainPiece> CollectLinkedTerrain(List<WebCompareDoc> docs, MeterBox clip, int maxTriangles,
            List<string> warnings, out int triangles)
        {
            const double toM = CameraPathMath.FeetToMeters;
            var pieces = new List<WebCompareTerrainPiece>();
            triangles = 0;
            var options = new Options { DetailLevel = ViewDetailLevel.Fine, ComputeReferences = false, IncludeNonVisibleObjects = false };
            foreach (var d in docs.Where(x => x.InstanceId != 0))
            {
                foreach (var e in WebCompareElements(d.Doc, BuiltInCategory.OST_Toposolid, BuiltInCategory.OST_Topography))
                {
                    var piece = new WebCompareTerrainPiece
                    {
                        Info = new GltfElementInfo
                        {
                            ElementId = e.Id.GetIdValue(),
                            UniqueId = e.UniqueId,
                            Category = e.Category?.Name ?? "Toposolid",
                            Family = d.Name,
                            Name = e.Name
                        },
                        MaterialName = "Terrain"
                    };
                    try
                    {
                        var geometry = e.get_Geometry(options);
                        if (geometry != null)
                        {
                            AddTerrainGeometry(geometry, d, clip, piece, ref triangles, maxTriangles, toM);
                        }
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                    {
                        warnings.Add($"連結地形 {e.Id.GetIdValue()}（{d.Name}）取得幾何失敗：{ex.Message}");
                    }

                    if (piece.Triangles.Count > 0)
                    {
                        pieces.Add(piece);
                    }

                    if (triangles >= maxTriangles)
                    {
                        warnings.Add("連結地形三角面達上限，後續地形元素略過。");
                        return pieces;
                    }
                }
            }

            return pieces;
        }

        private static void AddTerrainGeometry(IEnumerable<GeometryObject> objects, WebCompareDoc d, MeterBox clip,
            WebCompareTerrainPiece piece, ref int triangles, int maxTriangles, double toM)
        {
            foreach (var obj in objects)
            {
                if (obj is GeometryInstance gi)
                {
                    AddTerrainGeometry(gi.GetInstanceGeometry(), d, clip, piece, ref triangles, maxTriangles, toM);
                    continue;
                }

                if (!(obj is Solid solid) || solid.Faces.Size == 0)
                {
                    continue;
                }

                foreach (Face face in solid.Faces)
                {
                    var planarNormal = (face as PlanarFace)?.FaceNormal;
                    if (planarNormal != null && d.Transform.OfVector(planarNormal).Z < -0.7)
                    {
                        continue; // underside
                    }

                    var mesh = face.Triangulate();
                    if (mesh == null)
                    {
                        continue;
                    }

                    if (piece.MaterialName == "Terrain" && face.MaterialElementId != ElementId.InvalidElementId
                        && d.Doc.GetElement(face.MaterialElementId) is Material m && m.Color != null && m.Color.IsValid)
                    {
                        piece.MaterialName = m.Name;
                        piece.R = m.Color.Red / 255.0; piece.G = m.Color.Green / 255.0; piece.B = m.Color.Blue / 255.0;
                    }

                    for (var i = 0; i < mesh.NumTriangles; i++)
                    {
                        var tri = mesh.get_Triangle(i);
                        var v = new Vec3[3];
                        for (var k = 0; k < 3; k++)
                        {
                            var q = d.Transform.OfPoint(tri.get_Vertex(k));
                            v[k] = new Vec3(q.X * toM, q.Y * toM, q.Z * toM);
                        }

                        if (planarNormal != null)
                        {
                            var n = d.Transform.OfVector(planarNormal);
                            if ((v[1] - v[0]).Cross(v[2] - v[0]).Dot(new Vec3(n.X, n.Y, n.Z)) < 0)
                            {
                                var tmp = v[1]; v[1] = v[2]; v[2] = tmp;
                            }
                        }

                        foreach (var clipped in WebCompareMath.ClipTriangle(v[0], v[1], v[2], clip))
                        {
                            piece.Triangles.Add(clipped);
                            triangles++;
                        }

                        if (triangles >= maxTriangles)
                        {
                            return;
                        }
                    }
                }
            }
        }

        /// <summary>Re-clips the pieces to <paramref name="box"/> and writes them as one glTF node per terrain element.</summary>
        private static int AddTerrainPiecesToBuilder(GltfBuilder builder, List<WebCompareTerrainPiece> pieces, MeterBox box, Vec3 offset)
        {
            var added = 0;
            foreach (var piece in pieces)
            {
                var positions = new List<Vec3>();
                var normals = new List<Vec3>();
                var indices = new List<int>();
                foreach (var tri in piece.Triangles)
                {
                    foreach (var c in WebCompareMath.ClipTriangle(tri[0], tri[1], tri[2], box))
                    {
                        var n = (c[1] - c[0]).Cross(c[2] - c[0]).Normalized();
                        var gn = new Vec3(n.X, n.Z, -n.Y);
                        foreach (var p in c)
                        {
                            indices.Add(positions.Count);
                            positions.Add(new Vec3(p.X - offset.X, p.Z - offset.Z, -(p.Y - offset.Y)));
                            normals.Add(gn);
                        }
                    }
                }

                if (indices.Count == 0)
                {
                    continue;
                }

                builder.BeginElement(piece.Info);
                var material = builder.AddMaterial(piece.MaterialName, piece.R, piece.G, piece.B, 1.0);
                builder.AddTriangles(material, positions, normals, indices);
                builder.EndElement();
                added += indices.Count / 3;
            }

            return added;
        }
    }
}
