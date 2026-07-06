using NUnit.Framework;
using RevitMCP.Core.Grading;

namespace RevitMCP.Tests.Grading
{
    /// <summary>
    /// GradingNames.SanitizeElementName：Revit 元素名稱禁字消毒。
    /// 實機教訓：方案名「Slope 1:2」流入視圖/圖紙名稱時，Revit 以
    /// 「Name cannot include prohibited characters」拒絕，整個方案出圖失敗。
    /// </summary>
    [TestFixture]
    public class GradingNamesTests
    {
        [Test]
        public void Sanitize_SlopeRatioColon_ReplacedWithDash()
        {
            Assert.That(GradingNames.SanitizeElementName("Slope 1:2"), Is.EqualTo("Slope 1-2"));
        }

        [Test]
        public void Sanitize_AllRevitProhibitedCharacters_Replaced()
        {
            // Revit 禁字全集：\ : { } [ ] | ; < > ? ` ~
            var input = "a\\b:c{d}e[f]g|h;i<j>k?l`m~n";
            Assert.That(GradingNames.SanitizeElementName(input), Is.EqualTo("a-b-c-d-e-f-g-h-i-j-k-l-m-n"));
        }

        [Test]
        public void Sanitize_CleanName_Unchanged()
        {
            Assert.That(GradingNames.SanitizeElementName("整地-Offset 5m"), Is.EqualTo("整地-Offset 5m"));
        }

        [Test]
        public void Sanitize_Null_ReturnsEmpty()
        {
            Assert.That(GradingNames.SanitizeElementName(null), Is.EqualTo(string.Empty));
        }
    }
}
