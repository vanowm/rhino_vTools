# vTools  ·  v26.10.9.1452

vTools is a native Rhino 8 and Rhino 9 command suite for precision curve and surface editing, fabrication layout, unrolling and matching, alignment, annotation, selection, and object management.

## What this project includes

- Rhino plug-in entry point: vToolsPlugIn
- Native commands (62):
  - [vAlign](#valign-flow) *(26.8.20.807)* — rotates selected objects in World XY by aligning a hovered target curve to a clicked stationary reference, with faded live preview and optional separation distance
  - [vBiminiParts](#vbiminiparts-flow) *(26.5.21.1827)* — builds bimini cover pocket parts (facings, main pocket, secondary pockets, center reference line) from a selected boundary curve; pipe size configures pocket depths
  - [vCenter](#vcenter-flow) *(26.8.12.1742)* — places a point at the combined bounding-box center, weighted mass center, or equal-weight object center of an editable geometry selection
  - [vChamfer](#vchamfer-flow) *(26.5.7.723)* — adds a chamfer line perpendicular to the middle curve at an equidistant gap; places the chamfer where the gap between two diverging curves equals the specified length
  - [vCommandFailSound](#vcommandfailsound-flow) *(26.7.23.045)* — configures and toggles an audible notification when a Rhino command ends with any result other than Success or Cancel
  - [vCleanup](#vcleanup-flow) *(26.8.20.1740)* — simplifies scoped curves, finds covered overlaps, and identifies or deletes protected short segments and control-point candidates
  - [vCurveToSpline](#vcurvetospline-flow) *(26.4.24.934)* — converts selected curves to interpolated splines with join modes, smooth/kink control, and optional in-place replacement
  - [vDiamonds](#vdiamonds-flow) *(26.5.14.928)* — draws an argyle diamond pattern with optional bounding rectangle and size/count labels; supports BySize centering mode
  - [vDir](#vdir-flow) *(26.8.24.1747)* — flips one face, every face in a polysurface, or faces pointing opposite to a clicked reference face
  - [vDupBorder](#vdupborder-flow) *(26.8.13.829)* — duplicates whole-object or selected-face borders onto a chosen layer while carrying source groups, with optional source removal and grouping for ungrouped inputs
  - [vDupEdge](#vdupedge-flow) *(26.8.13.829)* — duplicates selected Brep, mesh, extrusion, or SubD edges onto a chosen layer while carrying source groups and optionally grouping ungrouped inputs
  - [vExportDXF](#vexportdxf-flow) *(26.9.29.1802)* — exports selected objects or the document as DXF with Rhino's current export scheme, group blocks, and optional cleanup
  - [vFacing](#vfacing-flow) *(26.5.29.1333)* — builds a four-piece closed facing boundary from a base curve and two side curves by offsetting the base inward by a specified size; collects inside objects and places the result with a DynamicDraw preview
  - [vFilterExec](#vfilterexec-flow) *(26.7.30.1223)* — runs a command with a temporary selection filter and optional current layer, restoring both afterward
  - [vFitBox](#vfitbox-flow) *(26.4.24.934)* — finds the minimum bounding box for selected objects by optimizing rotation angle
  - [vFPS](#vfps-flow) *(26.7.30.1041)* — toggles a per-viewport FPS overlay measured from short active-view rendering samples
  - [vGroup](#vgroup-flow) *(26.5.27.1300)* — groups selected objects by closed curve, surface, polysurface, or face boundaries; each boundary is grouped with the objects inside it
  - [vGroupsManager](#vgroupsmanager-flow) *(26.8.27.839)* — manages document groups in a modeless sortable tree with actual text values, selection-distinct highlighting, optional automatic selection, membership editing, renaming, ungrouping, and empty-group cleanup
  - [vHelp](#vhelp-flow) *(26.8.26.2013)* — opens the bundled vTools command index with short descriptions and links to complete help for every command
  - [vIsolate](#visolate-flow) *(26.7.22.1515)* — keeps selected objects visible while hiding every other visible normal object, optionally in a named Rhino hide set
  - [vJoin](#vjoin-flow) *(26.7.30.2016)* — joins selected objects, optionally joining copies while preserving the originals
  - [vLine](#vline-flow) *(26.4.27.2125)* — draws lines with chain modes, native construction modes at either endpoint, automatic angle constraint, and length constraints
  - [vLineLength](#vlinelength-flow) *(26.4.27.2125)* — resizes an open curve to a target total, additive, or subtractive length
  - [vMatch](#vmatch-flow) *(26.7.1.1535)* — click near an edge-mate dot produced by [vUnrollSrf](#vunrollsrf-flow) to align the neighbouring flat part; Auto mode assembles a whole BFS selection with optional randomisation
  - [vMiddleCurve](#vmiddlecurve-flow) *(26.4.27.2243)* — creates an interpolated curve equidistant between two selected curves; inherits their shared group when both belong to one
  - [vMirror](#vmirror-flow) *(26.8.20.703)* — mirrors selected objects across two-point, three-point, CPlane-axis, or planar-object planes, with persistent copying and text-flipping options
  - [vNest](#vnest-flow) *(26.10.6.1920)* - manages grain-aware planar part nesting across fixed-size tables with a modeless part editor and automatic re-nesting
  - [vNotches](#vnotches-flow) *(26.6.1.1529)* — places perpendicular notch marks along one or more selected curves at clicked positions; connected curves can be selected as a joined chain; a floating panel controls notch type, dimensions, optional label, per-curve side/reverse settings, and a multiple-notch batch adder with live hover preview
  - [vOffset](#voffset-flow) *(26.4.27.2243)* — runs built-in [`Offset`](https://docs.mcneel.com/rhino/8/help/en-us/commands/offset.htm) continuously and can trim or extend new offsets to curves touching the source endpoints
  - [vOrient2pt](#vorient2pt-flow) *(26.4.24.934)* — orients objects from a source two-point frame to a target two-point frame
  - [vOrient3pt](#vorient3pt-flow) *(26.4.24.934)* — orients objects from a source three-point frame to a target three-point frame; intermediate points are optional (Enter at src2 = 1-point translate, Enter at src3 = 2-point orient)
  - [vOverlaps](#voverlaps-flow) *(26.7.3.2104)* — selects covered or partially overlapping curves and highlights coincident edge intervals belonging to overlapping surface or polysurface faces
  - [vPart](#vpart-flow) *(26.5.18.1742)* — captures a closed perimeter from selected curves (gaps are bridged automatically), collects all visible objects inside the perimeter (curves trimmed at the boundary; other types included whole), and lets the user place the resulting Part with a full preview
  - [vPartSplit](#vpartsplit-flow) *(26.10.6.1739)* - splits a selected planar part into individually grouped material-width copies with per-part seam allowance and adjustable nominal-cut previews
  - [vPerpendicularTo](#vperpendicularto-flow) *(26.5.5.757)* — rotates curve A about its nearest endpoint so it is perpendicular to curve B in the active CPlane
  - [vPointAlongCurve](#vpointalongcurve-flow) *(26.10.2.1206)* - places a point at a chosen arc-length distance from a curve position, with snapped cursor-driven direction and live preview
  - [vPointNormalToSurface](#vpointnormaltosurface-flow) *(26.4.27.2109)* — places points projected onto the closest surface normal evaluation point
  - [vProjectToSurface](#vprojecttosurface-flow) *(26.7.16.1432)* — projects selected curves and points onto one or more target surfaces or polysurfaces with live preview; overhanging curve portions are clipped away
  - [vPointTrace](#vpointtrace-flow) *(26.4.30.1044)* — maps arc-length positions from a source curve onto a destination curve: pick points along the source and a corresponding point is placed on the destination at the same proportional arc-length position
  - [vRectangle](#vrectangle-flow) *(26.4.27.2259)* — creates an axis-aligned rectangle polyline from width/height inputs driven by numeric value or selected curve lengths
  - [vReGroup](#vregroup-flow) *(26.8.3.845)* — dissolves all existing groups (including nested sub-groups) on the selected objects and collects them into one new group
  - [vScallop](#vscallop-flow) *(26.4.27.2125)* — creates an arc scallop between two points or along a selected line
  - [vSetPt](#vsetpt-flow) *(26.5.28.1145)* — previews and aligns preselected edit-point or control-point grips, or cursor-nearest endpoints, using Rhino's [SetPt](https://docs.mcneel.com/rhino/8/help/en-us/commands/setpt.htm) command
  - [vShade](#vshade-flow) *(26.9.28.1022)* — repeatedly creates identified Reference-layer angle bisectors and perpendicular shade lines, with optional non-crossing connected boundaries
  - [vSmooth](#vsmooth-flow) *(26.8.4.0)* — adjusts a selected curve so it transitions smoothly (G1) into one or two connected neighbours by eliminating kinks at shared endpoints; per-end StrengthStart/StrengthEnd, Copy, and Join options
  - [vShow](#vshow-flow) *(26.7.22.1818)* — shows one named hidden-object set, or all named sets, without cancelling the command already in progress
  - [vSplit](#vsplit-flow) *(26.7.9.1647)* — interactively splits selected curves at picked real point markers with distinct add/remove colors and shapes plus point snapping
  - [vSplitAtCorners](#vsplitatcorners-flow) *(26.4.27.2125)* — splits curves at detected corners with interactive per-corner toggle preview
  - [vTangent](#vtangent-flow) *(26.5.5.757)* — moves a curve rigidly so one or both endpoints align tangentially to selected driver curves
  - [vTextAligned](#vtextaligned-flow) *(26.4.27.2125)* — places or repositions annotation text aligned and offset along a selected curve
  - [vTextFlip](#vtextflip-flow) *(26.4.27.2125)* — flips or rotates annotation text while preserving dimension and leader lines
  - [vTitle](#vtitle-flow) *(26.7.1.1755)* — places or edits a titled annotation text box with optional bounding rectangle; hover to highlight, click existing to edit
  - [vToggleAxes](#vtoggleaxes-flow) *(26.6.22.1811)* — toggles visible viewport axes (grid/construction axes plus display-mode Z axis)
  - [vToggleControlPoints](#vtogglecontrolpoints-flow) *(26.7.13.1046)* — toggles selected objects between edit points on the curve and off-curve control points
  - [vTogglePerpGumball](#vtoggleperpgumball-flow) *(26.4.24.1712)* — toggles a monitor that auto-orients the gumball perpendicular to selected grips
  - [vToggleView](#vtoggleview-flow) *(26.10.1.1632)* — toggles perspective/CPlane plan or projection, or activates a standard view with optional tab creation without interrupting another command
  - [vTrim](#vtrim-flow) *(26.4.24.1633)* — trims and extends curves with auto-cutter detection and join of extensions
  - [vTrimOff](#vtrimoff-flow) *(26.5.18.849)* — trims selected curves to the outer boundary of the enclosed region they collectively form; protruding ends are removed automatically
  - [vUnrollSrf](#vunrollsrf-flow) *(26.5.19.1918)* — unrolls selected surfaces through native [UnrollSrfUV](https://docs.mcneel.com/rhino/8/help/en-us/commands/unrollsrf.htm#unrollsrfuv) by default, or [UnrollSrf](https://docs.mcneel.com/rhino/8/help/en-us/commands/unrollsrf.htm) with UV disabled; reruns preserve labels, shared-edge markers, part identities and prior flat placement
  - [vUzip](#vuzip-flow) *(26.4.24.934)* — full U-zip workflow in one command: selects three U-shape arm curves, computes the inward-offset center curve with fillet, and optionally produces glass, vis, and parts output with label and tail settings
  - [vUzipCenter](#vuzipcenter-flow) *(26.5.1.1903)* — offsets a U-shape's three curves inward, fillets the inside corners, and produces a single joined open curve
  - [vUzipParts](#vuzipparts-flow) *(26.5.8.1249)* — creates U-zip parts from a center curve into labeled reference, plot, and cut output groups
- Shared command configuration file: vTools.config.json
- Runtime command diagnostics in `vTools.log` beside the loaded DLL
- Automatic history-break highlighting uses orange shading with a dark magenta outline for affected objects, including temporary copies of deleted history outputs. Rhino's own history warning remains unchanged. Highlights remain visible while the native modal warning is open, even if Rhino has already ended the command, and clear after the warning is accepted or canceled. Undo/Redo, document changes, or two Esc presses also clear them. This follows Rhino's broken-history warning setting; set `HistoryHighlight.enabled` to `false` in `vTools.config.json` and reload the plug-in to disable the monitor.
- Run `Toolbar\SyncToolbarIcons.ps1` with Rhino closed after changing toolbar artwork; it updates only matching vTools icons in active Rhino 8 and Rhino 9 toolbar files and preserves the rest of each toolbar layout.

## Requirements

- Rhino 8 and Rhino 9 (or Rhino 9 WIP) for their matching RhinoCommon assemblies
- .NET SDK 10.0+ with the .NET 7 targeting pack
- Windows

## Build

From this folder:

```powershell
.\build.ps1
```

This standalone Release build does not require Git and never commits or pushes.
Repository maintainers can use `.\build.ps1 -Publish` for the semantic-message,
signed-commit, push, and GitHub release workflow used by the normal VS Code build task.
When both Release DLLs already match the source-derived version and are not older than
the latest C# source, publishing reuses them and continues without recompiling, including
while Rhino has the DLL loaded.

Build behavior:

1. Release builds fail fast if stale output files are locked (for example, if Rhino holds `vTools.dll`).
2. Existing framework output directories are restored if compilation fails, so a failed build cannot remove the last working Release files.
3. After every successful Release build, a timestamped backup snapshot is created automatically.
4. When `-Publish` finds either changed DLL, the build commits both runtime outputs once and pushes a full-version tag. GitHub then creates a release containing distinctly named .NET 7 and .NET 10 DLLs plus the toolbar `.rui` file.

## Output

Release output is written to:

- bin/Release/net7.0-windows/vTools.dll
- bin/Release/net7.0-windows/vTools.config.json
- bin/Release/net7.0-windows/vTools.rui
- bin/Release/net7.0-windows/vToolsHelp.html
- bin/Release/net10.0-windows/vTools.dll
- bin/Release/net10.0-windows/vTools.config.json
- bin/Release/net10.0-windows/vTools.rui
- bin/Release/net10.0-windows/vToolsHelp.html
- Automatic backups: bin/Release/backups/YY.MM.DD.HHMMSS/&lt;target-framework&gt;/

## Command help

Open Rhino's Command Help panel and enable Auto-Update. While a vTools command is active,
the panel follows it to that command's bundled offline topic; F1 opens the same topic.
The help document is generated from the command flows below during each build and is also
embedded in the DLL for installations that do not include the separate HTML file. Topics
use Rhino's current help palette and follow the official command-help Steps and
Command-line options structure. Each topic links to a generated `vHelp` index containing
all commands, their short descriptions, and links to their complete topics. References to
other commands become help links only when a context-specific Markdown link is authored in
this README; native command links point to the matching official Rhino help. Numbered command steps continue across embedded option
lists instead of restarting at one. Edit
`Help/vToolsHelp.template.html` for layout and style;
the sample topic in that template can be previewed directly. Edit the README command flows
for generated help content; `Help/vToolsHelp.html` itself is regenerated on every build.
The build synchronizes generated help to existing Debug and Release output folders before
checking whether DLL compilation can be skipped. A newer external help file takes priority
over the embedded fallback, so help-only edits do not require a new DLL.

## Rhino usage

All command options persist by default unless stated otherwise.

Native commands (62): [vAlign](#valign-flow), [vBiminiParts](#vbiminiparts-flow), [vCenter](#vcenter-flow), [vChamfer](#vchamfer-flow), [vCommandFailSound](#vcommandfailsound-flow), [vCleanup](#vcleanup-flow), [vCurveToSpline](#vcurvetospline-flow), [vDiamonds](#vdiamonds-flow), [vDir](#vdir-flow), [vDupBorder](#vdupborder-flow), [vDupEdge](#vdupedge-flow), [vExportDXF](#vexportdxf-flow), [vFacing](#vfacing-flow), [vFilterExec](#vfilterexec-flow), [vFitBox](#vfitbox-flow), [vFPS](#vfps-flow), [vGroup](#vgroup-flow), [vGroupsManager](#vgroupsmanager-flow), [vHelp](#vhelp-flow), [vIsolate](#visolate-flow), [vJoin](#vjoin-flow), [vLine](#vline-flow), [vLineLength](#vlinelength-flow), [vMatch](#vmatch-flow), [vMiddleCurve](#vmiddlecurve-flow), [vMirror](#vmirror-flow), [vNest](#vnest-flow), [vNotches](#vnotches-flow), [vOffset](#voffset-flow), [vOrient2pt](#vorient2pt-flow), [vOrient3pt](#vorient3pt-flow), [vOverlaps](#voverlaps-flow), [vPart](#vpart-flow), [vPartSplit](#vpartsplit-flow), [vPerpendicularTo](#vperpendicularto-flow), [vPointAlongCurve](#vpointalongcurve-flow), [vPointNormalToSurface](#vpointnormaltosurface-flow), [vProjectToSurface](#vprojecttosurface-flow), [vPointTrace](#vpointtrace-flow), [vRectangle](#vrectangle-flow), [vReGroup](#vregroup-flow), [vScallop](#vscallop-flow), [vSetPt](#vsetpt-flow), [vShade](#vshade-flow), [vSmooth](#vsmooth-flow), [vShow](#vshow-flow), [vSplit](#vsplit-flow), [vSplitAtCorners](#vsplitatcorners-flow), [vTangent](#vtangent-flow), [vTextAligned](#vtextaligned-flow), [vTextFlip](#vtextflip-flow), [vTitle](#vtitle-flow), [vToggleAxes](#vtoggleaxes-flow), [vToggleControlPoints](#vtogglecontrolpoints-flow), [vTogglePerpGumball](#vtoggleperpgumball-flow), [vToggleView](#vtoggleview-flow), [vTrim](#vtrim-flow), [vTrimOff](#vtrimoff-flow), [vUnrollSrf](#vunrollsrf-flow), [vUzip](#vuzip-flow), [vUzipCenter](#vuzipcenter-flow), [vUzipParts](#vuzipparts-flow).

1. Load the plug-in assembly in Rhino.
1. Run one of the native commands.

### vAlign flow

1. Preselect objects or run `vAlign` and select the objects to rotate. Group members are treated as one movable object.
1. Click the stationary reference curve or surface/polysurface edge near the end that should receive the target, or press Enter to use World Ortho. Kinks of 30 degrees or more are independently selectable segments.
1. Hover a target curve or edge within the movable selection. The target is highlighted and the complete transformed result previews faded. With a reference, a thin line connects the picked points and moving the cursor across the target controls the rotation/offset side; hold Shift or Ctrl to reverse that side. Click any stationary curve or edge during this stage to replace the reference, including clicking the current reference near its other end. Without a reference, the target aligns vertically by default and horizontally while Shift or Ctrl is held, with cursor side choosing the direction. Click the target to apply the preview.
1. Alignment uses World XY rotation around World Z, independent of the active view. Open curves match the clicked ends so the objects are not inadvertently reversed.

Options:

- `Distance`: a non-negative separation from a selected reference edge. Cursor side controls the destination side. Enter `None` to rotate in place around the selected objects' center of mass without translating them. Accepted changes, including `None`, persist between runs and Rhino sessions even if the command is later canceled. Distance is ignored in reference-free World Ortho mode.
- `Loop`: `Yes` by default. After each alignment, selection is cleared and the command asks for objects, a reference, and a target again. Preselection is accepted only for the first cycle. `No` finishes after one alignment. Press Esc to finish the loop; completed alignments remain.
- `SaveDefaults`: `Yes` by default. Set `No` before entering temporary `Distance` or `Loop` values, including in a macro. Temporary choices last for this run and do not change saved defaults; this toggle resets to `Yes` at the next run. Switching back to `Yes` saves the current choices.

### vBiminiParts flow

1. Select bimini boundary curves (preselect supported).
1. The command joins the selected curves into a single closed boundary, then determines the seam curve (outward offset) and finished curve (either detected from an existing curve on the PLOT layer or computed as an inward offset), and breaks both at corners into top/bottom/left/right segments.
1. **Stage 2 — Main pocket**: click the center of up to 2 seam or finished top/bottom segments to identify the main pocket side(s). Press Enter to skip.
1. **Stage 3 — Secondary pocket**: click the center of remaining top/bottom segments for secondary pockets if fewer than 2 main pockets were picked. Press Enter to skip.
1. Before generating a secondary pocket, its length calculated from the main curve is compared with the previous secondary-pocket length stored in the Rhino document. When they differ, the command shows both lengths and asks whether to reuse the previous length. A successfully created secondary pocket updates the stored document value.
1. Output is built automatically:

    - **Facing parts** (port and starboard sides) with interior objects collected.
    - **Main pocket outline** — closed filleted rectangle trimmed to the boundary.
    - **Secondary pocket outline(s)** — closed filleted shapes with seam clearance.
    - **Center reference line** through all pocket center points.

1. Output layers: `PLOT` (finished curve segments), `CUT1` (seam and pocket outlines), `Reference` (center points and center line).
1. Standalone generated curves inherit the selected boundary curves' groups. Facing and pocket parts remain in their own generated groups.

Options:

- `PipeSize`: selects a pipe size group which sets `MainPktDepth`, `SecPktDepth`, and optional `ExtraRect` dimensions. All groups are configurable in `vTools.config.json` under `vBiminiParts`.

### vCenter flow

1. Preselect geometry or select it after running `vCenter`; add or remove individual objects before pressing Enter, with group selection matching [vFitBox](#vfitbox-flow).
1. A point preview updates as the selection changes.
1. Set `Method` from the option list or type `BoundingBox`, `Mass`, or `Objects` directly. The selected method is saved immediately.
1. Press Enter to place a normal Rhino point on the current layer while preserving the source selection.

Methods:

- `BoundingBox`: center of the combined world-coordinate bounding box.
- `Mass`: volume-weighted center for solids, otherwise area-weighted center, curve-length-weighted center, or the available point/fallback center.
- `Objects`: equal-weight average of each selected object's natural volume, area, length, point, or bounding-box center.

### vChamfer flow

1. Pick **curve 1** — near the corner of the two diverging curves. For one closed curve, pick it near the corner to chamfer; the closest G1 kink is used and the second-curve prompt is skipped.
1. For open curves, pick **curve 2** near the same corner.
Options:

    - `Length`: the desired chamfer line length — the perpendicular (equidistant) gap between both curves at the chamfer point. The chamfer is placed where the equidistant gap equals this value, with the line perpendicular to the middle curve.
    - `Trim`: `Yes` trims both curves to the chamfer endpoints; `No` only adds the line.
    - `Join` *(Trim=Yes only)*: `Yes` joins trimmed curves and the chamfer line into a single polycurve.

1. Optional — pick a **reference point** to reposition the chamfer: click anywhere near the curves. The chamfer moves to the arc position where the chamfer line's midpoint is exactly `Length` units away from the click (toward the corner). Press `ClearPoint` to revert to the gap-based placement.
1. Press Enter to apply.

Notes:
- Open-curve corner detection uses the closest endpoint pair. Closed-curve detection uses the G1 corner closest to the cursor and rebuilds the same object around the chamfer. Extension stubs (toward a virtual corner) are shown in the preview when `Trim=Yes` and the cut falls inside the extension zone.
- All options persist to `vTools.config.json` under `vChamfer`.

### vCommandFailSound flow

1. Press Enter when configuration is complete.

Options:

- `Enabled`: turns failure monitoring on or off and saves the state immediately.
- `Sound`: selects the system default, Asterisk, Exclamation, Hand, Question, or a custom audio file.
- `AudioFile`: opens a file picker and selects `Custom` automatically.
- `Preview`: plays the current choice without changing the watcher state.

While enabled, the selected sound plays whenever a Rhino command ends with any result other than `Success` or `Cancel`. Sound choices persist immediately in the shared `vTools.config.json`, and the watcher detaches automatically when the plug-in shuts down.
Failure monitoring is enabled by default when the plug-in loads.

### vCleanup flow

1. Preselect curves or add/remove individual curves at the command prompt. Press Enter with no curve selection to process every curve object in the document.
1. Press Enter after selection to approve and run cleanup.
1. Enabled processing runs in order: [SimplifyCrv](https://docs.mcneel.com/rhino/8/help/en-us/commands/simplifycrv.htm)-compatible curve simplification, shared [vOverlaps](#voverlaps-flow) detection, then short segment and adjacent control-point analysis. Open-curve endpoints remain protected.
1. Review the surviving highlighted, selected, or named-selection findings.

Options:

- `Threshold`: maximum short-geometry length in model units; the default is `0.02`.
- `HighlightShort`: highlights surviving short findings in magenta and overlaps in cyan until the next `vCleanup` run.
- `Preselect`: selects surviving `Short`, `Overlaps`, `All`, or `No` findings after deletion.
- `AutoDelete`: deletes `Short`, `Overlaps`, `All`, or `No` findings. Any deleting mode runs `SimplifyCrv` once more afterward when simplification is enabled.
- `SimplifyCrv`: runs Rhino-compatible curve simplification before analysis and, when deleting findings, once more afterward.
- `Overlaps`: runs shared [vOverlaps](#voverlaps-flow) detection before the short-geometry scan.
- `OverlapSegments`: when `Yes` (the default), splits the chosen source of a partial overlap and selects or deletes only its exact overlapping piece; `No` targets the chosen whole source curve. This setting is shared with `vOverlaps`.

With `AutoDelete=No`, results are saved as the document named selections `vCleanup Short geometry` and `vCleanup Overlaps`. All settings persist.

### vExportDXF flow

1. Select objects before starting, or leave nothing selected to export all normal visible, unlocked objects.
1. `vExportDXF` prepares the export using its saved choices before asking for a filename. Use `-vExportDXF` to change `NotchTrim`, `NotchSplitLayer`, `NotchJoin`, `Explode`, `TextBreak`, `Optimize`, or `CleanupAction` for one run; `SaveDefaults=Yes` keeps those choices for later exports. Optimization processes part geometry only, excluding nest table borders, labels, and label frames without excluding them from the DXF.
1. Choose the DXF path. Selected geometry is moved to the origin in the DXF only; document objects stay in place. Groups become DXF blocks, Rhino's current DXF scheme is used, and the previous selection is restored.
1. Nest table wrapper groups and material-table outlines are omitted from the DXF, preserving individual part groups instead of bundling an entire table into one block. Renamed tables are identified by stable metadata; older generated nest tables are also recognized. Table labels export in their own label-sized framed block, without the material rectangle or combined table outline. The rows are centered using measured glyph bounds, with explicit left attachment for compatible import positioning. Unnamed groups receive block names; failed block creation reports an error instead of exporting group members loose. Source groups and document membership are unchanged.

The DXF is written to a temporary file first, then copied over the chosen destination in place without renaming or replacing an existing file. Native temporary-path save messages and extra blank lines are suppressed; warnings, errors and the final destination summary remain visible.

Options:

- `NotchTrim`: `No` keeps touched curves as drawn; `Split` moves the between-leg piece to `NotchSplitLayer`; `Trim` omits that piece from the DXF. Default is `Split`. Only temporary export copies are changed.
- `NotchSplitLayer`: destination layer for split sections; default is `Reference`. Previously saved `NotchTrimLayer` values are still read. This does not rename or change vNotches' independent layer option.
- `Explode`: `No` preserves curve structure; `SplitAtCorners` separates curves at tangent discontinuities, retaining smooth joins; `Full` separates all polycurve/polyline components, including nested components. Default is `No`. Runs after notch splitting/trimming and joining. Only temporary export curves change; group blocks, text, annotations and other geometry stay intact.
- `NotchJoin`: `Yes` by default joins matched V/U notch geometry to touched line or polyline sections in the DXF copy after `Split` or `Trim`, only when the notch and all touched sections share a layer. The between-leg piece stays separate in `Split` mode. Branching notches or source curves with conflicting layers or groups remain unjoined. `No` exports the notch components separately.
- `Optimize`: `Yes` by default runs the configured [vCleanup](#vcleanup-flow) detection and simplification routines on part geometry before export; `No` skips cleanup.
- `CleanupAction`: `Ask` by default highlights remaining overlaps and short geometry and offers `Delete` or `Ignore`. `Delete` removes findings and rechecks within the same export; `Ignore` continues without deleting or splitting findings. These export choices are independent of vCleanup's own `AutoDelete` setting. `SaveDefaults=Yes` saves this action globally; otherwise it applies only to this run. Escape cancels review.
- `TextBreak`: `Yes` by default splits multiline text into separate single-line text objects in the DXF, including text in exported group blocks. Line placement, text styles, layers, and object metadata are preserved; document text is unchanged. Blank rows remain spacing rather than empty objects. `No` keeps multiline text intact.
- `SaveDefaults`: `No` uses these choices only for the current export; `Yes` saves them for this document and future documents.

[vNotches](#vnotches-flow) has its own independent notch-contact settings for changes made while placing notches.

### vCurveToSpline flow

1. Select source curves (preselect or postselect is supported). If all selected curves form one connected end-to-end chain they are automatically joined and treated as a single input.
1. Confirm to create interpolated curve output and select results.

Options:

- `Join`: `None` creates one spline per selected curve; `Connected` creates one spline per connected curve island; `All` creates one spline through all selected curves.
- `Smooth`: `Yes` blends all control points of a joined group into one smooth spline; `No` converts each segment to its own spline and joins them into a kink-preserving polycurve. It has no effect when `Join=None`.
- `SmoothClose`: `Yes` makes closed outputs smooth; `No` closes with an explicit seam point and leaves a kink.
- `ReplaceOriginal`: `Yes` deletes inputs after creating spline output; `No` keeps them.

Closed source curves and closed joined chains are created as closed splines.

### vDiamonds flow

Options:

    - `Width` / `Height`: single-diamond cell width and height. Accept decimal, fraction (`3+1/8`), or type `widthxheight` directly at the placement prompt (e.g. `3x2`).
    - `CountWidth` / `CountHeight`: number of diamond cells across/tall; decimals allowed (`3.5` = 3 full + half cell).
    - `BySize`: enter a target bounding box size as `widthxheight`. `RoundToDiamond=None` keeps the requested box and centers whole diamonds with equal margins; `Full` or `Half` treats it as a minimum and expands each dimension upward to the next whole- or half-diamond increment. Does not change `CountWidth`/`CountHeight`. Enter `0` to revert to count mode.
    - `Boundary=Yes/No`: show/hide the CUT1 bounding rectangle.
    - `Size=Yes/No`: show/hide the individual diamond size label (e.g. `2 x 2`), fitted to bbox width and stored as one PatternSmith-compatible ASCII annotation.
    - `Count=Yes/No`: show/hide separate grid-count and boundary-size annotations, enclosed by one pair of parentheses spanning both rows and fitted to the bbox width.
    - `LabelInside=FitAll/Fit/No`: `FitAll` uniformly scales and centers the complete annotation stack within 10% padding on every boundary side; `Fit` scales it to the padded width and aligns its bottom to the lower 10% inset; `No` keeps the labels above the boundary.

1. Current bounding box dimensions print to command history on every preview update.
1. Pick the placement point to commit. All objects are grouped. Output layers: `PLOT` (diamond lines), `CUT1` (boundary rect), `Reference` (labels).

### vDir flow

1. Preselected individual Brep, extrusion, or mesh faces flip immediately when `vDir` starts. Otherwise, hover a surface, polysurface, extrusion, mesh, or SubD face to preview the active operation, then click to apply it immediately. The hovered reference face is orange; additional faces affected by `FlipAll` or `SameDirection` are cyan.
1. From `SingleFace`, Shift temporarily applies `FlipAll` and Ctrl applies `SameDirection`. From either multi-face mode, Shift temporarily applies `SingleFace`, while Ctrl applies the other multi-face mode. Ctrl takes precedence when both modifiers are held; the hover preview and cursor prompt update immediately when a modifier changes.
1. Ctrl+Z and Ctrl+Y undo or redo one click while keeping `vDir` active. After the command ends, Rhino retains the same per-click undo records.
1. The clicked geometry is not left selected. Continue clicking faces and press Enter or Esc when finished. Each click creates one object replacement and one undo record.
1. Direction changes preserve shape, trims, attributes, groups, and object placement. An extrusion becomes its equivalent Brep when an individual face direction is changed because Rhino does not store independent extrusion-face orientation.

Options:

- `SingleFace`: flips only the clicked Brep, extrusion, or mesh face. Rhino does not support independently reversing one SubD face.
- `FlipAll`: reverses every face in a surface, polysurface, extrusion, or mesh, and reverses the complete SubD through Rhino's native whole-SubD orientation operation.
- `SameDirection`: keeps the clicked face as the reference and propagates coherent orientation across shared Brep or mesh edges, reversing only connected faces that disagree. SubD face orientation is inherently unified already.

The active mode persists and supports direct command-line macros such as `_-vDir _FlipAll`.

### vDupBorder flow

1. Preselect or select open surfaces, polysurfaces, meshes, SubDs, hatches, or individual Brep/mesh/SubD faces. Group selection is disabled so sources are chosen individually.
1. The command duplicates each whole-object border; selected Brep faces produce individual face borders, while selected mesh or SubD face sets produce their combined outer boundary.
1. Existing source groups are always applied to every resulting curve.

Options:

- `GroupIfNone`: when `Yes`, an ungrouped source and all curves created from it are placed into a new group.
- `RemoveSource`: deletes each source only after its border curves were created successfully.
- `Layer`: uses the shared searchable picker. `*Current*` follows the current document layer; a named choice persists by full path. `-vDupBorder` accepts the layer directly.

### vDupEdge flow

1. Preselect or select Brep, extrusion, mesh, or SubD edge subobjects. Normal Rhino multi-edge and double-click chain selection remains available.
1. Each duplicated curve inherits every group assigned to its source object.

Options:

- `GroupIfNone`: when `Yes`, an ungrouped source and all edges duplicated from it are placed into a new group.
- `Layer`: uses the shared searchable picker. `*Current*` follows the current document layer; a named choice persists by full path. `-vDupEdge` accepts the layer directly.

### vFacing flow

1. Select the facing curves (base + two sides). Multiple curves per role are supported. An optional chamfer piece may also be included.

   Role assignment rules:
   - Curves are grouped by layer; each layer group is treated as one role (base, side 1, side 2).
   - If all curves share the same layer: exactly 3 curves — roles are auto-detected by topology; more than 3 — the command prompts you to click the two side curves.
   - A single closed curve — the command splits it at corners (≥30°), highlights the pieces, and prompts you to click the base edge.
   - Four layer groups — the group that shares an endpoint with only one other group is identified as the chamfer piece and merged into the adjacent side.

1. The command builds the four-piece boundary: base, offset front (at `Size` distance inward), and two trimmed side segments. All visible objects inside the closed boundary are collected automatically.
1. A DynamicDraw preview follows the cursor. Pick the placement point to commit.
1. All output objects are placed as new geometry at the picked location and added to a single Rhino group. Originals are not deleted.

Options:

- `Size`: offset distance from the base curve to the inner facing edge. Persists to `vTools.config.json` under `vFacing`.

### vFilterExec flow

1. Enter the command to execute.
1. Choose one filter or enter a comma-separated combination.
1. The command runs with that filter and optional temporary current layer. The chosen layer and any locked parent layers are unlocked for the command. The previous filter, current layer, and layer lock states are restored when it finishes, is canceled, or fails.

Options:

- `Layer`: available at both prompts and reads its value from the command line, without opening a layer picker. Enter an existing visible, editable layer, including a nested layer by its full path. Locked layers and their locked parents are temporarily unlocked; their original locking behavior is restored afterward. Hidden and reference layers cannot be used. The default `*Current*`, `.` or `*` leaves the current layer unchanged. Layer choices apply only to this launch and its repeats; a new run defaults to `*Current*`.

Notes:

- `vFilterExec` is transparent and remains Rhino's [Repeat](https://docs.mcneel.com/rhino/8/help/en-us/commands/repeat.htm) command after the delegated command ends. Repeat reuses the command, filter, and layer choice, restoring the layer that was current before each repeat.
- If Rhino switches panel tabs while the filter is active, the previously selected tabs are restored when the command finishes, is canceled, or fails. Panels closed or moved during the command are left alone.
- Aliases and toolbar macros can use `! _vFilterExec _Layer "Reference" #_Line Curves` or `! _vFilterExec _Layer "Surface" #_Sweep2 Curve`; a leading hyphen is optional. Use `All` instead of `Curves` to allow every object type. Existing macros such as `! _vFilterExec #_Sweep2 Curve` remain valid.

Filters: `All`, `Points`, `PointClouds`, `Curves`, `Surfaces`, `Polysurfaces`, `Meshes`, `SubDs`, `Extrusions`, `Annotations`, `Hatches`, `Blocks`, `Lights`, `Grips`, `Edges`, `Faces`, and `Vertices`. Singular forms and aliases such as `Text`, `Dimensions`, `ControlPoints`, and `Instances` are accepted. Combine filters with `,`, `+`, `|`, or `;`.

### vFitBox flow

1. Select objects to fit.
Options:

    - `AngleStep`: sampling step in degrees and accepts direct numeric input during object picking.
    - `Rotate`: applies final rotation to both fit box and selected objects, keeps the longest in-plane side horizontal, and prefers the equivalent orientation that avoids unnecessary 180-degree flips.
    - `Fit`: optimize by `Height` or `Area`.

1. Confirm selection to generate the fit result.

    - Flat fits create a closed polyline and report two footprint dimensions.
    - Non-flat fits create a polysurface and report `3D: width x depth x height`.

### vFPS flow

1. The command toggles the viewport FPS overlay (`ON`/`OFF`).
1. Each viewport keeps its own rolling frame-rate measurement, displayed as a rounded integer in a fixed three-digit field.
1. While a mouse button is held, the active viewport is sampled continuously so very slow pan, orbit, or zoom movement still reports rendering throughput. No redraws are forced while the mouse is idle; naturally frequent redraw sequences can still update the measurement.
1. The enabled state is saved immediately in `vTools.config.json` and restored when the plugin loads.

### vGroup flow

1. Select objects to group — include boundary curves, surfaces, polysurfaces, or individual faces together with any objects to be placed inside groups.
1. Touching selected faces are joined into one boundary patch. Their shared edges are omitted and only the patch's naked perimeter contributes to boundary detection.
1. The command finds all closed polygon boundaries formed by the selected curves and generated face perimeters:

    - All curves are split at their mutual intersection points.
    - Segments with dead-end endpoints (degree-1 nodes) are iteratively removed until only closed-cycle core segments remain.
    - Each connected component is solved separately with Rhino's planar-region tracing, so junctions and unrelated parts do not change which closed boundaries are found. Small depth variations within the boundary tolerance are projected for boundary calculations only, leaving all selected geometry unchanged. Endpoint joining handles gaps within the boundary tolerance.

1. For each closed boundary, all selected objects whose representative point falls inside it are collected. Original curves that defined the boundary (e.g. crossing lines whose midpoint lies outside the inner polygon) are included via source-curve tracking.
1. Outer outlines appear progressively as boundaries are found. The completed preview shows only the retained grouping boundaries, not their nested inner outlines. Small depth differences within the boundary tolerance do not create another outline for the same part; distinct parts remain separate. Boundaries with identical member sets create only one group.
1. Each retained boundary and its interior objects are added to a Rhino group (minimum 2 members required).

### vGroupsManager flow

1. A modeless tree opens with every document group and its current object count. The Rhino document remains interactive while the window is open. Expand a group to list each member using vObjectPropertiesPlus terminology; text objects show their actual text value instead of `Annotation`. Changing the Rhino document selection selects a complete group row when all of its members are selected; partial group selections use the matching object rows and expand their groups. Tree selection remains strongly visible with a slightly less saturated version of the active color while focus is in Rhino.
1. Enable `Show children groups` to view any strictly contained group under its smallest containing group, then expand it to see its objects or further subgroups. The hierarchy is inferred from group membership, not names or command metadata. Equal groups and partially overlapping groups are not nested inside one another; ambiguous equal-size parents are resolved consistently. Objects represented by children are not repeated directly under their parent. Uncovered parent members remain visible. The option persists and defaults to the original flat view; changing views preserves tree/document selection and expands ancestors needed to show selected children.
1. Click either column header to sort groups by name or object count. Select one group and click `Rename`, or double-click its row.
1. Select any mix of group and individual-object rows with standard Ctrl/Shift selection. Their object union receives a cyan outlined temporary highlight that remains distinct from Rhino's yellow selected-object display.
1. Click `Select` to replace the document selection with the highlighted union. The unlabeled checkbox at the front of the button performs that selection whenever tree selection changes, is accessible even when no tree row is highlighted, is disabled by default, and persists immediately. Disabling it clears objects selected automatically while preserving unrelated manual selection. Closing the manager clears only its temporary highlights and leaves the document selection unchanged.
1. `Add` adds the currently selected Rhino objects to the groups represented by highlighted tree rows, expands the destination, and scrolls the first new member into view. `Remove` removes highlighted member rows directly; for highlighted group rows it removes the currently selected Rhino objects from those groups. When removed rows disappear, the nearest surviving row becomes selected. Each action creates one Rhino undo record.
1. `Ungroup` dissolves the selected group rows, `Ungroup Singles` dissolves every one-object group, and `Purge Empty` deletes every empty group. Each action creates one Rhino undo record and refreshes the tree while preserving its sort and expanded groups.

### vHelp flow

1. The bundled vTools command index opens in Rhino's Help panel.
1. Select a command name to open its complete description, workflow, options, and related-command links.

### vIsolate flow

1. In one selection stage, add or remove the objects to keep visible, type an optional set name such as `A`, and press Enter to confirm. Preselection remains editable; prompted selection includes whole groups and excludes subobjects. The pending name is displayed by the `Name` option and can be removed with `ClearName`. A name can also be supplied directly, such as `vIsolate "A"`.
1. Every other visible, unlocked normal object is hidden natively and the isolated objects remain selected. Named isolation records active set membership; blank input clears any prior membership and uses Rhino's ordinary unnamed hide behavior, matching Rhino's [Isolate](https://docs.mcneel.com/rhino/8/help/en-us/commands/isolate.htm).
1. `vIsolate` is transparent and can run without cancelling the command already in progress. When nested inside another command, it restores the selection that existed before isolation.
1. The packaged `vTools` toolbar and tab contain a blue/grey `vIsolate / vShow` flyout linked to the `vIsolate` toolbar. That toolbar contains the general `vIsolate / vShow` button, named `vIsolate A` through `vIsolate E` buttons, and the combined built-in [Isolate](https://docs.mcneel.com/rhino/8/help/en-us/commands/isolate.htm) / [Show](https://docs.mcneel.com/rhino/8/help/en-us/commands/show.htm) button. Named buttons isolate into the matching set on left-click and use transparent [vShow](#vshow-flow) to restore that set on right-click; the built-in button uses `!_Isolate` and `!_Show`.

### vJoin flow

1. Preselect at least two joinable objects, or run `vJoin` and select curves, surfaces, polysurfaces, extrusions, meshes, or SubDs.
1. Press Enter to confirm a preselection. `vJoin` can run transparently inside another command.
1. On success, the command reports the input and output geometry counts and types, plus whether originals or copies were joined.

Options:

- `Copy`: `No` joins the selected originals; `Yes` joins in-place copies and preserves the originals.
- `Layer`: `Source` inherits the first compatible input object's layer; `Current` places joined output on Rhino's current layer.

Both settings persist immediately.

### vLine flow

1. Pick the start point.
Options (Start point):

    - `Mode`: chain behavior for subsequent segments.
      - `Single`: create one segment and finish.
      - `Multiple`: after each segment, pick a fresh start point.
      - `Chained`: each next segment starts at the previous end point.
      - `Polyline`: build/update one polyline as you add vertices.
    - `BothSides`: creates a symmetric line centered on the picked start point.
    - `Normal`: starts on a selected curve or surface and follows its normal.
    - `Angled`: defines a reference direction and rotates it by the entered CPlane angle.
    - `Vertical`: starts a line constrained to the active CPlane Z-axis.
    - `FourPoint`: defines the direction with two points, then picks the line start.
    - `Bisector`: starts at an angle vertex and follows the bisector of two picked sides.
    - `Extension`: starts at the selected end of a curve and follows its tangent.
    - `Parallel`: defines the direction with two points, then picks the line start.
    - `Perpendicular` and `Tangent`: pick the first curve, keep it feedback-highlighted without selecting it, and defer the exact start point until the end constraint is known.
    - `BiTangent`: hover-clicks two curves and previews the candidate tangent line while hovering the second curve.
    - `3Point`: pick two line-end constraints, then a curve or point the line must cross in 3D. Use `Point` at any pick to lock an exact point instead of a curve. The last curve is highlighted on hover and valid line solutions preview before clicking. For a fixed endpoint, sliding endpoint curve, and crossing curve, intersections are solved from a temporary point-to-curve surface and reused while hovering. The final crossing must lie on the line segment within document tolerance. Creates one line and ends the command.
    - `Layer`: opens the shared searchable layer selector with `*Current*` as the first item. Choosing `*Current*` follows the document's current layer dynamically; choosing another layer stores its full path. `-vLine` accepts the layer name or full path directly instead. The choice persists. If Rhino's current layer is changed outside this option while vLine is running, that layer overrides the target for the rest of the current command without changing the saved choice.
    - Reference geometry is display-highlighted without changing Rhino object or subobject selection; existing preselection is preserved.

1. Pick the end point.
Options (End point):

    - `3Point`: after a regular fixed start point, keep that point and pick the sliding endpoint curve and 3D crossing curve. Available for an unconstrained single line.
    - `Normal`, `Angled`, `Vertical`, `FourPoint`, `Bisector`, `Perpendicular`, `Tangent`, `Extension`, and `Parallel`: remain available where compatible with the start definition; incompatible constructions and controls are hidden. `Vertical` constrains the endpoint along the active CPlane Z-axis. A direction-defining mode is offered once per line. Endpoint constraints such as `Perpendicular`, `Tangent`, and `ProjectTo` preserve an existing start direction such as `Extension` and solve only where both constraints are geometrically compatible.
    - `Extension` continues outward when the selected curve already ends at the current chained start; otherwise it uses the selected curve end as the target anchor.
    - When a constraint pair has multiple valid positions, the branch nearest the first curve click remains stable; the endpoint click chooses among solutions on that branch.
    - Curve-constrained previews show a short white tangent bar and center point at each solved endpoint.
    - `Perpendicular`: solve against the hovered curve and use the valid perpendicular point nearest the cursor.
    - `Tangent`: solve against the hovered curve and use the valid tangent point nearest the cursor.
    - `PerpNear`: solve perpendicular against the nearest curve or surface/polysurface edge.
    - `TanNear`: solve against the nearest curve or surface/polysurface edge and use the valid tangent point nearest the cursor.
    - `Auto`: choose perpendicular/tangent solution using `Priority`.
    - `FromPoint`: for a perpendicular-from-curve start, picks and locks an exact starting point on the source curve; run it again to change that point.
    - `FromFirstPoint`: for a perpendicular- or tangent-from-curve start, locks the start to the initially clicked point instead of allowing the solver to move it along the first curve.
    - `ProjectTo`: picks a curve, surface, polysurface, or mesh and constrains the endpoint to its nearest projected point.
    - `Priority`: auto-mode choice policy.
      - `Closest`: whichever solution is closer to cursor.
      - `PerpFirst`: prefer perpendicular when available.
      - `TanFirst`: prefer tangent when available.
      - `KeepCurrent`: keep previous auto choice when possible.
    - `PersistConstraint`: keeps current constraint mode (`Perp`/`Tangent`/etc.) for following segments.
    - `Length`: forces segment length from start point. Not persistent across sessions.
    - `Angle`: angle value; setting this automatically activates the angle constraint for the current invocation. Not persistent across sessions.
    - `AngleRef`: `Absolute` uses CPlane X-axis; `Relative` uses previous segment direction.
    - `Mode`, `BothSides`, and `Layer`: also available while placing the end point.
    - Direct numerical input sets the segment length without leaving the active construction mode.
    - A hidden target layer is reported at the cursor and command line; the line is still created on that layer.

### vLineLength flow

1. Click an open curve near the end you want to drive.
Options:

    - `Length`: target value used by current mode.
    - `ExtendMode`: extension style when target requires growth.
      - `Smooth`: smooth extension.
      - `Line`: straight-line extension.
    - `Mode`: how `Length` is interpreted.
      - `Total`: resulting full curve length.
      - `Add`: add value to current length.
      - `Subtract`: subtract value from current length.

Hidden keywords while editing:

- `total`, `add`, `subtract`: set mode directly.
- `add/subtract`: toggle between add and subtract.

### vMatch flow

1. Click near an **edge-mate dot** (placed by [vUnrollSrf](#vunrollsrf-flow)) on a flat unrolled part. The neighbouring part snaps so its mating edge aligns with the selected edge at the configured gap distance, with the parts placed on opposite sides of the matched edges to avoid overlap. Auto assembly retains each transformed part's live dot positions and orientation while traversing the remaining matches.
Options:

    - `Distance`: gap between matched edges.
    - `Auto`: enters automatic assembly of selected parts, including parts preselected before starting vMatch; press Enter to use that set, or click parts to add/remove them. Ordinary mode does one match per click. StartFrom chooses a random root or requests a stationary middle part.
    - `Overlaps`: check all part groups with edge-mate dots, including parts not moved during this run, and report how many surfaces overlap. The highlighted results update as parts move until the command ends.
    - `StartFrom` *(Auto only)*: `Random` by default chooses the starting part randomly. `Pick` requests a single click on a part from the selected set, keeps it fixed as the magenta middle, and matches outward in deterministic alternating opposite-side BFS waves. The side axis comes from the middle part's most widely separated matching dots. Previous RandStart settings are read automatically. Moved parts appear progressively, with rapid display updates coalesced to avoid repeated full-scene redraws. Auto returns to ordinary matching after one batch and leaves affected parts highlighted in cyan, with the picked middle in magenta, until the command ends.
    - `RandNext` *(Auto only)*: randomise the order of BFS neighbours only with `StartFrom=Random`. Ignored with `StartFrom=Pick`.

Options persist to `vTools.config.json` under `vMatch`.

Notes:

- Surfaces from the matched parts that share area are highlighted using the shared highlighter with orange shading and a dark outline, distinct from the cyan matching preview. Highlights remain while choosing another edge, refresh after matching, Auto assembly, undo/redo, or document moves, and clear when the overlap is removed or the command ends. Unrelated parts are not included unless included by `Overlaps`; touching edges alone do not count as overlapping surfaces.
- Visible and hidden matching dots are included without changing their visibility. If the same mate ID occurs more than once, the closest matching dot on another part is used. Auto considers only selected parts that have not already been placed; duplicate dots on the source part are ignored.

### vMiddleCurve flow

1. Select exactly 2 curves (preselect supported — press Enter to confirm).
1. The command aligns curve directions and seams automatically, then creates an interpolated curve equidistant between the two inputs.
1. Sample density is chosen adaptively and refined until the middle curve error is within tolerance.
1. If both input curves belong to the same Rhino group, the new middle curve (and any connector lines) is added to that group.

### vMirror flow

1. Preselect objects or run `vMirror`, select the objects to mirror, and press Enter. Rhino groups are expanded so every grouped member participates.
1. Pick the start of the mirror axis in the active CPlane, or choose a plane/directional option that defines the mirror immediately.
1. For a picked axis, pick its end while the mirrored result previews. The final preview remains visible until Rhino finishes the mirror and text post-processing, then the mirrored objects are selected.

Options:

- `3Point`: define a three-dimensional mirror plane with three points; a live rectangle displays the picked plane.
- `XAxis` / `YAxis` / `ZAxis`: use the corresponding active-CPlane axis or plane through the CPlane origin.
- `Object`: use a selected planar surface or polysurface face as the mirror plane.
- `Horizontal` / `Vertical`: immediately mirror in place across the corresponding active-CPlane axis through the selected objects' bounding-box center.
- `Left` / `Right` / `Top` / `Bottom`: mirror directly to that side of the selection in the active CPlane.
- `Distance`: gap between the original and directional mirrored bounding boxes for `Left`, `Right`, `Top`, and `Bottom`.
- `Copy`: `Yes` preserves the source objects; `No` mirrors the originals.
- `FlipText`: keeps mirrored annotations readable while preserving geometry and placement. Ordinary text and leaders follow their mirrored in-plane frame. Dimensions using Rhino's `Horizontal` text-angle mode retain their native mirrored position and angle while being corrected to read right-side-up; aligned dimension text follows its mirrored annotation frame.
- `SwapText`: applies the configured bidirectional replacements to mirrored vTitle text.
- `EditTextReplacements`: opens the persistent replacement-rule editor used by `SwapText`.

`Copy`, `FlipText`, `SwapText`, `Distance`, and replacement rules persist between runs.

### vNest flow

1. Preselected planar curve-based parts are added as soon as the window opens, including when command-line settings are supplied first; running vNest again adds the current preselection to an existing window without replacing its members. Click parts in the drawing, including edges, canonical interiors or preview copies, to add them without holding Shift; plain clicks retain the existing list and Ctrl-click removes the clicked part. Each added or clicked part becomes the active row and scrolls into view. Blank clicks retain the current selection; ordinary window/crossing selection also adds to existing membership, while Ctrl-drag retains native deselection behavior. An incomplete boundary pick does not clear valid nest members. There is no command-line selection stage or Enter confirmation, and the full-width list follows document selection. Included originals stay highlighted to identify nest membership. Grouped parts remain separate; disjoint ungrouped boundaries are recognized as separate parts. Large Reference-layer text supplies part names where available.
1. Review the material tables and part list in the modeless window. Selected valid rows identify both originals and preview copies in magenta, distinct from cyan nest membership and Rhino's native selection. Oversized or invalid parts keep their orange warning highlight even when their row is selected. Adding or removing parts retains surviving preview placements and table positions; new parts remain Pending until Nest runs again. Delete removes selected list rows from the nest without deleting source objects. The list follows current queued, trial, placed and skipped states and trial table/rotation values. Include or exclude parts, and edit each part's grain reference, rotation preset or custom increment, actual angle, table, and X/Y position. Edited positions become Fixed; manually edited angles override automatic presets and are retained by Nest.
1. Copies requests individually nested copies while retaining one source-settings row; each preview and output copy is independently positioned and grouped. Fixed anchors the first copy only, with further copies packed automatically. Select multiple rows to edit common settings together: unequal numbers and presets show `<varies>`, and mixed checkboxes are indeterminate. Table and X/Y remain single-part controls. Per-field reset overlays restore defaults, and Reset restores selected parts to one copy, automatic placement, detected grain and global options without changing originals.
1. Selection updates capture only newly added or incomplete parts, retaining unchanged rows and their settings. Group membership and outline bounds limit containment checks when resolving new parts; diagnostic selection timings report retained/captured counts separately from nesting time.
1. The Tables tab lists editable table names, width x occupied length, and occupied material in yards. Length and yardage use one decimal place. Matching three centered annotation rows occupy a tab at the middle of the table's left edge. The label box and table perimeter form one closed polyline without internal dividing edges; the outline and three separate annotations belong to the table group, with no separate label group. Each nested part retains its own child group. DXF export omits the table outline and exports only a label-sized framed block alongside the separate part blocks. Changing a name updates its label without restarting nesting. Settings changes during nesting automatically restart the search using the new values, retaining the current preview until new placements arrive, even with Auto off; Stop still stops without restarting. The accepted group uses the chosen name with a uniqueness suffix if needed. Table decorations are ignored when adding existing nested parts back to the list.
1. Table labels use a visible glyph height of 8% of material width, independent of model-space annotation scaling, reducing only when needed to fit long text within table length. Border clearance, row gaps and box padding scale with the labels. Horizontal table display spacing accommodates each following label box; vertical labels stay within their table's height. Preview and placed parts use the same display offsets without changing local nest geometry or material calculations.
1. Nesting reuses bounded geometry caches between runs, skips rotations only when their best possible placement cannot improve the score, and retains exact outline/clearance collision checks. Diagnostic profiles report pair calculations, anchor operations, collision work and cache reuse. Focus/rounding events that leave displayed settings unchanged do not invalidate a run; actual changes retain the prior preview until re-nesting, with Place disabled for a stale layout.
1. Press Nest to pack the included parts. The same button changes to Stop while searching; Stop retains the best valid layout, including already packed parts during the first pass. The status bar shows a percentage overlay without a remaining-time estimate, then shows elapsed time after completion or Stop, including part preparation. Throttled viewport previews show real trial placements; the best complete collision-free layout is restored when searching finishes. The status tooltip reports placement tests, completed layouts and accepted improvements. Nesting is manual by default; Auto optionally re-nests after relevant edits. The minus button deselects highlighted rows' originals without deleting them. Clear stops the search, clears the list and preview, and deselects originals without deleting geometry. Additional material tables are created automatically. Placed-part count and total consumed material take priority over table count; equal-yield placements favor skinny parts at the left end, then prefer the bottom edge of each horizontal table.
1. Use Location to move the entire preview with snapping. Before nesting, an empty table follows the cursor by its bottom-left corner; its accepted location is retained when parts are added. Enter accepts the displayed position; Esc restores the previous position. Place commits the accepted preview without rebuilding document selection, creating separately grouped part copies and table outlines in one undo record and retaining the original parts. Close discards the preview without creating geometry or clearing document selection.

Options:

- `Width`: each material table's width in document units; default `60`.
- `Length`: each material table's length in document units; default `315`.
- `Gap`: minimum distance between part outlines in document units; default `0` allows touching. Remembered between runs and available in the window and command-line settings. Applied to initial packing, refinement and fixed placements; does not add a table-edge margin or change source/copy outlines.
- `Grain`: supplies a grain direction. `Auto` uses a detected or user-specified guide; parts without guides use free rotation. `Force` asks for missing guides when Obey grain is enabled. `On` uses a guide if present, otherwise the part's source-plane up/down axis. `Off` ignores guides. The longest open curve named GRAIN or on a GRAIN leaf layer is preferred, case-insensitively and ignoring surrounding spaces. Polylines use their longest segment; other curved guides use the middle tangent. Lifted guides are projected only for direction measurement, without changing original geometry. Pick Grain, Draw, or the selected part's Grain angle field can specify a direction.
- `ObeyGrain`: `Yes` by default constrains specified grain to up/down across material width, using the deviation allowance and Grain turn rather than free rotation. Each part can enable or disable Obey grain independently. Disabling it preserves the guide but enables full free-rotation choices. Manually entered or fixed angles must still satisfy enabled grain constraints; invalid parts are skipped, not silently rotated or placed.
- `Rotation`: free-rotation presets for parts not obeying a specified grain: `NoRotation` preserves source angle, `AnyRotation` searches any angle, `45` offers eight source-relative orientations, `90` four, `180` two (the default), and `Custom` uses `RotationStep`. These are increments, not maximum deviations. For grain-constrained parts the selected-part Rotation dropdown instead offers `No reversal` or `Allow 180 reversal`, plus Default to inherit the global Grain turn. No reversal still aligns grain initially and permits its deviation allowance; it does not preserve an arbitrary source angle.
- `RotationStep`: custom increment in degrees, `0.1` through `360`, enabled when Custom is selected; default `45`. Only discrete custom orientations are searched. Use Any rotation for continuous-angle refinement.
- `GrainDeviation`: global allowance in degrees either side of permitted grain-aligned directions, `0` through `90`, default `0`; remembered between runs. Each part's Deviation uses this value when Default is checked, or its own allowance otherwise. Grain-constrained parts cannot exceed it even if the free-rotation preset is Any. Unconstrained parts ignore this allowance.
- `Allow180Turn`: `Yes` by default offers both grain-aligned directions; `No` permits only the forward direction. Both obey GrainDeviation. The window calls these `Allow 180 reversal` and `No reversal`. Each part can override this through its constrained Rotation dropdown. This setting does not restrict unconstrained rotation presets. Mirroring is controlled separately by Flip and must also satisfy enabled grain constraints.
- `Stack`: `Horizontal` places tables side by side; `Vertical` stacks them downward. Each table stays horizontal in either arrangement. Remembered between runs. Changing Stack retains all part rotations and local placements.
- `Flip`: allow mirrored parts during nesting; default `No`. Allow Flip enables automatic mirrored alternatives. The selected-part Flipped control fixes its handedness; Auto Rotation restores automatic rotation and flip choices. Mirroring is across material grain, so grain alignment is preserved, and copied annotation text remains readable.
- `SearchTime`: refinement budget in seconds after the initial layout; default `30`, range `0` through `3600`. Zero skips refinement. Larger values allow more joint layout and pair searches. Native polygon operations may slightly overrun the budget; cancellation is checked between operations. Remembered between runs.

Notes:

- Tables run horizontally, with Length along CPlane X and Width along CPlane Y. Rotation values remain relative to the original parts, so changing table orientation does not silently turn grain-locked parts.
- Pick Grain accepts any curve belonging to the part, regardless of layer; its tangent at the clicked point defines grain. Choose Draw to enter two snapped points instead. The guide is temporary: it adds no document objects and preserves the existing selection.
- Packing uses cutting outlines for material fit, polygon contact candidates and overlap checks, so labels and other reference details do not make an otherwise fitting part too large. Concave parts can share space inside overlapping bounding boxes without overlapping their outlines. Fixed placements are reserved first. After the initial layout, full-layout search retains multiple combinations of rotations and contact positions across different part orders, then refines part pairs. Only better complete valid layouts replace the saved best result. Free rotations are refined beyond coarse angle samples. Curves are sampled to polygons; this is a bounded heuristic search, not a guarantee of global optimality. Search statistics and timing are recorded in the plug-in log.
- Material yield uses the full allocated stock: every table before the last consumes its entire configured length, including unused tail space; only the last table is trimmed to its used length. Opening another table therefore charges the unused remainder of the preceding table, not just the new part's length. Existing tables are filled before allocating another. Skinny grain-guided parts favor the left end of a horizontal table when total material consumption is equivalent.
- Free-rotation alternatives retain their exact 180-degree counterparts. A dedicated refinement pass turns concave parts in their current footprint and refits partner pieces into the resulting space, using only allowed poses and accepting improved material yield. The status tooltip and diagnostics report actual half-turn tests.
- Narrow parts with enabled detected or user-specified grain are placed first in the initial pass (at least a 3:1 fitting-pose aspect ratio). With SearchTime greater than zero, a dedicated gap-filling pass relocates them between already placed parts only when total used material length strictly decreases, without moving fixed or larger parts, exceeding any table's existing used length, or adding tables. Filling a gap alone is not an improvement; equal-yield leftward alignment is allowed during other refinement. Grain and overlap constraints remain enforced.
- Oversized parts, conflicting fixed placements, or parts with no permitted rotation are highlighted on their original geometry and skipped. Place creates only successfully nested parts; the status lists the skipped count. The automatic table safety limit is 128.
- Geometry is preview-only until Place. Original objects and their groups are not changed. Nest copies preserve source layers and belong to individual part groups and their table group; table outlines use Reference.
- The cutting outline must be planar. Lifted or other off-plane detail curves are retained in their original geometry and moved with the part; they do not invalidate the whole list. Reference-layer decoration is not used as the cutting outline when a closed non-reference perimeter exists.
- The [Clipper2](https://github.com/AngusJohnson/Clipper2) polygon engine and [RectpackSharp](https://github.com/ThomasMiz/RectpackSharp) rectangle helper, with their license notices, are embedded in the plug-in; no separate geometry DLL needs to be installed.

### vNotches flow

Use Ctrl+Shift to select individual polycurve or polyline segments, including multiple segments from the same parent. Segments have independent row identities, Side, Reverse, and Both sides settings; touching selections can still link into a chain. The parent curve supplies group membership and source metadata. The Select button supports adding and removing segments as well as whole curves. All curve-ID labels share the width of the widest displayed ID, keeping the following controls vertically aligned.

Numeric fields, label text, and layer selectors show an inline reset icon only when their value differs from the built-in default. The icon restores that field without covering the spinner or dropdown arrows.

Connected runs inside a linked sequence retain their junction kinks even when another linked curve is separated by a gap. A notch snapped to a kink uses the middle orientation of the two touching curves; gaps are never bridged for placement.

1. Select one or more open or closed curves (preselect supported; press Enter to confirm). If all selected curves form one connected end-to-end chain, they are automatically joined and treated as a single curve with kinks preserved at segment junctions. For grouped curves, the outermost containing boundary in the part group determines the notch side: an inner curve (typically PLOT) faces toward the outer boundary (typically CUT1), while a curve already on that boundary faces inward. Smaller usable part groups take priority over enclosing assembly groups. Without a usable group boundary, newly selected curves use the inward side when they are closed or form an unambiguous closed boundary with visible curves at the same elevation, including endpoint connections and intersections. Each linked source segment is evaluated in its own direction. Open or ambiguous boundaries retain the existing side; Side remains manually adjustable and is preserved during reordering, linking, and reselection in the current session.
1. A floating **Notches** panel opens. Click positions along the curve(s) to place notches.
1. Use the disclosure chevron in each group header to collapse or restore the Notch, Multiple, and Label settings.
1. Numeric controls and readouts display at most three decimal places without unnecessary trailing zeroes.
Options (Notch group):

    - The `Notch` header checkbox controls notch geometry output. Notch and Label can both be enabled, but the command keeps at least one enabled.
    - `Type`: five checkbox-sized vector buttons select `I`, `V`, open `\/`, flat-capped `U`, or upside-down `T`, in that order. The icons and active highlight update from the current Width and Length values.
    - `Layer`: target layer for notch geometry, using the same packed-ARGB swatches as vObjectPropertiesPlus.
    - `Length`, `Width`, and `Offset`: compact numeric steppers; width controls `V`, `\/`, and `U` arm separation and the `T` crossbar.
    - The compact icon after the Type buttons previews `NotchTrim`: a continuous contacted curve (`No`), a contrasting between-leg section (`Split`), or a gap (`Trim`). Click it to cycle modes. When both legs of a `V`, open `\/`, or `U` notch touch one offset curve or two curves joined at an endpoint, `Split` moves the between-leg piece to `NotchTrimLayer` (default `Reference`), while `Trim` removes it. With Offset zero, the selected source curves are split or trimmed instead. Outer pieces retain their layer and group; undo and redo restore the edit with the notch.
    - Created notch curves are named `NOTCH` and carry `notches.db.*` user-string attributes describing their source curve, placement, dimensions, side, label settings, and layers. The disconnected `\/` legs and branched `T` stem/crossbar are grouped component curves tracked as one notch.

Options (Label group):

    - The `Label` header checkbox controls label output. The remaining label settings stay editable whether output is enabled or not.
    - Value text box: the label string placed at the notch. The next-label icon beside it scans existing document notch labels, including hidden and locked labels, and sets the value after the highest used label. When multiple numeric, alphabetic, or prefixed mixed sequences exist, choose one from the icon's dropdown. Numeric padding is preserved and alphabetic values continue from `Z` to `AA`; unrelated document text is ignored.
    - `AutoAdv`: when enabled, increments a trailing numeric or alphabetic suffix after each placement.
    - `Side`: checked prefers labels toward the curve start; unchecked prefers away from it.
    - `Layer`: target layer for label text, using the same packed-ARGB swatches as vObjectPropertiesPlus.
    - `Size`: manual label text height. `Auto` computes height proportionally from notch geometry; the adjacent percentage stepper scales the auto-computed height. When `Auto` is checked the manual size field is disabled; when unchecked the percentage stepper is disabled.
    - `Offset X` / `Offset Y`: numeric steppers for label position relative to the notch point (along-curve and across-curve). Label `Side` remembers the direction relative to the curve start: checked prefers toward the start, unchecked away from it, regardless of automatic notch-side changes. Both-side labels use the same along-curve preference.

Options (Multiple group):

    - `Start offset` / `End offset`: numeric steppers for the distances from each curve's respective ends to the first and last notch.
    - `Auto`: uses curvature-aware spacing. Every enabled curve contributes to one shared curvature envelope, so turns occurring at different positions on different curves all add density at their corresponding stations. Curve kinks are preferred station candidates, use the middle kink orientation, and replace nearby regular stations. The adjacent unlabeled integer control sets sensitivity in fine whole-number steps: zero produces uniform maximum-distance spacing, while larger values make tangent changes reduce spacing more strongly. `Distance` becomes the maximum gap, and one sampled turn transition cannot create clustered duplicate stations. Existing notches placed in the current session exclude candidates within one local neighboring-station spacing, measured along each curve. If any enabled curve rejects a station for this proximity, that station is skipped on all enabled curves so matched notches remain paired. Unchecking Auto restores the Number or Distance mode that was selected before Auto and immediately recalculates the inactive companion value.
    - `Number`: numeric stepper for the total number of notches; minimum is 1. When `Number=1` a single notch is placed at the start offset position only.
    - `Distance`: editable numeric stepper with a `1.0` button increment. Changing `Number` evenly distributes the fixed start/end span. In regular Distance mode it is the minimum repeated spacing; in Auto mode it is the maximum allowed spacing. Absolute stations use the shortest enabled curve as their distance base while curvature contributions remain combined from every enabled curve.
    - `Add`: creates the previewed notch batch. Notches already added to the current curve selection reserve their locations, so proposed stations closer than the active local spacing are omitted per curve while missing companion-curve stations remain available. Its inset `Separate` checkbox applies Number, Distance, or Auto spacing independently to every physical segment in a linked sequence, then maps the expanded station count across companion curves. For example, `Number=3` on a linked pair creates three notches per segment and six on an accompanying single curve. When labels are enabled, only the first new station receives the label and auto-advance runs once.

Options (Other panel controls):

    - `Percent`: display and place by relative curve position. When disabled, the control is highlighted if selected sequence lengths differ by more than 1/16 inch.
    - `Group`: group an ungrouped source curve with its notch and label outputs. Notches and labels always inherit every existing group of the source segment they land on, whether Group is enabled or not.
    - `Select`: return to individual-curve selection without selecting groups or ending the command. Its inset checkbox defaults unchecked: unchecked selection replaces the current curve set; checked selection keeps the current curves so others can be added or removed. The setting is saved immediately. Existing placed notches remain in the document. Replacement curves inherit Side by sequence, retained curves preserve their source-specific Side and oriented start, and explicitly clicking another end intentionally defines a new start.
    - Per-curve row — use the grip at the left to drag and reorder curves. The stable, optically centered curve number appears before its enable checkbox and highlights when the row or corresponding viewport curve is hovered. During dragging, the entire source row and curve are highlighted while surrounding rows move aside; a row moved within its original linked block is relinked automatically. Side and Reverse use compact borderless arrow controls. The smaller icon after Reverse toggles Both sides for that source curve, duplicating both notch geometry and label text; in a linked chain, the segment containing the placement point supplies this setting. The paired components share one placement for preview and undo. The command-line `BothSides` option toggles the curve under the cursor. Use the borderless link button between rows to join or separate placement sequences. The centered length badges use equal compact padding and `999.999` only as their minimum-width sample for individual and combined linked lengths; cumulative values include a clipping allowance, and larger values remain fully displayable in the scrollable row. The longest and shortest sequences also show transparent-background green `(+difference)` and red `(-difference)` superscripts after the applicable length.
    - Distance info: **From start**, **From end**, **From previous** show arc-length values rounded to three decimal places.
    - **Undo** / **Redo** buttons: step backward or forward through placements.

1. Created notch components are named `Notch`; created text objects are named `NotchLabel` and carry the same source, placement, geometry, layer, tangent, and label metadata as their matching notch, plus label and paired-notch identifiers.
1. Notch, label, multiple-placement, layer, Percent, and Group settings are saved in the Rhino document and override global settings when that document is reopened. Window and selection preferences remain global.
1. Press Enter to finish and keep all placed notches. Press Esc to cancel and remove them.

The floating panel adds scrollbars when resized below its content size.

Options persist to `vTools.config.json` under the `vNotches` section.

### vOffset flow

1. Select one source curve, or preselect it before starting the command.
1. Pick the offset side while previewing the final result.
1. The selected curve and settings are passed to Rhino's built-in [Offset](https://docs.mcneel.com/rhino/8/help/en-us/commands/offset.htm) command when the side is accepted.
1. After each completed offset, selection is cleared and `vOffset` starts again automatically.
1. Press Escape to exit the loop.
1. Press Enter to repeat `vOffset` for a new loop.

Options (available during source and side selection):

- `Distance`: fixed offset distance when `ThroughPoint=No`.
- `Loose`: passes Rhino's loose offset mode through to the final offset.
- `Corner`: selects `None`, `Sharp`, `Round`, `Smooth`, or `Chamfer` corner handling.
- `ThroughPoint`: uses the picked side point to determine offset distance instead of `Distance`.
- `Trim`: previews and commits the trimmed Rhino offset when enabled.
- `Tolerance`: calculation tolerance in model units.
- `BothSides`: previews and creates offsets on both sides of the source.
- `InCPlane`: offsets in the active CPlane when enabled; otherwise uses the curve plane.
- `Cap`: selects `None`, `Flat`, or `Round` end caps.
- `OutputLayer`: `Current` uses Rhino's current layer; `Input` uses the source curve layer.
- `Group`: `Auto` adds output to the source groups or creates a source/output group when ungrouped; `Yes` creates a separate group containing only that source and its outputs; `No` leaves grouping unchanged.
- `AutoTrim`: checks each open source endpoint independently. When the endpoint touches another curve, the new offset is trimmed if it crosses that curve, left unchanged if already touching, or extended when it falls short.
- `DeleteSource`: `No` by default retains the original; `Yes` deletes it only after all offset curves are created successfully. Cancellation or failed output preserves the original, and undo restores it together with removing the offset.

The options retain native order so existing hotkeys remain stable. `Group`, `AutoTrim`, and the other command settings persist; group changes share the offset undo record.

### vOrient2pt flow

1. Select objects to orient.
1. Pick source first point.
1. Pick target first point.
1. Pick source second point.
1. Pick target second point.

Options:

- `Copy`: creates an oriented copy when enabled; otherwise transforms the selected originals.

### vOrient3pt flow

1. Select objects to orient.
1. Pick source first point.
1. Pick target first point.
1. Pick source second point, or press Enter for 1-point orient (translation only).
1. Pick target second point, or press Enter to use source second point.
1. Pick source third point, or press Enter for 2-point orient.
1. Pick target third point, or press Enter to use source third point.

Options:

- `Copy`: creates an oriented copy when enabled; otherwise transforms the selected originals.

### vOverlaps flow

1. Optionally preselect curves, surfaces, polysurfaces, or individual face subobjects; if none, press Enter at the prompt to scan all visible curves and Brep faces in the document.
1. Press Enter to run: covered, duplicate, and partially overlapping curves are selected, while only coincident edge intervals belonging to overlapping face pairs receive the shared cyan-and-black overlap highlight.

Options:

- `Tolerance`: proximity threshold used for overlap detection; the default is `0.001` model units.
- `OverlapSegments`: splits partial findings and selects only their exact overlapping pieces when enabled; the default is `Yes`. Disable it to select the chosen whole source curve instead.

Behavior:
- **Same-path duplicates**: both curves follow the same path and have the same length. The oldest (lowest runtime serial number) is kept unselected as the original; all others are selected.
- **Covered curves**: a shorter curve lies entirely on top of a longer one. The shorter (covered) curve is selected.
- **Partial curve overlaps**: curves share a length-bearing interval but neither fully covers the other. The source with fewer endpoints connected to other visible document curves is chosen, even when only the overlapping pair was selected for analysis; equal endpoint connectivity chooses the newer Rhino object. With `OverlapSegments=Yes`, that source is partitioned at the exact overlap boundaries and only the overlapping piece is selected or deleted; its original ID remains on the longest non-overlapping remainder, and all pieces retain the source attributes and groups.
- **Compound curves**: polycurve and polyline components are compared automatically; there is no separate detection mode to configure.
- **Overlapping faces**: planar trimmed regions are compared by shared area; coincident curved faces use bidirectional interior checks. Faces that only touch at an edge are ignored, and only edge intervals shared by detected face pairs are highlighted.

Option persists to `vTools.config.json` under `vOverlaps`.

### vPart flow

1. Select the outer perimeter curves or individual polycurve segments (preselect or postselect; a single closed curve is also valid). Distinct selected segments from the same parent curve are kept separately; unselected segments inside the Part retain their original layer.
1. When a selected perimeter contains selected open dividing curves, click the perimeter on the side to keep. The perimeter may be a closed curve or assembled from separate curves or selected segments. The part is the region between the divider curves and that clicked outer section; the outer section away from the click is trimmed from the copy. Interior divider ends that stop short can extend to the selected perimeter. Without an explicit perimeter-side pick, the largest-boundary behavior is retained.
1. The command joins the selected curves into a closed loop.  If endpoints do not quite meet (gaps ≤ 200× model tolerance), straight-line bridge segments are inserted automatically.
1. All visible objects inside the closed perimeter are collected automatically (excluding the selected perimeter curves). Curves that cross the perimeter are split; only the inside segments are kept. With `Cleanup=Yes`, eligible straight interior lines on the generated perimeter's effective layer are omitted unless they are named or metadata-tagged notch geometry. Non-curve objects (text, dots, points, etc.) are included whole when their representative point falls inside.
1. A full DynamicDraw preview of the Part (perimeter + inside objects) follows the cursor. The perimeter uses its selected output-layer color; interior objects use their original layer colors. Changing Rhino's current layer during placement changes the perimeter destination without changing the previewed geometry or rerunning cleanup on that layer. Choosing the command's `Layer` option explicitly rebuilds the cleanup preview.
1. Pick the placement point to commit.  The Part is added as new objects at that location; originals are not deleted.
1. Press Esc to cancel without adding anything.

Options (available during both curve selection and placement):

- `Group`: when `Yes`, all output objects are placed into a single Rhino group.
- `JoinPerimeter`: when `Yes`, perimeter segments are joined into a single curve instead of being kept as individual segments.
- `Cleanup`: when `Yes`, omits eligible straight interior pieces only when they are on the generated perimeter's effective layer. In `Layer=*Source*` mode, Cleanup runs only when the perimeter uses one source layer; with multiple source layers it is skipped and reported during placement. Notch geometry and curves on unrelated layers are preserved.
- `Layer`: chooses the output layer for perimeter segments and gap bridges. `*Current*` follows Rhino's current layer for that command session; `*Source*` preserves each split perimeter segment's source layer, while joined output and bridges use the first actual boundary source layer. In scripted input, `.` selects `*Current*`, and `Source` or `*Source*` selects source layers.

### vPartSplit flow

1. Select one or more curve-based planar parts and their details. Preselection remains visible while adding or removing parts before Enter. The outer boundary can be one closed curve or several connected curves; text, dimensions, dots, and points can accompany the selected curves. Each selected part is split independently.
1. Review the split proposals. Orange lines on the originals show nominal cuts without seam allowance. The copy previews show their actual cut edges and PLOT sew lines. Drag the middle of a cut to slide it; drag near either end to pivot around the opposite end with object snapping. Parallel cuts are added or removed as needed to keep each final piece within the material width. With multiple sources, Part chooses which part Angle controls.
1. Press Enter to place the ghosted copies. Width, Seam, Mode, Layout, Distance, Direction, Angle, and Auto remain available during placement. Click to place, or press Enter to accept the displayed location. Esc cancels without changing the original parts.

Options:

- `Width`: material width in document units, default `60`. The final cutting outline, including allowance at every common seam, must fit this width.
- `Seam`: allowance added to each part at each shared cut, default `0.5` document units. Adjacent parts therefore overlap by `1.0` with the default. Zero creates adjacent cuts without overlap. Width must exceed twice Seam.
- `Mode`: `Even` distributes nominal cut positions roughly evenly across the part; `MaxWidth` fills each usable material-width band and leaves a smaller final remainder where needed.
- `Layout`: `Original` preserves each source part's alignment and spreads its pieces perpendicular to their cut lines; `Vertical` turns each copy's longest bounding-box edge upright and places copies side by side, using their rotated widths for spacing and choosing the nearest upright direction to avoid unwanted half-turns. Its orientation follows each piece's bounding box rather than the split line. Multiple source families are placed separately in the batch layout.
- `Distance`: clear distance between neighboring final cut edges in document units; default `1`. The same distance applies to both layout modes. Changing Layout or Distance retains manually adjusted cuts.
- `Angle`: split orientation in degrees within the part's plane. Available once the boundary has been identified.
- `Auto`: recalculates the initial orientation and layout for the current settings. Available during adjustment and placement.
- `Direction`: `Start` fills from the initial end; `End` fills from the opposite end; `Inward` fills symmetric full-width outer pairs toward the center, leaving one or two smaller center pieces; `Outward` fills full-width center pieces toward both ends, leaving symmetric outer remainders. In MaxWidth this changes the actual cuts while retaining seam allowance in the width limits. In Even, cuts remain evenly spaced and only output order changes. Part chooses which selected source Direction controls; Auto retains each source's direction. Side-by-side copies and part numbering follow the chosen order without mirroring; Original layout retains the parts' original relative alignment.

Notes:

- Source split-line previews omit seam allowance; copied-part previews and final output include it. Curved boundaries remain curved, and interior curve details are clipped as original fragments rather than being closed artificially.
- Originals are retained. Each output part has its own group and preserves source detail layers. Its cutting perimeter uses the original outer-boundary layer. Each common nominal cut is included in the copied parts as a PLOT sew line. Nominal cut lines on the originals use Reference and are grouped with their source parts.
- Width, Seam, Mode, Layout, Distance, and the last selected Direction are remembered in the shared configuration. Layout planning is shared with [vShade](#vshade-flow).

### vPerpendicularTo flow

1. Pick **curve A** — the curve to rotate.
1. Pick **curve B** — the reference curve (not moved).

Behavior:

- The nearest endpoint pair between A and B is found automatically.
- Curve A is rotated about its near endpoint in the active CPlane by the angle needed to make it perpendicular to B's tangent at B's near endpoint.
- Of the two possible perpendicular directions, the one requiring the smaller rotation is chosen.

### vPointAlongCurve flow

Places one point on an open or closed 3D curve at an arc-length distance from a picked starting position.

1. Select a curve, or preselect one before starting.
1. Pick the starting position on that curve. Object snapping is available.
1. Move the cursor toward the desired direction. A live point preview follows the chosen side; click to place it. Snapping remains available when choosing direction.

Options (available at every stage):

- `Distance`: non-negative distance along the curve in document units. Type a number directly at any prompt, or use the option. Zero places the point at the starting position. The value persists in `vTools.config.json`.
- `Project`: what to do beyond an open curve's ends. `Skip` (default) omits out-of-range points; `End` uses the endpoint and displays the actual distance in the preview and command output; `Straight` continues the end tangent; `Smooth` follows a smooth curve extension at the requested arc length. This option also persists and is available at every stage.

Notes:

- Direction always chooses the valid distance point closest to the cursor in the active viewport, whether snapped or not. On a closed curve, distances can wrap around the seam.
- With `Project=Skip`, the other direction is used if only one side has enough length. If neither direction fits, reduce Distance or change Project. Other Project modes never modify the source curve; only the point is created.
- The point uses the current layer and inherits the source curve's groups.
- Macro example: `! _vPointAlongCurve _Distance 5 _Pause _Pause _Pause`. A direct initial number can replace `_Distance 5`.

### vPointNormalToSurface flow

1. Select a target surface or polysurface face.
1. Pick points in space.
1. A point is placed on the closest evaluated surface location (normal evaluation point), with live preview from picked point to on-surface point.
1. Press Enter to finish.

### vProjectToSurface flow

1. Select one or more target surfaces or polysurfaces.
1. Select curves and point objects to project; the projected result previews while the selection changes.
1. Projected point objects and curve pieces are created on the target and selected; curve pieces use the current layer and source objects are left unchanged.

Behavior:
- Curves are pulled to every selected brep face by closest-point projection.
- Portions of a curve that do not touch the trimmed target face are skipped, so curves longer than the surface produce only the projected touching spans.

### vPointTrace flow

1. Click the source curve near the end you want to treat as the start.
1. Click the destination curve near the end you want to treat as the start.
1. Source and destination curves are highlighted but left unselected.
1. Pick points constrained to the source curve; a corresponding point is added on the destination at the same arc-length fraction.
1. A green dot previews the destination point while moving along the source.
1. Press Enter to finish.

### vRectangle flow

1. The command starts directly at bottom-left placement using the stored width and height. If curves are preselected, their total length replaces the width automatically.
1. Type `widthxheight`, such as `123x345` or `10+1/2x4+1/2`, during placement to update both dimensions together.
1. Enter one, two, or three construction-plane coordinates to place the corner directly; a single value is treated as `(value, 0, 0)`.
1. Pick the bottom-left corner. Press Enter to reuse the previous bottom-right position.
1. Live preview shows the rectangle and optional dimension label while moving the cursor.

Options:

- `Width`: changes the current rectangle width while picking the corner.
- `Height`: changes the current rectangle height while picking the corner.
- `Layer`: places the rectangle on the selected layer; `*Current*`, `.` or `*` follows Rhino's current layer.
- `Label`: adds a centered `width x height` annotation on the `Reference` layer, formatted with the document's fractional or decimal settings, fitted inside the rectangle with 10% padding on every side, and grouped with the rectangle.

### vReGroup flow

1. Preselect or select objects in any combination of groups and sub-groups.
1. If the selection spans exactly one group and every member of that group is selected, the existing group is reused and any ungrouped objects are simply added to it.
1. Otherwise, all existing group memberships (including nested sub-groups) are stripped from the selected objects, any groups left empty are deleted, and all selected objects are placed into one new group.

### vScallop flow

1. Select a line, or press Enter to pick two points.
1. Pick side point to define bulge direction.
Options:

    - `Size`: scallop bulge distance.
    - `Free`: when `Yes`, bulge is measured from midpoint to picked side point; when `No`, uses fixed `Size`.
    - `DeleteOriginal`: when selecting an existing line input, remove that original line after creating the scallop arc.

### vSetPt flow

1. Select open curves to align. Preselected curves seed the editable selection; add or remove curves before pressing Enter. Each curve keeps the end nearest the cursor when that curve is selected, even if its other end is nearer the eventual common target; deselecting and reselecting the curve captures a new end. Any preselected edit-point or control-point grip also seeds its owning curve and overrides endpoint detection for that curve. Closed curves are ignored.
1. With preview enabled, thin cyan temporary curves show each preselected grip, or otherwise the endpoint nearest to the viewport cursor, moving to a common target that follows the cursor at the selected points' view depth. Edit-point previews rebuild the curve through the target; control-point previews move the selected CV directly.
1. Grips are enabled and the identified points are selected automatically. After a successful [SetPt](https://docs.mcneel.com/rhino/8/help/en-us/commands/setpt.htm), including when the points are already at the chosen coordinate, the exact endpoints, edit points, or control points used remain visible and selected so Rhino displays the gumball; cancelling restores each curve's original grip visibility.
1. Pick the target location with `XSet=Yes YSet=Yes ZSet=Yes Alignment=World Copy=No` initially. With `Preview=All` and `Copy=No`, the original curves update when the placement changes, and Rhino replays their recorded history so dependent surfaces show their resulting shape in their document colors. Updates are throttled outside the mouse callback, without adding a delay after each rebuild. The preview is rolled back before Rhino's native [SetPt](https://docs.mcneel.com/rhino/8/help/en-us/commands/setpt.htm) commits the exact picked coordinate; Enter without a point or Escape cancels placement and restores the original curves and their history results. `Preview=Curves` uses temporary curve previews without editing originals or rebuilding history. Copy mode also uses temporary curve previews. Curves that are themselves history children, or sessions with history updates or undo recording disabled, also use temporary curve previews to avoid breaking history before confirmation or showing results that would not update after the final commit.
1. A committed move, including its history results, is one native Undo/Redo step; preview updates do not remain as separate undo steps. Press Enter to repeat `vSetPt`.

Options:

- `Preview`: `Off` hides previews; `Curves` shows temporary cyan curve previews without updating history surfaces; `All` (default) also previews supported history results during final placement. Available during selection and placement. The setting persists; an older enabled setting becomes `All`.
- `XSet`, `YSet`, `ZSet`: choose which coordinates align to the picked location. Disabled coordinates retain their original values. All three start enabled on each run.
- `Alignment`: `World` (initial value) or the active viewport's `CPlane` coordinate system.
- `Copy`: `No` (initial value) edits originals; `Yes` copies the edited curves using native SetPt.

### vShade flow

1. Select the first open curve near the end that forms the intended corner.
1. Select a different open curve near its corresponding corner end. The shade is placed immediately. The chosen curves remain highlighted without becoming selected.
1. Continue selecting curve pairs for additional corners in any order. Selecting an already placed corner recalculates that corner in place using the current options, retaining its label and position in the shade. Press Enter at the first-curve prompt when finished. Preselect an existing shade result before running `vShade` to continue that shade.

Options:

- `Offset`: sets the bisector distance from the tangent-defined corner to the center of the perpendicular line. Enter a non-negative number directly at either curve prompt to change it. Zero places that line directly at the corner and omits the zero-length bisector.
- `Chamfer`: sets the total perpendicular cap width, centered on the bisector endpoint. Zero omits the perpendicular line. Existing saved `Length` values are read as `Chamfer` until changed.
- `Reinforcement`: sets the radius of an arc centered at each perpendicular midpoint and ending on its two incident perimeter connections. Defaults to `6`; zero disables these arcs. An arc is omitted until both connections exist or when the radius lies beyond either one.
- `CreateReinf`: `No` by default; `Yes` creates a detached closed reinforcement part for each valid corner arc. Its outline follows the current finished boundary, including custom edited sides, without cut allowance, and uses `CutLayer`. Copies its corner label, groups each part separately, and moves it outward until its minimum clearance from the outer cut outline is 2 inches converted to document units. With no cut outline, clearance is measured from the finished boundary. Changing this option updates only detached reinforcement parts; it does not rebuild the shade or replace custom sides. Detached parts are excluded from shade splitting.
- `Label`: sets the next reinforcement label; Enter at its text prompt disables labels. A nonempty numeric or alphabetic suffix advances after each placed corner (for example, `1` to `2` or `A` to `B`). The next value is stored in the document and starts at `1` in each new document. Labels are 0.5 model units high, centered between a reinforcement arc and its perpendicular line, with the perpendicular line as the text's bottom direction. Numeric labels that could read as a different number upside-down receive a trailing orientation dot (for example, `6.` and `9.`). A corner without a valid reinforcement arc has no visible label.
- `Connect`: when `Yes`, rebuilds non-crossing perimeter connections among all placed corners, regardless of selection order. Ends belonging to the same source curve are matched first; remaining free ends are paired by proximity. With three or more corners, it closes the loop when valid connections exist, without requiring the first and last picks to share a source curve.
- `Join`: when `Yes`, joins connected perpendicular caps and perimeter lines into curves; `No` keeps each segment separate. Bisectors and reinforcement arcs remain separate.
- `FinishedLayer`: chooses the layer for perpendicular, connection, reinforcement-arc, label and joined-boundary geometry; `*Current*`, `.` or `*` follows Rhino's current layer. Shown immediately before `CutLayer`. Previous `Layer` settings are read automatically. Changing the current layer in Rhino's Layers panel also moves the finished shade output already placed during the active command. Bisectors always use `Reference`; cut outlines and detached reinforcement outlines keep their separate `CutLayer`.
- `Scallop`: when `Yes`, replaces each straight perimeter connector with an arc bulging toward the shade interior; `No` keeps straight connectors.
- `ScallopSize`: sets each arc's midpoint bulge distance. Enter an absolute model-unit value such as `4` or a percentage of that connector's endpoint span such as `5%` (the default). It must be positive; a size that makes scallops cross each other or a chamfer cap is rejected without replacing the existing result.
- `CutOffset`: offsets each closed shade perimeter outward by this non-negative model-unit distance. Zero (the default) omits the cut outline; open intermediate perimeters do not produce one.
- `CutLayer`: sets the layer for the outward cut outline. Defaults to `CUT1`, which is created only when a cut outline is produced and the layer does not exist.
- `ScallopTune`: set `ScallopSize` in the tune prompt, or type a value such as `12%` directly, then click perimeter sides to apply that size individually. `0` makes a clicked side a straight line; a positive model-unit value or percentage makes it an arc. Press Enter to return to corner selection. Individual choices are retained when the shade is continued later, even when `Join` combines the connectors into one boundary object.
- `OffsetTune`: set `Offset` in the tune prompt, then click a corner's bisector or perpendicular cap to move only that corner to the specified offset. The connected boundary, reinforcement, and cut outline update with it. Press Enter to return to corner selection.
- `Split`: appears once a closed cut or shade boundary exists. It proposes a low-material-length rotated layout from that boundary. Inside `Split`, `SplitWidth` sets the material width (default `63` model units) in both the cut and placement stages; each proposed part fits within `SplitWidth - 0.5`, and adjacent parts overlap by `0.5` total. Drag an orange cut: the endpoint nearest the grab follows the cursor along the shade boundary, while the opposite endpoint stays fixed. Rhino's active object snaps remain available; parallel cuts are added or removed as needed. `Auto` recalculates the proposal. Press Enter to switch to placing the ghosted parts in faded destination colors; click a placement point or press Enter for the displayed location. Accepting the placement finishes `vShade`; canceling Split resumes the current curve-selection stage. The original shade stays; each accepted split cut becomes one `Reference`-layer line grouped with that shade. Each laid-out part is a separate group whose outside perimeter is the `CutLayer` cut line, assembled from exact segments of the source cut curve. Each common seam has matching upright digits on both parts, fitted inside the 0.5-unit band between the cut edge and the `PLOT`-layer overlap mark. After acceptance, one native undo removes the split copies and source split cuts, leaving the original shade; the next undo reverses the shade creation or edits from this run. Redo restores them in the same order.

The clicked curve ends and their tangent extensions define the corner in the active CPlane. The command creates one shade from all placed corners. Ctrl+Z and Ctrl+Y step through placed corners and tune edits while the command remains active; neither appears as a command option. Connections that would cross existing perimeter lines are omitted. Main shade output except bisectors belongs to one shade group; detached reinforcement parts have their own groups. When the command finishes, the result includes the [vFitBox](#vfitbox-flow) size of the outer cut curve, or the closed shade perimeter when there is no cut curve. An open boundary has no outside fit-box size. Created objects carry `vShade.*` identification metadata; detached parts are named `ShadeReinforcementPart`. If both Offset and Chamfer are zero, no geometry is created.

### vSmooth flow

1. Select the target curve. Preselected connected curves seed the target and neighbour preview immediately; each remains individually toggleable.
1. Connected candidate curves are pickable. Click one to select it as the neighbour for that end — the start-end neighbour highlights **orange**, the end-end neighbour highlights **green**.
1. Click a selected neighbour again to deselect it. Click the target curve to clear all neighbour selections and start over.
Options:

    - `StrengthStart` / `StrengthEnd`: independent handle-length scale per end. 0 = near-degenerate handle (very tight turn at the endpoint), 1 = rotate the existing handle to the G1 direction (default), >1 = lengthen the handle for a more extended blend.
    - `Copy`: when `Yes`, a new curve is created and the original is kept; when `No` (default) the original is replaced in-place.
    - `Join`: when `Yes`, the smoothed curve and its connected neighbours are joined into a single polycurve; the separate originals are deleted.
    - `SmoothAll`: when `No` (default), only the target curve changes. When `Yes`, the tangent correction is shared between the target and each selected neighbour so the transition is spread across both curves.
    - Direct input accepts `start`, `start,end`, or `,end` to change only `StrengthStart`, both strengths, or only `StrengthEnd` respectively.

1. Press Enter to commit. If the input curve belongs to a group the output curve inherits that group.

### vShow flow

1. Run `vShow` while another command is active or from the normal command prompt.
1. Choose one of up to twenty most-recent active set options, enter any set name directly, or press Enter to show every named hidden set while leaving ordinary unnamed hidden objects untouched. Named sets created by Rhino's [Hide](https://docs.mcneel.com/rhino/8/help/en-us/commands/hide.htm) command are polled into the same persistent list.
1. Matching active set members are shown directly, their completed set membership is cleared, and the interrupted command remains active with its prior selection restored.
1. The [vIsolate](#visolate-flow) toolbar passes A-E directly as set-name input when a named isolate button is right-clicked.

### vSplit flow

1. Select curves to split.
1. Click near selected curves to add real warm-yellow circular point-object split markers; point picking is constrained to the chosen curves.
1. Existing split markers are snap points; hover one to preview it as a cool-blue X, then click to remove it. Both marker states use a black halo and the configured pink outline so they remain visible over similarly colored curves.
1. Press Enter to apply splitting and replace the original curves with split pieces.
1. When splitting would break history, affected objects are shaded orange with a dark magenta outline while a native-style `Rhino N History Warning` with `OK/Cancel` appears before any source curves are replaced. `Cancel` leaves the original geometry unchanged and restores temporary command state. The warning follows Rhino's shared setting and includes affected history records on source curves and dependent children. Other vTools commands and operations delegated to native Rhino commands use Rhino's built-in history warning handling.
Options:

    - `Points`: choose `Default`, `CP`, `EditPoints`, or `Hidden` while choosing split points. `Default` leaves the original point visibility untouched on start and restores each selected curve's original hidden/CP/edit-point state when switched back.

### vSplitAtCorners flow

1. Select curves to split.
1. Auto-detected corners appear as orange dots; click any to toggle it off (gray = excluded).
1. Click anywhere along a selected curve to add a manual split point (cyan X); click an existing cyan X to remove it.
1. Press Enter to apply splitting.
Options:

    - `Angle`: minimum detected corner angle in degrees.
    - `MinLength`: minimum resulting segment length to keep.
    - `ClearManual`: remove all manually added split points.
    - `ClearAll`: restore all removed auto-corners and remove all manual points.

### vTangent flow

1. Pick **S1** — click near the end of the subject curve you want aligned to D1.
1. Pick **D1** — the required driver curve; the tangent at the click point is used.
1. Pick **S2** — click the other end of the same subject curve (for a second alignment).
1. Pick **D2** — an optional second driver curve; press Enter to skip.

Behavior:

- With D1 only: the subject curve is translated and rotated rigidly so the S1-end tangent matches the D1 tangent at the pick point.
- With D1 and D2: an additional twist about the D1 tangent axis is applied to minimize the angular error between the S2-end tangent and D2 (or its reverse, whichever needs less rotation).
- The subject curve shape is not changed — only its position and orientation.

### vTextAligned flow

1. Click a curve to lock orientation base, or click existing text to edit/reposition it.
1. Pick placement point near locked curve.
Options:

    - `Text`: sets content for newly created text and active text edits.
    - `Height`: text height.
    - `Offset`: signed side offset from the curve to the true text bounds, including rotated and styled annotation bounds.
    - `Rotate`: rotates text orientation by 90 degrees each use.
    - `BothSides`: previews and creates a mirrored text pair with both visible text bounds centered at the same curve position and offset equally from the curve.
    - Newly created text inherits every group membership from the locked curve; local redo preserves those groups.

### vTextFlip flow

1. Select ordinary text, dimensions, leaders, or other annotation objects.
Options:

- `Flip`: mirrors selected text through its object-local frame. Dimension lines and leader arrows remain fixed, including custom text positions and leader-side alignment.
- `Rotate`: rotates selected text by 90 degrees without moving dimension or leader lines.
- `Clear`: clears the editable annotation selection.

### vTitle flow

1. Move the cursor — existing vTitle objects highlight when the cursor enters their bounding box.
1. **Click existing title** → enter edit mode (loads its text, size, and settings; the object group is highlighted).
1. **Click empty space** → place a new title at the cursor position.
Options:

    - `Text`: title string.
    - `Size`: final model-space text height; document annotation scaling does not change it.
    - `Padding`: percentage of text height added as padding on each side of the bounding box. Editing the title text object's `vTitlePadding` attribute also resizes its existing frame; use a finite non-negative number (for example, `50` for 50%). Editing an existing title loads its own stored padding.
    - `Box`: `Yes/No` — draw a padded bounding rectangle around the text.
    - `Layer`: opens the shared searchable layer selector. Use `*Current*` to follow the current layer; `-vTitle` accepts a layer name or full path directly. Default is `Reference`.

1. Press Enter to confirm, Esc to cancel.

Notes:
- Editing an existing title replaces it in place.
- If the text annotation is later changed externally (e.g. via `Properties`), the bounding box resizes automatically with the replacement.
- All settings persist to `vTools.config.json` under `vTitle`.

### vToggleAxes flow

1. The command toggles visible viewport axes: construction-plane grid axes and the display-mode Z axis are shown or hidden together.

### vToggleControlPoints flow

1. Select objects or selected points.
1. The command is transparent, so it can be run while another Rhino command is active.
1. If off-curve control points are visible, the command switches the selection to edit points on the curve.
1. If edit points are visible or selected, the command switches the selection to off-curve control points where supported.
1. If no points are visible on the selected curves, the command shows edit points first.
1. Run with nothing selected to turn points off.

### vTogglePerpGumball flow

1. The command toggles monitor state (`ON`/`OFF`).
2. The monitor restores its last state when the plug-in loads.
1. While `ON`, select one or more grips in a supported viewport.
1. The command auto-orients gumball so it stays perpendicular and view-stable without changing the viewport CPlane. Multiple curve endpoints use their shared perpendicular direction and keep the gumball at the selection center.
1. `Perspective` viewports keep default gumball, including when switched to `Parallel` projection.
1. When turning `OFF`, gumball orientation is reset to Rhino default.

Notes:

- Background gumball updates preserve the help shown for the current command, including other toggle commands.

### vToggleView flow

1. The plain command switches immediately. From the `Perspective` tab, it enters CPlane plan first unless the viewport is already in parallel plan, then returns to perspective. After a `Projection` change, the next plain toggle always enters plan first. From another tab, it activates the `Perspective` tab and shows its CPlane in plan.
1. To choose a mode explicitly, use the hyphenated form, for example `_-vToggleView _Plan`, `_-vToggleView _Perspective`, `_-vToggleView _Projection`, or `_-vToggleView _Top`. The choice applies immediately; Enter uses `Toggle`. To create missing tabs, set `NewTab` before choosing a mode, for example `_-vToggleView _NewTab=_Yes _Right`.

Options:

- `Toggle`: default behavior, without saving a mode preference. An oblique parallel view enters plan rather than perspective; after a `Projection` change, plan also takes priority on the next toggle. If no tab named `Perspective` exists and `NewTab=No`, the command uses the first perspective-projected model view, then the current view as a fallback.
- `Projection`: toggles only the active viewport between parallel and perspective, without switching or creating tabs, reorienting the camera, or changing the CPlane. Returning to perspective restores that viewport's saved perspective lens, avoiding FOV distortion; 50 mm is used only when no valid perspective lens has been saved. Use `_-vToggleView _Projection` in place of the projection-toggle macro for native [ViewportProperties](https://docs.mcneel.com/rhino/8/help/en-us/commands/viewportproperties.htm).
- `Plan`: activates the Perspective view using the same tab-creation/fallback rules and sets parallel projection looking down its current [CPlane](https://docs.mcneel.com/rhino/8/help/en-us/commands/cplane.htm).
- `Perspective`: activates an existing `Perspective` tab, creates one when `NewTab=Yes`, or uses the current tab if none exists and `NewTab=No`. It forces perspective projection. A switch from parallel restores that viewport's saved perspective lens, or uses 50 mm if none is saved; an existing perspective camera keeps its lens.
- `Top`, `Bottom`, `Front`, `Back`, `Left`, `Right`: activate an existing model-view tab with that name, preserving its camera, zoom, and CPlane. If the tab does not exist, `NewTab=Yes` creates a docked tab with the corresponding standard World view; `NewTab=No` sets that view and CPlane in the current tab without creating or renaming a tab.
- `NewTab=No/Yes`: saved preference, default `No`. `Yes` creates a missing named model-view tab instead of changing another view; existing matching tabs are always reused. A new Perspective tab inherits the active view's CPlane. `Projection` ignores this setting.

Notes:

- Tab names are matched without case sensitivity and ignore surrounding spaces.
- Perspective lenses are remembered separately for each viewport in the open document, including when switching to plan. Returning to perspective preserves navigation performed in the parallel view instead of restoring an old camera position.
- The command is transparent and does not modify geometry or selection. While another command is running, call `_vToggleView` for the immediate toggle, or `_-vToggleView _Right` for a forced choice. Do not prefix these transparent calls with `!`, which cancels the active command.

### vTrim flow

1. Choose a workflow: select cutting curves for [Regular mode](#vtrim-regular-mode), or press Enter for [AutoClosest mode](#vtrim-autoclosest-mode).

#### Regular mode

1. Select one or more cutting curves and press Enter.
1. `vTrim` passes those cutters to Rhino's native [Trim](https://docs.mcneel.com/rhino/8/help/en-us/commands/trim.htm) command, which handles target selection, preview, and trimming normally.

#### AutoClosest mode

1. Press Enter without selecting cutting curves.
1. Hover a target curve. `vTrim` finds the nearest valid contact on each side along the curve and previews the exact portion that will be removed. Between two cutters, only the intervening section is removed; both outer sections remain.
1. Click to apply the displayed trim. Hold Shift before clicking to preview and apply an extension from the hovered end instead.
1. Continue hovering and clicking targets, or press Enter when finished.

In parallel plan views, AutoClosest also uses apparent intersections in the view direction, including curves at different depths. Angled and perspective views use 3D contacts only. Trimmed and extended curves keep their original 3D geometry rather than being flattened.

#### AutoClosest options

    - `Extend`: `Line` or `Smooth` extension style.
    - `Join`: `Yes` or `No` for joining kept trim pieces.

#### Preview colors

    - Trim removal preview: red.
    - Extend addition preview: green.

### vTrimOff flow

1. Select at least 2 curves (open or closed, crossing or adjacent).
1. The command finds all enclosed regions formed by the selected curves and computes their combined outer boundary.
1. The original selected curves are replaced by the trimmed boundary curves; protruding ends that extend outside the enclosed region are discarded.
1. Uses the active CPlane for planar region detection.

### vUnrollSrf flow

1. Select surfaces, polysurfaces, or extrusions to unroll.
1. Select curves, points, or dots that should follow their nearest surface, or press Enter for none.
1. Choose the output start point. When replacing already processed parts, press Enter to preserve each part's prior position and orientation.
1. Flat parts receive matching labels and shared-edge `M###` dots for use with [vMatch](#vmatch-flow). The default top-level output layers are `Unrolled_surface`, `Unrolled_label`, and `Unrolled_dot`, ordered immediately below `Surface`; missing layers are created only when their corresponding output is committed. Polysurface markers retain their source-edge face association and exact position on the corresponding flat edge. Text is fitted to the trimmed flat part and the same height is applied to its original-surface label. Planar single-face parts are mapped exactly. Non-rational ruled faces with a collapsed apex use a tolerance-checked conical development that preserves lengths and UV coordinates; other parts and their following geometry use the native command selected by UV. Failures are marked instead of silently switching flattening algorithms.
1. Rerunning an already processed part preserves its part number and recoverable edge-dot identities, reuses its original label/group, and replaces its previous flat-group members after the new unroll succeeds. Existing flat labels recover the position of parts whose group contains only labels or dots. If several parts share a combined surface, their old faces are assigned back to their existing groups when every face is identified by an interior part label; the new geometry may have a different shape or face count. Ambiguous old joined geometry is retained with a warning, but every selected source is still unrolled into new output. Other existing parts update normally rather than creating another layout.

Options:

- `UV`: `Yes` by default delegates to native [UnrollSrfUV](https://docs.mcneel.com/rhino/8/help/en-us/commands/unrollsrf.htm#unrollsrfuv); `No` delegates to native [UnrollSrf](https://docs.mcneel.com/rhino/8/help/en-us/commands/unrollsrf.htm). Available during selection, following-geometry and placement prompts and remembered between runs.
- `QuietNative`: experimental command-history cleanup, `Yes` by default. After each native unroll call, removes recognized English area-calculation progress, within-tolerance notices, routine surface counts and redundant native numerical area warnings. Earlier history, other warnings, errors, vTools' numbered final-area and edge discrepancies, and unknown/localized messages are preserved. `No` retains native output, with numerical area warnings qualified by part number. Available at all option prompts and remembered between runs.
- `Labels`: creates `Text`, numbered `Dots`, or `None` for part identification.
- `RotateFlatParts`: rotates flat parts to roughly match the original source orientation as projected onto World XY, using both source label-frame directions when possible and the remaining direction for edge-on sources. Uses rotation only, not mirroring; labels, following geometry and mate dots move with the part. Otherwise keeps Rhino's unroller orientation. Reruns that preserve an existing part's placement retain its edited orientation.
- `EdgeDots`: creates matching shared-edge `M###` dots for [vMatch](#vmatch-flow).
- `Explode`: outputs separate flat faces instead of keeping each unrolled Brep joined.
- `SplitFaces`: unrolls polysurface faces separately while retaining their source-face associations.
- `KeepPropSurface`: preserves source surface object properties; otherwise uses the selected surface output layer.
- `KeepPropFollowing`: preserves properties of following curves, points, and dots; otherwise uses the corresponding output layers.
- `Spacing`: gap between automatically laid-out flat parts in model units.
- `XExtents`: maximum layout row width in model units; `0` disables row wrapping.
- `SurfaceLayer`: layer for committed flat surfaces, selected with the shared searchable layer picker.
- `LabelLayer`: layer for committed labels, selected with the shared searchable layer picker.
- `DotLayer`: layer for committed matching dots, selected with the shared searchable layer picker.

Individual edge-length discrepancies exceeding the document absolute tolerance are reported by part and source edge (`E1` is the first source topology edge), with a signed length in document units and a percentage of that source edge's length. Hidden transferred samples locate each edge on the actual flat geometry, including consecutive segments of a subdivided boundary; total perimeter and nearest-length guesses are not used. Edges that cannot be mapped are explicitly listed as unverified. Full-precision measurements are recorded in the diagnostic log.

The source currently being processed is highlighted with lightweight outlined wires, and completed flat parts appear progressively in their final layout positions. Output creation keeps redraw suspended; previews update only the active viewport without automatically redrawing other views or the Layers panel. Refreshes combine newly finished parts with the latest source highlight. The first source is shown immediately; later updates adapt to rendering cost, targeting no more than about ten percent display overhead rather than forcing a refresh before every native call. The final result always refreshes all views. The in-place command prompt uses one stable format for part, completed count, percentage and failures without adding progress lines to history. Area discrepancies exceeding estimated measurement error are reported for each identified part, using the final flat geometry, with the signed difference in squared document units and percent. Rhino's own numerical area warnings are suppressed with QuietNative=Yes; with QuietNative=No they retain a part number and `native unroll` prefix, distinguishing them from the independent final-geometry check. Native output with an unexpected face count is rejected rather than replacing existing parts. Native progress messages bypass command-echo settings; QuietNative cleans recognized routine messages afterward by rewriting command history. Routine messages can briefly appear during a long native call before cleanup runs. Cleanup is skipped if the history was replaced/truncated, changes during filtering, or exceeds one million characters. Raw captured output remains available to diagnostics and other active output captures. Diagnostics record individual enable/draw/pump times, total live-display time and history-replay time for performance comparison.

Options persist to `vTools.config.json` under the `vUnrollSrf` section. Layer choices are saved immediately when changed. The former vUnrollSrfUV command is consolidated into this command; use UV=Yes for that mode.

### vUzip flow

1. Optionally preselect up to three U-shape arm curves before running.
1. Select the three U-shape curves (left arm, right arm, bottom).

Options:

    - `Left` / `Right` / `Bottom`: inward offset distances for each arm.  Accepts decimal, fraction (`2 3/8`, `2-3/8`), or `z`/`zipper` keyword; offset distances may be `0`.
    - `Radius`: fillet radius at the two inside corners.
    - `Glass`: `Yes/No` — compute and show glass offset curves.
    - `Vis`: `Yes/No` — compute and show vis offset curves.
    - `Parts`: `Yes/No` — enable the parts-output pipeline.
    - `Label` *(when Parts=Yes)*: text label for generated part groups.
    - `Tail` *(when Parts=Yes)*: tail length for end curves.
    - `Options`: opens the full options dialog with shared hierarchical layer dropdowns and color swatches, plus additional offset values.

1. A cyan preview of the computed center curve is shown live.
1. When `Parts=No`: press Enter to accept, or click a boundary curve to trim/extend the ends.
1. When `Parts=Yes`: select boundary curves to trim/extend end caps; press Enter to accept.
1. The center curve (and optionally glass, vis, and parts output) is committed to the document.

### vUzipCenter flow

1. Select three curves that form a U shape (left arm, right arm, bottom). You may preselect up to three curves before running the command; a fourth preselected curve is used as the initial boundary.
Options:

    - `Left`: offset distance for the left arm.
    - `Right`: offset distance for the right arm.
    - `Bottom`: offset distance for the bottom curve.
    - `Radius`: fillet radius at the two inside corners.

   Distances accept fractional inch input (`2 3/8`, `2-3/8`, `3/8`, plain decimal) and the shorthand `z`/`zipper` (returns the left-arm default). Offset distances may be `0`; radius must be greater than `0`.

1. A cyan preview curve is displayed showing the computed result.
1. While previewing, adjust the same options to recompute live.
1. Click any existing curve to set or replace the boundary: the result is trimmed or extended to meet it.
1. Press Enter to accept and add the result curve to the document.

The `Options` command-line option opens the shared hierarchical layer dropdowns with color swatches for center, glass, and vis output layers. Settings persist under the `vUzipCenter` section.

### vUzipParts flow

1. Select the center curve.
Options:

- `Label`: text used when naming generated output. It remains available while placing and rebuilds all parts when changed.
- `Tail`: tail distance used when building end curves. It remains available while placing and rebuilds all parts when changed.

1. Pick placement point for generated groups, or cancel placement to remove generated objects.

## Configuration

Commands read and write vTools.config.json next to the plug-in assembly. Sections are created per command as options are used.
Builds merge newly introduced default keys into that runtime file without replacing existing values or custom sections.

Example:

```json
{
  "vUzipParts": {
    "label": "",
    "tail": 0.75,
    "layers": {
      "reference": { "name": "Reference", "color": "#FFFFFF" },
      "plot": { "name": "PLOT", "color": "#0F8A8A" },
      "cut": { "name": "CUT1", "color": "#CC3333" }
    },
    "parts": []
  }
}
```

## Startup output

When the plug-in loads, Rhino's command history shows:

```
vTools v26.7.3.HMM loaded — N commands: vBiminiParts, vChamfer, ...
```

The plug-in version, Rhino version, and DLL path are written to `vTools.log` beside the loaded DLL.

## Logging

- `vTools.log` beside the loaded DLL — cleared on every Rhino startup. First lines show the Rhino and plug-in versions plus the command list. All commands write diagnostics here via `Log.Write(tag, message)`.
- Temporary Isolate investigation: `vTools.commands.jsonl` beside the loaded DLL retains separate, timestamped Rhino sessions, native and plug-in command order, command-line history, selection/visibility changes, input state, and undo-record state. It observes without changing geometry, selection, or undo.
- The trace checks whether native `Isolate` hides objects it should retain. Five completed sessions with verified Isolate tests and no failures qualify for review and removal of the diagnostics; sessions without tests do not advance the count, and failures or incomplete traces reset it. Monitoring does not disable itself. Set `IsolationDiagnostics.enabled` to `false` in the runtime configuration and restart Rhino to disable it manually.

## Versioning

This project uses CalVer-style metadata (`yy.m.d.hmm`) in project and assembly versions, with no seconds and non-padded month/day/hour. The timestamp is generated from the newest plugin source file (`.cs`) rather than the compile time.

## License

This project is released under the MIT License. See LICENSE.
