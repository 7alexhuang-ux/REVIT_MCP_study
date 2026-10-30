using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using RevitMCP.Core.Walkthrough;

namespace RevitMCP.Tests.Walkthrough
{
    [TestFixture]
    public class ViewPositionsMathTests
    {
        private static readonly Vec3 Offset = new Vec3(1, 2, 3);

        private static void Near(Vec3 actual, double x, double y, double z, double tol = 1e-9) =>
            Assert.That(new[] { actual.X, actual.Y, actual.Z }, Is.EqualTo(new[] { x, y, z }).Within(tol));

        [Test]
        public void ToGltfPoint_SubtractsOffsetAndSwapsAxes()
        {
            // gltf = (x−ox, z−oz, −(y−oy))
            Near(ViewPositionsMath.ToGltfPoint(new Vec3(10, 20, 30), Offset), 9, 27, -18);
            Near(ViewPositionsMath.ToGltfDirection(new Vec3(0, 1, 0)), 0, 0, -1);
            Near(ViewPositionsMath.ToGltfDirection(new Vec3(0, 0, 1)), 0, 1, 0);
            Assert.That(ViewPositionsMath.ToGltfHeight(10.5, Offset), Is.EqualTo(7.5));
        }

        [Test]
        public void BuildPlanRange_RequiresCutPlane_AndSubtractsOriginZ()
        {
            Assert.That(ViewPositionsMath.BuildPlanRange(null, 5, 0, -1, Offset), Is.Null);
            Assert.That(ViewPositionsMath.BuildPlanRange(double.NaN, 5, 0, -1, Offset), Is.Null);
            var range = ViewPositionsMath.BuildPlanRange(4.2, 7, null, double.PositiveInfinity, Offset);
            Assert.That(range.CutPlaneMeters, Is.EqualTo(1.2).Within(1e-9));
            Assert.That(range.TopMeters, Is.EqualTo(4).Within(1e-9));
            Assert.That(range.BottomMeters, Is.Null, "拿不到的平面給 null，不猜值");
            Assert.That(range.ViewDepthMeters, Is.Null, "非有限值給 null");
        }

        [Test]
        public void BuildCrop_IsAxisAlignedBoxOfGltfCorners()
        {
            var corners = ViewPositionsMath.BoxCorners(new Vec3(10, 20, 0), new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1),
                new Vec3(-2, -3, 1), new Vec3(2, 3, 5));
            Assert.That(corners.Count, Is.EqualTo(8));
            var crop = ViewPositionsMath.BuildCrop(corners, Offset, true);
            Assert.That(crop.Active, Is.True);
            // x: 8..12 → 7..11；Revit z 1..5 → 減 oz=3 → −2..2；Revit y 17..23 → −(y−2) → −21..−15
            Near(crop.Min, 7, -2, -21);
            Near(crop.Max, 11, 2, -15);
            Assert.That(ViewPositionsMath.BuildCrop(new Vec3[0], Offset, true), Is.Null);
            Assert.That(ViewPositionsMath.BuildCrop(new[] { new Vec3(double.NaN, 0, 0) }, Offset, true), Is.Null);
        }

        [Test]
        public void BuildFrame_AxisAlignedSectionLookingNorth()
        {
            // Revit 剖面朝北（+Y）：right=+X、up=+Z、朝向觀看者的 BasisZ=X×Z=−Y，看向模型＝+Y。
            var frame = ViewPositionsMath.BuildFrame(new Vec3(10, 20, 3), new Vec3(1, 0, 0), new Vec3(0, 0, 1),
                new Vec3(-5, -2, -8), new Vec3(5, 4, 0), Offset, out var problem, out var note);
            Assert.That(problem, Is.Null);
            Assert.That(note, Is.Null);
            Assert.That(frame.WidthMeters, Is.EqualTo(10).Within(1e-9));
            Assert.That(frame.HeightMeters, Is.EqualTo(6).Within(1e-9));
            Assert.That(frame.DepthMeters, Is.EqualTo(8).Within(1e-9));
            // 局部中心 (0,1) → Revit (10,20,4) → gltf (9,1,−18)
            Near(frame.Origin, 9, 1, -18);
            Near(frame.Right, 1, 0, 0);
            Near(frame.Up, 0, 1, 0);
            Near(frame.ViewDirection, 0, 0, -1);
            // 右手系：right × up 指向觀看者（＝−viewDirection）
            var toViewer = frame.Right.Cross(frame.Up);
            Near(toViewer, 0, 0, 1);
        }

