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
        public void GetClass_僅六級坡標宜林地_其餘留空_超界丟例外()
        {
            // 山坡地土地可利用限度分類標準第 4 條：僅六級坡無條件屬宜林地。
            Assert.AreEqual("宜林地", SlopeClassifier.GetClass(6).Attribute);

            // 四、五級坡是否為宜林地另須土層深度／沖蝕程度／母岩硬度，
            // 單憑坡度不可判定，故必須留空，不得回推屬性。
            Assert.AreEqual(string.Empty, SlopeClassifier.GetClass(4).Attribute);
            Assert.AreEqual(string.Empty, SlopeClassifier.GetClass(5).Attribute);

            // 「加強保育地」以地質條件（沖蝕極嚴重／崩塌／地滑／脆弱母岩裸露）
            // 界定，與坡度無關，任何級別都不得出現此字樣。
            foreach (var cls in SlopeClassifier.Classes)
            {
                Assert.AreNotEqual("加強保育地", cls.Attribute, $"{cls.Name} 不得標為加強保育地");
            }

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
