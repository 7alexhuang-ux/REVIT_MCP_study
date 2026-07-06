using System.Collections.Generic;
using NUnit.Framework;
using RevitMCP.Core.Grading;

namespace RevitMCP.Tests.Grading
{
    [TestFixture]
    public class GradingSourceResolverTests
    {
        private static List<SchemeSourceSummary> TwoSchemesSharingOriginal()
        {
            // 原地形 T0 = 1000；方案一設計 = 2001，方案二設計 = 2002，皆自 1000 出發。
            return new List<SchemeSourceSummary>
            {
                new SchemeSourceSummary(2001, 1000, "外延 5m"),
                new SchemeSourceSummary(2002, 1000, "放坡 1:2"),
            };
        }

        [Test]
        public void Resolve_SelectedIsDesignResult_RedirectsToItsOriginal()
        {
            var result = GradingSourceResolver.Resolve(TwoSchemesSharingOriginal(), 2001);
            Assert.That(result.SourceId, Is.EqualTo(1000));
            Assert.That(result.Redirected, Is.True);
            Assert.That(result.RedirectedFromScheme, Is.EqualTo("外延 5m"));
        }

        [Test]
        public void Resolve_SelectedIsSecondDesign_StillRedirectsToSharedOriginal()
        {
            var result = GradingSourceResolver.Resolve(TwoSchemesSharingOriginal(), 2002);
            Assert.That(result.SourceId, Is.EqualTo(1000));
            Assert.That(result.Redirected, Is.True);
        }

        [Test]
        public void Resolve_SelectedIsTheOriginal_ReturnsUnchanged()
        {
            var result = GradingSourceResolver.Resolve(TwoSchemesSharingOriginal(), 1000);
            Assert.That(result.SourceId, Is.EqualTo(1000));
            Assert.That(result.Redirected, Is.False);
        }

        [Test]
        public void Resolve_EmptyRegistry_ReturnsSelectedUnchanged()
        {
            var result = GradingSourceResolver.Resolve(new List<SchemeSourceSummary>(), 5000);
            Assert.That(result.SourceId, Is.EqualTo(5000));
            Assert.That(result.Redirected, Is.False);
        }

        [Test]
        public void Resolve_NullSchemes_ReturnsSelectedUnchanged()
        {
            var result = GradingSourceResolver.Resolve(null, 5000);
            Assert.That(result.SourceId, Is.EqualTo(5000));
            Assert.That(result.Redirected, Is.False);
        }
    }
}
