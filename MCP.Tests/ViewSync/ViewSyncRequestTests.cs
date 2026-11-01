using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using RevitMCP.Core.ViewSync;

namespace RevitMCP.Tests.ViewSync
{
    [TestFixture]
    public class ViewSyncRequestTests
    {
        private static JObject Base(Action<JObject> tweak = null)
        {
            var p = JObject.Parse(@"{
              ""token"": ""x"",
              ""model"": { ""title"": ""Snowdon"", ""guid"": ""G-1"" },
              ""originOffsetMeters"": [100, 200, 10],
              ""state"": {
                ""camera"": { ""position"": [0, 10, 20], ""target"": [0, 0, 0], ""up"": [0, 1, 0], ""fovVerticalDeg"": 50, ""aspect"": 1.6, ""orthographic"": false },
                ""clipping"": { ""enabled"": true, ""boxWorld"": { ""min"": [0, 0, -5], ""max"": [10, 8, 5] } }
              }
            }");
            tweak?.Invoke(p);
            return p;
        }

        private static string CodeOf(TestDelegate act) => Assert.Throws<ViewSyncException>(act).Code;

        [Test]
        public void ApplyTemp_ParsesAllFields()
        {
            var r = ViewSyncRequestParser.ParseApplyTemp(Base());
            Assert.That(r.ModelTitle, Is.EqualTo("Snowdon")); Assert.That(r.ModelGuid, Is.EqualTo("G-1"));
            Assert.That(r.HasOrigin, Is.True); Assert.That(r.OriginOffsetMeters.X, Is.EqualTo(100));
            Assert.That(r.Camera.Position.Z, Is.EqualTo(20)); Assert.That(r.Camera.FovVerticalDeg, Is.EqualTo(50)); Assert.That(r.Camera.Aspect, Is.EqualTo(1.6));
            Assert.That(r.Camera.Orthographic, Is.False);
            Assert.That(r.Camera.FovModel, Is.EqualTo(RevitMCP.Core.ViewSync.FovCropModel.FocalPlane), "預設＝Revit 實測的焦距平面模型");
            Assert.That(r.Clipping.Enabled, Is.True); Assert.That(r.Clipping.Max.Value.Y, Is.EqualTo(8));
        }

        [Test]
        public void ApplyTemp_RequiresModelOriginAndCamera()
        {
            Assert.That(CodeOf(() => ViewSyncRequestParser.ParseApplyTemp(Base(p => p.Remove("model")))), Is.EqualTo("model_missing"));
            Assert.That(CodeOf(() => ViewSyncRequestParser.ParseApplyTemp(Base(p => p.Remove("originOffsetMeters")))), Is.EqualTo("origin_missing"));
            Assert.That(CodeOf(() => ViewSyncRequestParser.ParseApplyTemp(Base(p => ((JObject)p["state"]).Remove("camera")))), Is.EqualTo("camera_missing"));
            Assert.That(CodeOf(() => ViewSyncRequestParser.ParseApplyTemp(null)), Is.EqualTo("bad_request"));
        }

        [Test]
        public void Camera_BadVectors_AreRejected()
        {
            Assert.That(CodeOf(() => ViewSyncRequestParser.ParseApplyTemp(Base(p => p["state"]["camera"]["position"] = new JArray(1, 2)))), Is.EqualTo("bad_request"));
            Assert.That(CodeOf(() => ViewSyncRequestParser.ParseApplyTemp(Base(p => p["state"]["camera"]["position"] = new JArray(1, "a", 3)))), Is.EqualTo("bad_request"));
            Assert.That(CodeOf(() => ViewSyncRequestParser.ParseApplyTemp(Base(p => p["state"]["camera"]["fovVerticalDeg"] = "wide"))), Is.EqualTo("bad_request"));
        }

        [Test]
        public void Camera_FovModelNear_AndMissingUpDefaultsToY()
        {
            var r = ViewSyncRequestParser.ParseApplyTemp(Base(p => { p["state"]["camera"]["fovModel"] = "near"; ((JObject)p["state"]["camera"]).Remove("up"); }));
            Assert.That(r.Camera.FovModel, Is.EqualTo(RevitMCP.Core.ViewSync.FovCropModel.NearPlane));
            Assert.That(r.Camera.Up.Y, Is.EqualTo(1));
        }