        [Test]
        public void BuildFrame_RotatedSection_CornersMatchTheCropBox()
        {
            var angle = 30 * Math.PI / 180;
            var right = new Vec3(Math.Cos(angle), Math.Sin(angle), 0);
            var up = new Vec3(0, 0, 1);
            var origin = new Vec3(50, 60, 2);
            var min = new Vec3(-3, -1, -6);
            var max = new Vec3(7, 5, 0);
            var frame = ViewPositionsMath.BuildFrame(origin, right, up, min, max, Offset, out var problem, out _);
            Assert.That(problem, Is.Null);
            Assert.That(frame.Right.Length, Is.EqualTo(1).Within(1e-9));
            Assert.That(frame.Up.Length, Is.EqualTo(1).Within(1e-9));
            Assert.That(frame.ViewDirection.Length, Is.EqualTo(1).Within(1e-9));
            Assert.That(Math.Abs(frame.Right.Dot(frame.Up)), Is.LessThan(1e-9));
            Assert.That(Math.Abs(frame.ViewDirection.Dot(frame.Right)), Is.LessThan(1e-9));
            Assert.That(Math.Abs(frame.ViewDirection.Dot(frame.Up)), Is.LessThan(1e-9));

            // 規格：八個角點＝origin ± right·w/2 ± up·h/2 與其加上 viewDirection·depth，要等於 CropBox 局部 z∈[−6,0] 的角點。
            var basisZ = right.Cross(up); // 朝向觀看者
            var expected = ViewPositionsMath.BoxCorners(origin, right, up, basisZ, min, max)
                .Select(p => ViewPositionsMath.ToGltfPoint(p, Offset)).ToList();
            var actual = new System.Collections.Generic.List<Vec3>();
            foreach (var sx in new[] { -1, 1 }) foreach (var sy in new[] { -1, 1 }) foreach (var depth in new[] { 0, frame.DepthMeters.Value })
                actual.Add(frame.Origin + frame.Right * (sx * frame.WidthMeters / 2) + frame.Up * (sy * frame.HeightMeters / 2) + frame.ViewDirection * depth);
            foreach (var corner in expected)
                Assert.That(actual.Any(a => (a - corner).Length < 1e-9), Is.True, $"缺角點 {corner}");
        }

        [Test]
        public void BuildFrame_RejectsBadInputs_AndNotesNearExtent()
        {
            var x = new Vec3(1, 0, 0); var z = new Vec3(0, 0, 1);
            Assert.That(ViewPositionsMath.BuildFrame(Vec3.Zero, x, z, new Vec3(0, 0, -1), new Vec3(0, 3, 0), Offset, out var p1, out _), Is.Null);
            StringAssert.Contains("寬或高", p1);
            Assert.That(ViewPositionsMath.BuildFrame(Vec3.Zero, x, x, new Vec3(0, 0, -1), new Vec3(2, 3, 0), Offset, out var p2, out _), Is.Null);
            StringAssert.Contains("垂直", p2);
            Assert.That(ViewPositionsMath.BuildFrame(Vec3.Zero, Vec3.Zero, z, new Vec3(0, 0, -1), new Vec3(2, 3, 0), Offset, out var p3, out _), Is.Null);
            StringAssert.Contains("長度", p3);
            Assert.That(ViewPositionsMath.BuildFrame(Vec3.Zero, x, z, new Vec3(0, 0, -1), new Vec3(2, double.NaN, 0), Offset, out var p4, out _), Is.Null);
            StringAssert.Contains("NaN", p4);

            var frame = ViewPositionsMath.BuildFrame(Vec3.Zero, x, z, new Vec3(0, 0, -4), new Vec3(2, 3, 0.5), Offset, out var problem, out var note);
            Assert.That(problem, Is.Null);
            StringAssert.Contains("maxZ", note);
            Assert.That(frame.DepthMeters, Is.EqualTo(4));

            var noDepth = ViewPositionsMath.BuildFrame(Vec3.Zero, x, z, new Vec3(0, 0, 0.2), new Vec3(2, 3, 1), Offset, out _, out _);
            Assert.That(noDepth.DepthMeters, Is.EqualTo(0), "深度不得為負");
        }

