#if REVIT2024_OR_GREATER
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitMCP.Core.Grading;

namespace RevitMCP.Core
{
    public partial class CommandExecutor
    {
        /// <summary>
        /// 樓板草稿複製：複製選取的樓板，並在目前視圖隱藏原本的樓板（僅該視圖，不影響其他視圖），
        /// 方便使用者在複製出來的樓板上調整下一個方案的範圍，不用手動複製＋隱藏兩個步驟。
        /// 這是準備下一版樓板草稿的暫存動作，不寫入整地方案登記簿。
        /// </summary>
        private object DuplicateFloorDraft(JObject parameters)
        {
            var doc = _uiApp.ActiveUIDocument.Document;
            var floorIds = parameters["floorIds"]?.Values<long>().ToArray() ?? new long[0];
            if (floorIds.Length == 0)
            {
                throw new InvalidOperationException("請至少提供一片樓板 ID（floorIds）。");
            }

            var originals = new List<Floor>();
            foreach (var id in floorIds)
            {
                if (!(doc.GetElement(new ElementId(checked((int)id))) is Floor floor))
                {
                    throw new InvalidOperationException($"元素 ID {id} 不存在或不是樓板（Floor）。");
                }

                originals.Add(floor);
            }

            var view = _uiApp.ActiveUIDocument.ActiveView;
            var newFloorIds = new List<long>();
            var hiddenCount = 0;

            using (var transaction = new Transaction(doc, "複製樓板草稿"))
            {
                GradingFailuresPreprocessor.Attach(transaction, new GradingFailuresPreprocessor());
                if (transaction.Start() != TransactionStatus.Started)
                {
                    throw new InvalidOperationException("無法啟動複製樓板草稿交易。");
                }

                foreach (var floor in originals)
                {
                    var copiedIds = ElementTransformUtils.CopyElement(doc, floor.Id, XYZ.Zero);
                    foreach (var copiedId in copiedIds)
                    {
                        newFloorIds.Add(copiedId.GetIdValue());
                    }

                    if (view != null && floor.CanBeHidden(view) && !floor.IsHidden(view))
                    {
                        view.HideElements(new List<ElementId> { floor.Id });
                        hiddenCount++;
                    }
                }

                if (transaction.Commit() != TransactionStatus.Committed)
                {
                    throw new InvalidOperationException("複製樓板草稿交易未能提交。");
                }
            }

            return new
            {
                OriginalFloorIds = floorIds,
                NewFloorIds = newFloorIds,
                HiddenInCurrentView = hiddenCount,
                Message = $"已複製 {newFloorIds.Count} 片樓板；原本 {hiddenCount} 片已在目前視圖隱藏（僅該視圖）。"
            };
        }
    }
}
#endif
