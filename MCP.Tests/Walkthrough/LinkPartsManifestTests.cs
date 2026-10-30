using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NUnit.Framework;
using RevitMCP.Core.Walkthrough;

namespace RevitMCP.Tests.Walkthrough
{
    [TestFixture]
    public class LinkPartsManifestTests
    {
        // 模擬的 BuiltInCategory 整數值（測試專案沒有 Revit API；實際集合見 RevitGltfExportContext.StructuralMepBuiltInCategories）。
        private const int Framing = -2001320, Column = -2001330, Pipe = -2008044, Duct = -2008000, Wall = -2000011;
        private static readonly HashSet<int> Hidden = new HashSet<int> { Framing, Column, Pipe, Duct };

        private static List<CategoryTriangles> Cat(params object[] triples)
        {
            var list = new List<CategoryTriangles>();
            for (var i = 0; i < triples.Length; i += 3)
                list.Add(new CategoryTriangles { BuiltInCategory = (int)triples[i], Name = (string)triples[i + 1], Triangles = (int)triples[i + 2] });
            return list;
        }

        [Test]
        public void Slug_AsciiLowercaseUnderscore_AndStripsRvt()
        {
            var used = new HashSet<string>();
            Assert.That(LinkPartsManifest.Slug("Snowdon Towers Sample Structural.rvt", used), Is.EqualTo("snowdon_towers_sample_structural"));
            Assert.That(LinkPartsManifest.Slug("  A--B  ", used), Is.EqualTo("a_b"));
        }

        [Test]
        public void Slug_DuplicateGetsSuffix_AndNonAsciiUsesStableHash()
        {
            var used = new HashSet<string>();
            Assert.That(LinkPartsManifest.Slug("MEP-電氣", used), Is.EqualTo("mep"));
            Assert.That(LinkPartsManifest.Slug("MEP 電氣", used), Is.EqualTo("mep_2"));
            Assert.That(LinkPartsManifest.Slug("MEP", used), Is.EqualTo("mep_3"));

            var a = LinkPartsManifest.Slug("給排水", new HashSet<string>());
            var b = LinkPartsManifest.Slug("給排水", new HashSet<string>());
            Assert.That(a, Is.EqualTo(b), "同一標題的雜湊要跨執行穩定");
            StringAssert.StartsWith("link_", a);
            Assert.That(a, Is.Not.EqualTo(LinkPartsManifest.Slug("空調", new HashSet<string>())));
            Assert.That(LinkPartsManifest.PartFileName("mep"), Is.EqualTo("part_mep.glb"));
        }

        [Test]
        public void DefaultVisible_HiddenWhenStructuralOrMepAtLeastHalf()
        {
            Assert.That(LinkPartsManifest.ComputeDefaultVisible(Cat(Pipe, "Pipes", 50, Wall, "Walls", 50), Hidden), Is.False, "剛好 50% → 不顯示");
            Assert.That(LinkPartsManifest.ComputeDefaultVisible(Cat(Pipe, "Pipes", 49, Wall, "Walls", 51), Hidden), Is.True);
            Assert.That(LinkPartsManifest.ComputeDefaultVisible(Cat(Framing, "Structural Framing", 10, Column, "Structural Columns", 5, Wall, "Floors", 6), Hidden), Is.False, "多類別加總");
            Assert.That(LinkPartsManifest.ComputeDefaultVisible(Cat(Wall, "Facades", 100), Hidden), Is.True);
            Assert.That(LinkPartsManifest.ComputeDefaultVisible(Cat(), Hidden), Is.True, "空分檔預設顯示");
        }

        [Test]
        public void DefaultVisible_ChineseDisplayNames_JudgedByBuiltInCategoryNotName()
        {
            // Revit 以 /language CHT 執行：顯示名稱是「結構構架」「管」，名稱比對會全部落空；改看 BuiltInCategory 整數值。
            Assert.That(LinkPartsManifest.ComputeDefaultVisible(Cat(Framing, "結構構架", 80, Wall, "牆", 20), Hidden), Is.False);
            Assert.That(LinkPartsManifest.ComputeDefaultVisible(Cat(Pipe, "管", 60, Wall, "牆", 40), Hidden), Is.False);
            Assert.That(LinkPartsManifest.ComputeDefaultVisible(Cat(Wall, "牆", 90, Pipe, "管", 10), Hidden), Is.True);
            // 同一個 BuiltInCategory，中英文名稱判斷結果相同。
            var zh = LinkPartsManifest.ComputeDefaultVisible(Cat(Duct, "風管", 100), Hidden);
            var en = LinkPartsManifest.ComputeDefaultVisible(Cat(Duct, "Ducts", 100), Hidden);
            Assert.That(zh, Is.EqualTo(en));
            Assert.That(zh, Is.False);
            // BuiltInCategory 已知時以它為準：英文名稱叫 "Pipes" 但不在清單內的值不算機電。
            Assert.That(LinkPartsManifest.ComputeDefaultVisible(Cat(Wall, "Pipes", 100), Hidden), Is.True);
        }

