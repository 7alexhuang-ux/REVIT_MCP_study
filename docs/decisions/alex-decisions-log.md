# Alex 決策紀錄（Revit 外掛，Revit 跳轉）

> 文件性質：決策紀錄（人類與 AI 共用）。建立：2026-11-18，分支 `imrevitqafix`。
> 來源正本：Grok Bot 派工排程的 `C:\Temp\grokbot_sessions\BACKLOG.md`（不在 repo 內，這裡照抄並標註來源行，方便日後對帳）。
> 規則：每條標「補記（決策日期，來源）」；不補寫原文沒有的內容。**只收 2026-11-18 與 Revit 外掛有關的決定**。
> 注意編號：本檔「IM Q1～Q6」是 Issue Manager 規格（`docs/spec/revit-link.md`）給 Alex 的六題；「派工者 Q1～Q4」是 21:33 派工排程對「外掛整合完成後待決事項」自己編的四題，兩者不同。ADR 的正式寫法見 [ADR D-015（Revit 跳轉）](../adr/D-015-revit-link-addin.md)。

## A. Alex 親自回答的題目（IM Q1～Q6）

答案在外掛規格 [REVIT_LINK.md](../REVIT_LINK.md) 開頭的對照表（建造者 imrevitaddinjump 依 Alex 答案寫入）；BACKLOG 記載這些答案是 2026-11-18 20:25～20:27 由 Alex 回覆（[PARENT 20:25]～[PARENT 20:27]）。

| 補記（決策日期，來源） | 題 | Alex 的決定 |
|---|---|---|
| 補記（2026-11-18 20:25～20:27，BACKLOG 該筆「Alex Q1～Q5 已答」＋REVIT_LINK.md 對照表） | IM Q1 反向觸發 | 功能區按鈕與 Claude 指令兩個都做 |
| 同上 | IM Q2 元素查議題 | 做：在 Revit 選取元件（例如牆 W-12）可看到網頁上的相關議題 |
| 同上（另 BACKLOG 2026-11-18 21:33 那筆重申「Q3 版本提醒→維持每次提醒（Alex 11-18 已定）」） | IM Q3 模型版本不同 | 每次跳轉都先警告、仍然跳轉 |
| 同上 | IM Q4 專案換檔批次改連結 | 要（網頁端功能），外掛不需做 |
| 同上 | IM Q5 外部可見度 | imperms 前由專案設定控制，預設看不到；外部永遠看不到識別碼、不能跳轉 |
| 補記（2026-11-18，REVIT_LINK.md 對照表；BACKLOG 該筆只寫「Q1～Q5 已答」，Q6 的回覆時間缺） | IM Q6 IFC 跳轉 | 排進本期（IFC 換算修正另由 imifcfloorfix 處理，外掛不需懂 IFC） |

## B. Alex 的其他 2026-11-18 指示（與外掛工作流程有關）

| 補記（決策日期，來源） | 指示 |
|---|---|
| 補記（2026-11-18 17:47，BACKLOG 該筆「依 Alex 17:47 規則」） | 新的 worktree 一律開在 repo 內 `.worktrees/`（並加進 `.git/info/exclude`）。本分支 `.worktrees/imrevitqafix` 即依此規則 |
| 補記（2026-11-18 17:32，BACKLOG 該筆「[PARENT 17:32]」） | imrevitjump（IM 端）要「下一輪立刻開」，要求 spec 先寫入 Q1～Q6 再實作 |
| 補記（2026-11-18 20:25，BACKLOG 該筆「[PARENT 20:25]」） | 開 imrevitaddinjump（外掛 C# 整合、Release 建置、裝進 Revit，2025 優先、2024 保持可用；實機 R1～R4、R6） |
| 補記（2026-11-18 20:26，BACKLOG 該筆「[PARENT 20:26]」） | 另開 imifcfloorfix（查樓板 13/39、開口 0/167 對應根因並修 IFC id 對應，用同一份 Revit 2024 匯出資料重驗）與 imnextsec（Next.js 升修補版）。這兩件不屬外掛，列此只為對照 |

## C. 派工排程依 Alex 既有原則代決的項目（不是 Alex 本人當場回答）

來源：BACKLOG 2026-11-18 21:33 那筆（Grok Bot 派工排程）。標示「代決」代表 Alex 可推翻。

| 補記（決策日期，來源） | 題（派工者編號） | 代決內容 |
|---|---|---|
| 補記（2026-11-18 21:33，BACKLOG 2026-11-18 21:33 那筆） | 派工者 Q1 Revit 2024 實測 | 要做，併進 imifcfloorfix（目前 R24 DLL 只編譯＋複製、沒有開過 Revit 2024；此描述出處是 imrevitaddinjump_result.txt，非 BACKLOG） |
| 補記（2026-11-18 21:33，BACKLOG 2026-11-18 21:33 那筆） | 派工者 Q3 版本提醒太吵是否改算法 | 維持每次提醒（依 Alex 11-18 已定，即 IM Q3） |
| 補記（2026-11-18 21:33，BACKLOG 2026-11-18 21:33 那筆） | 派工者 Q4 兩個舊 QA/QC 失敗誰處理 | 視為技術問題，開 imrevitqafix 處理，並把決策寫進外掛 repo `docs/decisions`（即本檔） |
| 補記（2026-11-18 21:46，BACKLOG 2026-11-18 21:46 那筆） | imrevitaddinjumpv 的非阻擋項 | 自行決定修（不誤導規則）：N1 IM 橫幅→imrevitjumpn；N4～N7 外掛文件→imrevitqafix |

## D. 仍待 Alex 決定（原文照抄，未代決）

補記（2026-11-18 21:33，BACKLOG 2026-11-18 21:33 那筆）：

- 派工者 Q2：RevitMCP 外掛無簽章，Revit 每次啟動跳「無法驗證發佈者」，要不要選「永遠載入」。
- hld070 的 70-1～70-4（不屬外掛，列此為對照；BACKLOG 只列編號未列細節；細節在 Alex_Diary 的 hld070 報告與 wait_alex.md 十五）。

## 變更歷史

- 2026-11-18：建立（imrevitqafix）。
- 2026-11-18：imrevitqafixr 改用時間戳定位 BACKLOG（行號會漂移）、更正 hld070 與 Revit 2024 出處。
