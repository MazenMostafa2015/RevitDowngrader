# Revit Downgrader (IFC round-trip)

Windows desktop app that "downgrades" Revit 2024+ project files (`.rvt`) to Revit 2023 by an
**IFC round trip**, then **validates** the result and writes a CSV report.

> Revit has no supported way to save to an older version. This tool *rebuilds* the model from IFC.
> It is lossy by design: views, sheets, schedules, native families and most annotations are not
> preserved. The validation report quantifies exactly what was lost. Treat the output as a
> coordination model, not an authoring model.

## Workflow

1. Pick a folder of `.rvt` files (Revit 2024+).
2. A newer Revit is launched; the add-in records statistics of each original and exports it to IFC (IFC2x3 CV2).
3. Revit 2023 is launched; the add-in opens each IFC, saves it as a Revit 2023 `.rvt`, re-opens the saved file and records statistics.
4. The app compares both statistics sets and writes `RevitDowngrade_Report_<timestamp>.csv`:
   element counts per category, family types, views, schedules lost, sheets lost, parameters lost.

Output goes to `<selected folder>\Revit2023_Downgraded\` (originals are never modified).
`.rfa` families are **skipped** (IFC export only supports project documents).

## Requirements

- Windows 10/11 x64
- Revit 2023 **and** at least one of Revit 2024+ installed, licensed and able to start
- To build: PowerShell 5.1+, internet access (the script fetches the .NET 8 SDK and Inno Setup if missing)

## Build

```powershell
powershell -ExecutionPolicy Bypass -File .\Build-Installer.ps1
```

Produces `dist\RevitDowngrader-Setup.exe`. Add-ins are built per Revit year against the local
`RevitAPI.dll` (NuGet fallback). Revit 2023/2024 = .NET Framework 4.8, Revit 2025/2026 = .NET 8.
For Revit 2027 verify the target runtime and pass `-TfmOverride @{2027='<tfm>'}` if needed.

## Using it

- Revit windows appear while processing. Do not touch them.
- **First run per Revit version:** Revit shows an *unsigned add-in* dialog. Choose **Always Load**.
  Later runs are silent. (Alternatively sign `RevitDowngrader.Addin.dll` with your code-signing certificate.)
- The add-in is dormant unless the app launched that Revit session. The per-user `.addin` manifest
  is written before and removed after every run.
- Timeouts: 15 min for Revit startup, 45 min without progress. Cancel stops Revit.
- If a run has failures, the work folder under `%LOCALAPPDATA%\RevitDowngrader\work\` is kept (job and result files).

## Known limitations

- Not tested against every Revit release; Revit dialogs that need a human decision can stall a run until the timeout.
- Parameter comparison samples up to 5,000 elements per category (speed). Category and parameter names depend on the Revit UI language.
- IFC import maps elements to Revit categories using Revit 2023's IFC settings, so category counts will differ.

## Project layout

```
src/RevitDowngrader        WPF app (net48): UI, orchestrator, validator
src/RevitDowngrader.Addin  Add-in that runs inside Revit (one build per Revit year)
Setup.iss                  Inno Setup script
Build-Installer.ps1        One-click build
```
