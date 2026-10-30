using System;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using RevitMCP.Core.Walkthrough;

namespace RevitMCP.Tests.ViewSync
{
    [TestFixture]
    public class GltfRevitModelTests
    {
        private static JObject SceneExtras(GltfBuilder builder)
        {
            var m = builder.AddMaterial("m", 1, 1, 1, 1);
            builder.BeginElement(new GltfElementInfo { ElementId = 1, Category = "Walls", Source = "(host)" });
            builder.AddTriangles(m, new[] { new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(0, 1, 0) }, null, new[] { 0, 1, 2 });
            builder.EndElement();
            var glb = builder.BuildGlb();
            var json = JObject.Parse(Encoding.UTF8.GetString(glb, 20, (int)BitConverter.ToUInt32(glb, 12)));
            return (JObject)json["scenes"][0]["extras"];
        }

        [Test]
        public void SceneExtras_CarryRevitModel_NextToOriginOffset()
        {
            var extras = SceneExtras(new GltfBuilder
            {
                OriginOffsetMeters = new Vec3(1, 2, 3),
                RevitModel = new RevitModelInfo { Title = "Snowdon \"A\"", Path = @"C:\Models\Snowdon.rvt", Guid = "0d2f7a1e-0000-4000-8000-000000000001" },
            });
            Assert.That(extras["originOffsetMeters"].ToObject<double[]>(), Is.EqualTo(new[] { 1.0, 2.0, 3.0 }));
            Assert.That((string)extras["revitModel"]["title"], Is.EqualTo("Snowdon \"A\""));
            Assert.That((string)extras["revitModel"]["path"], Is.EqualTo(@"C:\Models\Snowdon.rvt"));
            Assert.That((string)extras["revitModel"]["guid"], Is.EqualTo("0d2f7a1e-0000-4000-8000-000000000001"));
        }

        [Test]
        public void SceneExtras_NoRevitModel_NoRevitModelKey()
        {
            var extras = SceneExtras(new GltfBuilder { OriginOffsetMeters = new Vec3(1, 2, 3) });
            Assert.That(extras["revitModel"], Is.Null);
        }
    }
}
