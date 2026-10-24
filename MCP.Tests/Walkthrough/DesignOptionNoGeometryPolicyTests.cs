using NUnit.Framework;
using RevitMCP.Core.Walkthrough;

namespace RevitMCP.Tests.Walkthrough
{
    /// <summary>
    /// AR_CPC 實測（C:\Temp\revit-design-options-fast\design_options_export.json）的 noGeometry 分類：
    /// 非幾何類放行；帷幕板 Glazed／Wall、牆 Aluminium 等實體類必須仍算失敗。
    /// </summary>
    [TestFixture]
    public class DesignOptionNoGeometryPolicyTests
    {
        // category, builtIn, class, name, expected reason
        [TestCase("帷幕板", "OST_CurtainWallPanels", "Panel", "Empty_Panel", "empty-curtain-panel")]
        [TestCase("帷幕板", null, "FamilyInstance", "empty_panel", "empty-curtain-panel")]
        [TestCase("帷幕牆格點", "OST_CurtainGridsWall", "CurtainGridLine", "網格線", "curtain-grid-line")]
        [TestCase("Curtain Grids", null, "", "Grid Line", "curtain-grid-line")]
        [TestCase("自動繪製標註", null, "Dimension", "線性尺寸標註型式", "dimension")]
        [TestCase("Dimensions", "OST_Dimensions", "Dimension", "Linear Dimension Style", "dimension")]
        [TestCase("參考平面", "OST_CLines", "ReferencePlane", "參考平面", "reference-plane")]
        [TestCase("<草圖>", "OST_SketchLines", "ModelLine", "模型線", "sketch-model-line")]
        [TestCase("豎井開口", "OST_ShaftOpening", "Opening", "開口切割", "opening-cut")]
        [TestCase("矩形直牆開口", "OST_SWallRectOpening", "Opening", "矩形直牆開口", "opening-cut")]
        [TestCase("約束", "OST_Constraints", "Constraint", "Alignment", "constraint")]
        [TestCase(null, null, "Level", "1F Level", "level")]
        [TestCase("", null, "Sketch", "草圖", "sketch")]
        public void KnownNonGeometry_IsIgnored(string category, string builtIn, string cls, string name, string reason)
        {
            Assert.AreEqual(reason, DesignOptionNoGeometryPolicy.IgnoreReason(category, builtIn, cls, name));
        }

        // AR_CPC 可疑清單：實體類無幾何一律不放行。
        [TestCase("帷幕板", "OST_CurtainWallPanels", "Panel", "Glazed")]
        [TestCase("帷幕板", "OST_CurtainWallPanels", "Panel", "Wall")]
        [TestCase("帷幕板", null, "FamilyInstance", "Glazed")]
        [TestCase("牆", "OST_Walls", "Wall", "Aluminium")]
        [TestCase("牆", "OST_Walls", "Wall", "M_Storefront Option-2")]
        [TestCase("牆", "OST_Walls", "Wall", "Grill_Wall")]
        [TestCase(null, null, "Wall", "Aluminium")]
        [TestCase(null, null, "FamilyInstance", "Wall Profile7")]
        [TestCase("門", "OST_Doors", "FamilyInstance", "Single Glass Door")]
        [TestCase("帷幕牆豎框", "OST_CurtainWallMullions", "FamilyInstance", "Rectangular Mullion")]
        public void SolidCategories_StayFailures(string category, string builtIn, string cls, string name)
        {
            Assert.IsNull(DesignOptionNoGeometryPolicy.IgnoreReason(category, builtIn, cls, name));
        }

        [Test]
        public void EmptyPanelRule_DoesNotLeakToOtherCategoriesOrNames()
        {
            // 同名但不是帷幕板分類 → 不放行；帷幕板分類但名稱不是 Empty_Panel → 不放行。
            Assert.IsNull(DesignOptionNoGeometryPolicy.IgnoreReason("牆", "OST_Walls", "Wall", "Empty_Panel"));
            Assert.IsNull(DesignOptionNoGeometryPolicy.IgnoreReason("帷幕板", "OST_CurtainWallPanels", "Panel", "Empty_Panel_Glazed"));
        }

        [Test]
        public void LevelAndSketchRule_RequiresNoCategory()
        {
            // 有分類的元素不能只靠 class 名稱被放行。
            Assert.IsNull(DesignOptionNoGeometryPolicy.IgnoreReason("牆", "OST_Walls", "Sketch", "Aluminium"));
            Assert.IsNull(DesignOptionNoGeometryPolicy.IgnoreReason("家具", "OST_Furniture", "Level", "x"));
        }

        // ---- 2026-10-24 實機重跑後新增四類規則 ----

        private static NoGeometryFacts Panel(string name, double? w, double? h, long? host = null, bool hostExported = false, int hostTris = 0) =>
            new NoGeometryFacts
            {
                CategoryName = "帷幕板", BuiltInCategory = "OST_CurtainWallPanels", ClassName = "Panel", Name = name,
                PanelWidthFeet = w, PanelHeightFeet = h, PanelSizeSource = "parameter",
                HostWallId = host, HostWallExported = hostExported, HostWallTriangles = hostTris
            };

        [Test]
        public void ZeroSizeGlazedPanel_IsIgnoredWithBasis()
        {
            var d = DesignOptionNoGeometryPolicy.Evaluate(Panel("Glazed", 0, 0));
            Assert.IsNotNull(d);
            Assert.AreEqual("zero-size-curtain-panel", d.Reason);
            StringAssert.Contains("width=0.000000", d.Basis);
            StringAssert.Contains("height=0.000000", d.Basis);
        }