        [Test]
        public void BuildFrame_RightUpViewDirection_AreOrthonormal_AndViewDirectionIsMinusRightCrossUp()
        {
            // Revit 基底：BasisX＝右、BasisY＝上、BasisZ＝右×上＝View.ViewDirection（朝向觀看者）。
            // 輸出的 viewDirection 必須是 −View.ViewDirection（從相機看向模型）。
            foreach (var degrees in new[] { 0.0, 30.0, 90.0, 137.0, 225.0 })
            {
                var a = degrees * Math.PI / 180;
                var right = new Vec3(Math.Cos(a), Math.Sin(a), 0);
                var up = new Vec3(0, 0, 1);
                var revitViewDirection = right.Cross(up); // View.ViewDirection（指向觀看者）
                var frame = ViewPositionsMath.BuildFrame(new Vec3(5, 6, 7), right, up, new Vec3(-2, -1, -3), new Vec3(2, 1, 0), Offset, out var problem, out _);
                Assert.That(problem, Is.Null);
                Assert.That(Math.Abs(frame.Right.Dot(frame.Up)), Is.LessThanOrEqualTo(0.01));
                Assert.That(Math.Abs(frame.Right.Dot(frame.ViewDirection)), Is.LessThanOrEqualTo(0.01));
                Assert.That(Math.Abs(frame.Up.Dot(frame.ViewDirection)), Is.LessThanOrEqualTo(0.01));
                var expected = ViewPositionsMath.ToGltfDirection(revitViewDirection * -1).Normalized();
                Near(frame.ViewDirection, expected.X, expected.Y, expected.Z, 1e-9);
                // glTF 座標也維持 right×up＝−viewDirection（換軸是旋轉，不翻手性）
                var crossed = frame.Right.Cross(frame.Up);
                Near(crossed, -frame.ViewDirection.X, -frame.ViewDirection.Y, -frame.ViewDirection.Z, 1e-9);
            }
        }

        [Test]
        public void BuildPlanRange_RejectsCutBelowBottom_WithReason()
        {
            Assert.That(ViewPositionsMath.BuildPlanRange(2.0, 5, 3.0, 1, Offset, out var problem), Is.Null);
            Assert.That(problem, Is.EqualTo("cutBelowBottom"));
            Assert.That(ViewPositionsMath.BuildPlanRange(null, 5, 3.0, 1, Offset, out problem), Is.Null);
            Assert.That(problem, Is.EqualTo("noCutPlane"));
            var equal = ViewPositionsMath.BuildPlanRange(3.0, 5, 3.0, 1, Offset, out problem);
            Assert.That(problem, Is.Null);
            Assert.That(equal.CutPlaneMeters, Is.EqualTo(equal.BottomMeters.Value), "切高等於下緣可以");
        }

        [Test]
        public void BuildPlanRange_UsesAbsoluteHeights_FromLevelElevationPlusOffset()
        {
            // 呼叫端把「樓層高程＋偏移」加成絕對高度；這裡驗證扣原點 Z 後才是 glTF Y。
            var levelZ = 12.0; // 樓層 Revit Z（公尺）
            var range = ViewPositionsMath.BuildPlanRange(levelZ + 1.2, levelZ + 2.3, levelZ + 0.0, levelZ - 1.0, new Vec3(0, 0, 10), out _);
            Assert.That(range.CutPlaneMeters, Is.EqualTo(3.2).Within(1e-9));
            Assert.That(range.TopMeters, Is.EqualTo(4.3).Within(1e-9));
            Assert.That(range.BottomMeters, Is.EqualTo(2.0).Within(1e-9));
            Assert.That(range.ViewDepthMeters, Is.EqualTo(1.0).Within(1e-9));
        }

        [Test]
        public void BuildFrame_FarClipOff_GivesNullDepth_AndJsonNull()
        {
            var x = new Vec3(1, 0, 0); var z = new Vec3(0, 0, 1);
            var frame = ViewPositionsMath.BuildFrame(Vec3.Zero, x, z, new Vec3(-2, -1, -6), new Vec3(2, 1, 0), Offset, false, out var problem, out _);
            Assert.That(problem, Is.Null);
            Assert.That(frame.DepthMeters, Is.Null, "不裁剪時不輸出 0");
            var file = ViewPositionsMath.BuildFile("T", Offset, null,
                new[] { new ViewPositionEntry { UniqueId = "s", Name = "S", Kind = "section", Frame = frame, ViewDirection = frame.ViewDirection } }, null);
            Assert.That(file["views"][0]["frame"]["depthMeters"].Type, Is.EqualTo(JTokenType.Null));
            var clipped = ViewPositionsMath.BuildFrame(Vec3.Zero, x, z, new Vec3(-2, -1, -6), new Vec3(2, 1, 0), Offset, true, out _, out _);
            Assert.That(clipped.DepthMeters, Is.EqualTo(6).Within(1e-9));
        }

