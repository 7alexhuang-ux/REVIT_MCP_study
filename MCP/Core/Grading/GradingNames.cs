using System.Text;

namespace RevitMCP.Core.Grading
{
    /// <summary>
    /// 整地模組的元素命名工具（純函式，無 Revit API 相依）。
    /// </summary>
    public static class GradingNames
    {
        // Revit 元素名稱禁字全集（「Name cannot include prohibited characters」）：\ : { } [ ] | ; < > ? ` ~
        private static readonly char[] ProhibitedCharacters =
            { '\\', ':', '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~' };

        /// <summary>
        /// 把 Revit 禁止出現在元素名稱的字元一律換成連字號。
        /// 方案名稱（如「Slope 1:2」）會流入視圖與圖紙名稱，未消毒會使整個方案出圖失敗。
        /// </summary>
        public static string SanitizeElementName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(name.Length);
            foreach (var character in name)
            {
                builder.Append(System.Array.IndexOf(ProhibitedCharacters, character) >= 0 ? '-' : character);
            }

            return builder.ToString();
        }
    }
}
