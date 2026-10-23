using System;
using System.Text;
using NUnit.Framework;
using RevitMCP.Core.Walkthrough;

namespace RevitMCP.Tests.Walkthrough
{
    [TestFixture]
    public class DesignOptionExtrasTests
    {
        [TestCase(true)]
        [TestCase(false)]
        public void BuildGlb_OptionNodePreservesStableKeysAndEscapesNames(bool primary)
        {
            var builder = Triangle(new GltfElementInfo
            {
                ElementId = 42, UniqueId = "element-uid", Category = "Walls", Family = "Basic Wall", Source = "(host)",
                DesignOption = new GltfDesignOptionInfo
                {
                    SetKey = "set-uid", SetName = "集合\"甲\"", OptionKey = "option-uid", OptionName = "方案\\A\n下一行", IsPrimary = primary
                }
            });
            var json = Json(builder);
            StringAssert.Contains("\"elementId\":42", json);
            StringAssert.Contains("\"uniqueId\":\"element-uid\"", json);
            StringAssert.Contains("\"category\":\"Walls\"", json);
            StringAssert.Contains("\"family\":\"Basic Wall\"", json);
            StringAssert.Contains("\"source\":\"(host)\"", json);
            StringAssert.Contains("\"designOption\":{\"setKey\":\"set-uid\",\"setName\":\"集合\\\"甲\\\"\",\"optionKey\":\"option-uid\",\"optionName\":\"方案\\\\A\\n下一行\",\"isPrimary\":" + (primary ? "true" : "false") + "}", json);
        }

        [Test]
        public void BuildGlb_MainModelOmitsDesignOptionFieldEntirely()
        {
            var json = Json(Triangle(new GltfElementInfo { ElementId = 1, UniqueId = "main-element" }));
            StringAssert.DoesNotContain("designOption", json);
            StringAssert.Contains("\"uniqueId\":\"main-element\"", json);
        }

        private static GltfBuilder Triangle(GltfElementInfo info)
        {
            var builder = new GltfBuilder();
            var material = builder.AddMaterial("Default", 0.5, 0.5, 0.5, 1);
            builder.BeginElement(info);
            builder.AddTriangles(material, new[] { Vec3.Zero, new Vec3(1, 0, 0), new Vec3(0, 1, 0) }, null, new[] { 0, 1, 2 });
            builder.EndElement();
            return builder;
        }

        private static string Json(GltfBuilder builder)
        {
            var glb = builder.BuildGlb();
            return Encoding.UTF8.GetString(glb, 20, BitConverter.ToInt32(glb, 12)).TrimEnd(' ');
        }
    }
}
