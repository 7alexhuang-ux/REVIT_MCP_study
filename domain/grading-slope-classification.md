---
name: grading-slope-classification
description: "整地坡度分級 SOP：以台灣水土保持技術規範「六級坡」為設計地形頂面著色與分級（坡度熱區圖）。坡度以設計面法線計算（slope% = 100×√(nx²+ny²)/nz），再依六級坡固定級距分級。當使用者提到坡度熱區、坡度分級、六級坡、宜林地、加強保育地、水保坡度、山坡地坡級時觸發。"
metadata:
  version: "1.0"
  updated: "2026-07-06"
  created: "2026-07-06"
  contributors:
    - "Alex Huang (需求、六級坡權威級距)"
  references:
    - "水土保持技術規範（行政院農業委員會）"
  related: ["floor-slope-analysis.md"]
  referenced_by: []
  tags: [Revit, Grading, Slope, 坡度, 六級坡, 水保, 山坡地, MCP, create_slope_heatmap]
---

# 整地坡度分級標準作業程序（Grading Slope Classification SOP）

## 1. 目的

為整地後的設計地形提供**符合台灣水土保持法規**的坡度視覺化與分級，供山坡地開發之坡度分析、宜林地／加強保育地判別與送審參考。對應工具 `create_slope_heatmap`。

## 2. 權威分類：水土保持技術規範 六級坡

坡度熱區的著色級距一律以此表為準，**不得以模型一般知識替代**：

| 級別 | 坡度範圍 | 屬性 |
|---|---|---|
| 一級坡 | 5% 以下 | |
| 二級坡 | 超過 5% 至 15% | |
| 三級坡 | 超過 15% 至 30% | |
| 四級坡 | 超過 30% 至 40% | |
| 五級坡 | 超過 40% 至 55% | 宜林地 |
| 六級坡 | 超過 55% | 加強保育地 |

邊界採**右閉**判定：`≤5→一級`、`(5,15]→二級`、`(15,30]→三級`、`(30,40]→四級`、`(40,55]→五級`、`>55→六級`。實作見純函式 `SlopeClassifier.Classify(坡度%)`（NUnit 覆蓋，零 Revit 相依）。

## 3. 坡度計算方法

於設計地形頂面各取樣點以**面法線**求表面坡度，不另建網格：

```
slope% = 100 × √(nx² + ny²) / nz
```

其中 (nx, ny, nz) 為該點朝上單位法線（nz > 0）。近垂直面（nz ≤ 0）不計。此法與 `floor-slope-analysis.md` 的樓板坡度演算法同源（法向量與 Z 軸夾角）。

## 4. 熱區著色

- AVF（`SpatialFieldManager`）以**級別（1..6）為值**寫入，取得離散色帶（非連續漸層）。
- 色帶：一級（綠）→ 六級（紫），對應六級坡各級固定色。
- 圖例列出級別、坡度範圍、屬性（宜林地／加強保育地）與各級取樣點數。

## 5. 定位與限制

- **僅視覺化定位**坡度分布；**坡度統計權威值以正式水保計算書為準**，工具不重算亦不取代簽證數字。
- 一視圖僅能掛一種 AVF 顯示樣式；坡度熱區與挖填熱區須各自置於不同視圖（出圖協調 `create_grading_presentation` 會自動複製視圖分別著色）。

## 6. 相關

- 整地本體與放坡：見整地模組設計 `docs/superpowers/specs/2026-07-02-toposolid-footprint-cut-fill-design.md`。
- 樓板排水坡度（不同用途）：`floor-slope-analysis.md`。
