using System;
using System.Collections.Generic;

namespace RevitMCP.Core.Walkthrough
{
    /// <summary>
    /// Quadric-error edge-collapse mesh simplification (Garland–Heckbert), following the iterative threshold scheme of
    /// Sven Forstmann's "Fast-Quadric-Mesh-Simplification" (MIT). Used by the web-compare GLB export to shrink
    /// over-heavy elements (statues, carved furniture) instead of dropping them. Pure math, no Revit API.
    /// Input/output are welded indexed triangles; open borders are kept in place.
    /// </summary>
    public static class MeshSimplifier
    {
        private struct Quadric
        {
            public double A00, A01, A02, A03, A11, A12, A13, A22, A23, A33;

            public static Quadric FromPlane(double a, double b, double c, double d) => new Quadric
            {
                A00 = a * a, A01 = a * b, A02 = a * c, A03 = a * d,
                A11 = b * b, A12 = b * c, A13 = b * d,
                A22 = c * c, A23 = c * d, A33 = d * d
            };

            public void Add(in Quadric q)
            {
                A00 += q.A00; A01 += q.A01; A02 += q.A02; A03 += q.A03;
                A11 += q.A11; A12 += q.A12; A13 += q.A13;
                A22 += q.A22; A23 += q.A23; A33 += q.A33;
            }

            public double Error(double x, double y, double z) =>
                A00 * x * x + 2 * A01 * x * y + 2 * A02 * x * z + 2 * A03 * x +
                A11 * y * y + 2 * A12 * y * z + 2 * A13 * y +
                A22 * z * z + 2 * A23 * z + A33;
        }

        private struct Tri
        {
            public int V0, V1, V2;
            public double E0, E1, E2, E3;
            public bool Deleted, Dirty;
            public double Nx, Ny, Nz;

            public int this[int i]
            {
                get => i == 0 ? V0 : i == 1 ? V1 : V2;
                set { if (i == 0) V0 = value; else if (i == 1) V1 = value; else V2 = value; }
            }

            public double Err(int i) => i == 0 ? E0 : i == 1 ? E1 : E2;
        }

        private struct Vert
        {
            public double X, Y, Z;
            public int TStart, TCount;
            public Quadric Q;
            public bool Border;
        }

        private struct Ref
        {
            public int Tid, TVertex;
        }

        /// <summary>
        /// Simplifies a welded triangle mesh toward <paramref name="targetTriangles"/>.
        /// <paramref name="positions"/> is xyz-interleaved; <paramref name="indices"/> a triangle list.
        /// Returns the kept vertices (as indices into the input, so callers can carry attributes) and the new triangles.
        /// </summary>
        public static void Simplify(IReadOnlyList<double> positions, IReadOnlyList<int> indices, int targetTriangles,
            out List<double> outPositions, out List<int> outIndices, out List<int> sourceVertex, double aggressiveness = 7)
        {
            var verts = new Vert[positions.Count / 3];
            for (var i = 0; i < verts.Length; i++)
            {
                verts[i].X = positions[i * 3];
                verts[i].Y = positions[i * 3 + 1];
                verts[i].Z = positions[i * 3 + 2];
            }

            var tris = new List<Tri>(indices.Count / 3);
            for (var i = 0; i + 2 < indices.Count; i += 3)
            {
                if (indices[i] == indices[i + 1] || indices[i + 1] == indices[i + 2] || indices[i] == indices[i + 2])
                {
                    continue;
                }

                tris.Add(new Tri { V0 = indices[i], V1 = indices[i + 1], V2 = indices[i + 2] });
            }

            var state = new State { Verts = verts, Tris = tris };
            state.Run(Math.Max(4, targetTriangles), aggressiveness);
            state.Compact(out outPositions, out outIndices, out sourceVertex);
        }

        private sealed class State
        {
            public Vert[] Verts;
            public List<Tri> Tris;
            private readonly List<Ref> _refs = new List<Ref>();

