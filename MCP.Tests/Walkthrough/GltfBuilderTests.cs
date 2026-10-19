using System;
using System.Text;
using NUnit.Framework;
using RevitMCP.Core.Walkthrough;

namespace RevitMCP.Tests.Walkthrough
{
    [TestFixture]
    public class GltfBuilderTests
    {
        private static void ParseGlb(byte[] glb, out string json, out byte[] bin)
        {
            Assert.That(BitConverter.ToUInt32(glb, 0), Is.EqualTo(0x46546C67u), "magic");
            Assert.That(BitConverter.ToUInt32(glb, 4), Is.EqualTo(2u), "version");
            Assert.That(BitConverter.ToUInt32(glb, 8), Is.EqualTo((uint)glb.Length), "total length");
            Assert.That(glb.Length % 4, Is.EqualTo(0));

            var jsonLength = (int)BitConverter.ToUInt32(glb, 12);
            Assert.That(BitConverter.ToUInt32(glb, 16), Is.EqualTo(0x4E4F534Au), "JSON chunk type");
            Assert.That(jsonLength % 4, Is.EqualTo(0));
            json = Encoding.UTF8.GetString(glb, 20, jsonLength).TrimEnd(' ');

            var binStart = 20 + jsonLength;
            if (binStart == glb.Length)
            {
                bin = new byte[0];
                return;
            }

            var binLength = (int)BitConverter.ToUInt32(glb, binStart);
            Assert.That(BitConverter.ToUInt32(glb, binStart + 4), Is.EqualTo(0x004E4942u), "BIN chunk type");
            Assert.That(binStart + 8 + binLength, Is.EqualTo(glb.Length));
            bin = new byte[binLength];
            Buffer.BlockCopy(glb, binStart + 8, bin, 0, binLength);
        }

        [Test]
        public void BuildGlb_SingleTriangle_ValidStructureAndBounds()
        {
            var builder = new GltfBuilder();
            var red = builder.AddMaterial("Brick", 0.8, 0.2, 0.1, 1.0);
            builder.AddTriangles(red,
                new[] { new Vec3(0, 0, 0), new Vec3(2, 0, 0), new Vec3(0, 3, -1) },
                null,
                new[] { 0, 1, 2 });

            var glb = builder.BuildGlb();
            ParseGlb(glb, out var json, out var bin);

            Assert.That(builder.TriangleCount, Is.EqualTo(1));
            Assert.That(builder.VertexCount, Is.EqualTo(3));
            Assert.That(builder.MaterialCount, Is.EqualTo(1));
            StringAssert.Contains("\"version\":\"2.0\"", json);
            StringAssert.Contains("\"POSITION\":0", json);
            StringAssert.Contains("\"min\":[0,0,-1]", json);
            StringAssert.Contains("\"max\":[2,3,0]", json);
            // baseColorFactor is linear: sRGB 0.8/0.2/0.1 -> 0.6038/0.0331/0.0100; the sRGB values stay in material extras.
            StringAssert.Contains("\"baseColorFactor\":[0.6038", json);
            StringAssert.Contains("\"srgb\":[0.8,0.2,0.1]", json);
            StringAssert.DoesNotContain("BLEND", json);
            StringAssert.Contains("\"byteLength\":" + bin.Length, json);

            // positions 9 floats + normals 9 floats + 3 uint indices = 84 bytes.
            Assert.That(bin.Length, Is.EqualTo(84));
            Assert.That(BitConverter.ToSingle(bin, 12), Is.EqualTo(2f)); // second vertex X
            Assert.That(BitConverter.ToUInt32(bin, 72 + 8), Is.EqualTo(2u)); // third index
        }

        [Test]
        public void BuildGlb_ComputedNormalFollowsWindingOrder()
        {
            var builder = new GltfBuilder();
            var m = builder.AddMaterial("Floor", 0.5, 0.5, 0.5, 1);
            // (b - a) x (c - a) points to +Y for this winding.
            builder.AddTriangles(m, new[] { new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(0, 0, -1) }, null, new[] { 0, 1, 2 });
            ParseGlb(builder.BuildGlb(), out _, out var bin);

            Assert.That(BitConverter.ToSingle(bin, 36), Is.EqualTo(0f).Within(1e-6));
            Assert.That(BitConverter.ToSingle(bin, 40), Is.EqualTo(1f).Within(1e-6));
            Assert.That(BitConverter.ToSingle(bin, 44), Is.EqualTo(0f).Within(1e-6));
        }

        [Test]
        public void AddMaterial_DeduplicatesQuantizedColorsAndSkipsEmptyMaterials()
        {
            var builder = new GltfBuilder();
            var a = builder.AddMaterial("A", 0.5, 0.5, 0.5, 1);
            var b = builder.AddMaterial("B", 0.5001, 0.5, 0.5, 1);
            var glass = builder.AddMaterial("Glass", 0.6, 0.8, 0.9, 0.3);
            var unused = builder.AddMaterial("Unused", 0, 0, 1, 1);
            Assert.That(b, Is.EqualTo(a));
            Assert.That(glass, Is.Not.EqualTo(a));

            var quad = new[] { new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(1, 1, 0), new Vec3(0, 1, 0) };
            builder.AddTriangles(a, quad, null, new[] { 0, 1, 2, 0, 2, 3 });
            builder.AddTriangles(glass, quad, null, new[] { 0, 1, 2 });
            builder.AddTriangles(a, quad, null, new[] { 0, 2, 3 });

            ParseGlb(builder.BuildGlb(), out var json, out _);
            Assert.That(builder.TriangleCount, Is.EqualTo(4));
            Assert.That(builder.MaterialCount, Is.EqualTo(2));
            StringAssert.Contains("\"alphaMode\":\"BLEND\"", json);
            StringAssert.DoesNotContain("Unused", json);
            // Second batch appended to the same bucket is re-based: indices 4..7 exist.
            StringAssert.Contains("\"count\":9,\"type\":\"SCALAR\"", json);
            Assert.That(unused, Is.GreaterThanOrEqualTo(0));
        }

