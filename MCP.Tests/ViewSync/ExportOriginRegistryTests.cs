using System;
using System.IO;
using NUnit.Framework;
using RevitMCP.Core.ViewSync;
using RevitMCP.Core.Walkthrough;

namespace RevitMCP.Tests.ViewSync
{
    [TestFixture]
    public class ExportOriginRegistryTests
    {
        private string _dir;
        private string _file;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "origin_reg_" + Guid.NewGuid().ToString("N"));
            _file = Path.Combine(_dir, "export_origins.json");
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch (Exception) { }
        }

        private static ExportOriginEntry E(string guid, string path, double x, string at = null) => new ExportOriginEntry
        {
            DocumentGuid = guid, DocumentPath = path, Title = "t", OriginOffsetMeters = new[] { x, 2, 0 },
            OutputDir = "out", ExportedAt = at,
        };

        [Test]
        public void MissingFile_LoadsEmpty_FindNull()
        {
            Assert.That(ExportOriginRegistry.Load(_file), Is.Empty);
            Assert.That(ExportOriginRegistry.Find(_file, "g", "p"), Is.Null);
        }

        [Test]
        public void Upsert_ThenFind_RoundTrips()
        {
            Assert.That(ExportOriginRegistry.Upsert(_file, E("G1", @"C:\a.rvt", -433.698)), Is.True);
            var hit = ExportOriginRegistry.Find(_file, "g1", @"C:\other.rvt"); // GUID 不分大小寫，路徑不同仍同一文件
            Assert.That(hit, Is.Not.Null);
            Assert.That(hit.Origin.X, Is.EqualTo(-433.698).Within(1e-9));
        }

        [Test]
        public void Upsert_SameDocument_KeepsLatestOnly_DifferentDocumentsCoexist()
        {
            ExportOriginRegistry.Upsert(_file, E("G1", @"C:\a.rvt", 1, "2026-10-30T00:00:00Z"));
            ExportOriginRegistry.Upsert(_file, E("G2", @"C:\b.rvt", 9));
            ExportOriginRegistry.Upsert(_file, E("G1", @"C:\a.rvt", 5, "2026-10-31T00:00:00Z"));
            Assert.That(ExportOriginRegistry.Load(_file).Count, Is.EqualTo(2));
            Assert.That(ExportOriginRegistry.Find(_file, "G1", null).Origin.X, Is.EqualTo(5));
            Assert.That(ExportOriginRegistry.Find(_file, "G2", null).Origin.X, Is.EqualTo(9));
        }

        [Test]
        public void NoGuid_FallsBackToPathMatch_CaseInsensitive()
        {
            ExportOriginRegistry.Upsert(_file, E(null, @"C:\A.rvt", 3));
            Assert.That(ExportOriginRegistry.Find(_file, "", @"c:\a.RVT"), Is.Not.Null);
            Assert.That(ExportOriginRegistry.Find(_file, "", @"c:\b.rvt"), Is.Null);
        }

        [Test]
        public void InvalidEntry_Rejected_CorruptFileTolerated()
        {
            Assert.That(ExportOriginRegistry.Upsert(_file, new ExportOriginEntry { DocumentGuid = "g", OriginOffsetMeters = new double[] { 1, 2 } }), Is.False);
            Assert.That(ExportOriginRegistry.Upsert(_file, E("", "", 1)), Is.False);
            Directory.CreateDirectory(_dir);
            File.WriteAllText(_file, "{ not json");
            Assert.That(ExportOriginRegistry.Load(_file), Is.Empty);
            Assert.That(ExportOriginRegistry.Upsert(_file, E("G1", "p", 1)), Is.True); // 壞檔可被覆寫修復
        }

        [Test]
        public void Resolve_PrefersRegistry_ElseFallback()
        {
            ExportOriginRegistry.Upsert(_file, E("G1", "p", -433.698));
            var fb = new Func<(Vec3? origin, string source)>(() => (new Vec3(7, 7, 7), "camera.json"));
            var r1 = ExportOriginRegistry.Resolve(_file, "G1", "p", fb);
            Assert.That(r1.FromRegistry, Is.True);
            Assert.That(r1.Origin.Value.X, Is.EqualTo(-433.698).Within(1e-9));
            var r2 = ExportOriginRegistry.Resolve(_file, "GX", "px", fb);
            Assert.That(r2.FromRegistry, Is.False);
            Assert.That(r2.Origin.Value.X, Is.EqualTo(7));
            var r3 = ExportOriginRegistry.Resolve(_file, "GX", "px", null);
            Assert.That(r3.Origin.HasValue, Is.False);
            // 比對：登錄命中且差 > 1 mm 才算 Mismatch
            Assert.That(ViewSyncMath.CompareOrigin(new Vec3(-433.698, 2, 0), r1.Origin), Is.EqualTo(ViewSyncMath.OriginMatch.Match));
            Assert.That(ViewSyncMath.CompareOrigin(new Vec3(-433.70, 2, 0), r1.Origin), Is.EqualTo(ViewSyncMath.OriginMatch.Mismatch));
        }
    }
}