            public void Run(int target, double aggressiveness)
            {
                var deletedTriangles = 0;
                var deleted0 = new List<bool>();
                var deleted1 = new List<bool>();
                var triangleCount = Tris.Count;
                for (var iteration = 0; iteration < 100; iteration++)
                {
                    if (triangleCount - deletedTriangles <= target)
                    {
                        break;
                    }

                    if (iteration % 5 == 0)
                    {
                        UpdateMesh(iteration);
                        triangleCount = Tris.Count;
                        deletedTriangles = 0;
                    }

                    for (var i = 0; i < Tris.Count; i++)
                    {
                        var t = Tris[i];
                        t.Dirty = false;
                        Tris[i] = t;
                    }

                    // Error threshold grows each pass so cheap collapses go first.
                    var threshold = 0.000000001 * Math.Pow(iteration + 3, aggressiveness);
                    for (var i = 0; i < Tris.Count; i++)
                    {
                        var t = Tris[i];
                        if (t.E3 > threshold || t.Deleted || t.Dirty)
                        {
                            continue;
                        }

                        for (var j = 0; j < 3; j++)
                        {
                            if (t.Err(j) >= threshold)
                            {
                                continue;
                            }

                            var i0 = t[j];
                            var i1 = t[(j + 1) % 3];
                            if (Verts[i0].Border != Verts[i1].Border)
                            {
                                continue;
                            }

                            CalculateError(i0, i1, out var px, out var py, out var pz);
                            Resize(deleted0, Verts[i0].TCount);
                            Resize(deleted1, Verts[i1].TCount);
                            if (Flipped(px, py, pz, i1, i0, deleted0) || Flipped(px, py, pz, i0, i1, deleted1))
                            {
                                continue;
                            }

                            Verts[i0].X = px; Verts[i0].Y = py; Verts[i0].Z = pz;
                            Verts[i0].Q.Add(Verts[i1].Q);
                            var tstart = _refs.Count;
                            UpdateTriangles(i0, i0, deleted0, ref deletedTriangles);
                            UpdateTriangles(i0, i1, deleted1, ref deletedTriangles);
                            var tcount = _refs.Count - tstart;
                            if (tcount <= Verts[i0].TCount)
                            {
                                for (var k = 0; k < tcount; k++)
                                {
                                    _refs[Verts[i0].TStart + k] = _refs[tstart + k];
                                }

                                _refs.RemoveRange(tstart, tcount);
                            }
                            else
                            {
                                Verts[i0].TStart = tstart;
                            }

                            Verts[i0].TCount = tcount;
                            break;
                        }

                        if (triangleCount - deletedTriangles <= target)
                        {
                            break;
                        }
                    }
                }
            }

            private static void Resize(List<bool> list, int count)
            {
                list.Clear();
                for (var i = 0; i < count; i++)
                {
                    list.Add(false);
                }
            }

            /// <summary>True when moving <paramref name="i0"/> to p would flip or degenerate one of its triangles.</summary>
            private bool Flipped(double px, double py, double pz, int i1, int i0, List<bool> deleted)
            {
                var v0 = Verts[i0];
                for (var k = 0; k < v0.TCount; k++)
                {
                    var r = _refs[v0.TStart + k];
                    var t = Tris[r.Tid];
                    if (t.Deleted)
                    {
                        continue;
                    }

                    var id1 = t[(r.TVertex + 1) % 3];
                    var id2 = t[(r.TVertex + 2) % 3];
                    if (id1 == i1 || id2 == i1)
                    {
                        deleted[k] = true;
                        continue;
                    }

                    Normalize(Verts[id1].X - px, Verts[id1].Y - py, Verts[id1].Z - pz, out var d1x, out var d1y, out var d1z);
                    Normalize(Verts[id2].X - px, Verts[id2].Y - py, Verts[id2].Z - pz, out var d2x, out var d2y, out var d2z);
                    if (Math.Abs(d1x * d2x + d1y * d2y + d1z * d2z) > 0.999)
                    {
                        return true;
                    }

                    Normalize(d1y * d2z - d1z * d2y, d1z * d2x - d1x * d2z, d1x * d2y - d1y * d2x, out var nx, out var ny, out var nz);
                    deleted[k] = false;
                    if (nx * t.Nx + ny * t.Ny + nz * t.Nz < 0.2)
                    {
                        return true;
                    }
                }

                return false;
            }