        [Test]
        public void DefaultVisible_UnknownBuiltIn_FallsBackToEnglishName()
        {
            Assert.That(LinkPartsManifest.ComputeDefaultVisible(Cat(0, "Pipes", 100), Hidden), Is.False);
            Assert.That(LinkPartsManifest.ComputeDefaultVisible(Cat(0, "管", 100), Hidden), Is.True, "未知 BuiltInCategory 又非英文名：無從判斷，預設顯示");
        }

        [Test]
        public void DefaultVisible_SpecEnglishNamesAllCount()
        {
            foreach (var c in new[]
            {
                "Structural Framing", "Structural Columns", "Structural Foundations", "Structural Rebar",
                "Pipes", "Pipe Fittings", "Pipe Accessories", "Ducts", "Duct Fittings", "Duct Accessories", "Air Terminals",
                "Conduits", "Conduit Fittings", "Cable Trays", "Cable Tray Fittings", "Mechanical Equipment", "Electrical Equipment",
                "Lighting Fixtures", "Plumbing Fixtures", "Sprinklers", "Electrical Fixtures",
                "Flex Pipes", "Flex Ducts", "Pipe Insulations", "Duct Insulations", "Duct Linings", "Fire Alarm Devices",
                "Communication Devices", "Data Devices", "Security Devices", "Nurse Call Devices", "Telephone Devices"
            })
            {
                Assert.That(LinkPartsManifest.IsStructuralOrMep(c), Is.True, c);
            }

            Assert.That(LinkPartsManifest.IsStructuralOrMep("Walls"), Is.False);
            Assert.That(LinkPartsManifest.IsStructuralOrMep("Property Lines"), Is.False);
            Assert.That(LinkPartsManifest.IsStructuralOrMep((string)null), Is.False);
        }

        [Test]
        public void TopCategories_SortedByTrianglesThenName_MergesSameBuiltIn_TakesFive()
        {
            var top = LinkPartsManifest.TopCategories(Cat(
                1, "A", 5, 2, "B", 9, 3, "C", 9, 4, "D", 1, 5, "E", 7, 6, "F", 3, 7, "Z", 0, 1, "A", 1), 5);
            Assert.That(top.Select(c => c.Name), Is.EqualTo(new[] { "B", "C", "E", "A", "F" }));
            Assert.That(top.First(c => c.Name == "A").Triangles, Is.EqualTo(6), "同一 BuiltInCategory 合併");
            Assert.That(top.First(c => c.Name == "B").BuiltInCategory, Is.EqualTo(2), "保留 BuiltInCategory");
        }
        [Test]
        public void BuildGlb_PartKey_WritesPartAndOriginInSceneExtras()
        {
            var builder = new GltfBuilder { OriginOffsetMeters = new Vec3(1, 2, 3), PartKey = "mep", PartLabel = "MEP \"電氣\"" };
            var m = builder.AddMaterial("m", 1, 1, 1, 1);
            builder.BeginElement(new GltfElementInfo { ElementId = 1, Category = "Pipes", Source = "MEP" });
            builder.AddTriangles(m, new[] { new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(0, 1, 0) }, null, new[] { 0, 1, 2 });
            builder.EndElement();
            var glb = builder.BuildGlb();
            var jsonLength = (int)BitConverter.ToUInt32(glb, 12);
            var json = Encoding.UTF8.GetString(glb, 20, jsonLength);
            StringAssert.Contains("\"originOffsetMeters\":[1,2,3]", json);
            StringAssert.Contains("\"part\":{\"key\":\"mep\",\"label\":\"MEP \\\"電氣\\\"\"}", json);
        }

        [Test]
        public void BuildGlb_NoPartKey_KeepsOriginOnlyExtras()
        {
            var builder = new GltfBuilder { OriginOffsetMeters = new Vec3(73.785, -80.999, 0) };
            var glb = builder.BuildGlb();
            var json = Encoding.UTF8.GetString(glb, 20, (int)BitConverter.ToUInt32(glb, 12));
            StringAssert.Contains("\"extras\":{\"originOffsetMeters\":[73.785,-80.999,0]}", json);
            StringAssert.DoesNotContain("\"part\"", json);
        }
    }
}