        [Test]
        public void Clipping_EnabledWithoutBox_IsRejected_DisabledIgnoresBox()
        {
            Assert.That(CodeOf(() => ViewSyncRequestParser.ParseApplyTemp(Base(p => ((JObject)p["state"]["clipping"]).Remove("boxWorld")))), Is.EqualTo("bad_request"));
            var r = ViewSyncRequestParser.ParseApplyTemp(Base(p => p["state"]["clipping"]["enabled"] = false));
            Assert.That(r.Clipping.Enabled, Is.False);
        }

        [Test]
        public void Fields_AreAcceptedAtTopLevelToo()
        {
            var p = Base(x => { var cam = x["state"]["camera"]; ((JObject)x["state"]).Remove("camera"); x["camera"] = cam; });
            Assert.That(ViewSyncRequestParser.ParseApplyTemp(p).Camera.Position.Y, Is.EqualTo(10));
        }

        [Test]
        public void Hidden_StringsAndLinkObjects()
        {
            var r = ViewSyncRequestParser.ParseApplyTemp(Base(p => p["state"]["hiddenUniqueIds"] = JArray.Parse(
                @"[""abc-1"", "" abc-2 "", { ""linkTitle"": ""MEP.rvt"", ""uniqueId"": ""lnk-1"" }, { ""uniqueId"": ""abc-3"" }, 5, """", { ""linkTitle"": ""x"" }]")));
            Assert.That(r.Hidden.Count, Is.EqualTo(4));
            Assert.That(r.Hidden[1].UniqueId, Is.EqualTo("abc-2"));
            Assert.That(r.Hidden[2].LinkTitle, Is.EqualTo("MEP.rvt")); Assert.That(r.Hidden[2].UniqueId, Is.EqualTo("lnk-1"));
            Assert.That(r.Hidden[3].LinkTitle, Is.Null);
        }

        [Test]
        public void Hidden_NotArray_Rejected_TooMany_Rejected()
        {
            Assert.That(CodeOf(() => ViewSyncRequestParser.ParseApplyTemp(Base(p => p["state"]["hiddenUniqueIds"] = "abc"))), Is.EqualTo("bad_request"));
            var big = new JArray(Enumerable.Range(0, ViewSyncRequestParser.MaxHiddenEntries + 1).Select(i => (object)("id" + i)).ToArray());
            Assert.That(CodeOf(() => ViewSyncRequestParser.ParseApplyTemp(Base(p => p["state"]["hiddenUniqueIds"] = big))), Is.EqualTo("bad_request"));
        }

        [Test]
        public void Layers_ParseVisibilityAndClampTransparency()
        {
            var r = ViewSyncRequestParser.ParseApplyTemp(Base(p => p["state"]["layers"] = JArray.Parse(
                @"[{ ""source"": ""MEP.rvt"", ""visible"": false }, { ""source"": ""(host)"", ""visible"": true, ""transparency"": 0.4 }, { ""source"": ""S"", ""transparency"": 7 }, { ""source"": """" }]")));
            Assert.That(r.Layers.Count, Is.EqualTo(3));
            Assert.That(r.Layers[0].Visible, Is.False);
            Assert.That(r.Layers[1].Transparency, Is.EqualTo(0.4));
            Assert.That(r.Layers[2].Transparency, Is.EqualTo(1.0), "超出 0–1 夾回");
            Assert.That(r.Layers[2].Visible, Is.True, "沒寫 visible 預設顯示");
        }

        [Test]
        public void ModeAndDesignOptions()
        {
            var r = ViewSyncRequestParser.ParseApplyTemp(Base(p =>
            {
                p["state"]["mode"] = "consistent";
                p["state"]["designOptions"] = JObject.Parse(@"{ ""setA"": ""opt1"", ""setB"": ""opt2"", ""bad"": 5 }");
            }));
            Assert.That(r.Mode, Is.EqualTo("consistent"));
            Assert.That(r.DesignOptions.Count, Is.EqualTo(2)); Assert.That(r.DesignOptions["setA"], Is.EqualTo("opt1"));
        }

