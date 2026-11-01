using System;
using System.Linq;
using NUnit.Framework;
using RevitMCP.Core.ViewSync;
using RevitMCP.Core.Walkthrough;

namespace RevitMCP.Tests.ViewSync
{
    [TestFixture]
    public class ViewSyncMathTests
    {
        private const double Ft = 0.3048;
        private static readonly Vec3 Offset = new Vec3(100, 200, 10);

        private static void Near(Vec3 actual, double x, double y, double z, double tol = 1e-9) =>
            Assert.That(new[] { actual.X, actual.Y, actual.Z }, Is.EqualTo(new[] { x, y, z }).Within(tol));

        // ---------------------------------------------------------------- 座標

        [Test]
        public void GltfToRevit_MatchesSpecFormula()
        {
            // x_ft=(gx+ox)/0.3048、y_ft=(−gz+oy)/0.3048、z_ft=(gy+oz)/0.3048
            Near(ViewSyncMath.GltfPointToRevitFeet(new Vec3(1, 2, 3), Offset), (1 + 100) / Ft, (-3 + 200) / Ft, (2 + 10) / Ft);
            Near(ViewSyncMath.GltfDirToRevit(new Vec3(1, 2, 3)), 1, -3, 2);
        }

        [Test]
        public void GltfRevit_RoundTripsWithViewPositionsMath()
        {
            var revitMeters = new Vec3(131.2, 77.7, 15.5);
            var gltf = ViewPositionsMath.ToGltfPoint(revitMeters, Offset);
            var feet = ViewSyncMath.GltfPointToRevitFeet(gltf, Offset);
            Near(feet, revitMeters.X / Ft, revitMeters.Y / Ft, revitMeters.Z / Ft, 1e-9);
            Near(ViewSyncMath.RevitFeetToGltfPoint(feet, Offset), gltf.X, gltf.Y, gltf.Z, 1e-9);
            Near(ViewSyncMath.GltfDirToRevit(ViewPositionsMath.ToGltfDirection(new Vec3(0.3, -0.5, 0.8))), 0.3, -0.5, 0.8);
        }

        // ---------------------------------------------------------------- 相機

        [Test]
        public void Camera_ForwardAndUp_AreOrthonormalInRevitCoordinates()
        {
            // 在 glTF 原點上方 10 m、看向 −Z 方向（＝Revit +Y 北）的位置。
            var cam = ViewSyncMath.ComputeCamera(new Vec3(0, 10, 0), new Vec3(0, 10, -20), new Vec3(0, 1, 0), Vec3.Zero);
            Near(cam.Forward, 0, 1, 0);
            Near(cam.Up, 0, 0, 1);
            Assert.That(cam.Forward.Dot(cam.Up), Is.EqualTo(0).Within(1e-12));
            Assert.That(cam.TargetDistanceFeet, Is.EqualTo(20 / Ft).Within(1e-9));
            Near(cam.EyeFeet, 0, 0, 10 / Ft);
        }

        [Test]
        public void Camera_UpNotPerpendicular_IsOrthogonalized()
        {
            var cam = ViewSyncMath.ComputeCamera(new Vec3(0, 0, 0), new Vec3(5, 5, 0), new Vec3(0, 1, 0), Offset);
            Assert.That(cam.Forward.Length, Is.EqualTo(1).Within(1e-12));
            Assert.That(cam.Up.Length, Is.EqualTo(1).Within(1e-12));
            Assert.That(cam.Forward.Dot(cam.Up), Is.EqualTo(0).Within(1e-12));
            Assert.That(cam.Up.Z, Is.GreaterThan(0));
        }

        [Test]
        public void Camera_LookingStraightDown_FallsBackToValidUp()
        {
            var cam = ViewSyncMath.ComputeCamera(new Vec3(0, 50, 0), new Vec3(0, 0, 0), new Vec3(0, 1, 0), Vec3.Zero);
            Near(cam.Forward, 0, 0, -1);
            Assert.That(cam.Up.Length, Is.EqualTo(1).Within(1e-12));
            Assert.That(cam.Forward.Dot(cam.Up), Is.EqualTo(0).Within(1e-12));
        }

