using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

#if REVIT2025_OR_GREATER
using IdType = System.Int64;
#else
using IdType = System.Int32;
#endif

namespace RevitMCP.Core
{
    public partial class CommandExecutor
    {
        #region 族群載入 (load_family)

        /// <summary>
        /// overwrite=true 時讓既有同名族群被覆蓋（含參數值）；false 時保留專案內既有版本。
        /// </summary>
        private class LoadFamilyToolOptions : IFamilyLoadOptions
        {
            private readonly bool _overwrite;

            public LoadFamilyToolOptions(bool overwrite)
            {
                _overwrite = overwrite;
            }

            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                overwriteParameterValues = _overwrite;
                return _overwrite;
            }

            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
            {
                source = FamilySource.Family;
                overwriteParameterValues = _overwrite;
                return _overwrite;
            }
        }

        /// <summary>
        /// 從 .rfa 載入族群到目前專案。可用 typeNames 只載入指定類型（LoadFamilySymbol）。
        /// 族群已存在且 overwrite=false 時不視為失敗，直接回傳既有族群（AlreadyLoaded=true）；
        /// 此時若有指定 typeNames，仍會補載專案內尚未存在的類型。
        /// </summary>
        private object LoadFamily(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;

            string familyPath = parameters["familyPath"]?.Value<string>();
            bool overwrite = parameters["overwrite"]?.Value<bool>() ?? false;
            var typeNames = (parameters["typeNames"] as JArray)?
                .Select(t => t?.Value<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n.Trim())
                .Distinct()
                .ToList() ?? new List<string>();

            if (string.IsNullOrWhiteSpace(familyPath))
                return LoadFamilyError("請指定 familyPath（.rfa 的絕對路徑）");
            if (!Path.IsPathRooted(familyPath))
                return LoadFamilyError($"familyPath 必須是絕對路徑: {familyPath}");
            if (!string.Equals(Path.GetExtension(familyPath), ".rfa", StringComparison.OrdinalIgnoreCase))
                return LoadFamilyError($"familyPath 不是 .rfa 檔: {familyPath}");
            if (!File.Exists(familyPath))
                return LoadFamilyError($"找不到族群檔: {familyPath}");
            if (doc.IsFamilyDocument)
                return LoadFamilyError("目前作用中的文件是族群文件，請切換到專案文件再載入");

            // Revit 以檔名作為載入後的族群名稱
            string familyName = Path.GetFileNameWithoutExtension(familyPath);
            Family existing = FindFamilyByName(doc, familyName);
            bool alreadyLoaded = existing != null;

            // 已存在、不覆蓋、也沒有要補的類型 → 不開交易，直接回傳既有族群
            if (alreadyLoaded && !overwrite && typeNames.Count == 0)
                return LoadFamilyResult(doc, existing, true, null, null);

            var loadedTypeNames = new List<string>();
            var missingTypeNames = new List<string>();
            Family family = existing;

            using (Transaction trans = new Transaction(doc, "MCP: 載入族群"))
            {
                trans.Start();
                try
                {
                    var options = new LoadFamilyToolOptions(overwrite);

                    if (typeNames.Count == 0)
                    {
                        // 已存在時 LoadFamily 回傳 false（未變更），以既有族群為準
                        if (doc.LoadFamily(familyPath, options, out Family loaded) && loaded != null)
                            family = loaded;
                    }
                    else
                    {
                        foreach (string typeName in typeNames)
                        {
                            if (!overwrite && family != null && FindSymbol(doc, family, typeName) != null)
                                continue; // 類型已在專案中，不覆蓋就略過

                            FamilySymbol symbol;
                            bool ok = overwrite
                                ? doc.LoadFamilySymbol(familyPath, typeName, options, out symbol)
                                : doc.LoadFamilySymbol(familyPath, typeName, out symbol);

                            if (symbol == null && family == null)
                                family = FindFamilyByName(doc, familyName);
                            if (symbol == null && family != null)
                                symbol = FindSymbol(doc, family, typeName);

                            if (symbol != null)
                            {
                                family = symbol.Family;
                                if (ok) loadedTypeNames.Add(typeName);
                            }
                            else
                            {
                                missingTypeNames.Add(typeName);
                            }
                        }
                    }

                    if (family == null)
                        family = FindFamilyByName(doc, familyName);

                    if (family == null)
                    {
                        trans.RollBack();
                        string reason = missingTypeNames.Count > 0
                            ? $"族群檔中找不到指定類型: {string.Join(", ", missingTypeNames)}"
                            : "LoadFamily 失敗（回傳 false 且專案中找不到該族群）";
                        return LoadFamilyError(reason, familyName);
                    }

                    trans.Commit();
                }
                catch (Exception ex)
                {
                    if (trans.HasStarted() && !trans.HasEnded())
                        trans.RollBack();
                    return LoadFamilyError(ex.Message, familyName);
                }
            }

            return LoadFamilyResult(doc, family, alreadyLoaded,
                typeNames.Count > 0 ? loadedTypeNames : null,
                typeNames.Count > 0 ? missingTypeNames : null);
        }

        private static Family FindFamilyByName(Document doc, string familyName)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(Family))
                .Cast<Family>()
                .FirstOrDefault(f => string.Equals(f.Name, familyName, StringComparison.OrdinalIgnoreCase));
        }

        private static FamilySymbol FindSymbol(Document doc, Family family, string typeName)
        {
            return family.GetFamilySymbolIds()
                .Select(id => doc.GetElement(id) as FamilySymbol)
                .FirstOrDefault(s => s != null && string.Equals(s.Name, typeName, StringComparison.Ordinal));
        }

        private static object LoadFamilyResult(Document doc, Family family, bool alreadyLoaded,
            List<string> loadedTypeNames, List<string> missingTypeNames)
        {
            var symbols = family.GetFamilySymbolIds()
                .Select(id => doc.GetElement(id) as FamilySymbol)
                .Where(s => s != null)
                .OrderBy(s => s.Name)
                .Select(s => new
                {
                    ElementId = s.Id.GetIdValue(),
                    Name = s.Name,
                    Category = s.Category?.Name ?? family.FamilyCategory?.Name
                })
                .ToList();

            return new
            {
                Success = true,
                FamilyId = family.Id.GetIdValue(),
                FamilyName = family.Name,
                AlreadyLoaded = alreadyLoaded,
                Symbols = symbols,
                LoadedTypeNames = loadedTypeNames,
                MissingTypeNames = missingTypeNames
            };
        }

        private static object LoadFamilyError(string error, string familyName = null)
        {
            return new
            {
                Success = false,
                FamilyId = (IdType?)null,
                FamilyName = familyName,
                AlreadyLoaded = false,
                Symbols = new object[0],
                Error = error
            };
        }

        #endregion
    }
}
