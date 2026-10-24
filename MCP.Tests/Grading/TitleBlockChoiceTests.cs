using NUnit.Framework;
using RevitMCP.Core.Grading;

namespace RevitMCP.Tests.Grading
{
    [TestFixture]
    public class TitleBlockChoiceTests
    {
        [Test]
        public void FromParameter_Null_UsesAutomatic()
        {
            var choice = TitleBlockChoice.FromParameter(null);

            Assert.That(choice.IsAutomatic, Is.True);
            Assert.That(choice.IsBlank, Is.False);
            Assert.That(choice.ExplicitId, Is.Null);
        }

        [Test]
        public void FromParameter_BlankSentinel_UsesBlankSheet()
        {
            var choice = TitleBlockChoice.FromParameter(TitleBlockChoice.BlankSentinelId);

            Assert.That(choice.IsAutomatic, Is.False);
            Assert.That(choice.IsBlank, Is.True);
            Assert.That(choice.ExplicitId, Is.Null);
        }

        [Test]
        public void FromParameter_PositiveId_UsesExplicitTitleBlock()
        {
            var choice = TitleBlockChoice.FromParameter(123);

            Assert.That(choice.IsAutomatic, Is.False);
            Assert.That(choice.IsBlank, Is.False);
            Assert.That(choice.ExplicitId, Is.EqualTo(123));
        }

        [Test]
        public void FromParameter_Zero_UsesAutomatic()
        {
            var choice = TitleBlockChoice.FromParameter(0);

            Assert.That(choice.IsAutomatic, Is.True);
        }
    }
}