        [Test]
        public void Camera_PositionEqualsTarget_Throws()
        {
            var ex = Assert.Throws<ViewSyncException>(() => ViewSyncMath.ComputeCamera(new Vec3(1, 2, 3), new Vec3(1, 2, 3), new Vec3(0, 1, 0), Vec3.Zero));
            Assert.That(ex.Code, Is.EqualTo("camera_degenerate"));
        }

        // ---------------------------------------------------------------- 透視視野

        [Test]
        public void HorizontalFov_FromVerticalAndAspect()
        {
            Assert.That(ViewSyncMath.HorizontalFovDegrees(60, 1), Is.EqualTo(60).Within(1e-9));
            var h = ViewSyncMath.HorizontalFovDegrees(60, 16.0 / 9.0);
            Assert.That(Math.Tan(h * Math.PI / 360), Is.EqualTo(16.0 / 9.0 * Math.Tan(30 * Math.PI / 180)).Within(1e-12));
            Assert.That(h, Is.EqualTo(91.49).Within(0.01));
        }

        [Test]
        public void PerspectiveCrop_TargetPlane_WidthIsTwiceDistanceTimesTanHalfHorizontalFov()
        {
            var (min, max) = ViewSyncMath.ComputePerspectiveCrop(50, 1.6, 100, 1, 1000, FovCropModel.TargetPlane);
            var hfov = ViewSyncMath.HorizontalFovDegrees(50, 1.6);
            var width = max.X - min.X;
            Assert.That(width, Is.EqualTo(2 * 100 * Math.Tan(hfov * Math.PI / 360)).Within(1e-9), "裁剪框寬＝2×目標距離×tan(水平視角/2)");
            Assert.That((max.X - min.X) / (max.Y - min.Y), Is.EqualTo(1.6).Within(1e-12), "寬高比保持");
            Assert.That(min.X, Is.EqualTo(-max.X).Within(1e-12));
            Assert.That(max.Z, Is.EqualTo(-1)); Assert.That(min.Z, Is.EqualTo(-1000));
        }

        [Test]
        public void PerspectiveCrop_NearPlane_MatchesExistingCameraPathMath()
        {
            var (min, max) = ViewSyncMath.ComputePerspectiveCrop(45, 2, 50, 1, 500, FovCropModel.NearPlane);
            var (emin, emax) = CameraPathMath.CropBoxFromFieldOfView(45, 2, 1, 500);
            Near(min, emin.X, emin.Y, emin.Z); Near(max, emax.X, emax.Y, emax.Z);
        }

        // 2026-10-31 Revit 2024 實測（Snowdon「IM 對位（臨時）」視圖 2492777）：
        //   裁剪框 X ±0.07373、Y ±0.04142 ft（近 1.09 ft、遠 1090 ft）→ Revit 裁剪形狀讀回 垂直 45.00°、水平 72.80°；
        //   X／Y 加倍 → 79.28°／111.71°（tan 線性、焦距 0.1 ft 固定，與近裁剪 1.09 ft 無關）。
        [Test]
        public void PerspectiveCrop_FocalPlane_MatchesRevitMeasurement()
        {
            var (min, max) = ViewSyncMath.ComputePerspectiveCrop(45, 1.78, 207.0, 1.0903314498558707, 1090.3314498558707, FovCropModel.FocalPlane);
            Assert.That(max.Y, Is.EqualTo(0.0414213562373095).Within(1e-12));
            Assert.That(max.X, Is.EqualTo(0.07373001410241091).Within(1e-12));
            Assert.That(min.X, Is.EqualTo(-max.X)); Assert.That(min.Y, Is.EqualTo(-max.Y));
            Assert.That(max.Z, Is.EqualTo(-1.0903314498558707)); Assert.That(min.Z, Is.EqualTo(-1090.3314498558707));
            Assert.That(ViewSyncMath.ImpliedFovDegrees(max.Y, ViewSyncMath.RevitPerspectiveFocalFeet), Is.EqualTo(45).Within(1e-9));
            Assert.That(ViewSyncMath.ImpliedFovDegrees(max.X, ViewSyncMath.RevitPerspectiveFocalFeet), Is.EqualTo(72.80).Within(0.01));
            Assert.That(ViewSyncMath.ImpliedFovDegrees(2 * max.Y, ViewSyncMath.RevitPerspectiveFocalFeet), Is.EqualTo(79.28).Within(0.01), "實測加倍點");
        }