            private void UpdateTriangles(int i0, int vi, List<bool> deleted, ref int deletedTriangles)
            {
                var v = Verts[vi];
                for (var k = 0; k < v.TCount; k++)
                {
                    var r = _refs[v.TStart + k];
                    var t = Tris[r.Tid];
                    if (t.Deleted)
                    {
                        continue;
                    }

                    if (deleted[k])
                    {
                        t.Deleted = true;
                        Tris[r.Tid] = t;
                        deletedTriangles++;
                        continue;
                    }

                    t[r.TVertex] = i0;
                    t.Dirty = true;
                    t.E0 = CalculateError(t.V0, t.V1, out _, out _, out _);
                    t.E1 = CalculateError(t.V1, t.V2, out _, out _, out _);
                    t.E2 = CalculateError(t.V2, t.V0, out _, out _, out _);
                    t.E3 = Math.Min(t.E0, Math.Min(t.E1, t.E2));
                    Tris[r.Tid] = t;
                    _refs.Add(r);
                }
            }

            private void UpdateMesh(int iteration)
            {
                if (iteration > 0)
                {
                    Tris.RemoveAll(t => t.Deleted);
                }

                if (iteration == 0)
                {
                    for (var i = 0; i < Verts.Length; i++)
                    {
                        Verts[i].Q = default;
                    }

                    for (var i = 0; i < Tris.Count; i++)
                    {
                        var t = Tris[i];
                        var p0 = Verts[t.V0];
                        var p1 = Verts[t.V1];
                        var p2 = Verts[t.V2];
                        double ax = p1.X - p0.X, ay = p1.Y - p0.Y, az = p1.Z - p0.Z;
                        double bx = p2.X - p0.X, by = p2.Y - p0.Y, bz = p2.Z - p0.Z;
                        Normalize(ay * bz - az * by, az * bx - ax * bz, ax * by - ay * bx, out var nx, out var ny, out var nz);
                        t.Nx = nx; t.Ny = ny; t.Nz = nz;
                        Tris[i] = t;
                        var q = Quadric.FromPlane(nx, ny, nz, -(nx * p0.X + ny * p0.Y + nz * p0.Z));
                        Verts[t.V0].Q.Add(q);
                        Verts[t.V1].Q.Add(q);
                        Verts[t.V2].Q.Add(q);
                    }

                    for (var i = 0; i < Tris.Count; i++)
                    {
                        var t = Tris[i];
                        t.E0 = CalculateError(t.V0, t.V1, out _, out _, out _);
                        t.E1 = CalculateError(t.V1, t.V2, out _, out _, out _);
                        t.E2 = CalculateError(t.V2, t.V0, out _, out _, out _);
                        t.E3 = Math.Min(t.E0, Math.Min(t.E1, t.E2));
                        Tris[i] = t;
                    }
                }

                for (var i = 0; i < Verts.Length; i++)
                {
                    Verts[i].TStart = 0;
                    Verts[i].TCount = 0;
                }

                foreach (var t in Tris)
                {
                    Verts[t.V0].TCount++;
                    Verts[t.V1].TCount++;
                    Verts[t.V2].TCount++;
                }

                var tstart = 0;
                for (var i = 0; i < Verts.Length; i++)
                {
                    Verts[i].TStart = tstart;
                    tstart += Verts[i].TCount;
                    Verts[i].TCount = 0;
                }

                _refs.Clear();
                for (var i = 0; i < Tris.Count * 3; i++)
                {
                    _refs.Add(default);
                }

                for (var i = 0; i < Tris.Count; i++)
                {
                    var t = Tris[i];
                    for (var j = 0; j < 3; j++)
                    {
                        var vi = t[j];
                        _refs[Verts[vi].TStart + Verts[vi].TCount] = new Ref { Tid = i, TVertex = j };
                        Verts[vi].TCount++;
                    }
                }

                if (iteration == 0)
                {
                    // A vertex is on an open border when one of its fan neighbours appears in only one fan triangle.
                    var counts = new Dictionary<int, int>();
                    for (var i = 0; i < Verts.Length; i++)
                    {
                        Verts[i].Border = false;
                    }

                    for (var i = 0; i < Verts.Length; i++)
                    {
                        counts.Clear();
                        for (var j = 0; j < Verts[i].TCount; j++)
                        {
                            var t = Tris[_refs[Verts[i].TStart + j].Tid];
                            for (var k = 0; k < 3; k++)
                            {
                                var id = t[k];
                                counts[id] = counts.TryGetValue(id, out var c) ? c + 1 : 1;
                            }
                        }

                        foreach (var kv in counts)
                        {
                            if (kv.Value == 1)
                            {
                                Verts[kv.Key].Border = true;
                            }
                        }
                    }
                }
            }

