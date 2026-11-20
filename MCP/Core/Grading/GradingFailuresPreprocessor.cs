#if REVIT2024_OR_GREATER
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace RevitMCP.Core.Grading
{
    /// <summary>
    /// 整地交易的失敗預處理：自動刪除可忽略的警告（如元素重疊），
    /// 避免模態對話框阻塞無人值守的 MCP 呼叫（方案三曾因此阻塞 126.9 秒）。
    /// 錯誤（Error 以上）不處理，交由既有回滾機制。
    /// </summary>
    internal sealed class GradingFailuresPreprocessor : IFailuresPreprocessor
    {
        private readonly List<string> _dismissedWarnings = new List<string>();

        public IReadOnlyList<string> DismissedWarnings => _dismissedWarnings;

        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            foreach (var failure in failuresAccessor.GetFailureMessages())
            {
                if (failure.GetSeverity() == FailureSeverity.Warning)
                {
                    _dismissedWarnings.Add(failure.GetDescriptionText());
                    failuresAccessor.DeleteWarning(failure);
                }
            }

            return FailureProcessingResult.Continue;
        }

        public static void Attach(Transaction transaction, GradingFailuresPreprocessor preprocessor)
        {
            var options = transaction.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(preprocessor);
            options.SetClearAfterRollback(true);
            transaction.SetFailureHandlingOptions(options);
        }
    }
}
#endif