        // 重現空白畫面：p_temp.json（相機 [473.8,30,-1375.6] → 目標 [433.8,2,-1415.6]，距離約 63 m）用舊 TargetPlane
        // 換算出 X ±152.68、Y ±85.78 ft；Revit 以 0.1 ft 焦距解讀 → 視角約 179.9°，建築縮成幾個像素。
        [Test]
        public void PerspectiveCrop_OldTargetPlane_ReproducesBlankView()
        {
            var cam = ViewSyncMath.ComputeCamera(new Vec3(473.8, 30, -1375.6), new Vec3(433.8, 2, -1415.6), new Vec3(0, 1, 0), new Vec3(-433.698, -1414.604, 0));
            var (_, oldMax) = ViewSyncMath.ComputePerspectiveCrop(45, 1.78, cam.TargetDistanceFeet, 1.0903314498558707, 1090.3314498558707, FovCropModel.TargetPlane);
            Assert.That(oldMax.Y, Is.EqualTo(85.77663735383068).Within(1e-6), "與 Revit 讀到的舊裁剪框一致");
            Assert.That(ViewSyncMath.ImpliedFovDegrees(oldMax.Y, ViewSyncMath.RevitPerspectiveFocalFeet), Is.GreaterThan(179.8));

            var (_, newMax) = ViewSyncMath.ComputePerspectiveCrop(45, 1.78, cam.TargetDistanceFeet, 1.0903314498558707, 1090.3314498558707, FovCropModel.FocalPlane);
            Assert.That(ViewSyncMath.ImpliedFovDegrees(newMax.Y, ViewSyncMath.RevitPerspectiveFocalFeet), Is.EqualTo(45).Within(1e-9));
            Assert.That(newMax.Y, Is.EqualTo(0.0414213562373095).Within(1e-12), "焦距模型與目標距離無關");
        }

        [Test]
        public void RescaleCropToFov_CorrectsDifferentFocalDistance()
        {
            // 既有相機焦距 24 mm（Snowdon Camera at Kitchen 01 實測）：以 0.1 ft 算出的框，Revit 讀回會比較寬。
            const double focal = 0.024 / 0.3048;
            var (min, max) = ViewSyncMath.ComputePerspectiveCrop(45, 1.78, 1, 1, 100, FovCropModel.FocalPlane);
            double Fov(double half) => ViewSyncMath.ImpliedFovDegrees(half, focal);
            var (fmin, fmax) = ViewSyncMath.RescaleCropToFov(min, max, 45, ViewSyncMath.HorizontalFovDegrees(45, 1.78), Fov(max.Y), Fov(max.X));
            Assert.That(Fov(fmax.Y), Is.EqualTo(45).Within(1e-9));
            Assert.That(Fov(fmax.X), Is.EqualTo(ViewSyncMath.HorizontalFovDegrees(45, 1.78)).Within(1e-9));
            Assert.That(fmin.X, Is.EqualTo(-fmax.X)); Assert.That(fmin.Z, Is.EqualTo(min.Z)); Assert.That(fmax.Z, Is.EqualTo(max.Z));
        }