        [Test]
        public void OpenView_RequiresViewUniqueId_ModelOptional()
        {
            var r = ViewSyncRequestParser.ParseOpenView(JObject.Parse(@"{ ""token"": ""x"", ""viewUniqueId"": ""u-1"" }"));
            Assert.That(r.ViewUniqueId, Is.EqualTo("u-1"));
            Assert.That(CodeOf(() => ViewSyncRequestParser.ParseOpenView(JObject.Parse(@"{ ""viewUniqueId"": "" "" }"))), Is.EqualTo("bad_request"));
        }

        // ---------------------------------------------------------------- create_view

        [Test]
        public void CreateView_3d_NeedsCamera_AndName()
        {
            var ok = ViewSyncRequestParser.ParseCreateView(Base(p => { p["kind"] = "3D"; p["name"] = "IM-1"; }));
            Assert.That(ok.Kind, Is.EqualTo("3d")); Assert.That(ok.Name, Is.EqualTo("IM-1"));
            Assert.That(CodeOf(() => ViewSyncRequestParser.ParseCreateView(Base(p => { p["kind"] = "3d"; }))), Is.EqualTo("bad_request"));
            Assert.That(CodeOf(() => ViewSyncRequestParser.ParseCreateView(Base(p => { p["kind"] = "3d"; p["name"] = "a"; ((JObject)p["state"]).Remove("camera"); }))), Is.EqualTo("camera_missing"));
            Assert.That(CodeOf(() => ViewSyncRequestParser.ParseCreateView(Base(p => { p["kind"] = "elevation"; p["name"] = "a"; }))), Is.EqualTo("bad_request"));
        }

        [Test]
        public void CreateView_Section_ParsesPlane()
        {
            var r = ViewSyncRequestParser.ParseCreateView(Base(p =>
            {
                p["kind"] = "section"; p["name"] = "S";
                p["plane"] = JObject.Parse(@"{ ""origin"": [1,2,3], ""normal"": [0,0,-1], ""right"": [1,0,0], ""up"": [0,1,0], ""width"": 10, ""height"": 4, ""depth"": 6 }");
            }));
            Assert.That(r.Plane.Width, Is.EqualTo(10)); Assert.That(r.Plane.Right.Value.X, Is.EqualTo(1)); Assert.That(r.Plane.Normal.Z, Is.EqualTo(-1));
            Assert.That(CodeOf(() => ViewSyncRequestParser.ParseCreateView(Base(p => { p["kind"] = "section"; p["name"] = "S"; }))), Is.EqualTo("plane_missing"));
            Assert.That(CodeOf(() => ViewSyncRequestParser.ParseCreateView(Base(p =>
            {
                p["kind"] = "section"; p["name"] = "S";
                p["plane"] = JObject.Parse(@"{ ""origin"": [1,2,3], ""normal"": [0,0,-1], ""up"": [0,1,0], ""width"": 10, ""height"": 4 }");
            }))), Is.EqualTo("plane_invalid"));
        }

        [TestCase("plan")]
        [TestCase("ceiling")]
        public void CreateView_PlanAndCeiling_CutHeightFromHintOrBoxTop(string kind)
        {
            var viaHint = ViewSyncRequestParser.ParseCreateView(Base(p => { p["kind"] = kind; p["name"] = "P"; p["levelHint"] = JObject.Parse(@"{ ""cutHeightMeters"": 3.5, ""levelUniqueId"": ""lv"" }"); }));
            Assert.That(viaHint.CutHeightMeters, Is.EqualTo(3.5)); Assert.That(viaHint.LevelUniqueId, Is.EqualTo("lv"));
            var viaBox = ViewSyncRequestParser.ParseCreateView(Base(p => { p["kind"] = kind; p["name"] = "P"; }));
            Assert.That(viaBox.CutHeightMeters, Is.EqualTo(8), "剖面框頂面（glTF Y）");
            Assert.That(CodeOf(() => ViewSyncRequestParser.ParseCreateView(Base(p => { p["kind"] = kind; p["name"] = "P"; p["state"]["clipping"]["enabled"] = false; }))), Is.EqualTo("level_hint_missing"));
        }
    }
}
