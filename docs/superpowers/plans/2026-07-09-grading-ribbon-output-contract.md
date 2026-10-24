# Grading Ribbon Output Contract Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the grading Ribbon output buttons always create sheets, export PDF, export Excel, allow per-run title block selection, and fall back to blank sheets when no title block exists.

**Architecture:** Keep the existing Revit command structure. Add a small pure policy helper for title block choices, a WPF dialog for Ribbon title block selection, and wire the selected `titleBlockId` into the existing comparison/presentation executors. Executors will return both PDF and Excel result objects.

**Tech Stack:** C# .NET Framework 4.8 for Revit 2024, Autodesk Revit API, Newtonsoft.Json, ClosedXML, NUnit, TypeScript tool schemas.

## Global Constraints

- Revit 2024+ grading Ribbon only.
- Do not fail output only because the model has no TitleBlock family.
- Use `ElementId.InvalidElementId` for blank sheets.
- Both `比較出圖` and `各別出圖` must export PDF and Excel.
- `各別出圖` PDF defaults to one combined PDF.
- Ribbon labels must not contain `...` or `…`.
- User chooses title block every time from Ribbon output buttons.

---

### Task 1: Title Block Choice Policy

**Files:**
- Create: `MCP/Core/Grading/TitleBlockChoice.cs`
- Modify: `MCP.Tests/RevitMCP.Tests.csproj`
- Create: `MCP.Tests/Grading/TitleBlockChoiceTests.cs`

**Interfaces:**
- Produces: `TitleBlockChoice.BlankSentinelId`, `TitleBlockChoice.FromParameter(long?)`, `TitleBlockChoice.IsBlank`.

- [ ] **Step 1: Write failing tests**

```csharp
[Test]
public void FromParameter_Null_UsesAutomatic()
{
    var choice = TitleBlockChoice.FromParameter(null);
    Assert.That(choice.IsAutomatic, Is.True);
    Assert.That(choice.IsBlank, Is.False);
}

[Test]
public void FromParameter_NegativeOne_UsesBlankSheet()
{
    var choice = TitleBlockChoice.FromParameter(-1);
    Assert.That(choice.IsBlank, Is.True);
}

[Test]
public void FromParameter_PositiveId_UsesExplicitTitleBlock()
{
    var choice = TitleBlockChoice.FromParameter(123);
    Assert.That(choice.ExplicitId, Is.EqualTo(123));
}
```

- [ ] **Step 2: Run tests and verify red**

Run: `dotnet test MCP.Tests\RevitMCP.Tests.csproj --no-restore --filter TitleBlockChoice`

Expected: compile/test failure because `TitleBlockChoice` does not exist.

- [ ] **Step 3: Implement helper**

Create `TitleBlockChoice` with automatic, blank, and explicit states.

- [ ] **Step 4: Run tests and verify green**

Run: `dotnet test MCP.Tests\RevitMCP.Tests.csproj --no-restore --filter TitleBlockChoice`

Expected: pass.

### Task 2: Ribbon Dialogs And Commands

**Files:**
- Create: `MCP/Commands/TitleBlockPickerDialog.cs`
- Modify: `MCP/Commands/GradingRibbonCommands.cs`
- Modify: `MCP/Application.cs`

**Interfaces:**
- Consumes: `titleBlockId=-1` means blank sheet.
- Produces: `SlopeHeatmapCommand`, updated `GradingComparisonCommand`, updated `GradingPresentationCommand`.

- [ ] **Step 1: Add title block picker dialog**

Dialog lists model TitleBlock symbols plus `無圖框空白圖紙`, defaults to A0, else first symbol, else blank.

- [ ] **Step 2: Wire output commands**

Both output buttons call picker every time and pass `titleBlockId`.

- [ ] **Step 3: Add six-level slope command**

Button opens scheme picker and calls `create_slope_heatmap`.

- [ ] **Step 4: Reorder and rename Ribbon buttons**

Order: 新整地方案, 整地檢視, 方案總覽, 六級坡度, 比較出圖, 各別出圖, 板高程斜板, 邊坡率.

### Task 3: Executor Output Contract

**Files:**
- Modify: `MCP/Core/Commands/CommandExecutor.GradingComparison.cs`
- Modify: `MCP/Core/Commands/CommandExecutor.GradingPresentation.cs`
- Modify: `MCP/Core/Commands/CommandExecutor.SheetLayout.cs`

**Interfaces:**
- Consumes: `titleBlockId=-1`.
- Produces: return payloads with `Pdf`, `Excel`, `UsedBlankTitleBlock`.

- [ ] **Step 1: Resolve blank title block fallback**

Use explicit ID, blank sentinel, A0/first symbol fallback, then `ElementId.InvalidElementId`.

- [ ] **Step 2: Support blank comparison sheet layout**

When sheet has no title block instance, use A0 landscape printable bounds for comparison layout instead of throwing.

- [ ] **Step 3: Export Excel from both output commands**

Call `ExportGradingComparison` after sheet/PDF creation and include result in response.

### Task 4: Tool Metadata, Docs, Build, Deploy

**Files:**
- Modify: `MCP-Server/src/tools/grading-tools.ts`
- Modify: `log/2026-07.md`

- [ ] **Step 1: Update tool descriptions**

Document PDF+Excel and blank-sheet fallback.

- [ ] **Step 2: Run verification**

Run C# tests, npm tests, and `dotnet build -c Release.R24 RevitMCP.csproj`.

- [ ] **Step 3: Deploy Revit 2024 DLL**

Verify Revit is closed, copy `MCP/bin/Release.R24/RevitMCP.dll` to `%APPDATA%\Autodesk\Revit\Addins\2024\RevitMCP\`, compare hashes.