        [Test]
        public void ParseFovModel_DefaultsToFocalPlane()
        {
            Assert.That(ViewSyncRequestParser.ParseFovModel(null), Is.EqualTo(FovCropModel.FocalPlane));
            Assert.That(ViewSyncRequestParser.ParseFovModel("focal"), Is.EqualTo(FovCropModel.FocalPlane));
            Assert.That(ViewSyncRequestParser.ParseFovModel("Target"), Is.EqualTo(FovCropModel.TargetPlane));
            Assert.That(ViewSyncRequestParser.ParseFovModel("near"), Is.EqualTo(FovCropModel.NearPlane));
        }

        [Test]
        public void PerspectiveCrop_RoundTripsThroughImpliedFov()
        {
            var (_, max) = ViewSyncMath.ComputePerspectiveCrop(70, 1.5, 40, 1, 400, FovCropModel.TargetPlane);
            Assert.That(ViewSyncMath.ImpliedFovDegrees(max.Y, 40), Is.EqualTo(70).Within(1e-9));
        }

        [TestCase(0.0, 1.0)]
        [TestCase(180.0, 1.0)]
        [TestCase(-5.0, 1.0)]
        [TestCase(60.0, 0.0)]
        [TestCase(60.0, -1.0)]
        [TestCase(double.NaN, 1.0)]
        public void PerspectiveCrop_InvalidInputs_Throw(double fov, double aspect)
        {
            var ex = Assert.Throws<ViewSyncException>(() => ViewSyncMath.ComputePerspectiveCrop(fov, aspect, 10, 1, 100, FovCropModel.TargetPlane));
            Assert.That(ex.Code, Is.EqualTo("fov_invalid"));
        }

        [Test]
        public void PerspectiveCrop_BadClipDistances_Throw()
        {
            Assert.Throws<ViewSyncException>(() => ViewSyncMath.ComputePerspectiveCrop(60, 1, 10, 0, 100, FovCropModel.TargetPlane));
            Assert.Throws<ViewSyncException>(() => ViewSyncMath.ComputePerspectiveCrop(60, 1, 10, 10, 5, FovCropModel.TargetPlane));
            Assert.Throws<ViewSyncException>(() => ViewSyncMath.ComputePerspectiveCrop(60, 1, 0, 1, 5, FovCropModel.TargetPlane));
        }

        // ---------------------------------------------------------------- 剖面框

        [Test]
        public void ClipBox_ConvertsAxisAlignedBoxToRevit()
        {
            // glTF: x∈[0,10]、y∈[0,5]（高）、z∈[−20,0] → Revit x∈[100,110]、y∈[200,220]、z∈[10,15]（公尺，再除 0.3048）
            var box = ViewSyncMath.ComputeClipBox(new Vec3(0, 0, -20), new Vec3(10, 5, 0), Offset);
            Near(box.MinFeet, 100 / Ft, 200 / Ft, 10 / Ft);
            Near(box.MaxFeet, 110 / Ft, 220 / Ft, 15 / Ft);
        }

        [Test]
        public void ClipBox_SwappedMinMax_StillOrdered()
        {
            var box = ViewSyncMath.ComputeClipBox(new Vec3(10, 5, 0), new Vec3(0, 0, -20), Offset);
            Assert.That(box.MinFeet.X, Is.LessThan(box.MaxFeet.X));
            Assert.That(box.MinFeet.Y, Is.LessThan(box.MaxFeet.Y));
            Assert.That(box.MinFeet.Z, Is.LessThan(box.MaxFeet.Z));
        }

        [Test]
        public void ClipBox_ZeroThickness_Throws()
        {
            var ex = Assert.Throws<ViewSyncException>(() => ViewSyncMath.ComputeClipBox(new Vec3(0, 0, 0), new Vec3(10, 0, 10), Vec3.Zero));
            Assert.That(ex.Code, Is.EqualTo("clip_box_empty"));
        }