            private double CalculateError(int a, int b, out double px, out double py, out double pz)
            {
                var q = Verts[a].Q;
                q.Add(Verts[b].Q);
                var border = Verts[a].Border && Verts[b].Border;
                var det = Det3(q.A00, q.A01, q.A02, q.A01, q.A11, q.A12, q.A02, q.A12, q.A22);
                if (Math.Abs(det) > 1e-15 && !border)
                {
                    px = -1 / det * Det3(q.A01, q.A02, q.A03, q.A11, q.A12, q.A13, q.A12, q.A22, q.A23);
                    py = 1 / det * Det3(q.A00, q.A02, q.A03, q.A01, q.A12, q.A13, q.A02, q.A22, q.A23);
                    pz = -1 / det * Det3(q.A00, q.A01, q.A03, q.A01, q.A11, q.A13, q.A02, q.A12, q.A23);
                    return q.Error(px, py, pz);
                }

                var va = Verts[a];
                var vb = Verts[b];
                double mx = (va.X + vb.X) / 2, my = (va.Y + vb.Y) / 2, mz = (va.Z + vb.Z) / 2;
                var e1 = q.Error(va.X, va.Y, va.Z);
                var e2 = q.Error(vb.X, vb.Y, vb.Z);
                var e3 = q.Error(mx, my, mz);
                var error = Math.Min(e1, Math.Min(e2, e3));
                if (error == e1) { px = va.X; py = va.Y; pz = va.Z; }
                else if (error == e2) { px = vb.X; py = vb.Y; pz = vb.Z; }
                else { px = mx; py = my; pz = mz; }
                return error;
            }

            // Rows (a,b,c),(d,e,f),(g,h,i).
            private static double Det3(double a, double b, double c, double d, double e, double f, double g, double h, double i)
                => a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);

            public void Compact(out List<double> positions, out List<int> indices, out List<int> sourceVertex)
            {
                var remap = new int[Verts.Length];
                for (var i = 0; i < remap.Length; i++)
                {
                    remap[i] = -1;
                }

                positions = new List<double>();
                indices = new List<int>();
                sourceVertex = new List<int>();
                foreach (var t in Tris)
                {
                    if (t.Deleted || t.V0 == t.V1 || t.V1 == t.V2 || t.V0 == t.V2)
                    {
                        continue;
                    }

                    for (var j = 0; j < 3; j++)
                    {
                        var vi = t[j];
                        if (remap[vi] < 0)
                        {
                            remap[vi] = sourceVertex.Count;
                            sourceVertex.Add(vi);
                            positions.Add(Verts[vi].X);
                            positions.Add(Verts[vi].Y);
                            positions.Add(Verts[vi].Z);
                        }

                        indices.Add(remap[vi]);
                    }
                }
            }
        }

        private static void Normalize(double x, double y, double z, out double nx, out double ny, out double nz)
        {
            var len = Math.Sqrt(x * x + y * y + z * z);
            if (len < 1e-30)
            {
                nx = ny = nz = 0;
                return;
            }

            nx = x / len; ny = y / len; nz = z / len;
        }
    }
}