        [Test]
        public void BuildFile_HasSpecShape_AndWritesSampleForWebValidation()
        {
            var planRange = ViewPositionsMath.BuildPlanRange(1.2 + 3, 4 + 3, 0 + 3, -1 + 3, Offset);
            var planCrop = ViewPositionsMath.BuildCrop(ViewPositionsMath.BoxCorners(new Vec3(10, 20, 0), new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1),
                new Vec3(-5, -4, -1), new Vec3(5, 4, 4)), Offset, true);
            var section = ViewPositionsMath.BuildFrame(new Vec3(10, 20, 3), new Vec3(1, 0, 0), new Vec3(0, 0, 1),
                new Vec3(-5, -2, -8), new Vec3(5, 4, 0), Offset, out _, out _);
            var angle = 30 * Math.PI / 180;
            var elevation = ViewPositionsMath.BuildFrame(new Vec3(50, 60, 2), new Vec3(Math.Cos(angle), Math.Sin(angle), 0), new Vec3(0, 0, 1),
                new Vec3(-3, -1, -6), new Vec3(7, 5, 0), Offset, out _, out _);
            var down = ViewPositionsMath.ToGltfDirection(ViewPositionsMath.LookDirection(new Vec3(1, 0, 0), new Vec3(0, 1, 0)));
            var up = ViewPositionsMath.ToGltfDirection(ViewPositionsMath.LookDirection(new Vec3(1, 0, 0), new Vec3(0, -1, 0)));
            var levels = new[] { new ViewPositionLevel { LevelUniqueId = "level-1", Name = "1F", ElevationMeters = ViewPositionsMath.ToGltfHeight(3, Offset), RevitZMeters = 3 } };
            var views = new[]
            {
                new ViewPositionEntry { UniqueId = "plan-1", Name = "1F 平面", Kind = "plan", LevelUniqueId = "level-1", ViewRange = planRange, Crop = planCrop, ViewDirection = down },
                new ViewPositionEntry { UniqueId = "ceiling-1", Name = "1F 天花", Kind = "ceiling", LevelUniqueId = "level-1", ViewRange = planRange, Crop = planCrop, ViewDirection = up },
                new ViewPositionEntry { UniqueId = "section-1", Name = "A-A 剖面", Kind = "section", Frame = section, ViewDirection = section.ViewDirection },
                new ViewPositionEntry { UniqueId = "elev-1", Name = "北立面", Kind = "elevation", Frame = elevation, ViewDirection = elevation.ViewDirection },
                new ViewPositionEntry { UniqueId = "detail-1", Name = "詳圖 1", Kind = "detail" },
            };
            var file = ViewPositionsMath.BuildFile("AR_TEST", Offset, levels, views, new[] { "sample warning" });

            Assert.That(file["schemaVersion"].Value<int>(), Is.EqualTo(1));
            Assert.That(file["originOffsetMeters"].ToObject<double[]>(), Is.EqualTo(new[] { 1.0, 2.0, 3.0 }));
            Assert.That(file["revitDoc"].Value<string>(), Is.EqualTo("AR_TEST"));
            Assert.That(file["levels"][0]["elevationMeters"].Value<double>(), Is.EqualTo(0).Within(1e-9));
            Assert.That(file["levels"][0]["revitZMeters"].Value<double>(), Is.EqualTo(3).Within(1e-9));
            var views0 = (JArray)file["views"];
            Assert.That(views0.Count, Is.EqualTo(5));
            Assert.That(views0[0]["viewRange"]["cutPlaneMeters"].Value<double>(), Is.EqualTo(1.2).Within(1e-9));
            Assert.That(views0[0]["viewDirection"].ToObject<double[]>(), Is.EqualTo(new[] { 0.0, -1.0, 0.0 }).Within(1e-9), "平面看向 glTF −Y");
            Assert.That(views0[1]["viewDirection"].ToObject<double[]>(), Is.EqualTo(new[] { 0.0, 1.0, 0.0 }).Within(1e-9), "天花朝上看");
            Assert.That(views0[0]["frame"].Type, Is.EqualTo(JTokenType.Null));
            Assert.That(views0[2]["frame"]["depthMeters"].Value<double>(), Is.EqualTo(8).Within(1e-9));
            Assert.That(views0[2]["sectionLine"]["p0"].ToObject<double[]>(), Is.EqualTo(new[] { 4.0, 1.0, -18.0 }).Within(1e-9));
            Assert.That(views0[4]["viewRange"].Type, Is.EqualTo(JTokenType.Null));
            Assert.That(views0[4]["frame"].Type, Is.EqualTo(JTokenType.Null));

            // 供離線對照網站端 convertViewPositions（見回報）：測試輸出夾內留一份樣本。
            var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "view_positions.sample.json");
            File.WriteAllText(path, file.ToString(Newtonsoft.Json.Formatting.Indented));
            TestContext.WriteLine("sample: " + path);
        }
    }
}