        // ---------------------------------------------------------------- 剖面視圖框

        [Test]
        public void SectionFrame_Basis_IsOrthonormalRightHanded_AndZPointsToViewer()
        {
            // 站在 glTF +Z 側（Revit −Y 南側）看向 −Z（Revit +Y 北）：右＝glTF +X、上＝glTF +Y。
            var f = ViewSyncMath.ComputeSectionFrame(new Vec3(5, 2, 0), new Vec3(0, 0, -1), new Vec3(0, 1, 0), new Vec3(1, 0, 0), 10, 4, 6, Offset);
            Near(f.BasisX, 1, 0, 0); Near(f.BasisY, 0, 0, 1);
            Near(f.BasisZ, 0, -1, 0); // −look（Revit 慣例：朝向觀看者）= 南
            Assert.That(f.BasisX.Dot(f.BasisY), Is.EqualTo(0).Within(1e-12));
            Assert.That(f.BasisX.Cross(f.BasisY).Dot(f.BasisZ), Is.EqualTo(1).Within(1e-12));
            Near(f.OriginFeet, (5 + 100) / Ft, (0 + 200) / Ft, (2 + 10) / Ft);
            Near(f.LocalMinFeet, -5 / Ft, -2 / Ft, -6 / Ft);
            Near(f.LocalMaxFeet, 5 / Ft, 2 / Ft, 0);
        }

        [TestCase(1, 0, 0)]
        [TestCase(-1, 0, 0)]
        [TestCase(0, 0, 1)]
        [TestCase(0, 0, -1)]
        [TestCase(0.6, 0, 0.8)]
        public void SectionFrame_AnyHorizontalLook_BasisIsOrthonormalAndBasisZIsMinusLook(double lx, double ly, double lz)
        {
            var look = new Vec3(lx, ly, lz);
            var f = ViewSyncMath.ComputeSectionFrame(Vec3.Zero, look, new Vec3(0, 1, 0), null, 3, 3, 3, Offset);
            var lookRevit = ViewSyncMath.GltfDirToRevit(look).Normalized();
            Near(f.BasisZ, -lookRevit.X, -lookRevit.Y, -lookRevit.Z, 1e-12);
            Assert.That(f.BasisX.Length, Is.EqualTo(1).Within(1e-12));
            Assert.That(f.BasisY.Length, Is.EqualTo(1).Within(1e-12));
            Assert.That(f.BasisX.Dot(f.BasisY), Is.EqualTo(0).Within(1e-12));
            Assert.That(f.BasisX.Dot(f.BasisZ), Is.EqualTo(0).Within(1e-12));
            Assert.That(f.BasisY.Dot(f.BasisZ), Is.EqualTo(0).Within(1e-12));
            Assert.That(f.BasisX.Cross(f.BasisY).Dot(f.BasisZ), Is.EqualTo(1).Within(1e-12));
        }

        [Test]
        public void SectionFrame_LeftHandedRight_IsRejected()
        {
            var ex = Assert.Throws<ViewSyncException>(() =>
                ViewSyncMath.ComputeSectionFrame(Vec3.Zero, new Vec3(0, 0, -1), new Vec3(0, 1, 0), new Vec3(-1, 0, 0), 1, 1, 1, Vec3.Zero));
            Assert.That(ex.Code, Is.EqualTo("plane_basis_inconsistent"));
        }