        [Test]
        public void BuildGlb_EmptyBuilder_IsStillValid()
        {
            var glb = new GltfBuilder().BuildGlb();
            ParseGlb(glb, out var json, out var bin);
            Assert.That(bin.Length, Is.EqualTo(0));
            StringAssert.Contains("\"nodes\":[]", json);
        }

        [Test]
        public void AddTriangles_ValidatesInput()
        {
            var builder = new GltfBuilder();
            var m = builder.AddMaterial("M", 1, 1, 1, 1);
            var tri = new[] { new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(0, 1, 0) };
            Assert.Throws<ArgumentOutOfRangeException>(() => builder.AddTriangles(5, tri, null, new[] { 0, 1, 2 }));
            Assert.Throws<ArgumentException>(() => builder.AddTriangles(m, tri, null, new[] { 0, 1 }));
            Assert.Throws<ArgumentOutOfRangeException>(() => builder.AddTriangles(m, tri, null, new[] { 0, 1, 3 }));
            Assert.Throws<ArgumentException>(() => builder.AddTriangles(m, tri, new[] { new Vec3(0, 0, 1) }, new[] { 0, 1, 2 }));
        }

        [Test]
        public void MaterialName_IsJsonEscaped()
        {
            var builder = new GltfBuilder();
            var m = builder.AddMaterial("玻璃 \"清\"\\A", 0.2, 0.2, 0.2, 1);
            builder.AddTriangles(m, new[] { new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(0, 1, 0) }, null, new[] { 0, 1, 2 });
            ParseGlb(builder.BuildGlb(), out var json, out _);
            StringAssert.Contains("\"name\":\"玻璃 \\\"清\\\"\\\\A\"", json);
        }

        [Test]
        public void BeginElement_WritesOneNodePerElementWithExtras()
        {
            var builder = new GltfBuilder();
            var red = builder.AddMaterial("Red", 1, 0, 0, 1);
            var blue = builder.AddMaterial("Blue", 0, 0, 1, 1);
            var tri = new[] { new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(0, 1, 0) };

            builder.BeginElement(new GltfElementInfo { ElementId = 12345, UniqueId = "uid-1", Category = "牆", Family = "基本牆", Name = "W1" });
            builder.AddTriangles(red, tri, null, new[] { 0, 1, 2 });
            builder.AddTriangles(blue, tri, null, new[] { 0, 1, 2 });
            builder.EndElement();
            builder.BeginElement(new GltfElementInfo { ElementId = 67890, UniqueId = "uid-2", Category = "門", Family = "單扇門" });
            builder.AddTriangles(red, tri, null, new[] { 0, 1, 2 });
            builder.EndElement();

            Assert.That(builder.ElementNodeCount, Is.EqualTo(2));
            Assert.That(builder.TriangleCount, Is.EqualTo(3));
            Assert.That(builder.MaterialCount, Is.EqualTo(2));

            ParseGlb(builder.BuildGlb(), out var json, out var bin);
            StringAssert.Contains("\"extras\":{\"elementId\":12345,\"uniqueId\":\"uid-1\",\"category\":\"牆\",\"family\":\"基本牆\",\"source\":\"(host)\"}", json);
            StringAssert.Contains("\"extras\":{\"elementId\":67890,\"uniqueId\":\"uid-2\",\"category\":\"門\",\"family\":\"單扇門\",\"source\":\"(host)\"}", json);
            StringAssert.Contains("\"scenes\":[{\"nodes\":[0,1]}]", json);
            // element 1: 2 primitives (red+blue), element 2: 1 primitive; 3 primitives x (pos+normal+index) accessors.
            StringAssert.Contains("\"name\":\"Element 67890\"", json);
            Assert.That(bin.Length, Is.EqualTo(3 * (3 * 12 + 3 * 12 + 3 * 4)));
        }

        [Test]
        public void EndElement_WithoutGeometry_AddsNoNode()
        {
            var builder = new GltfBuilder();
            builder.BeginElement(new GltfElementInfo { ElementId = 1 });
            builder.EndElement();
            Assert.That(builder.ElementNodeCount, Is.EqualTo(0));
        }

        [Test]
        public void MergedAndElementGeometry_CanCoexist()
        {
            var builder = new GltfBuilder();
            var m = builder.AddMaterial("M", 1, 1, 1, 1);
            var tri = new[] { new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(0, 1, 0) };
            builder.AddTriangles(m, tri, null, new[] { 0, 1, 2 });
            builder.BeginElement(new GltfElementInfo { ElementId = 7 });
            builder.AddTriangles(m, tri, null, new[] { 0, 1, 2 });
            builder.EndElement();

            ParseGlb(builder.BuildGlb(), out var json, out _);
            StringAssert.Contains("\"name\":\"RevitModel\",\"mesh\":0", json);
            StringAssert.Contains("\"elementId\":7", json);
            Assert.That(builder.MaterialCount, Is.EqualTo(1));
        }
    }
}