        [Test]
        public void ZeroSizePanel_ToleratesTinyError()
        {
            Assert.AreEqual("zero-size-curtain-panel", DesignOptionNoGeometryPolicy.Evaluate(Panel("Glazed", 1e-6, 5e-5)).Reason);
        }

        [TestCase(80.0 / 304.8, 80.0 / 304.8)]   // 成功匯出的 Glazed 嵌板實測 80 mm
        [TestCase(0.0, 2.5)]                      // 只有一邊為 0 不放行
        [TestCase(2.5, 0.0)]
        public void SizedGlazedPanel_StaysFailure(double w, double h)
        {
            Assert.IsNull(DesignOptionNoGeometryPolicy.Evaluate(Panel("Glazed", w, h)));
        }

        [Test]
        public void GlazedPanel_WithUnknownSize_StaysFailure()
        {
            Assert.IsNull(DesignOptionNoGeometryPolicy.Evaluate(Panel("Glazed", null, null)));
            Assert.IsNull(DesignOptionNoGeometryPolicy.Evaluate(Panel("Glazed", 0, null)));
        }

        [Test]
        public void ZeroSizeRule_OnlyForSystemPanelClass()
        {
            // 非 class Panel（例如帷幕板分類的 FamilyInstance）即使尺寸為 0 也不放行。
            var f = Panel("Glazed", 0, 0);
            f.ClassName = "FamilyInstance";
            Assert.IsNull(DesignOptionNoGeometryPolicy.Evaluate(f));
            // 非帷幕板分類的 Panel 不放行。
            f = Panel("Glazed", 0, 0);
            f.CategoryName = "牆"; f.BuiltInCategory = "OST_Walls";
            Assert.IsNull(DesignOptionNoGeometryPolicy.Evaluate(f));
        }

        [Test]
        public void WallPanel_WithExportedHostWall_IsIgnoredWithBasis()
        {
            var d = DesignOptionNoGeometryPolicy.Evaluate(Panel("Wall", 3.0, 4.0, host: 5134349, hostExported: true, hostTris: 120));
            Assert.IsNotNull(d);
            Assert.AreEqual("embedded-wall-panel", d.Reason);
            StringAssert.Contains("5134349", d.Basis);
            StringAssert.Contains("120", d.Basis);
        }

        [Test]
        public void WallPanel_HostWallNotFound_StaysFailure()
        {
            Assert.IsNull(DesignOptionNoGeometryPolicy.Evaluate(Panel("Wall", 3.0, 4.0, host: null)));
        }

        [Test]
        public void WallPanel_HostWallNotExported_StaysFailure()
        {
            Assert.IsNull(DesignOptionNoGeometryPolicy.Evaluate(Panel("Wall", 3.0, 4.0, host: 1, hostExported: false)));
            // 宿主牆標示已匯出但三角面為 0 也不放行。
            Assert.IsNull(DesignOptionNoGeometryPolicy.Evaluate(Panel("Wall", 3.0, 4.0, host: 1, hostExported: true, hostTris: 0)));
        }

        [Test]
        public void HostWallRule_DoesNotApplyToNonPanels()
        {
            var f = Panel("Wall", 3.0, 4.0, host: 1, hostExported: true, hostTris: 10);
            f.ClassName = "FamilyInstance";
            Assert.IsNull(DesignOptionNoGeometryPolicy.Evaluate(f));
        }

        [TestCase("Aluminium")]
        [TestCase("1F Level")]
        [TestCase("Grill_Wall")]
        public void SketchPlane_NoCategory_IsIgnored(string name)
        {
            var d = DesignOptionNoGeometryPolicy.Evaluate(new NoGeometryFacts { ClassName = "SketchPlane", Name = name });
            Assert.AreEqual("sketch-plane", d.Reason);
            Assert.AreEqual("sketch-plane", DesignOptionNoGeometryPolicy.IgnoreReason(null, null, "SketchPlane", name));
        }

        [Test]
        public void SketchPlane_WithCategory_StaysFailure()
        {
            Assert.IsNull(DesignOptionNoGeometryPolicy.IgnoreReason("牆", "OST_Walls", "SketchPlane", "Aluminium"));
        }

        [TestCase("Family", "Wall Profile7")]
        [TestCase("Element", "Wall Profile-1")]
        [TestCase("Element", "Wall Profile-3")]
        [TestCase("Element", "Wall Profile12")]
        public void WallProfile_NoCategory_IsIgnored(string cls, string name)
        {
            var d = DesignOptionNoGeometryPolicy.Evaluate(new NoGeometryFacts { ClassName = cls, Name = name });
            Assert.AreEqual("wall-profile-definition", d.Reason);
        }

        [TestCase("Element", "Aluminium")]
        [TestCase("Element", "Profile-1")]
        [TestCase("Family", "Generic Model")]
        [TestCase("Element", "")]
        [TestCase("FamilyInstance", "Wall Profile7")]
        public void OtherUncategorizedElements_StayFailures(string cls, string name)
        {
            Assert.IsNull(DesignOptionNoGeometryPolicy.IgnoreReason(null, null, cls, name));
        }

        [Test]
        public void WallProfile_WithCategory_StaysFailure()
        {
            Assert.IsNull(DesignOptionNoGeometryPolicy.IgnoreReason("常規模型", "OST_GenericModel", "Element", "Wall Profile-1"));
        }

        [Test]
        public void NullFacts_AreSafe()
        {
            Assert.IsNull(DesignOptionNoGeometryPolicy.Evaluate(null));
        }

        [Test]
        public void NullInputs_AreSafe()
        {
            Assert.IsNull(DesignOptionNoGeometryPolicy.IgnoreReason(null, null, null, null));
        }
    }
}
