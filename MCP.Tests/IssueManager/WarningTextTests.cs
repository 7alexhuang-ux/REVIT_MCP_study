using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using RevitMCP.Core.IssueManager;

namespace RevitMCP.Tests.IssueManager
{
    /// <summary>D-813：伺服器警告碼 → 白話。每個已知碼都有白話、白話不含代碼；未知碼只顯示通用句。</summary>
    [TestFixture]
    public class WarningTextTests
    {
        [Test]
        public void EveryKnownCodeHasPlainChineseAndNeverShowsTheCode()
        {
            foreach (var code in WarningText.KnownCodes)
            {
                var text = WarningText.Describe(code + ":3");
                Assert.AreNotEqual(WarningText.Unknown, text, "已知碼不可落到通用句：" + code);
                StringAssert.DoesNotContain(code, text);
                Assert.IsFalse(Regex.IsMatch(text, "[a-z]+_[a-z_0-9]+"), "白話不可含底線英文碼：" + text);
                Assert.IsTrue(Regex.IsMatch(text, "[\u4e00-\u9fff]"), "要有中文：" + text);
            }
        }

        [Test]
        public void KnownCodesCoverTheCodesTheServerProducesAccordingToTheReview()
        {
            foreach (var code in new[] { "params_invalid", "named_views_skipped", "non_3d_viewports_without_positions", "viewports_without_model", "params_not_uploadable" })
                CollectionAssert.Contains(WarningText.KnownCodes, code);
        }

        [Test]
        public void CountParametersAreShownInPlainSentences()
        {
            Assert.AreEqual("有 3 個具名視圖沒有匯出（資料不完整或格式不合），其他視圖正常。", WarningText.Describe("named_views_skipped:3"));
            StringAssert.Contains("2 個平面、立面或剖面視圖沒有位置資料", WarningText.Describe("non_3d_viewports_without_positions:2"));
            StringAssert.Contains("4 個 3D 視圖放在圖紙上", WarningText.Describe("viewports_without_model:4"));
            StringAssert.Contains("面積表沿用上一版", WarningText.Describe("params_invalid"));
        }

        [Test]
        public void UnknownViewShowsSheetNumberButNotTheUniqueId()
        {
            var text = WarningText.Describe("unknown_view:A-101:3f2a9c-uid-0001");
            StringAssert.Contains("A-101", text);
            StringAssert.DoesNotContain("3f2a9c", text);
            StringAssert.DoesNotContain("unknown_view", WarningText.Describe("unknown_view::uid-only"));
        }

        [Test]
        public void UnknownOrBrokenCodesGiveTheGenericSentenceWithoutTheCode()
        {
            foreach (var code in new[] { "totally_new_code:9", "PARAMS_INVALID", "", "   ", null, "測試警告" })
            {
                var text = WarningText.Describe(code);
                Assert.AreEqual(WarningText.Unknown, text);
                if (!string.IsNullOrWhiteSpace(code)) StringAssert.DoesNotContain(code, text);
            }
            StringAssert.DoesNotContain("abc", WarningText.Describe("named_views_skipped:abc"));
        }

        [Test]
        public void DescribeAllDropsDuplicateGenericSentencesAndKeepsOrder()
        {
            var list = WarningText.DescribeAll(new[] { "params_invalid", "new_a", "new_b", "named_views_skipped:1" });
            Assert.AreEqual(3, list.Count);
            Assert.AreEqual(WarningText.Describe("params_invalid"), list[0]);
            Assert.AreEqual(WarningText.Unknown, list[1]);
            Assert.AreEqual(0, WarningText.DescribeAll(null).Count);
            Assert.IsFalse(list.Any(item => item.Contains("new_a")));
        }
    }
}
