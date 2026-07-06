using System;
using NUnit.Framework;
using RevitMCP.Core.Grading;

namespace RevitMCP.Tests.Grading
{
    [TestFixture]
    public class SlopeClassifierTests
    {
        // 水保六級坡右閉邊界：≤5→1、(5,15]→2、(15,30]→3、(30,40]→4、(40,55]→5、>55→6。
        [TestCase(0.0, 1)]
        [TestCase(5.0, 1)]
        [TestCase(5.01, 2)]
        [TestCase(15.0, 2)]
        [TestCase(15.01, 3)]
        [TestCase(30.0, 3)]
        [TestCase(30.01, 4)]
        [TestCase(40.0, 4)]
        [TestCase(40.01, 5)]
        [TestCase(55.0, 5)]
        [TestCase(55.01, 6)]
        [TestCase(120.0, 6)]
        public void Classify_六級坡邊界正確(double slopePercent, int expectedLevel)
        {
            Assert.AreEqual(expectedLevel, SlopeClassifier.Classify(slopePercent));
        }

        [Test]
        public void Classify_負值或非有限數值_丟例外()
        {
            Assert.Throws<ArgumentException>(() => SlopeClassifier.Classify(-1));
            Assert.Throws<ArgumentException>(() => SlopeClassifier.Classify(double.NaN));
            Assert.Throws<ArgumentException>(() => SlopeClassifier.Classify(double.PositiveInfinity));
        }

        [Test]
        public void GetClass_五級坡宜林地_六級坡加強保育地_超界丟例外()
        {
            Assert.AreEqual("宜林地", SlopeClassifier.GetClass(5).Attribute);
            Assert.AreEqual("加強保育地", SlopeClassifier.GetClass(6).Attribute);
            Assert.Throws<ArgumentOutOfRangeException>(() => SlopeClassifier.GetClass(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => SlopeClassifier.GetClass(7));
        }

        [Test]
        public void Classes_共六級且級別遞增()
        {
            Assert.AreEqual(6, SlopeClassifier.Classes.Count);
            for (var i = 0; i < SlopeClassifier.Classes.Count; i++)
            {
                Assert.AreEqual(i + 1, SlopeClassifier.Classes[i].Level);
            }
        }
    }
}