        [Test]
        public void SectionFrame_DegenerateInputs_Throw()
        {
            Assert.That(Assert.Throws<ViewSyncException>(() => ViewSyncMath.ComputeSectionFrame(Vec3.Zero, Vec3.Zero, new Vec3(0, 1, 0), null, 1, 1, 1, Vec3.Zero)).Code, Is.EqualTo("plane_invalid"));
            Assert.That(Assert.Throws<ViewSyncException>(() => ViewSyncMath.ComputeSectionFrame(Vec3.Zero, new Vec3(0, 1, 0), new Vec3(0, 1, 0), null, 1, 1, 1, Vec3.Zero)).Code, Is.EqualTo("plane_invalid"));
            Assert.That(Assert.Throws<ViewSyncException>(() => ViewSyncMath.ComputeSectionFrame(Vec3.Zero, new Vec3(0, 0, -1), new Vec3(0, 1, 0), null, 0, 1, 1, Vec3.Zero)).Code, Is.EqualTo("plane_invalid"));
            Assert.That(Assert.Throws<ViewSyncException>(() => ViewSyncMath.ComputeSectionFrame(Vec3.Zero, new Vec3(0, 0, -1), new Vec3(0, 1, 0), null, 1, 1, 0, Vec3.Zero)).Code, Is.EqualTo("plane_invalid"));
        }

        // ---------------------------------------------------------------- 樓層與平面範圍

        private static readonly LevelRef[] Levels =
        {
            new LevelRef { UniqueId = "L3", Name = "3F", ElevationFeet = 30 },
            new LevelRef { UniqueId = "L1", Name = "1F", ElevationFeet = 0 },
            new LevelRef { UniqueId = "L2", Name = "2F", ElevationFeet = 15 },
        };

        [TestCase(0.0, "L1")]
        [TestCase(14.99, "L1")]
        [TestCase(15.0, "L2")]
        [TestCase(20.0, "L2")]
        [TestCase(99.0, "L3")]
        public void PickLevel_NearestAtOrBelowCut(double cut, string expectedId)
        {
            Assert.That(ViewSyncMath.PickLevel(Levels, cut, out var below).UniqueId, Is.EqualTo(expectedId));
            Assert.That(below, Is.False);
        }

        [Test]
        public void PickLevel_CutBelowAllLevels_UsesLowestAndFlags()
        {
            var l = ViewSyncMath.PickLevel(Levels, -5, out var below);
            Assert.That(l.UniqueId, Is.EqualTo("L1")); Assert.That(below, Is.True);
        }

        [Test]
        public void PickLevel_NoLevels_ReturnsNull()
        {
            Assert.That(ViewSyncMath.PickLevel(new LevelRef[0], 3, out _), Is.Null);
            Assert.That(ViewSyncMath.PickLevel(null, 3, out _), Is.Null);
        }

        [Test]
        public void FitPlanRange_KeepsOrderTopCutBottomDepth()
        {
            foreach (var cut in new[] { -3.0, 0.0, 4.0, 12.5, 40.0 })
            {
                var r = ViewSyncMath.FitPlanRange(0, 0, 0, 0, cut);
                Assert.That(r.Cut, Is.EqualTo(cut));
                Assert.That(r.Top, Is.GreaterThanOrEqualTo(r.Cut), "cut=" + cut);
                Assert.That(r.Cut, Is.GreaterThanOrEqualTo(r.Bottom), "cut=" + cut);
                Assert.That(r.Bottom, Is.GreaterThanOrEqualTo(r.ViewDepth), "cut=" + cut);
            }
        }

        [Test]
        public void FitPlanRange_KeepsExistingTopWhenAlreadyHighEnough()
        {
            var r = ViewSyncMath.FitPlanRange(7.5, 4, 0, -3, 5);
            Assert.That(r.Top, Is.EqualTo(7.5)); Assert.That(r.Bottom, Is.EqualTo(0)); Assert.That(r.ViewDepth, Is.EqualTo(-3));
        }

        // ---------------------------------------------------------------- 名稱

        [Test]
        public void UniqueName_AddsSuffixOnCollision_CaseInsensitive()
        {
            Assert.That(ViewSyncMath.UniqueName("A", new[] { "B" }), Is.EqualTo("A"));
            Assert.That(ViewSyncMath.UniqueName("A", new[] { "a" }), Is.EqualTo("A (2)"));
            Assert.That(ViewSyncMath.UniqueName("A", new[] { "A", "A (2)", "a (3)" }), Is.EqualTo("A (4)"));
            Assert.That(ViewSyncMath.UniqueName("A", null), Is.EqualTo("A"));
        }

