using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using RevitMCP.Core.ViewSync;

namespace RevitMCP.Tests.ViewSync
{
    [TestFixture]
    public class CameraChangeMessageTests
    {
        private static object Row(long id, string name) => new { Id = id, Name = name, HeightAboveLevelCm = 160.0, Level = "1F" };

        [Test]
        public void Build_HasEventAndDataEnvelope()
        {
            var msg = CameraChangeMessage.Build(new object[] { Row(1, "A"), Row(2, "B") }, "Snowdon");
            Assert.That((string)msg["Event"], Is.EqualTo("camera_changed"));
            Assert.That((string)msg["Data"]["documentTitle"], Is.EqualTo("Snowdon"));
            var cameras = (JArray)msg["Data"]["cameras"];
            Assert.That(cameras.Count, Is.EqualTo(2));
            // 相機列欄位與 list_cameras 的 Cameras 列相同（PascalCase，原樣輸出）
            Assert.That((long)cameras[0]["Id"], Is.EqualTo(1));
            Assert.That((string)cameras[1]["Name"], Is.EqualTo("B"));
            Assert.That((double)cameras[0]["HeightAboveLevelCm"], Is.EqualTo(160.0));
        }

        [Test]
        public void Build_IsAnEventNotAResponse()
        {
            var msg = CameraChangeMessage.Build(new object[0], "x");
            Assert.That(msg.ContainsKey("RequestId"), Is.False);
            Assert.That(msg.ContainsKey("Success"), Is.False);
        }

        [Test]
        public void Build_NullAndEmptyCameras_GiveEmptyArray()
        {
            Assert.That(((JArray)CameraChangeMessage.Build(null, "x")["Data"]["cameras"]).Count, Is.EqualTo(0));
            Assert.That(((JArray)CameraChangeMessage.Build(new List<object>(), null)["Data"]["cameras"]).Count, Is.EqualTo(0));
        }

        [Test]
        public void Build_SkipsNullRows()
        {
            var msg = CameraChangeMessage.Build(new object[] { Row(1, "A"), null }, "x");
            Assert.That(((JArray)msg["Data"]["cameras"]).Count, Is.EqualTo(1));
        }

        [Test]
        public void Serialize_RoundTripsAsSingleLineJson()
        {
            var json = CameraChangeMessage.Serialize(CameraChangeMessage.Build(new object[] { Row(7, "中文名稱") }, "專案"));
            Assert.That(json, Does.Not.Contain("\n"));
            var back = JObject.Parse(json);
            Assert.That((string)back["Event"], Is.EqualTo("camera_changed"));
            Assert.That((string)back["Data"]["cameras"][0]["Name"], Is.EqualTo("中文名稱"));
            Assert.That((string)back["Data"]["documentTitle"], Is.EqualTo("專案"));
        }
    }
}
