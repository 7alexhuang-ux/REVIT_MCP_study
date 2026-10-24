namespace RevitMCP.Core.Grading
{
    /// <summary>Pure policy for Ribbon/output title block selection. Revit binding happens in CommandExecutor.</summary>
    public sealed class TitleBlockChoice
    {
        public const long BlankSentinelId = -1;

        private TitleBlockChoice(bool isAutomatic, bool isBlank, long? explicitId)
        {
            IsAutomatic = isAutomatic;
            IsBlank = isBlank;
            ExplicitId = explicitId;
        }

        public bool IsAutomatic { get; }

        public bool IsBlank { get; }

        public long? ExplicitId { get; }

        public static TitleBlockChoice FromParameter(long? titleBlockId)
        {
            if (!titleBlockId.HasValue || titleBlockId.Value == 0)
            {
                return new TitleBlockChoice(isAutomatic: true, isBlank: false, explicitId: null);
            }

            if (titleBlockId.Value == BlankSentinelId)
            {
                return new TitleBlockChoice(isAutomatic: false, isBlank: true, explicitId: null);
            }

            return new TitleBlockChoice(isAutomatic: false, isBlank: false, explicitId: titleBlockId.Value);
        }
    }
}