        [Test]
        public void SanitizeViewName_ReplacesForbiddenCharacters()
        {
            Assert.That(ViewSyncMath.SanitizeViewName("IM-CPC-1030-01"), Is.EqualTo("IM-CPC-1030-01"));
            Assert.That(ViewSyncMath.SanitizeViewName("a:b{c}[d]|e;f<g>h?i`j~k\\l"), Is.EqualTo("a_b_c__d__e_f_g_h_i_j_k_l"));
            Assert.That(ViewSyncMath.SanitizeViewName("  "), Is.EqualTo("IM 視圖"));
            Assert.That(ViewSyncMath.SanitizeViewName(null), Is.EqualTo("IM 視圖"));
            Assert.That(ViewSyncMath.SanitizeViewName(new string('x', 500)), Has.Length.EqualTo(200));
            Assert.That(ViewSyncMath.SanitizeViewName("中文 視圖\n"), Is.EqualTo("中文 視圖"));
        }

        [Test]
        public void TempViewName_DependsOnProjection()
        {
            Assert.That(ViewSyncMath.TempViewName(false), Is.EqualTo("IM 對位（臨時）"));
            Assert.That(ViewSyncMath.TempViewName(true), Is.EqualTo("IM 對位（臨時・正交）"));
        }

        // ---------------------------------------------------------------- 顯示模式

        [TestCase("consistent", ViewSyncDisplayMode.FlatColors)]
        [TestCase("Consistent-Colors", ViewSyncDisplayMode.FlatColors)]
        [TestCase("flatColors", ViewSyncDisplayMode.FlatColors)]
        [TestCase("一致色彩", ViewSyncDisplayMode.FlatColors)]
        [TestCase("shaded", ViewSyncDisplayMode.Shading)]
        [TestCase("shading", ViewSyncDisplayMode.Shading)]
        [TestCase("著色", ViewSyncDisplayMode.Shading)]
        [TestCase("wireframe", ViewSyncDisplayMode.Wireframe)]
        [TestCase("線框", ViewSyncDisplayMode.Wireframe)]
        [TestCase("hidden_line", ViewSyncDisplayMode.HiddenLine)]
        [TestCase("hlr", ViewSyncDisplayMode.HiddenLine)]
        [TestCase("隱藏線", ViewSyncDisplayMode.HiddenLine)]
        [TestCase("realistic", ViewSyncDisplayMode.Realistic)]
        [TestCase("寫實", ViewSyncDisplayMode.Realistic)]
        [TestCase("something-else", ViewSyncDisplayMode.None)]
        [TestCase("", ViewSyncDisplayMode.None)]
        [TestCase(null, ViewSyncDisplayMode.None)]
        public void DisplayMode_Parse(string input, ViewSyncDisplayMode expected)
        {
            Assert.That(ViewSyncMath.ParseDisplayMode(input), Is.EqualTo(expected));
        }

        // ---------------------------------------------------------------- 核對

        [Test]
        public void TitleMatches_IgnoresCaseAndRvtExtension()
        {
            Assert.That(ViewSyncMath.TitleMatches("Snowdon.rvt", "snowdon"), Is.True);
            Assert.That(ViewSyncMath.TitleMatches(" Snowdon ", "SNOWDON.RVT"), Is.True);
            Assert.That(ViewSyncMath.TitleMatches("Snowdon", "Snowdon2"), Is.False);
            Assert.That(ViewSyncMath.TitleMatches("", ""), Is.False);
        }

        [Test]
        public void TitleMatches_IgnoresGlbAndGltfExtension()
        {
            Assert.That(ViewSyncMath.TitleMatches("Snowdon.glb", "Snowdon.rvt"), Is.True);
            Assert.That(ViewSyncMath.TitleMatches("snowdon.GLTF", "Snowdon"), Is.True);
            Assert.That(ViewSyncMath.TitleMatches("Snowdon.glb", "Snowdon2"), Is.False);
        }

        [Test]
        public void CutHeightToRevitFeet_AddsOriginZOnce()
        {
            // 世界 y（glTF 公尺、已扣原點）3 m，原點 Z＝10 m → Revit (3+10) m
            Assert.That(ViewSyncMath.CutHeightToRevitFeet(3, 10), Is.EqualTo(13 / 0.3048).Within(1e-9));
            Assert.That(ViewSyncMath.CutHeightToRevitFeet(-1.5, 0), Is.EqualTo(-1.5 / 0.3048).Within(1e-9));
        }

        [Test]
        public void FitCeilingRange_KeepsOrder_TopFromUpperLevelOrThreeMeters()
        {
            foreach (var cut in new[] { -2.0, 0.0, 2.5, 8.0, 40.0 })
                foreach (double? upper in new double?[] { null, 0.5, 9.0, 12.0, 100.0 })
                {
                    var r = ViewSyncMath.FitCeilingRange(cut, upper);
                    Assert.That(r.Top, Is.GreaterThan(r.Cut), $"cut={cut} upper={upper}");
                    Assert.That(r.Cut, Is.GreaterThanOrEqualTo(r.Bottom));
                    Assert.That(r.Bottom, Is.GreaterThanOrEqualTo(r.ViewDepth));
                }
            Assert.That(ViewSyncMath.FitCeilingRange(8, 12).Top, Is.EqualTo(12));                       // 用上一層
            Assert.That(ViewSyncMath.FitCeilingRange(8, null).Top, Is.EqualTo(8 + 3 / 0.3048).Within(1e-9)); // 切高 + 3 m
            Assert.That(ViewSyncMath.FitCeilingRange(8, 5).Top, Is.EqualTo(8 + 3 / 0.3048).Within(1e-9));    // 上一層比切高低 → 退回 +3 m
        }

        [Test]
        public void CompareModel_GuidWinsOverTitle()
        {
            var m = ViewSyncMath.ModelMatch.Match;
            Assert.That(ViewSyncMath.CompareModel("x", "ABC-1", "Snowdon", "abc-1"), Is.EqualTo(m), "有 guid 只比 guid");
            Assert.That(ViewSyncMath.CompareModel("Snowdon", "ABC-1", "Snowdon", "abc-2"), Is.EqualTo(ViewSyncMath.ModelMatch.Mismatch));
            Assert.That(ViewSyncMath.CompareModel("Snowdon", null, "snowdon", "g"), Is.EqualTo(ViewSyncMath.ModelMatch.TitleOnly));
            Assert.That(ViewSyncMath.CompareModel("Other", null, "snowdon", "g"), Is.EqualTo(ViewSyncMath.ModelMatch.Mismatch));
            Assert.That(ViewSyncMath.CompareModel(null, null, "snowdon", "g"), Is.EqualTo(ViewSyncMath.ModelMatch.Unspecified));
        }

        [Test]
        public void CompareOrigin_ToleranceOneMillimeter()
        {
            Assert.That(ViewSyncMath.CompareOrigin(new Vec3(1, 2, 3), null), Is.EqualTo(ViewSyncMath.OriginMatch.Unverified));
            Assert.That(ViewSyncMath.CompareOrigin(new Vec3(1, 2, 3), new Vec3(1, 2, 3.0005)), Is.EqualTo(ViewSyncMath.OriginMatch.Match));
            Assert.That(ViewSyncMath.CompareOrigin(new Vec3(1, 2, 3), new Vec3(1, 2, 3.01)), Is.EqualTo(ViewSyncMath.OriginMatch.Mismatch));
        }
    }
}
