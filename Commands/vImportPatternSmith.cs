using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Display;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.UI;
using Eto.Forms;
using Material = vTools.MaterialLibrary.MaterialPreset;
using PastePlacement = vTools.Commands.vPasteFromPatternSmith.PastePlacement;

namespace vTools.Commands;

/// <summary>Reads PatternSmith project geometry without opening or changing its material library.</summary>
public sealed class vImportPatternSmith : vToolsCommand
{
  // Import defaults and limits.
  internal const string Extension="project"; // Bare filename extension registered with Rhino.
  internal const string FileTypeName="PatternSmith Project (*.project)"; // Label displayed by Rhino Open and Import.
  private const int MaximumArchiveEntries=4096; // Maximum ZIP members; only project.xml is read.
  private const long MaximumProjectXmlBytes=128*1024*1024; // Maximum uncompressed bytes/characters of untrusted project XML.
  private const int MaximumImportObjects=250000; // Maximum expanded geometry objects, including copies, per import.
  private const int NativeGeometryTimeoutMilliseconds=30000; // Positive upper bound for the isolated spline and effect decoder.
  private const int MaximumSeamPrimitives=8; // Allow for the offset edge and short join or cap pieces at both ends.
  private const int MaximumNotchPrimitives=8; // Maximum lines reserved for one native notch in the expanded import budget.
  private const string DefaultNotchExecutionStyle="Discrete"; // Legacy notches without an execution style are separate marks, not part of the cut edge.
  private const double NativeSeamJoinToleranceInches=.011; // PatternSmith's default connection tolerance for native allowance chains.
  private const int MaximumBackgroundImageBytes=32*1024*1024; // Maximum compressed image size in bytes, per image.
  private const long MaximumClipboardBackgroundBytes=128L*1024*1024; // Maximum total compressed background data extracted per paste, in bytes.
  private const long MaximumBackgroundPixels=64L*1024*1024; // Maximum decoded pixels per background image.
  private const double FullBackgroundOpacity=100; // PatternSmith opacity percentage for a fully visible image.
  private const string BackgroundLayerName="PatternSmith Background"; // Separate layer so photos do not become sewing-part geometry.
  private const string BackgroundCacheFolder="PatternSmithPhotos"; // Per-user vTools texture assets, keyed by image content for reuse.
  private const int BackgroundDrawOrder=-1; // Rhino draw order below ordinary objects.
  private const int BackgroundWireDensity=-1; // Hide the picture surface's isocurves.
  private const double MinimumLengthInches=1e-10; // Positive native-inch cutoff for degenerate geometry.
  private const double UnspecifiedTableLengthInches=315; // Used only when a roll instance has no saved length or occupied extent.
  private const double UnspecifiedTableWidthInches=60; // Used only when a roll template has no saved width or occupied extent.
  private const double AutomaticTableClearanceInches=12; // Clearance between imported tables when auto spacing is selected in vNest.
  [CommandDefault("vImportPatternSmith","vTools.Commands.vImportPatternSmith.TableInfoRecognitionToleranceInches","Positive distance in inches for recognizing rectangular native table-info labels.")]
  private const double TableInfoRecognitionToleranceInches=.001; // Positive physical tolerance for matching a native table to a rectangular info label.
  private static readonly double ConfiguredTableInfoRecognitionToleranceInches=CommandDefaults.Get("vImportPatternSmith","vTools.Commands.vImportPatternSmith.TableInfoRecognitionToleranceInches",TableInfoRecognitionToleranceInches); // Applied while reading native-inch geometry.
  private const double DefaultTextHeightInches=1; // Positive height for legacy text without a saved height.
  [CommandDefault("vImportPatternSmith","vTools.Commands.vImportPatternSmith.TableInfoTextPadding","Minimum inset inside imported table-info frames, as a nonnegative fraction of the tallest label.")]
  private const double TableInfoTextPadding=0.10; // Nonnegative fraction of the tallest label; does not change its font or size.
  private static readonly double ConfiguredTableInfoTextPadding=CommandDefaults.Get("vImportPatternSmith","vTools.Commands.vImportPatternSmith.TableInfoTextPadding",TableInfoTextPadding); // Applied only to imported table-info frames.
  [CommandDefault("vImportPatternSmith","vTools.Commands.vImportPatternSmith.TableInfoClearanceInches","Minimum gap in inches between an imported table-info frame and neighboring workspace objects. Zero only prevents overlap.")]
  private const double TableInfoClearanceInches=1; // Nonnegative physical inches; zero only prevents overlap.
  private static readonly double ConfiguredTableInfoClearanceInches=CommandDefaults.Get("vImportPatternSmith","vTools.Commands.vImportPatternSmith.TableInfoClearanceInches",TableInfoClearanceInches); // Applied after measuring the resized frame.
  private const int DialogWidth=760,DialogHeight=490,DialogMinimumWidth=690,DialogMinimumHeight=350; // Initial and minimum dialog dimensions in logical pixels.
  private const int DialogPadding=10; // Outer inset and inter-control spacing in pixels.
  private const int MappingColumnGap=12,MappingRowGap=6; // Logical pixels separating the mapping columns and rows.
  private const int SourceColumnWidth=220,ToolColumnWidth=133,DestinationMinimumWidth=250; // Logical pixels keeping source and destination aligned.
  private const int SwatchSize=14; // Square tool and layer color size in logical pixels.
  private const int VisibleNameLimit=36; // Maximum characters shown in a source row; full names remain on hover.
  private const int VisibleFilenameLimit=65; // Maximum characters shown in the file header; full filename remains on hover.
  private const UnitSystem NativeModelUnits=UnitSystem.Inches; // Native project coordinates, including text heights, are inches regardless of PatternSmith display preferences.
  [CommandDefault("vImportPatternSmith","vTools.Commands.vImportPatternSmith.DefaultContents","Parts only: Import workspace patterns without creating tables. Nests only: Import native nests and their copies without workspace patterns. Unnested parts + nests: Import native nests and only workspace patterns not used in a nest. All: Import the workspace patterns plus native nests and copies.",OptionKeys=ContentsKey)]
  private const ImportContents DefaultContents=ImportContents.UnnestedPartsAndNests; // Factory contents: PartsOnly, NestsOnly, UnnestedPartsAndNests, or All.
  private static readonly ImportContents ConfiguredDefaultContents=CommandDefaults.Get("vImportPatternSmith","vTools.Commands.vImportPatternSmith.DefaultContents",DefaultContents); // Fallback before the preferred contents are saved.
  [CommandChoicesAttribute("vImportPatternSmith","contents",false,"PartsOnly","NestsOnly","UnnestedPartsAndNests","All")]
  private const string ContentsKey="contents"; // Saved import contents enum name.
  private static readonly string[] ContentsLabels=["Parts only","Nests only","Unnested parts + nests","All"]; // Import dialog labels in ImportContents order.
  [CommandDefault("vImportPatternSmith","vTools.Commands.vImportPatternSmith.DefaultIncludeEmptyNests","On: Include native nests that contain no geometry. Off: Skip empty nests. Only applies when importing nests.",OptionKeys=IncludeEmptyNestsKey)]
  private const bool DefaultIncludeEmptyNests=false; // true imports unused rolls, false skips them.
  private static readonly bool ConfiguredDefaultIncludeEmptyNests=CommandDefaults.Get("vImportPatternSmith","vTools.Commands.vImportPatternSmith.DefaultIncludeEmptyNests",DefaultIncludeEmptyNests); // Fallback before the preference is saved.
  private const string IncludeEmptyNestsKey="includeEmptyNests"; // Saved boolean preference for importing empty nests.
  private const string ProjectMaterialChoice="Keep project material"; // No changes to the shared named material library.
  private const string UnassignedChoice="No material"; // Source part remains unassigned; table retains its physical roll settings.
  private const string DefaultCutLayer="CUT1",DefaultMarkLayer="PLOT",DefaultReferenceLayer="Reference"; // Default target names for cut, mark and reference tools.
  private static readonly string[] PastePlacementLabels=["At cursor","Pick point"]; // Order matches the clipboard placement modes.

  private static readonly XNamespace ProjectNs="http://autometrix.com/xml";
  private static readonly XNamespace References="http://schemas.microsoft.com/2003/10/Serialization/";
  private static readonly XNamespace Types="http://www.w3.org/2001/XMLSchema-instance";
  private static readonly CultureInfo Invariant=CultureInfo.InvariantCulture;
  private static readonly HashSet<PendingOpen> PendingOpens=[];
  private const RegexOptions TableInfoMatching=RegexOptions.IgnoreCase|RegexOptions.CultureInvariant|RegexOptions.NonBacktracking; // Stable, linear matching for legacy table captions.
  private static readonly Regex LegacyTableName=new(@"^table\s*[0-9]+$",TableInfoMatching);
  private static readonly Regex LegacyPartCount=new(@"^[0-9]+\s+parts?$",TableInfoMatching);
  private static readonly Regex LegacyYardage=new(@"^[0-9]+(?:[.,][0-9]+)?\s+(?:yds?|yards?)$",TableInfoMatching);
  private static readonly Regex TableUsedSize=new(@"^([0-9]+(?:[.,][0-9]+)?)\s*(?:x|\u00d7)\s*([0-9]+(?:[.,][0-9]+)?)$",TableInfoMatching);
  private const string TableInfoSuffix=" information"; // Suffix used by vTools exports to identify a modern table-info pattern.

  public override string EnglishName=>nameof(vImportPatternSmith);
  protected override Result RunCommand(RhinoDoc doc,RunMode mode)
  {
    string path=string.Empty;
    if(mode==RunMode.Scripted)
    {
      var result=Rhino.Input.RhinoGet.GetString("PatternSmith project file",false,ref path);
      if(result!=Result.Success)return result;
    }
    else
    {
      using var picker=new Eto.Forms.OpenFileDialog{Title="Import PatternSmith project",Filters={new FileFilter("PatternSmith project","."+Extension)}};
      if(picker.ShowDialog(RhinoEtoApp.MainWindowForDocument(doc))!=DialogResult.Ok)return Result.Cancel;
      path=picker.FileName;
    }
    return Import(doc,path,mode!=RunMode.Scripted,false);
  }

  internal static Result ImportClipboard(RhinoDoc doc,bool interactive)
  {
    try
    {
      var cursor=CapturePasteCursor(doc);
      var payload=PatternSmithClipboard.Read();
      if(payload==null)
      {
        RhinoApp.WriteLine($"{nameof(vPasteFromPatternSmith)}: Copy workspace patterns or editor geometry from PatternSmith first.");
        return Result.Nothing;
      }
      using var project=ReadProjectXml(PatternSmithClipboard.ReadContainer(payload),clipboard:true);
      return ApplyProject(doc,"Clipboard",project,interactive,opening:false,clipboard:true,pasteCursor:cursor);
    }
    catch(OperationCanceledException){return Result.Cancel;}
    catch(Exception error)
    {
      Log.Write(nameof(vPasteFromPatternSmith),"clipboard paste failed: {0}",error);
      RhinoApp.WriteLine($"{nameof(vPasteFromPatternSmith)}: {error.Message}");
      return Result.Failure;
    }
  }

  internal static Result Import(RhinoDoc doc,string? path,bool interactive,bool opening)
  {
    try
    {
      if(string.IsNullOrWhiteSpace(path))return Result.Cancel;
      var project=ReadProject(path);
      bool queued=false;
      try
      {
        if(opening&&interactive)
        {
          PendingOpens.Add(new PendingOpen(doc,path,project));
          queued=true;
          return Result.Success;
        }
        return ApplyProject(doc,path,project,interactive,opening);
      }
      finally{if(!queued)project.Dispose();}
    }
    catch(OperationCanceledException){return Result.Cancel;}
    catch(Exception ex){return ReportImportError(ex);}
  }
  private static Result ReportImportError(Exception ex)
  {
    Log.Write(nameof(vImportPatternSmith),"read failed: {0}",ex);
    RhinoApp.WriteLine($"{nameof(vImportPatternSmith)}: {ex.Message}");
    return Result.Failure;
  }
  internal static void CancelPendingOpens()
  {
    foreach(var request in PendingOpens.ToArray())request.Dispose();
  }
  private sealed class PendingOpen : IDisposable
  {
    private readonly RhinoDoc _doc;
    private readonly string _path;
    private Project? _project;

    internal PendingOpen(RhinoDoc doc,string path,Project project)
    {
      _doc=doc;_path=path;_project=project;
      RhinoDoc.EndOpenDocument+=Opened;
      RhinoDoc.CloseDocument+=Closed;
    }
    private void Opened(object? sender,DocumentOpenEventArgs e)
    {
      if(e.DocumentSerialNumber!=_doc.RuntimeSerialNumber||e.Merge||e.Reference)return;
      RhinoDoc.EndOpenDocument-=Opened;
      RhinoApp.Idle+=AfterOpen;
    }
    private void AfterOpen(object? sender,EventArgs e)
    {
      if(!_doc.IsAvailable){Dispose();return;}
      if(RhinoApp.InCommand!=0||_doc.InCommand(false)!=0)return;
      RhinoApp.Idle-=AfterOpen;
      // vFileTypeTemplate runs its queued template application during the first idle pass.
      System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
        System.Windows.Threading.DispatcherPriority.ApplicationIdle,new Action(ImportAfterTemplate));
    }
    private void ImportAfterTemplate()
    {
      using var project=_project;
      _project=null;
      try
      {
        if(project==null||!_doc.IsAvailable)return;
        ApplyProject(_doc,_path,project,true,true);
      }
      catch(OperationCanceledException){}
      catch(Exception ex){ReportImportError(ex);}
      finally{Dispose();}
    }
    private void Closed(object? sender,DocumentEventArgs e)
    {if(e.DocumentSerialNumber==_doc.RuntimeSerialNumber)Dispose();}
    public void Dispose()
    {
      RhinoDoc.EndOpenDocument-=Opened;RhinoApp.Idle-=AfterOpen;RhinoDoc.CloseDocument-=Closed;
      _project?.Dispose();_project=null;PendingOpens.Remove(this);
    }
  }
  private static Result ApplyProject(RhinoDoc doc,string path,Project project,bool interactive,bool opening,bool clipboard=false,Point3d? pasteCursor=null)
  {
    if(opening)
    {
      var previous=doc.ModelUnitSystem;
      if(previous!=NativeModelUnits)
      {
        double factor=previous is UnitSystem.None or UnitSystem.CustomUnits?1:RhinoMath.UnitScale(previous,NativeModelUnits);
        double tolerance=doc.ModelAbsoluteTolerance*factor;
        doc.ModelUnitSystem=NativeModelUnits;
        if(double.IsFinite(tolerance)&&tolerance>0)doc.ModelAbsoluteTolerance=tolerance;
      }
      Log.Write(nameof(vImportPatternSmith),"opening {0}: template units={1}, project units={2}",Path.GetFileName(path),previous,doc.ModelUnitSystem);
    }
    var mapping=new ImportMappings(doc,project,clipboard);
    if(interactive)
    {
      using var dialog=new MappingDialog(doc,project,mapping,Path.GetFileName(path),clipboard);
      if(!dialog.ShowModal(RhinoEtoApp.MainWindowForDocument(doc)))return Result.Cancel;
    }
    if(!doc.IsAvailable)return Result.Cancel;
    var selection=project.SelectContents(mapping.Contents,mapping.EmptyTables);
    if(!selection.HasContent)
    {
      RhinoApp.WriteLine($"{nameof(vImportPatternSmith)}: No content matches the selected import mode.");
      return Result.Nothing;
    }
    ResolveNativeGeometry(project,selection);
    using var paste=clipboard?new ClipboardPlacement(doc,project,selection.Workspace,mapping):null;
    if(paste!=null)
    {
      if(mapping.Placement==PastePlacement.PickPoint&&!doc.IsHeadless)
      {
        var picked=paste.PickPoint(pasteCursor);
        if(picked!=Result.Success)return picked;
      }
      else if(pasteCursor is {IsValid:true} target)paste.PlaceAt(target);
    }
    var outcome=WriteProject(doc,project,mapping,selection,opening,paste);
    if(interactive)
    {
      if(clipboard)vPasteFromPatternSmith.SavePasteBackgroundImage(mapping.PasteBackground);
      else mapping.SavePreferences();
    }
    if(opening)FrameOpenedProject(doc);
    string command=clipboard?nameof(vPasteFromPatternSmith):nameof(vImportPatternSmith);
    RhinoApp.WriteLine(clipboard
      ?$"{command}: pasted {(project.EditorClipboard?CountText.Format(paste!.Count,"object"):CountText.Format(outcome.Parts,"pattern"))}."
      :$"{command}: imported {CountText.Format(outcome.Parts,"pattern")}, "+
        $"{CountText.Format(outcome.Tables,"nest table")} and {CountText.Format(outcome.Copies,"nested copy","nested copies")}.");
    foreach(var warning in project.Warnings.OrderBy(pair=>pair.Key))
      RhinoApp.WriteLine($"{command}: {warning.Value} {warning.Key}.");
    return Result.Success;
  }

  private static void FrameOpenedProject(RhinoDoc doc)
  {
    if(doc.IsHeadless)return;
    var views=doc.Views.GetViewList(Rhino.Display.ViewTypeFilter.Model);
    var view=views.FirstOrDefault(candidate=>string.Equals(candidate.MainViewport.Name,
      nameof(Rhino.Display.DefinedViewportProjection.Top),StringComparison.OrdinalIgnoreCase))??
      views.FirstOrDefault(candidate=>candidate.MainViewport.Id==doc.Views.ActiveView?.MainViewport.Id)??views.FirstOrDefault();
    if(view==null)return;
    doc.Views.ActiveView=view;
    var viewport=view.MainViewport;
    viewport.SetProjection(Rhino.Display.DefinedViewportProjection.Top,
      nameof(Rhino.Display.DefinedViewportProjection.Top),true);
    view.Maximized=true;
    viewport.ZoomExtents();
    doc.Views.Redraw();
  }

  private sealed record ToolLayer(string Key,string Name,string Process,int Slot)
  {
    internal Color Color=>Process switch{"Cut" when Slot==0=>Color.Red,"Cut"=>Color.Magenta,"Mark" or "DotMark"=>Color.DarkCyan,
      "Notch"=>Color.Orange,"Drill" or "Punch"=>Color.BlueViolet,"Crease"=>Color.SaddleBrown,_=>Color.Gray};
    internal string Tool=>Slot<0?Process:$"{Process} [{Slot+1}]";
  }
  private sealed record ProjectMaterial(string Id,string Name,double Width,double Gap);
  private sealed record Item(ToolLayer Layer,GeometryBase Geometry,string Kind,string Template)
  {
    internal SplineData? Spline {get;init;}
    internal CurveEffects? Effects {get;init;}
    internal bool BoundaryCandidate {get;init;}
    internal ImportedSeamSource[]? SeamSources {get;init;}
    internal ImportedNotch? Notch {get;init;}
    internal int NotchComponent {get;init;}
  }
  private sealed record SplineData(Point3d[] Points,bool Closed);
  private sealed record CurveEffects(XElement Request,ToolLayer[] Layers,int MaximumObjects);
  private sealed record ImportedSeamSource(Item Source,double SignedWidthInches,string Start,string End);
  private sealed record ImportedNotch(Item Source,Point3d SewAnchor,Point3d CutAnchor,Vector3d Direction,string Type,string Mode,double Depth,double Width,int Components,bool Inline);
  private sealed record PhotoSource(Guid Id,string ProjectFile,string ImageFile);
  private sealed record BackgroundPhoto(PhotoSource Source,Point3d Center,double Scale,double Angle,double Opacity);
  private sealed record Pattern(string Key,string Name,Point3d Location,double Angle,string Material,int Copies,bool Strict,
    bool FaceDown,bool Nestable,List<Item> Items)
  {
    internal NestWorkflow.PatternSmithPartState? State {get;init;}
    internal double? RotationStep {get;init;}
    internal bool MaterialEnforced {get;init;}
    internal int ExpandedItemCount {get;set;}
    internal BackgroundPhoto? Photo {get;init;}
  }
  private sealed record Copy(Pattern Pattern,Point3d Location,double Angle,string Flip);
  private sealed record Table(string Name,ProjectMaterial Material,double Length,bool FaceDown,Copy[] Copies)
  {
    internal Pattern? LabelPattern {get;init;}
    internal (double Length,double Width)? LabelUsedSize {get;init;} // Advisory inches, used only when native measurements are missing.
    internal bool HasCopies=>Copies.Any(copy=>copy.Pattern.Items.Count>0);
  }
  private enum ImportContents { PartsOnly, NestsOnly, UnnestedPartsAndNests, All }
  private sealed record ImportSelection(Pattern[] Workspace,Table[] Tables)
  {
    internal bool HasContent=>Workspace.Length>0||Tables.Length>0;
    internal IEnumerable<Copy> Copies=>Tables.SelectMany(table=>table.Copies).Where(copy=>copy.Pattern.Items.Count>0);
    internal IEnumerable<Pattern> GeometryPatterns=>Workspace.Concat(Copies.Select(copy=>copy.Pattern)).Distinct();
    internal ToolLayer[] UsedLayers=>LayersFor(GeometryPatterns);
  }

  private static ToolLayer[] LayersFor(IEnumerable<Pattern> patterns)=>patterns.SelectMany(part=>part.Items)
    .SelectMany(item=>item.Effects is {} effects?effects.Layers.Prepend(item.Layer):[item.Layer])
    .DistinctBy(layer=>layer.Key).OrderBy(layer=>layer.Name,StringComparer.OrdinalIgnoreCase).ToArray();

  private sealed class Project : IDisposable
  {
    internal bool EditorClipboard {get;init;}
    internal List<Pattern> Patterns {get;}=[];
    internal List<Table> Tables {get;}=[];
    internal Dictionary<string,ProjectMaterial> Materials {get;}=new(StringComparer.Ordinal);
    internal Dictionary<string,int> Warnings {get;}=new(StringComparer.Ordinal);
    internal ImportSelection SelectContents(ImportContents contents,bool includeEmpty)
    {
      var selectedTables=contents==ImportContents.PartsOnly?[]:Tables.Where(table=>includeEmpty||table.HasCopies).ToArray();
      if(contents==ImportContents.NestsOnly)return new([],selectedTables);
      var nested=contents==ImportContents.UnnestedPartsAndNests?
        selectedTables.SelectMany(table=>table.Copies).Select(copy=>copy.Pattern).ToHashSet():[];
      var workspace=Patterns.Where(pattern=>pattern.Items.Count>0&&!nested.Contains(pattern)).ToArray();
      var tableHeaders=selectedTables.Select(table=>table.LabelPattern).OfType<Pattern>().ToHashSet();
      if(tableHeaders.Count==0)return new(workspace,selectedTables);
      var names=Tables.Select(table=>TableMatchName(table.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
      double tolerance=Math.Max(MinimumLengthInches,ConfiguredTableInfoRecognitionToleranceInches);
      bool hasWorkspaceParts=workspace.Any(pattern=>!tableHeaders.Contains(pattern)&&
        !TryTableInfoFrame(pattern,Transform.Identity,tolerance,names,out _,out _));
      return new(hasWorkspaceParts?workspace:workspace.Where(pattern=>!tableHeaders.Contains(pattern)).ToArray(),selectedTables);
    }
    internal void Warn(string reason)=>Warnings[reason]=Warnings.GetValueOrDefault(reason)+1;
    public void Dispose(){foreach(var item in Patterns.SelectMany(pattern=>pattern.Items))item.Geometry.Dispose();}
  }

  private sealed class ProjectXml
  {
    private readonly Dictionary<string,XElement> _ids=new(StringComparer.Ordinal);
    internal ProjectXml(XElement root)
    {
      foreach(var node in root.DescendantsAndSelf())
        if(node.Attribute(References+"Id") is {} id&&!_ids.TryAdd(id.Value,node))
          throw new InvalidDataException("The project has duplicate object references.");
    }
    internal XElement? Resolve(XElement? element)
    {
      if(element?.Attribute(References+"Ref") is {} reference)
        return _ids.GetValueOrDefault(reference.Value)??throw new InvalidDataException("The project has a missing object reference.");
      return element;
    }
    internal XElement? Field(XElement? element,string name)=>Resolve(Child(element,name));
    internal string Text(XElement? element,string name,string fallback="")=>Field(element,name)?.Value??fallback;
    internal string Name(XElement? element,string fallback)
      =>Text(element,"Name").Trim() is {Length:>0} name?name:fallback;
    internal double Number(XElement? element,string name,double fallback=0)
    {
      var value=Field(element,name);
      return value==null||IsNil(value)?fallback:ParseNumber(Child(value,"Value")?.Value??value.Value);
    }
    internal bool Flag(XElement? element,string name,bool fallback=false)
      =>bool.TryParse(Text(element,name),out var value)?value:fallback;
    internal Point3d Point(XElement? node)=>new(Number(node,"X"),Number(node,"Y"),0);
    internal IEnumerable<XElement> Entries(XElement? node)
    {
      XNamespace collection=PatternSmithPartContract.CollectionNamespace;
      node=Resolve(node);
      var items=Resolve(node?.Element(collection+"list")??node?.Element(collection+"m_dictionary")??node);
      return items?.Elements()??[];
    }
    internal Point3d[] Points(XElement? node)=>Entries(node).Select(point=>Point(Resolve(point))).ToArray();
  }
  private static XElement? Child(XElement? element,string name)=>element?.Elements().FirstOrDefault(child=>child.Name.LocalName==name);
  private static bool IsNil(XElement? element)=>element==null||(bool?)element.Attribute(Types+"nil")==true;
  private static string TypeOf(XElement element)=>((string?)element.Attribute(Types+"type")??element.Name.LocalName).Split(':')[^1];
  private static string ReferenceId(XElement? element)=>((string?)element?.Attribute(References+"Id"))??string.Empty;
  private static double ParseNumber(string value)=>double.TryParse(value,NumberStyles.Float,Invariant,out double number)&&double.IsFinite(number)
    ?number:throw new InvalidDataException("The project contains a non-finite or invalid coordinate or dimension.");
  private static bool IsFaceDown(string name)=>name.EndsWith(NestWorkflow.FaceDownSuffix,StringComparison.OrdinalIgnoreCase);
  private static string CleanName(string name)=>NestWorkflow.CuttingTableName(name,false);
  private static string LayerKey(ProjectXml xml,XElement? layer)=>xml.Text(layer,"Id",ReferenceId(layer));

  private static Project ReadProject(string path)
  {
    using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
    using var archive=new ZipArchive(file,ZipArchiveMode.Read);
    if(archive.Entries.Count>MaximumArchiveEntries)throw new InvalidDataException("Too many entries in the PatternSmith project.");
    var entries=archive.Entries.Where(entry=>entry.FullName=="project.xml").ToArray();
    if(entries.Length!=1||entries[0].Length>MaximumProjectXmlBytes)
      throw new InvalidDataException("The PatternSmith project data is missing, duplicated or too large.");
    using var data=entries[0].Open();
    using var reader=XmlReader.Create(data,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=MaximumProjectXmlBytes});
    var root=XDocument.Load(reader).Root;
    if(root?.Name!=ProjectNs+"PresentationProject")throw new InvalidDataException("This is not a supported PatternSmith project.");
    return ReadProjectXml(root,clipboard:false);
  }

  private static Project ReadProjectXml(XElement root,bool clipboard)
  {
    var xml=new ProjectXml(root);
    var project=new Project{EditorClipboard=clipboard&&root.Name==PatternSmithClipboard.EditorContainerName};
    try
    {
      var tools=new Dictionary<string,(string Process,int Slot)>(StringComparer.Ordinal);
      foreach(var link in xml.Entries(Child(root,clipboard?"ToolReferenceMap":"ToolReferences")))
      {
        var key=xml.Field(link,"Key");var tool=xml.Field(link,"Value");var id=LayerKey(xml,key);
        if(id.Length>0&&!IsNil(tool))tools[id]=(xml.Text(tool,"ProcessType","Reference"),(int)xml.Number(tool,"Index",-1));
      }
      ToolLayer LayerFor(XElement node)
      {
        var layer=xml.Field(node,"Layer");
        string key=LayerKey(xml,layer);string name=xml.Name(layer,DefaultReferenceLayer);
        if(key.Length==0)key=name;
        var tool=tools.TryGetValue(key,out var configured)?configured:(InferProcess(name),-1);
        return new(key,name,tool.Item1,tool.Item2);
      }
      int objectCount=0;
      void ReadItems(Pattern pattern,XElement owner)
      {
        int before=objectCount;
        foreach(var entry in xml.Entries(xml.Field(owner,"Entities")))
        {
          var entity=xml.Resolve(entry)!;
          if(!TryReadEntity(xml,entity,LayerFor(entity),out var item))
            throw new InvalidDataException($"Cannot import '{pattern.Name}': unsupported PatternSmith entity {TypeOf(entity)}.");
          if(item==null)
            throw new InvalidDataException($"Cannot import '{pattern.Name}': its {TypeOf(entity)} entity has no valid geometry.");
          item=item with {BoundaryCandidate=item.Geometry is Curve&&!xml.Flag(entity,"ExcludeFromPerimeter")};
          pattern.Items.Add(item);
          if(++objectCount>MaximumImportObjects)throw new InvalidDataException("Too many objects in the project.");
          var effects=ReadCurveEffects(xml,entity,LayerFor,MaximumImportObjects-objectCount,pattern.Name);
          if(effects==null)continue;
          pattern.Items[^1]=item with {Effects=effects};
          objectCount+=effects.MaximumObjects;
        }
        pattern.ExpandedItemCount=objectCount-before;
      }
      if(project.EditorClipboard)
      {
        var editor=new Pattern("","PatternSmith editor",Point3d.Origin,0,"",1,false,false,false,[]);
        project.Patterns.Add(editor);
        ReadItems(editor,root);
        if(editor.Items.Count==0)throw new InvalidDataException("The PatternSmith editor clipboard has no supported geometry.");
        return project;
      }
      var photoPaths=new Dictionary<Guid,(string Project,string Image)>();
      if(clipboard)
      {
        foreach(var entry in xml.Entries(xml.Field(root,"FilePaths")))
        {
          var link=xml.Resolve(entry);
          if(!Guid.TryParse(xml.Text(link,"Key"),out var id))continue;
          var value=xml.Field(link,"Value");
          photoPaths.TryAdd(id,(xml.Text(value,"ProjectFileName"),xml.Text(value,"ImageFileName")));
        }
      }
      var materials=Child(root,"Materials");
      var templates=new Dictionary<XElement,ProjectMaterial>();
      ProjectMaterial ReadMaterial(XElement? element)
      {
        if(IsNil(element))return new("","Custom",0,0);
        if(templates.TryGetValue(element!,out var found))return found;
        var id=xml.Text(element,"MaterialID",ReferenceId(element));string name=xml.Name(element,"Custom");
        var shape=xml.Field(element,"MaterialPattern");
        double width=xml.Number(shape,"Width");
        var value=new ProjectMaterial(id,name,Math.Max(0,width),Math.Max(0,xml.Number(element,"CopySpacing")));
        templates[element!]=value;
        if(id.Length>0)project.Materials[id]=value;
        return value;
      }
      foreach(var node in xml.Entries(Child(materials,"Templates")))ReadMaterial(xml.Resolve(node));

      var patterns=new Dictionary<XElement,Pattern>();
      foreach(var source in xml.Entries(Child(root,"Patterns")))
      {
        var node=xml.Resolve(source)!;
        string name=xml.Name(node,"Part "+(project.Patterns.Count+1).ToString(Invariant));
        var constraints=xml.Field(node,"Constraints");
        var quota=xml.Field(constraints,"Quota");
        var rotation=xml.Field(xml.Field(constraints,"Rotation"),"Step");
        var step=rotation==null?(double?)null:xml.Number(rotation,"Value");
        var material=xml.Field(constraints,"Material");
        double count=Math.Max(1,xml.Number(quota,"Base",1))*Math.Max(1,xml.Number(quota,"Multiplier",1));
        var pattern=new Pattern(xml.Text(node,"PersistentId",xml.Text(node,"Id",ReferenceId(node))),name,
          xml.Point(xml.Field(node,"Location")),xml.Number(node,"Rotation"),
          xml.Text(material,"MaterialId"),(int)Math.Min(NestWorkflow.ConfiguredMaximumCopies,count),
          xml.Flag(quota,"Enforce"),IsFaceDown(name),xml.Flag(node,"AllowNesting",true),[])
        {
          State=NestWorkflow.ReadPatternSmithPartState(node.Element(node.Name.Namespace+PatternSmithPartContract.StateMember)?.Value??
            constraints?.Element(XName.Get(PatternSmithPartContract.StateMember,PatternSmithPartContract.ConstraintNamespace))?.Value),
          RotationStep=step is {} increment?(increment==PatternSmithPartContract.FreeRotationStep?-1:Math.Round(RhinoMath.ToDegrees(increment),9)):null,
          MaterialEnforced=xml.Text(material,"ConstraintType")=="Strict",
          Photo=clipboard?ReadClipboardPhoto(xml,node,photoPaths,xml.Text(root,"ProjectFileName"),project):null
        };
        project.Patterns.Add(pattern);patterns.Add(node,pattern);
        ReadItems(pattern,node);
        if(pattern.Material.Length>0&&!project.Materials.ContainsKey(pattern.Material))
          project.Materials.Add(pattern.Material,new(pattern.Material,pattern.Material,0,0));
      }
      foreach(var entry in clipboard?[]:xml.Entries(Child(root,"Nests")))
      {
        var node=xml.Resolve(entry);string name=xml.Name(node,"Nest "+(project.Tables.Count+1).ToString(Invariant));
        var roll=xml.Field(node,"Material");var material=ReadMaterial(xml.Field(roll,"MaterialTemplate"));
        var copies=new List<Copy>();
        foreach(var item in xml.Entries(xml.Field(node,"Copies")))
        {
          var copy=xml.Resolve(item);
          var source=xml.Field(copy,"Source");
          if(source==null||!patterns.TryGetValue(source,out var pattern))throw new InvalidDataException("A nest refers to a missing part.");
          string flip=xml.Text(copy,"Flip","None");
          if(flip is not ("None" or "X" or "Y"))throw new InvalidDataException("Unsupported PatternSmith copy mirror axis: "+flip);
          copies.Add(new(pattern,xml.Point(xml.Field(copy,"Location")),xml.Number(copy,"Rotation"),flip));
          objectCount+=pattern.ExpandedItemCount;
          if(objectCount>MaximumImportObjects)throw new InvalidDataException("Too many expanded nested objects in the project.");
        }
        project.Tables.Add(new(name,material,Math.Max(0,xml.Number(roll,"Length")),IsFaceDown(name),copies.ToArray()));
      }
      MatchNativeTableLabels(project);
      if(!project.Patterns.Any(pattern=>pattern.Items.Count>0))throw new InvalidDataException("The project contains no supported geometry.");
      return project;
    }
    catch{project.Dispose();throw;}
  }

  private static BackgroundPhoto? ReadClipboardPhoto(ProjectXml xml,XElement pattern,
    Dictionary<Guid,(string Project,string Image)> paths,string projectFile,Project project)
  {
    try
    {
      var edit=xml.Field(pattern,"EditBackground");
      if(IsNil(edit))return null;
      Guid.TryParse(xml.Text(pattern,"BackgroundID"),out var photoId);
      Guid.TryParse(xml.Text(pattern,"PersistentId"),out var patternId);
      var path=paths.GetValueOrDefault(patternId);
      string imageFile=string.IsNullOrWhiteSpace(path.Image)?xml.Text(edit,"ImageFileName"):path.Image;
      if(photoId==Guid.Empty&&string.IsNullOrWhiteSpace(imageFile))return null;
      double scale=xml.Number(edit,"ScaleFactor");
      double opacity=Math.Clamp(xml.Number(edit,"ImageOpacity",FullBackgroundOpacity)/FullBackgroundOpacity,0,1);
      if(scale<=0)return null;
      // The paste option controls visibility in Rhino; a hidden PatternSmith preview must not suppress the photo.
      if(opacity==0)opacity=1;
      var center=xml.Point(xml.Field(edit,"Location"));
      var editorOffset=xml.Point(xml.Field(pattern,"EntityBackgroundPhotoLocation"));
      // The editor translates the curves by this offset; here they retain their workspace coordinates.
      center-=editorOffset-Point3d.Origin;
      if(!center.IsValid)return null;
      return new(new(photoId,string.IsNullOrWhiteSpace(path.Project)?projectFile:path.Project,imageFile),center,scale,
        -xml.Number(edit,"ImageRotation"),opacity);
    }
    catch(InvalidDataException)
    {
      project.Warn("background images had invalid placement data");
      return null;
    }
  }

  private static bool TryReadEntity(ProjectXml xml,XElement node,ToolLayer layer,out Item? item)
  {
    string kind=TypeOf(node);GeometryBase? geometry=null;SplineData? spline=null;
    Point3d Pt(string field)=>xml.Point(xml.Field(node,field));
    switch(kind)
    {
      case "Line":
        var a=Pt("A");var b=Pt("B");if(a.DistanceTo(b)>MinimumLengthInches)geometry=new LineCurve(a,b);
        break;
      case "Arc":
      case "Circle":
        double radius=xml.Number(node,"Radius");if(radius<=MinimumLengthInches)break;
        var center=Pt("Center");
        if(kind=="Circle")
        {
          var circle=new Circle(center,radius).ToNurbsCurve();
          if(xml.Text(node,"Direction")=="Clockwise")circle.Reverse();
          geometry=circle;break;
        }
        double start=xml.Number(node,"StartAngle"),sweep=xml.Number(node,"SweepAngle");
        if(Math.Abs(sweep)*radius<=MinimumLengthInches)break;
        Point3d At(double angle)=>center+new Vector3d(radius*Math.Cos(angle),radius*Math.Sin(angle),0);
        if(Math.Abs(sweep)>=2*Math.PI)geometry=new Circle(center,radius).ToNurbsCurve();
        else
        {
          var arc=new Arc(At(start),At(start+sweep/2),At(start+sweep));
          geometry=arc.IsValid?new ArcCurve(arc):new LineCurve(At(start),At(start+sweep));
        }
        break;
      case "Ellipse":
        double major=xml.Number(node,"A"),minor=xml.Number(node,"B");
        if(major<=MinimumLengthInches||minor<=MinimumLengthInches)break;
        var axes=Plane.WorldXY;
        axes.Origin=Pt("Center");axes.Rotate(xml.Number(node,"Rotation"),Vector3d.ZAxis);
        var ellipse=new Ellipse(axes,major,minor).ToNurbsCurve();
        if(xml.Text(node,"Direction")=="Clockwise")ellipse.Reverse();
        geometry=ellipse;
        break;
      case "Rectangle":
        double width=xml.Number(node,"Width"),rectHeight=xml.Number(node,"Height");
        if(width<=MinimumLengthInches||rectHeight<=MinimumLengthInches)break;
        double angle=xml.Number(node,"Rotation");
        var corner=Pt("A");
        var xAxis=new Vector3d(Math.Cos(angle),Math.Sin(angle),0);
        var yAxis=Vector3d.CrossProduct(Vector3d.ZAxis,xAxis);
        var along=corner+xAxis*width;var across=corner+yAxis*rectHeight;var opposite=along+yAxis*rectHeight;
        geometry=xml.Text(node,"Direction")=="Clockwise"?PolylineGeometry([corner,across,opposite,along],true):
          PolylineGeometry([corner,along,opposite,across],true);
        break;
      case "Polyline":
        geometry=PolylineGeometry(xml.Points(xml.Field(node,"Points")),xml.Flag(node,"IsClosed"));
        break;
      case "Bezier":
      case "BezierGroup":
        geometry=BezierGeometry(xml.Points(xml.Field(node,"ControlPoints")));
        break;
      case "Spline":
        var points=xml.Points(xml.Field(node,"ControlPoints"));bool closed=xml.Flag(node,"IsClosed");
        geometry=ApproximateSpline(points,closed);
        if(geometry!=null)spline=new(points,closed);
        break;
      case "Text":
      case "Font":
        string text=xml.Text(node,"Text");double height=xml.Number(node,"Height",DefaultTextHeightInches);
        if(text.Length>0&&height>MinimumLengthInches)
        {
          var plane=Plane.WorldXY;plane.Origin=Pt("Center");plane.Rotate(xml.Number(node,"Rotation"),Vector3d.ZAxis);
          var annotation=new TextEntity{PlainText=text,Plane=plane,TextHeight=height,Justification=TextJustification.MiddleCenter,
            TextHorizontalAlignment=TextHorizontalAlignment.Center,TextVerticalAlignment=TextVerticalAlignment.Middle};
          if(kind=="Font"&&xml.Field(node,"Font") is {} descriptor&&xml.Text(descriptor,"Name").Trim() is {Length:>0} family)
          {
            string style=xml.Text(descriptor,"Style");
            annotation.Font=Rhino.DocObjects.Font.FromQuartetProperties(family,style.Contains("Bold",StringComparison.OrdinalIgnoreCase),
              style.Contains("Italic",StringComparison.OrdinalIgnoreCase));
          }
          geometry=annotation;
        }
        break;
      case "Point":
        geometry=new Rhino.Geometry.Point(Pt("Center"));
        break;
      default:item=null;return false;
    }
    item=geometry==null?null:new(layer,geometry,kind,xml.Text(node,"TemplateId")){Spline=spline};
    return true;
  }
  private static CurveEffects? ReadCurveEffects(ProjectXml xml,XElement source,Func<XElement,ToolLayer> layerFor,int budget,string pattern)
  {
    var seam=xml.Field(source,"Seam");
    double seamWidth=xml.Number(seam,"Width");
    var attachments=xml.Entries(xml.Field(source,"DynamicEntities"));
    if(Math.Abs(seamWidth)<=MinimumLengthInches&&!attachments.Any())return null;
    var request=new XElement("Effects");
    var layers=new List<ToolLayer>();
    int maximumObjects=0;
    void Add(XElement effect,XElement owner,int capacity)
    {
      if(capacity>budget-maximumObjects)throw new InvalidDataException("Too many generated objects in the PatternSmith project.");
      maximumObjects+=capacity;
      var layer=layerFor(owner);
      int index=layers.FindIndex(existing=>existing.Key==layer.Key);
      if(index<0){index=layers.Count;layers.Add(layer);}
      effect.SetAttributeValue("layer",index);
      request.Add(effect);
    }
    XAttribute Attr(XElement node,string field,string fallback)=>new(field,xml.Text(node,field,fallback));
    XAttribute Number(XElement? node,string field)=>new(field,xml.Number(node,field));
    if(Math.Abs(seamWidth)>MinimumLengthInches)
      Add(new XElement("Seam",new XAttribute("Width",seamWidth),Attr(seam!,"StartStyle","Extend"),Attr(seam!,"EndStyle","Extend")),
        seam!,MaximumSeamPrimitives);
    foreach(var entry in attachments)
    {
      var effect=xml.Resolve(entry);
      if(IsNil(effect))continue;
      switch(TypeOf(effect!))
      {
        case "Anchor":
          double count=xml.Number(effect,"Count");
          if(count<0||count>MaximumImportObjects||count!=Math.Truncate(count))
            throw new InvalidDataException($"Cannot import '{pattern}': invalid PatternSmith anchor count.");
          if(count>0)Add(new XElement("Anchor",Attr(effect!,"EntityType","Point"),new XAttribute("Count",(int)count),
            Number(effect,"Radius"),Number(effect,"Length"),Number(effect,"DistanceFromStart"),Number(effect,"DistanceFromEnd")),effect!,(int)count);
          break;
        case "Notch":
          var position=xml.Field(effect,"Position");
          if(IsNil(position))throw new InvalidDataException($"Cannot import '{pattern}': a PatternSmith notch is missing its position.");
          string execution=xml.Text(effect,"ExecutionStyle",DefaultNotchExecutionStyle),style=xml.Text(effect,"Style","Slit");
          bool inline=execution=="Inline";
          if(!inline&&execution!=DefaultNotchExecutionStyle)
            throw new InvalidDataException($"Cannot import '{pattern}': unsupported PatternSmith notch execution style {execution}.");
          if(inline&&style is not ("V" or "Castle"))
            throw new InvalidDataException($"Cannot import '{pattern}': unsupported inline PatternSmith notch profile {style}.");
          // Inline profiles use the cut layer, even when PatternSmith assigned the notch its own layer.
          Add(new XElement("Notch",new XAttribute("Style",style),new XAttribute("ExecutionStyle",execution),Number(effect,"Depth"),Number(effect,"Width"),
            Number(position,"Distance"),Attr(position!,"DistanceUnits","Native"),Attr(position!,"Placement","Start"),
            Attr(position!,"Side","Internal")),inline?(Math.Abs(seamWidth)>MinimumLengthInches?seam!:source):effect!,MaximumNotchPrimitives);
          break;
        default:throw new InvalidDataException($"Cannot import '{pattern}': unsupported PatternSmith attachment {TypeOf(effect!)}.");
      }
    }
    if(maximumObjects==0)return null;

    // Send only the known geometry fields to the native decoder, not the source object graph.
    string kind=TypeOf(source);
    XElement Pt(string name,Point3d point)=>new(name,new XAttribute("x",point.X),new XAttribute("y",point.Y));
    XElement Points(string field)=>new("Points",xml.Points(xml.Field(source,field)).Select(point=>Pt("P",point)));
    XElement Curve(string type,params object[] fields)=>new(type,fields);
    var curve=kind switch
    {
      "Line"=>Curve(kind,Pt("A",xml.Point(xml.Field(source,"A"))),Pt("B",xml.Point(xml.Field(source,"B")))),
      "Polyline"=>Curve(kind,new XAttribute("Closed",xml.Flag(source,"IsClosed")),Points("Points")),
      "Arc" or "Circle"=>Curve(kind,Pt("Center",xml.Point(xml.Field(source,"Center"))),Number(source,"Radius"),
        Number(source,"StartAngle"),Number(source,"SweepAngle"),Attr(source,"Direction","Counterclockwise")),
      "Bezier" or "BezierGroup" or "Spline"=>Curve(kind,new XAttribute("Closed",xml.Flag(source,"IsClosed")),Points("ControlPoints")),
      "Ellipse"=>Curve(kind,Pt("Center",xml.Point(xml.Field(source,"Center"))),Number(source,"A"),Number(source,"B"),
        Number(source,"Rotation"),Attr(source,"Direction","Counterclockwise")),
      "Rectangle"=>Curve(kind,Pt("A",xml.Point(xml.Field(source,"A"))),Number(source,"Width"),Number(source,"Height"),
        Number(source,"Rotation"),Attr(source,"Direction","Counterclockwise")),
      _=>throw new InvalidDataException($"Cannot import '{pattern}': PatternSmith effects on {kind} are not supported.")
    };
    request.AddFirst(curve);
    return new(request,layers.ToArray(),maximumObjects);
  }
  private static Curve? PolylineGeometry(Point3d[] input,bool closed)
  {
    var points=new List<Point3d>(input.Length+1);
    foreach(var point in input)if(points.Count==0||point.DistanceTo(points[^1])>MinimumLengthInches)points.Add(point);
    if(closed&&points.Count>2&&points[0].DistanceTo(points[^1])>MinimumLengthInches)points.Add(points[0]);
    return points.Count>=2?new PolylineCurve(points):null;
  }
  private static Curve? ApproximateSpline(Point3d[] points,bool closed)
  {
    if(points.Length<3)return PolylineGeometry(points,closed);
    var knots=closed?CurveKnotStyle.ChordPeriodic:CurveKnotStyle.Chord;
    if(closed&&points[^1].DistanceTo(points[0])<=MinimumLengthInches)points=points[..^1];
    return Curve.CreateInterpolatedCurve(points,3,knots)??PolylineGeometry(points,closed);
  }
  private static Curve? BezierGeometry(Point3d[] points)
  {
    if(points.Length<4||(points.Length-1)%3!=0)return null;
    if(points.Length==4)return NurbsCurve.Create(false,3,points);
    var result=new PolyCurve();
    for(int index=0;index+3<points.Length;index+=3)
    {
      using var segment=NurbsCurve.Create(false,3,points[index..(index+4)]);
      if(segment==null||!result.Append(segment)){result.Dispose();return null;}
    }
    return result;
  }
  private static void ResolveNativeGeometry(Project project,ImportSelection selection)
  {
    var items=selection.GeometryPatterns.SelectMany(pattern=>pattern.Items.Select((item,index)=>(pattern,index,item)))
      .Where(entry=>entry.item.Spline!=null||entry.item.Effects!=null).ToArray();
    var splines=items.Where(entry=>entry.item.Spline!=null).ToArray();
    var effects=items.Where(entry=>entry.item.Effects!=null).ToArray();
    var effectGroups=effects.Select(entry=>entry.pattern).Distinct().Select((pattern,index)=>(pattern,index))
      .ToDictionary(entry=>entry.pattern,entry=>entry.index);
    if(splines.Length==0&&effects.Length==0)return;
    var converted=new HashSet<int>();
    string? directory=null;
    try
    {
      string sdk=vExportPatternSmith.FindPatternSmithLibrary();
      directory=Path.Combine(Path.GetTempPath(),"vTools","PatternSmithImport",Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(directory);
      string helper=Path.Combine(directory,"PatternSmithSpline.exe"),input=Path.Combine(directory,"splines.xml"),output=Path.Combine(directory,"curves.xml");
      using(var resource=typeof(vImportPatternSmith).Assembly.GetManifestResourceStream(vExportPatternSmith.NativeBuilderResourceName)
        ??throw new InvalidOperationException("The PatternSmith spline decoder is missing."))
      using(var file=new FileStream(helper,FileMode.CreateNew,FileAccess.Write,FileShare.None))resource.CopyTo(file);
      new XElement("PatternSmithReadSplines",splines.Select((entry,index)=>new XElement("Spline",
        new XAttribute("id",index),new XAttribute("closed",entry.item.Spline!.Closed),entry.item.Spline.Points.Select(point=>
          new XElement("P",new XAttribute("x",point.X),new XAttribute("y",point.Y))))),
        effects.Select((entry,index)=>new XElement("Effects",new XAttribute("id",index),new XAttribute("group",effectGroups[entry.pattern]),
          entry.item.Effects!.Request.Elements()))).Save(input);
      var start=new System.Diagnostics.ProcessStartInfo(helper){UseShellExecute=false,CreateNoWindow=true,
        WorkingDirectory=directory,RedirectStandardOutput=true,RedirectStandardError=true};
      start.ArgumentList.Add(sdk);start.ArgumentList.Add(input);start.ArgumentList.Add(output);
      using var process=System.Diagnostics.Process.Start(start)??throw new InvalidOperationException("Could not start the PatternSmith spline decoder.");
      var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
      if(!process.WaitForExit(NativeGeometryTimeoutMilliseconds))
      {
        process.Kill();process.WaitForExit();
        throw new TimeoutException("PatternSmith geometry decoding timed out.");
      }
      _=stdout.GetAwaiter().GetResult();
      if(process.ExitCode!=0)throw new InvalidDataException(stderr.GetAwaiter().GetResult());
      using var reader=XmlReader.Create(output,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=MaximumProjectXmlBytes});
      var result=XElement.Load(reader);
      foreach(var curve in result.Elements("Curve"))
      {
        if(!int.TryParse((string?)curve.Attribute("id"),NumberStyles.None,Invariant,out int index)||index<0||
          index>=splines.Length||converted.Contains(index))continue;
        var points=curve.Elements("P").Select(point=>new Point3d(ParseNumber((string?)point.Attribute("x")??""),
          ParseNumber((string?)point.Attribute("y")??""),0)).ToArray();
        if(BezierGeometry(points) is not {} geometry)continue;
        var entry=splines[index];
        if(!geometry.IsValid||entry.item.Spline!.Closed&&!geometry.IsClosed){geometry.Dispose();continue;}
        entry.pattern.Items[entry.index]=entry.item with {Geometry=geometry};entry.item.Geometry.Dispose();converted.Add(index);
      }
      var decoded=new HashSet<int>();
      var xml=new ProjectXml(result);
      var allowancePieces=new List<(Pattern Pattern,Item Item)>();
      var notches=new List<(int Effect,XElement Node)>();
      static bool IsNotchStroke(XElement node)=>node.Name!="Anchor"&&node.Name!="SewAnchor";
      foreach(var resolved in result.Elements("Effects"))
      {
        if(!int.TryParse((string?)resolved.Attribute("id"),NumberStyles.None,Invariant,out int index)||
          index<0||index>=effects.Length||!decoded.Add(index))throw new InvalidDataException("Invalid PatternSmith effect decoder response.");
        var entry=effects[index];var recipe=entry.item.Effects!;
        if((string?)resolved.Attribute("error") is {} error)
          throw new InvalidDataException($"Cannot import '{entry.pattern.Name}': {error}");
        int count=0;
        foreach(var primitive in resolved.Elements())
        {
          count+=primitive.Name=="Notch"?primitive.Elements().Count(IsNotchStroke):1;
          if(count>recipe.MaximumObjects)throw new InvalidDataException("Too many generated PatternSmith objects.");
          if(primitive.Name=="Notch"){notches.Add((index,primitive));continue;}
          var generated=ReadEffectPrimitive(xml,primitive,recipe,entry.pattern.Name);
          if((bool?)primitive.Attribute("seam")==true)
          {
            var seam=recipe.Request.Element("Seam")!;
            double width=ParseNumber((string?)seam.Attribute("Width")??"");
            generated=generated with {Kind="seam allowance",SeamSources=[new(entry.pattern.Items[entry.index],width,
              (string)seam.Attribute("StartStyle")!,(string)seam.Attribute("EndStyle")!)]};
            allowancePieces.Add((entry.pattern,generated));
          }
          entry.pattern.Items.Add(generated);
        }
      }
      if(decoded.Count!=effects.Length)throw new InvalidDataException("The PatternSmith geometry decoder did not return every requested effect.");
      JoinImportedAllowances(allowancePieces);
      foreach(var (index,node) in notches)
      {
        var entry=effects[index];var recipe=entry.item.Effects!;
        var cutAnchor=xml.Point(xml.Field(node,"Anchor"));
        var sewAnchor=xml.Point(xml.Field(node,"SewAnchor"));
        double Number(string name)=>ParseNumber((string?)node.Attribute(name)??"");
        double angle=Number("angle");
        var direction=new Vector3d(Math.Cos(angle),Math.Sin(angle),0);
        var source=entry.pattern.Items[entry.index];
        bool external=(bool?)node.Attribute("external")==true;
        string type=(string?)node.Attribute("style") switch{"Slit"=>"I","Castle"=>external?MatchingNotches.ExternalCastleType:"U",
          "V"=>external?MatchingNotches.ExternalVType:"V","T"=>"T",
          _=>throw new InvalidDataException("Unsupported imported PatternSmith notch style.")};
        var fragments=node.Elements().Where(IsNotchStroke).ToArray();
        var mark=new ImportedNotch(source,sewAnchor,cutAnchor,direction,type,(bool?)node.Attribute("percent")==true?"percent":"length",
          Number("depth"),Number("width"),fragments.Length,(bool?)node.Attribute("inline")==true);
        for(int component=0;component<fragments.Length;component++)
          entry.pattern.Items.Add(ReadEffectPrimitive(xml,fragments[component],recipe,entry.pattern.Name) with {Notch=mark,NotchComponent=component});
      }
    }
    catch(Exception ex)
    {
      if(effects.Length>0)
        throw new InvalidDataException("Could not reproduce the PatternSmith seam, notch, or anchor geometry. "+ex.Message,ex);
      Log.Write(nameof(vImportPatternSmith),"native spline decoding not available: {0}",ex.Message);
    }
    finally
    {
      if(directory!=null)
      {
        try{Directory.Delete(directory,true);}
        catch(IOException ex){Log.Write(nameof(vImportPatternSmith),"decoder cleanup failed: {0}",ex.Message);}
        catch(UnauthorizedAccessException ex){Log.Write(nameof(vImportPatternSmith),"decoder cleanup failed: {0}",ex.Message);}
      }
    }
    for(int index=converted.Count;index<splines.Length;index++)
      project.Warn("splines rebuilt through their fit points (native decoder unavailable for these curves)");
  }
  private static Item ReadEffectPrimitive(ProjectXml xml,XElement primitive,CurveEffects recipe,string pattern)
  {
    if(!int.TryParse((string?)primitive.Attribute("layer"),NumberStyles.None,Invariant,out int layer)||
      layer<0||layer>=recipe.Layers.Length||!TryReadEntity(xml,primitive,recipe.Layers[layer],out var item)||item==null)
      throw new InvalidDataException($"Cannot import '{pattern}': the native geometry decoder returned an invalid effect primitive.");
    return item;
  }

  private static void JoinImportedAllowances(List<(Pattern Pattern,Item Item)> pieces)
  {
    foreach(var group in pieces.GroupBy(piece=>(piece.Pattern,piece.Item.Layer.Key)))
    {
      var members=group.ToArray();
      if(members.Length>1&&members.All(piece=>piece.Item.Geometry is Curve))
      {
        var joined=Curve.JoinCurves(members.Select(piece=>(Curve)piece.Item.Geometry),NativeSeamJoinToleranceInches,false,out int[] keys);
        if(joined.Length>0&&keys.Length==members.Length&&keys.All(key=>key>=0&&key<joined.Length)&&
          keys.Distinct().Count()==joined.Length&&joined.All(curve=>curve.IsValid))
        {
          var originals=members.Select(piece=>piece.Item).ToHashSet(ReferenceEqualityComparer.Instance);
          group.Key.Pattern.Items.RemoveAll(item=>originals.Contains(item));
          var lineage=members.Select((member,index)=>(member.Item,Key:keys[index])).ToLookup(entry=>entry.Key);
          var replacements=joined.Select((curve,index)=>new Item(members[0].Item.Layer,curve,"seam allowance","")
          {
            SeamSources=lineage[index].SelectMany(entry=>entry.Item.SeamSources!)
              .DistinctBy(source=>source.Source,ReferenceEqualityComparer.Instance).ToArray()
          }).ToArray();
          group.Key.Pattern.Items.AddRange(replacements);
          foreach(var piece in members)piece.Item.Geometry.Dispose();
          continue;
        }
        foreach(var curve in joined)curve.Dispose();
      }
    }
  }
  private static string InferProcess(string name)=>name.ToUpperInvariant() switch
  {
    "CUT" or "CUT1" or "CUT2"=>"Cut","PLOT" or "MARK"=>"Mark","DRILL"=>"Drill","PUNCH"=>"Punch",
    "CREASE"=>"Crease","PRINT"=>"Print","NOTCH"=>"Notch",_=>"Reference"
  };
  private static string DefaultLayer(ToolLayer layer)=>layer.Process switch
  {
    "Cut"=>layer.Slot>0?"CUT"+(layer.Slot+1).ToString(Invariant):DefaultCutLayer,
    "Mark"=>DefaultMarkLayer,"DotMark"=>"DOT MARK","Reference"=>layer.Name,
    "Notch"=>"NOTCH","Punch"=>"PUNCH","Drill"=>"DRILL","Crease"=>"CREASE","Print"=>"PRINT",_=>layer.Name
  };

  private sealed class ImportMappings
  {
    internal readonly Dictionary<string,string> Layers=new(StringComparer.Ordinal);
    internal readonly Dictionary<string,Material?> Materials=new(StringComparer.Ordinal);
    internal Material[] Available {get;private set;}=[];
    internal ImportContents Contents;
    internal bool EmptyTables;
    internal PastePlacement Placement;
    internal bool PasteBackground;
    internal ImportMappings(RhinoDoc doc,Project project,bool clipboard=false)
    {
      (Contents,EmptyTables)=ToolsOptionStore.Read(nameof(vImportPatternSmith),section=>
      {
        var contents=ToolsOptionStore.TryGetString(section,ContentsKey,out var saved)&&
          Enum.TryParse<ImportContents>(saved,true,out var parsed)&&Enum.IsDefined(parsed)?parsed:ConfiguredDefaultContents;
        bool includeEmpty=ToolsOptionStore.TryGetBool(section,IncludeEmptyNestsKey,out bool empty)?empty:ConfiguredDefaultIncludeEmptyNests;
        return (contents,includeEmpty);
      });
      if(clipboard)
      {
        Contents=ImportContents.PartsOnly;EmptyTables=false;Placement=vPasteFromPatternSmith.ConfiguredPlacement;
        PasteBackground=vPasteFromPatternSmith.ConfiguredPasteBackgroundImage;
      }
      RefreshPresets();
      foreach(var layer in LayersFor(project.Patterns))Layers[layer.Key]=DefaultLayer(layer);
      foreach(var material in project.Materials.Values)
      {
        if(clipboard)
        {
          var linked=Available.Where(preset=>preset.PatternSmith?.Id==material.Id).Take(2).ToArray();
          Materials[material.Id]=linked.Length==1?linked[0]:null;
          continue;
        }
        Materials[material.Id]=Available.FirstOrDefault(preset=>preset.PatternSmith?.Id==material.Id&&
          preset.PatternSmith.Name.Equals(material.Name,StringComparison.OrdinalIgnoreCase))??Available.FirstOrDefault(preset=>
          preset.Name.Equals(material.Name,StringComparison.OrdinalIgnoreCase)||
          preset.PatternSmith?.Name.Equals(material.Name,StringComparison.OrdinalIgnoreCase)==true)??ProjectRecipe(material,doc);
      }
    }
    internal bool RefreshPresets()
    {
      if(!MaterialLibrary.TrySnapshot(out var snapshot,out _)||Available.SequenceEqual(snapshot.Presets))return false;
      Available=snapshot.Presets.ToArray();
      return true;
    }
    internal void SavePreferences()=>ToolsOptionStore.Update(nameof(vImportPatternSmith),section=>
    {
      section[ContentsKey]=Contents.ToString();
      section[IncludeEmptyNestsKey]=EmptyTables;
    });
  }
  private static Material ProjectRecipe(ProjectMaterial material,RhinoDoc doc)
  {
    double scale=InchesToModel(doc);
    return new Material(material.Name,(material.Width>0?material.Width:UnspecifiedTableWidthInches)*scale,material.Gap*scale);
  }
  private static double InchesToModel(RhinoDoc doc)=>doc.ModelUnitSystem is UnitSystem.None or UnitSystem.CustomUnits?
    1:RhinoMath.UnitScale(NativeModelUnits,doc.ModelUnitSystem);

  private sealed class MappingDialog : Dialog<bool>
  {
    internal MappingDialog(RhinoDoc doc,Project project,ImportMappings mapping,string file,bool clipboard=false)
    {
      Title=clipboard?"Paste from PatternSmith":"PatternSmith Import";ClientSize=new Eto.Drawing.Size(DialogWidth,DialogHeight);Resizable=true;
      MinimumSize=new Eto.Drawing.Size(DialogMinimumWidth,DialogMinimumHeight);
      Padding=new Eto.Drawing.Padding(DialogPadding);
      var layersPage=new TabPage{Text="Layers map"};
      void PopulateLayers(IReadOnlyList<ToolLayer> layerRows)
      {
        if(layerRows.Count==0)
        {
          layersPage.Content=new Panel{Padding=new Eto.Drawing.Padding(DialogPadding),Content=new Label{Text="No layers to map"}};
          return;
        }
        var layerTable=Columns("PatternSmith layer","Tool","Rhino layer");
        foreach(var row in layerRows)
        {
          string current=mapping.Layers[row.Key];
          var field=LayerSelector.CreateDropDown(doc,current,width:DestinationMinimumWidth);
          field.SelectedIndexChanged+=(_,_)=>
          {
            if(!LayerSelector.IsDropDownUpdating(field))
              mapping.Layers[row.Key]=LayerSelector.GetDropDownValue(field,mapping.Layers[row.Key]);
          };
          var tool=WithSwatch(Swatch(row.Color),new Label{Text=ToolCaption(row),VerticalAlignment=VerticalAlignment.Center},false);
          tool.Width=ToolColumnWidth;
          layerTable.Rows.Add(new TableRow(Name(row.Name,SourceColumnWidth),tool,new TableCell(field,true)));
        }
        layersPage.Content=Scroll(layerTable);
      }

      var materialPage=new TabPage{Text="Materials map"};
      var leadingChoices=clipboard?new[]{UnassignedChoice}:new[]{ProjectMaterialChoice,UnassignedChoice};
      int presetOffset=leadingChoices.Length;
      bool PopulateMaterials(ImportSelection selection)
      {
        var used=selection.Workspace.Select(part=>part.Material)
          .Concat(selection.Tables.Select(table=>table.Material.Id)).ToHashSet();
        var rows=project.Materials.Values.Where(row=>used.Contains(row.Id))
          .OrderBy(row=>row.Name,StringComparer.OrdinalIgnoreCase).ThenBy(row=>row.Id).ToArray();
        if(rows.Length==0)
        {
          materialPage.Content=new Panel{Padding=new Eto.Drawing.Padding(DialogPadding),Content=new Label{Text="No assigned materials"}};
          return false;
        }
        var materialTable=Columns("PatternSmith material","Roll width","vNest material");
        var refreshChoices=new List<Action>();
        foreach(var row in rows)
        {
          var projectRecipe=ProjectRecipe(row,doc);
          var field=new DropDown();
          Material[] presets=[];
          bool updating=false;
          var refreshStyle=MaterialAppearance.Picker(field,choice=>choice>=presetOffset&&choice-presetOffset<presets.Length?presets[choice-presetOffset].PatternSmith:null);
          void RefreshChoices()
          {
            var selected=mapping.Materials[row.Id];
            presets=mapping.Available;
            int index=Array.IndexOf(presets,selected);
            if(index<0&&selected!=null&&selected!=projectRecipe)
            {
              index=Array.FindIndex(presets,value=>value.Name.Equals(selected.Name,StringComparison.OrdinalIgnoreCase));
              if(index<0){index=presets.Length;presets=[..presets,selected];}
              else mapping.Materials[row.Id]=presets[index];
            }
            updating=true;
            try
            {
              field.DataStore=leadingChoices.Concat(presets.Select(preset=>preset.Name)).ToArray();
              field.SelectedIndex=selected==null?presetOffset-1:index<0?0:index+presetOffset;
              refreshStyle();
            }
            finally{updating=false;}
          }
          field.SelectedIndexChanged+=(_,_)=>
          {
            if(!updating&&field.SelectedIndex is int choice&&choice>=0)
              mapping.Materials[row.Id]=choice>=presetOffset?presets[choice-presetOffset]:
                choice==presetOffset-1?null:projectRecipe;
          };
          refreshChoices.Add(RefreshChoices);RefreshChoices();
          var name=Name(row.Name,SourceColumnWidth);name.ToolTip=$"{row.Name}  |  ID: {row.Id}";
          string width=row.Width>0?row.Width.ToString("0.###",Invariant)+" in":"";
          var picker=MaterialAppearance.WithManager(field,doc,this,()=>
          {
            if(mapping.RefreshPresets())foreach(var refresh in refreshChoices)refresh();
          });
          picker.Width=DestinationMinimumWidth;
          materialTable.Rows.Add(new TableRow(name,Name(width,ToolColumnWidth),new TableCell(picker,true)));
        }
        materialPage.Content=Scroll(materialTable);
        return true;
      }

      var tabs=new TabControl{Pages={materialPage,layersPage}};
      var contents=new DropDown{Width=DestinationMinimumWidth,DataStore=ContentsLabels,SelectedIndex=(int)mapping.Contents,
        ToolTip="Unnested parts + nests skips workspace copies of nested patterns. All includes both the workspace patterns and their nested copies."};
      var empty=new CheckBox{Text="Include empty nests",Checked=mapping.EmptyTables,Visible=project.Tables.Any(table=>!table.HasCopies)};
      var status=new Label();
      var import=new Button{Text=clipboard?"Paste":"Import"};
      ToolLayer[] layerRows=[];
      void RefreshScope()
      {
        var selection=project.SelectContents(mapping.Contents,mapping.EmptyTables);
        empty.Enabled=mapping.Contents!=ImportContents.PartsOnly;
        layerRows=selection.UsedLayers;
        PopulateLayers(layerRows);
        if(!PopulateMaterials(selection)&&layerRows.Length>0)tabs.SelectedIndex=1;
        import.Enabled=selection.HasContent;
        status.Text=selection.HasContent&&clipboard?(project.EditorClipboard?
          CountText.Format(selection.Workspace.Sum(pattern=>pattern.Items.Count),"editor object"):CountText.Format(selection.Workspace.Length,"workspace pattern")):
          selection.HasContent?$"{CountText.Format(selection.Workspace.Length,"workspace pattern")} | "+
          $"{CountText.Format(selection.Tables.Length,"nest table")} | "+
          $"{CountText.Format(selection.Copies.Count(),"nested copy","nested copies")}":"No content matches this choice.";
      }
      RefreshScope();
      contents.SelectedIndexChanged+=(_,_)=>{if(contents.SelectedIndex>=0){mapping.Contents=(ImportContents)contents.SelectedIndex;RefreshScope();}};
      empty.CheckedChanged+=(_,_)=>{mapping.EmptyTables=empty.Checked==true;RefreshScope();};
      import.Click+=(_,_)=>
      {
        var paths=layerRows.Select(row=>mapping.Layers[row.Key]).ToArray();
        if(paths.Any(path=>path.Split(new[]{"::"},StringSplitOptions.None).Any(string.IsNullOrWhiteSpace)))
        {tabs.SelectedIndex=1;status.Text="Choose a valid Rhino layer on each row.";return;}
        Close(true);
      };
      var cancel=new Button{Text="Cancel"};cancel.Click+=(_,_)=>Close(false);
      DefaultButton=import;AbortButton=cancel;
      TableLayout Bar(params TableCell[] cells)=>new()
      {Spacing=new Eto.Drawing.Size(DialogPadding,0),Rows={new TableRow(cells)}};
      var filename=new Label{Text=ShortName(file,VisibleFilenameLimit),ToolTip=file,VerticalAlignment=VerticalAlignment.Center};
      var units=new Label{Text="Rhino units: "+doc.ModelUnitSystem,VerticalAlignment=VerticalAlignment.Center};
      var layout=new TableLayout
      {
        Spacing=new Eto.Drawing.Size(0,DialogPadding),
        Rows={new TableRow(new TableCell(Bar(new TableCell(filename,true),units),true))}
      };
      if(clipboard)
      {
        var placement=new DropDown{DataStore=PastePlacementLabels,SelectedIndex=(int)mapping.Placement,
          ToolTip="At cursor pastes where the cursor was when the command started. Pick point previews the objects and lets you snap or click."};
        placement.SelectedIndexChanged+=(_,_)=>{if(placement.SelectedIndex>=0)mapping.Placement=(PastePlacement)placement.SelectedIndex;};
        var photo=new CheckBox{Text="Paste background image",Checked=mapping.PasteBackground,
          ToolTip="Embed the background image if the local PatternSmith project or image file is available."};
        photo.CheckedChanged+=(_,_)=>mapping.PasteBackground=photo.Checked==true;
        layout.Rows.Add(new TableRow(new TableCell(Bar(new Label{Text="Placement",VerticalAlignment=VerticalAlignment.Center},
          placement,photo,new TableCell(null,true)),true)));
      }
      else layout.Rows.Add(new TableRow(new TableCell(Bar(new Label{Text="Contents",VerticalAlignment=VerticalAlignment.Center},
        contents,empty,new TableCell(null,true)),true)));
      layout.Rows.Add(new TableRow(new TableCell(tabs,true)){ScaleHeight=true});
      layout.Rows.Add(new TableRow(new TableCell(Bar(new TableCell(status,true),import,cancel),true)));
      Content=layout;
    }
    private static string ShortName(string value,int limit)=>value.Length>limit?value[..(limit-3)]+"...":value;
    private static Label Name(string name,int width)=>new()
    {Text=ShortName(name,VisibleNameLimit),ToolTip=name,Width=width,VerticalAlignment=VerticalAlignment.Center};
    private static TableLayout Columns(string first,string second,string third)=>new()
    {
      Padding=new Eto.Drawing.Padding(DialogPadding),Spacing=new Eto.Drawing.Size(MappingColumnGap,MappingRowGap),
      Rows={new TableRow(Name(first,SourceColumnWidth),Name(second,ToolColumnWidth),new TableCell(new Label{Text=third},true))}
    };
    private static Scrollable Scroll(TableLayout table)=>new()
    {
      Content=new TableLayout{Rows={new TableRow(new TableCell(table,true)),new TableRow(new TableCell(null,true)){ScaleHeight=true}}},
      Border=BorderType.None,ExpandContentWidth=true
    };
    private static Panel Swatch(Color color)=>new()
    {Width=SwatchSize,Height=SwatchSize,BackgroundColor=Eto.Drawing.Color.FromArgb(color.ToArgb())};
    private static Control WithSwatch(Panel swatch,Control value,bool stretch)=>new TableLayout
    {
      Spacing=new Eto.Drawing.Size(MappingRowGap,0),
      Rows={new TableRow(new StackLayout{VerticalContentAlignment=VerticalAlignment.Center,Items={swatch}},new TableCell(value,stretch))}
    };
    private static string ToolCaption(ToolLayer row)
    {
      if(row.Process is "Cut" or "Notch" or "Punch" or "Crease"&&row.Slot is 0 or 1)return $"{row.Process} [Steer{row.Slot+1}]";
      return row.Process=="DotMark"?"Dot mark":row.Process;
    }
  }

  private static Transform Placement(Point3d location,double angle,string flip,double scale,Point3d offset)
  {
    var reflection=Transform.Scale(Plane.WorldXY,flip=="X"?-1:1,flip=="Y"?-1:1,1);
    return Transform.Translation(offset-Point3d.Origin+(location-Point3d.Origin)*scale)*
      reflection*Transform.Rotation(angle,Vector3d.ZAxis,Point3d.Origin)*Transform.Scale(Point3d.Origin,scale);
  }

  private sealed record ImportResult(int Parts,int Tables,int Copies);
  private sealed record AddedPattern(string Key,Guid[] Objects);

  private static string[] TableInfoRows(Pattern pattern)=>pattern.Items.Select(item=>item.Geometry).OfType<TextEntity>()
    .SelectMany(text=>text.PlainText.Split(new[]{'\r','\n'},StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries)).ToArray();

  private static string TableMatchName(string name)
  {
    string clean=CleanName(name).Trim();
    return LegacyTableName.IsMatch(clean)?string.Concat(clean.Where(ch=>!char.IsWhiteSpace(ch))):clean;
  }

  private static void MatchNativeTableLabels(Project project)
  {
    if(project.Tables.Count==0)return;
    var names=project.Tables.Select(table=>TableMatchName(table.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var members=project.Tables.SelectMany(table=>table.Copies.Select(copy=>copy.Pattern)).ToHashSet();
    var labels=new Dictionary<string,List<(Pattern Pattern,string Name,string[] Rows)>>(StringComparer.OrdinalIgnoreCase);
    foreach(var pattern in project.Patterns)
    {
      if(members.Contains(pattern)||!TryTableInfoFrame(pattern,Transform.Identity,Math.Max(MinimumLengthInches,ConfiguredTableInfoRecognitionToleranceInches),names,out _,out _))continue;
      var rows=TableInfoRows(pattern);
      string key=pattern.Name.EndsWith(TableInfoSuffix,StringComparison.OrdinalIgnoreCase)?
        TableMatchName(pattern.Name[..^TableInfoSuffix.Length]):string.Empty;
      var matching=rows.Where(row=>names.Contains(TableMatchName(row))).GroupBy(TableMatchName,StringComparer.OrdinalIgnoreCase).ToArray();
      string? name=matching.FirstOrDefault(group=>group.Key.Equals(key,StringComparison.OrdinalIgnoreCase))?.First();
      if(name==null&&matching.Length==1){key=matching[0].Key;name=matching[0].First();}
      if(name==null||key.Length==0)continue;
      if(!labels.TryGetValue(key,out var candidates))labels.Add(key,candidates=[]);
      candidates.Add((pattern,name,rows));
    }
    var unique=project.Tables.Select((table,index)=>(Table:table,Index:index)).GroupBy(entry=>TableMatchName(entry.Table.Name),StringComparer.OrdinalIgnoreCase)
      .Where(group=>group.Count()==1).Select(group=>group.Single()).ToArray();
    foreach(var (table,index) in unique)
    {
      if(!labels.TryGetValue(TableMatchName(table.Name),out var matched)||matched.Count!=1)continue;
      var (header,name,rows)=matched[0];
      var texts=rows.Where(row=>!TableMatchName(row).Equals(TableMatchName(name),StringComparison.OrdinalIgnoreCase)&&
        !LegacyPartCount.IsMatch(row)&&!LegacyYardage.IsMatch(row)&&!TableUsedSize.IsMatch(row)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
      (double Length,double Width)? size=null;
      var measures=rows.Select(row=>TableUsedSize.Match(row)).Where(match=>match.Success).Take(2).ToArray();
      if(measures.Length==1&&TryLabelNumber(measures[0].Groups[1].Value,out double length)&&
        TryLabelNumber(measures[0].Groups[2].Value,out double width))size=(length,width);
      var material=table.Material;
      if(texts.Length==1&&!material.Name.Equals(texts[0],StringComparison.OrdinalIgnoreCase))
      {
        var known=project.Materials.Values.Where(value=>value.Name.Equals(texts[0],StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        string id=known.Length==1?known[0].Id:Guid.NewGuid().ToString("N");
        material=material with {Id=id,Name=known.Length==1?known[0].Name:texts[0]};
        project.Materials.TryAdd(id,material);
      }
      project.Tables[index]=table with {Name=name,Material=material,FaceDown=table.FaceDown||IsFaceDown(name),LabelPattern=header,LabelUsedSize=size};
    }
  }

  private static bool TryLabelNumber(string text,out double value)=>
    double.TryParse(text.Replace(',','.'),NumberStyles.AllowDecimalPoint,Invariant,out value)&&double.IsFinite(value)&&value>0;

  private static bool TryTableInfoFrame(Pattern pattern,Transform pose,double tolerance,IReadOnlySet<string> tableNames,
    out Item? border,out Rectangle3d frame)
  {
    border=null;frame=Rectangle3d.Unset;
    var nonText=pattern.Items.Where(item=>item.Geometry is not TextEntity).Take(2).ToArray();
    if(nonText.Length!=1||nonText[0].Geometry is not Curve curve||!curve.IsClosed||
       !curve.TryGetPolyline(out var corners)||corners.Count!=5)return false;
    var labels=TableInfoRows(pattern);
    if(labels.Length<2)return false;
    bool modern=false;
    if(!pattern.Nestable&&pattern.Name.EndsWith(TableInfoSuffix,StringComparison.OrdinalIgnoreCase))
    {
      var name=TableMatchName(pattern.Name[..^TableInfoSuffix.Length]);
      modern=tableNames.Contains(name)||labels.Any(label=>TableMatchName(label).Equals(name,StringComparison.OrdinalIgnoreCase))&&
        labels.Any(LegacyYardage.IsMatch);
    }
    bool legacy=labels.Any(label=>LegacyTableName.IsMatch(CleanName(label)))&&
      labels.Any(LegacyPartCount.IsMatch)&&labels.Any(LegacyYardage.IsMatch);
    if(!modern&&!legacy)return false;

    var origin=pose*corners[0];var right=pose*corners[1];var far=pose*corners[2];var up=pose*corners[3];
    var basis=new Plane(origin,right-origin,up-origin);
    double width=origin.DistanceTo(right),height=origin.DistanceTo(up);
    if(!basis.IsValid||width<=tolerance||height<=tolerance||
       far.DistanceTo(basis.PointAt(width,height))>tolerance||up.DistanceTo(basis.PointAt(0,height))>tolerance)
      return false;
    frame=new Rectangle3d(basis,new Interval(0,width),new Interval(0,height));
    border=nonText[0];
    return true;
  }

  private static Rectangle3d FitTableInfoFrame(Rectangle3d frame,IEnumerable<TextEntity> texts,double tolerance)
  {
    var glyphs=BoundingBox.Empty;double height=0;
    foreach(var text in texts)
    {
      height=Math.Max(height,text.TextHeight);
      var strokes=text.Explode();
      try
      {
        var bounds=BoundingBox.Empty;
        foreach(var stroke in strokes??[])bounds.Union(stroke.GetBoundingBox(frame.Plane));
        glyphs.Union(bounds.IsValid?bounds:text.GetBoundingBox(frame.Plane));
      }
      finally{foreach(var stroke in strokes??[])stroke.Dispose();}
    }
    if(!glyphs.IsValid)return frame;
    double padding=Math.Max(tolerance,height*Math.Max(0,ConfiguredTableInfoTextPadding));
    Interval Fit(Interval previous,double min,double max)
    {
      double low=min-padding,high=max+padding;
      if(low>=previous.Min&&high<=previous.Max)return previous;
      double size=Math.Max(previous.Length,high-low);
      double start=Math.Min(low,Math.Max(previous.Min,high-size));
      return new Interval(start,start+size);
    }
    return new Rectangle3d(frame.Plane,
      Fit(frame.X,glyphs.Min.X,glyphs.Max.X),Fit(frame.Y,glyphs.Min.Y,glyphs.Max.Y));
  }

  private static Vector3d TableInfoShift(BoundingBox frame,IReadOnlyList<BoundingBox> obstacles,double clearance)
  {
    double Slide(bool xAxis,int direction)
    {
      var blocked=new List<Interval>();
      foreach(var box in obstacles)
      {
        bool sharesBand=xAxis
          ?frame.Min.Y<box.Max.Y+clearance&&frame.Max.Y>box.Min.Y-clearance
          :frame.Min.X<box.Max.X+clearance&&frame.Max.X>box.Min.X-clearance;
        if(!sharesBand)continue;
        double low=(xAxis?box.Min.X-frame.Max.X:box.Min.Y-frame.Max.Y)-clearance;
        double high=(xAxis?box.Max.X-frame.Min.X:box.Max.Y-frame.Min.Y)+clearance;
        blocked.Add(direction>0?new Interval(low,high):new Interval(-high,-low));
      }
      double distance=0;
      foreach(var span in blocked.OrderBy(span=>span.T0))
      {
        if(span.T0>=distance)break;
        distance=Math.Max(distance,span.T1);
      }
      return direction*distance;
    }

    return new[]{new Vector3d(Slide(true,1),0,0),new Vector3d(Slide(true,-1),0,0),
      new Vector3d(0,Slide(false,1),0),new Vector3d(0,Slide(false,-1),0)}.MinBy(shift=>shift.SquareLength);
  }

  private static TextEntity PrepareText(RhinoDoc doc,TextEntity source,Transform pose,double scale,bool reverseX,bool preserveFont)
  {
    var text=(TextEntity)source.Duplicate();
    try
    {
      // Attaching a document style can replace the unstyled text's height.
      double height=source.TextHeight*scale;
      var plane=text.Plane;plane.Origin=pose*plane.Origin;
      var axis=pose*plane.XAxis;if(reverseX)axis=-axis;
      if(!axis.Unitize())throw new InvalidDataException("Could not orient imported text.");
      text.Plane=new Plane(plane.Origin,axis,Vector3d.CrossProduct(Vector3d.ZAxis,axis));
      text.ParentDimensionStyle=doc.DimStyles.Current;text.DimensionStyleId=doc.DimStyles.Current.Id;
      if(!AnnotationTextTransform.ApplyFixedDisplayTextHeight(doc,text,doc.Views.ActiveView?.ActiveViewport,height,1))
        throw new InvalidDataException("Could not size imported text.");
      if(preserveFont)
      {
        text.Font=source.Font;
        text.TextHorizontalAlignment=TextHorizontalAlignment.Center;
        text.TextVerticalAlignment=TextVerticalAlignment.Middle;
      }
      return text;
    }
    catch{text.Dispose();throw;}
  }

  private static GeometryBase PrepareGeometry(RhinoDoc doc,Item item,Transform pose,double scale,bool reverseX)
  {
    if(item.Geometry is TextEntity text)return PrepareText(doc,text,pose,scale,reverseX,item.Kind=="Font");
    var geometry=item.Geometry.Duplicate();
    try
    {
      if(!geometry.Transform(pose))throw new InvalidDataException("Could not transform imported "+item.Kind+".");
      return geometry;
    }
    catch{geometry.Dispose();throw;}
  }

  private static Point3d? CapturePasteCursor(RhinoDoc doc)
  {
    if(doc.IsHeadless)return null;
    var cursor=System.Windows.Forms.Cursor.Position;
    var view=doc.Views.ActiveView;
    if(view==null||!view.ScreenRectangle.Contains(cursor))
      view=doc.Views.GetViewList(ViewTypeFilter.Model).FirstOrDefault(candidate=>candidate.ScreenRectangle.Contains(cursor))??view;
    if(view?.ActiveViewport is not {} viewport)return null;
    var bounds=viewport.Bounds;
    var pixel=view.ScreenToClient(cursor);
    if(!bounds.Contains(pixel))pixel=new System.Drawing.Point(bounds.Left+bounds.Width/2,bounds.Top+bounds.Height/2);
    var ray=viewport.ClientToWorld(pixel);
    var plane=viewport.ConstructionPlane();
    if(ray.IsValid&&Rhino.Geometry.Intersect.Intersection.LinePlane(ray,plane,out double parameter)&&ray.PointAt(parameter) is {IsValid:true} hit)
      return hit;
    var center=plane.ClosestPoint(viewport.CameraTarget);
    return center.IsValid?center:null;
  }

  private sealed record PhotoAsset(byte[] Data,string Extension,int PixelWidth,int PixelHeight);
  private sealed record PlacedPhoto(PhotoAsset Asset,Rectangle3d Frame,double Opacity);

  private static string CachePhoto(PhotoAsset asset,string directory)
  {
    Directory.CreateDirectory(directory);
    byte[] hash=System.Security.Cryptography.SHA256.HashData(asset.Data);
    string path=Path.Combine(directory,Convert.ToHexString(hash)+asset.Extension);
    if(File.Exists(path))
    {
      using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete);
      if(file.Length==asset.Data.LongLength&&System.Security.Cryptography.SHA256.HashData(file).AsSpan().SequenceEqual(hash))
        return path;
    }

    // Keep the Picture's backing file available for viewport reloads and Save Textures.
    string staging=Path.Combine(directory,Guid.NewGuid().ToString("N")+".tmp");
    bool created=false;
    try
    {
      using(var file=new FileStream(staging,FileMode.CreateNew,FileAccess.Write,FileShare.None))
      {created=true;file.Write(asset.Data);}
      File.Move(staging,path,overwrite:true);
      return path;
    }
    finally{if(created)File.Delete(staging);}
  }

  private static string? LocalPhotoPath(string path,bool archive)
  {
    // A clipboard reference must not initiate network access or traverse a link.
    if(path.Length<3||!char.IsAsciiLetter(path[0])||path[1]!=':'||path[2]!='\\'||path.IndexOf(':',2)>=0)return null;
    string full=Path.GetFullPath(path);
    if(new DriveInfo(Path.GetPathRoot(full)!).DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram))return null;
    string extension=Path.GetExtension(full).ToLowerInvariant();
    if(archive?extension!=".project":extension is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff"))return null;
    var components=new Stack<FileSystemInfo>();
    var image=new FileInfo(full);
    components.Push(image);
    for(var parent=image.Directory;parent!=null;parent=parent.Parent)components.Push(parent);
    while(components.TryPop(out var component))
      if(!component.Exists||(component.Attributes&FileAttributes.ReparsePoint)!=0)return null;
    return full;
  }

  private static byte[] ReadPhotoBytes(PhotoSource source)
  {
    static byte[] ReadImage(Stream input,long count)
    {
      if(count<=0||count>MaximumBackgroundImageBytes)throw new InvalidDataException("PatternSmith background image size is unsupported.");
      var bytes=new byte[(int)count];
      input.ReadExactly(bytes);
      return bytes;
    }
    if(source.Id!=Guid.Empty&&LocalPhotoPath(source.ProjectFile,archive:true) is {} project)
    {
      using var file=new FileStream(project,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
      using var archive=new ZipArchive(file,ZipArchiveMode.Read);
      if(archive.Entries.Count>MaximumArchiveEntries)throw new InvalidDataException("Too many entries in the PatternSmith project.");
      string name="images/"+source.Id.ToString("D");
      var entries=archive.Entries.Where(entry=>entry.FullName.Equals(name,StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
      if(entries.Length==1)
      {
        using var image=entries[0].Open();
        return ReadImage(image,entries[0].Length);
      }
    }
    if(LocalPhotoPath(source.ImageFile,archive:false) is {} imagePath)
    {
      using var file=new FileStream(imagePath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
      return ReadImage(file,file.Length);
    }
    throw new FileNotFoundException("The referenced PatternSmith background image could not be found in a local project or file.");
  }

  private static (int Width,int Height,string Extension) DescribePhoto(byte[] bytes)
  {
    using var input=new MemoryStream(bytes,writable:false);
    using var image=System.Drawing.Image.FromStream(input,useEmbeddedColorManagement:false,validateImageData:false);
    int width=image.Width,height=image.Height;
    if(width<=0||height<=0||(long)width*height>MaximumBackgroundPixels)
      throw new InvalidDataException("PatternSmith background resolution is unsupported.");
    var format=image.RawFormat;
    var extension=format.Equals(System.Drawing.Imaging.ImageFormat.Jpeg)?".jpg":format.Equals(System.Drawing.Imaging.ImageFormat.Png)?".png":
      format.Equals(System.Drawing.Imaging.ImageFormat.Bmp)?".bmp":format.Equals(System.Drawing.Imaging.ImageFormat.Gif)?".gif":
      format.Equals(System.Drawing.Imaging.ImageFormat.Tiff)?".tiff":throw new InvalidDataException("Unsupported PatternSmith background image format.");
    return (width,height,extension);
  }

  private static Rectangle3d PhotoFrame(BackgroundPhoto photo,int pixelWidth,int pixelHeight,Transform pose,double scale)
  {
    double width=pixelWidth*photo.Scale,height=pixelHeight*photo.Scale;
    if(!double.IsFinite(width)||!double.IsFinite(height)||width<=MinimumLengthInches||height<=MinimumLengthInches)
      throw new InvalidDataException("PatternSmith background calibration is invalid.");
    var x=new Vector3d(Math.Cos(photo.Angle),Math.Sin(photo.Angle),0);
    var y=Vector3d.CrossProduct(Vector3d.ZAxis,x);
    var frame=new Plane(photo.Center-x*(width*.5)-y*(height*.5),x,y);
    if(!frame.Transform(pose))throw new InvalidDataException("Could not transform the PatternSmith background image.");
    return new Rectangle3d(frame,width*scale,height*scale);
  }

  private sealed class ClipboardPlacement : IDisposable
  {
    private readonly Dictionary<Item,(GeometryBase Geometry,Color Color)> _items=new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Pattern,PlacedPhoto> _photos=new(ReferenceEqualityComparer.Instance);
    private readonly Point3d _anchor;
    private Vector3d _offset;
    internal int Count=>_items.Count;

    internal ClipboardPlacement(RhinoDoc doc,Project project,Pattern[] patterns,ImportMappings maps)
    {
      var bounds=BoundingBox.Empty;
      var colors=new Dictionary<string,Color>(StringComparer.OrdinalIgnoreCase);
      double scale=InchesToModel(doc);
      if(!double.IsFinite(scale)||scale<=0)throw new InvalidOperationException("Unsupported Rhino model units.");
      try
      {
        foreach(var pattern in patterns)
        {
          var pose=Placement(pattern.Location,pattern.Angle,"None",scale,Point3d.Origin);
          foreach(var item in pattern.Items)
          {
            string layer=maps.Layers[item.Layer.Key];
            if(!colors.TryGetValue(layer,out var color))
            {
              int index=doc.Layers.FindByFullPath(layer,-1);
              colors.Add(layer,color=index>=0?doc.Layers[index].Color:item.Layer.Color);
            }
            var geometry=PrepareGeometry(doc,item,pose,scale,false);
            _items.Add(item,(geometry,color));
            bounds.Union(geometry.GetBoundingBox(true));
          }
        }
        if(!bounds.IsValid)throw new InvalidDataException("Could not measure the clipboard geometry.");
        _anchor=bounds.Center;
        if(maps.PasteBackground)PreparePhotos(project,patterns,scale);
      }
      catch{Dispose();throw;}
    }

    internal void PlaceAt(Point3d target)=>_offset=target-_anchor;
    internal Point3d PlacedPoint(Point3d point)=>point+_offset;
    internal PlacedPhoto? PhotoFor(Pattern pattern)=>_photos.GetValueOrDefault(pattern);

    private void PreparePhotos(Project project,Pattern[] patterns,double scale)
    {
      var assets=new Dictionary<PhotoSource,PhotoAsset>();
      long bytesTotal=0;
      foreach(var pattern in patterns)
      {
        if(pattern.Photo is not {} photo)continue;
        try
        {
          if(!assets.TryGetValue(photo.Source,out var asset))
          {
            byte[] bytes=ReadPhotoBytes(photo.Source);
            if(bytes.LongLength>MaximumClipboardBackgroundBytes-bytesTotal)
              throw new InvalidDataException("The PatternSmith background images exceed the paste size limit.");
            var media=DescribePhoto(bytes);
            bytesTotal+=bytes.LongLength;
            assets.Add(photo.Source,asset=new PhotoAsset(bytes,media.Extension,media.Width,media.Height));
          }
          var pose=Placement(pattern.Location,pattern.Angle,"None",scale,Point3d.Origin);
          _photos.Add(pattern,new(asset,PhotoFrame(photo,asset.PixelWidth,asset.PixelHeight,pose,scale),photo.Opacity));
        }
        catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or
          System.Runtime.InteropServices.ExternalException)
        {
          Log.Write(nameof(vPasteFromPatternSmith),"background omitted for {0}: {1}",pattern.Name,ex.Message);
          project.Warn("background images could not be read");
        }
      }
    }

    internal Result PickPoint(Point3d? initial)
    {
      using var get=new GetPoint();
      get.SetCommandPrompt(initial.HasValue?"Place PatternSmith objects (Enter for initial cursor position)":"Place PatternSmith objects");
      get.AcceptNothing(initial.HasValue);
      get.DynamicDraw+=(_,e)=>
      {
        if(!e.CurrentPoint.IsValid)return;
        e.Display.PushModelTransform(Transform.Translation(e.CurrentPoint-_anchor));
        try
        {
          foreach(var photo in _photos.Values)e.Display.DrawPolyline(photo.Frame.ToPolyline(),Color.Gray,1);
          foreach(var (geometry,color) in _items.Values)
          {
            if(geometry is Curve curve)e.Display.DrawCurve(curve,color);
            else if(geometry is TextEntity text)e.Display.DrawText(text,color);
            else if(geometry is Rhino.Geometry.Point point)e.Display.DrawPoint(point.Location,color);
          }
        }
        finally{e.Display.PopModelTransform();}
      };
      var result=get.Get();
      if(result==GetResult.Point){PlaceAt(get.Point());return Result.Success;}
      if(result==GetResult.Nothing&&initial is {} cursor){PlaceAt(cursor);return Result.Success;}
      return get.CommandResult();
    }

    internal GeometryBase DuplicatePlaced(Item item)
    {
      var geometry=_items[item].Geometry.Duplicate();
      try
      {
        if(_offset.SquareLength>0)
        {
          if(geometry is TextEntity text){var plane=text.Plane;plane.Origin+=_offset;text.Plane=plane;}
          else if(!geometry.Translate(_offset))throw new InvalidDataException("Could not place imported "+item.Kind+".");
        }
        return geometry;
      }
      catch{geometry.Dispose();throw;}
    }

    public void Dispose()
    {
      foreach(var (geometry,_) in _items.Values)geometry.Dispose();
      _items.Clear();
      _photos.Clear();
    }
  }

  private static ObjectAttributes ImportedNotchAttributes(RhinoDoc doc,Item item,Guid sourceId,int layer,
    Transform pose,double scale,ClipboardPlacement? paste,Guid match)
  {
    var notch=item.Notch!;
    var anchor=pose*notch.SewAnchor;
    if(paste!=null)anchor=paste.PlacedPoint(anchor);
    if(doc.Objects.FindId(sourceId)?.Geometry is not Curve source||!source.ClosestPoint(anchor,out double parameter))
      throw new InvalidDataException("Could not bind an imported PatternSmith notch to its curve.");
    double station=parameter<=source.Domain.T0?0:parameter>=source.Domain.T1?source.GetLength():
      source.GetLength(new Interval(source.Domain.T0,parameter));
    var tangent=source.TangentAt(parameter);
    var direction=pose*notch.CutAnchor-pose*notch.SewAnchor;
    double offset=direction.Length;
    if(offset<=RhinoMath.ZeroTolerance)
    {
      offset=0;direction=MatchingNotches.IsExternalType(notch.Type)?-notch.Direction:notch.Direction;
      direction.Transform(pose);
    }
    string side=Vector3d.CrossProduct(Vector3d.ZAxis,tangent)*direction>=0?"Left":"Right";
    string layerName=doc.Layers[layer].FullPath;
    var attributes=MatchingNotches.Attributes(source,sourceId,0,notch.Mode,station,notch.Depth*scale,offset,notch.Type,notch.Width*scale,
      side,layerName,tangent,layer);
    MatchingNotches.BindReference(doc,attributes,source,station);
    MatchingNotches.SetMember(attributes,attributes.ObjectId,match,"notch");
    var prefix=MatchingNotches.Prefix;
    attributes.SetUserString(MatchingNotches.SourceCurveIdsKey,System.Text.Json.JsonSerializer.Serialize(new[]{sourceId}));
    attributes.SetUserString(prefix+"source_reversed","[false]");
    attributes.SetUserString(prefix+"both_sides","0");
    if(notch.Inline)
      attributes.SetUserString("PatternSmith.notchExecutionStyle","Inline");
    if(notch.Components>1)
    {
      attributes.SetUserString(prefix+"component_set",match.ToString("N"));
      attributes.SetUserString(prefix+"component_index",item.NotchComponent.ToString(Invariant));
      attributes.SetUserString(prefix+"component_count",notch.Components.ToString(Invariant));
    }
    return attributes;
  }

  private static ImportResult WriteProject(RhinoDoc doc,Project project,ImportMappings maps,ImportSelection selection,bool opening,
    ClipboardPlacement? paste=null)
  {
    var added=new List<Guid>();var groups=new List<int>();var layers=new List<int>();var partKeys=new List<string>();
    int partCount=0,copyCount=0;string session=Guid.NewGuid().ToString("N");
    var original=new Dictionary<Pattern,AddedPattern>();
    var sources=selection.Workspace.Where(pattern=>pattern.State!=null)
      .GroupBy(pattern=>pattern.State!.Source,StringComparer.Ordinal).Select(group=>
      {
        var standalone=group.Where(pattern=>!pattern.State!.Placed).Take(2).ToArray();
        return (group.Key,Pattern:standalone.Length switch {0=>group.First(),1=>standalone[0],_=>null});
      }).Where(source=>source.Pattern!=null).ToDictionary(source=>source.Key,source=>source.Pattern!,StringComparer.Ordinal);
    if(!selection.HasContent)throw new InvalidDataException("No content to import.");
    double scale=InchesToModel(doc);
    if(!double.IsFinite(scale)||scale<=0)throw new InvalidOperationException("Unsupported Rhino model units.");
    uint undo=doc.BeginUndoRecord(paste==null?nameof(vImportPatternSmith):nameof(vPasteFromPatternSmith));
    bool initialModified=doc.Modified;
    try
    {
      var mappedLayerIds=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
      int GetLayerPath(string path,Color color)
      {
        if(mappedLayerIds.TryGetValue(path,out int layer))return layer;
        int parent=-1;string portion="";
        foreach(string name in path.Split(new[]{"::"},StringSplitOptions.None))
        {
          if(string.IsNullOrWhiteSpace(name))throw new InvalidOperationException("Invalid layer path: "+path);
          portion=portion.Length==0?name:portion+"::"+name;
          layer=doc.Layers.FindByFullPath(portion,-1);
          if(layer<0)
          {
            using var created=new Layer{Name=name,Color=color};
            if(parent>=0)created.ParentLayerId=doc.Layers[parent].Id;
            layer=doc.Layers.Add(created);
            if(layer<0)throw new InvalidOperationException("Could not create layer "+portion+".");
            layers.Add(layer);
          }
          parent=layer;
        }
        mappedLayerIds.Add(path,parent);return parent;
      }
      int GetLayer(Item item)=>GetLayerPath(maps.Layers[item.Layer.Key],item.Layer.Color);
      void AddBackground(Pattern pattern)
      {
        if(paste?.PhotoFor(pattern) is not {} photo)return;
        Guid id=Guid.Empty;
        try
        {
          int layer=GetLayerPath(BackgroundLayerName,Color.Gray);
          var plane=photo.Frame.Plane;plane.Origin=paste.PlacedPoint(plane.Origin);
          string local=System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
          if(string.IsNullOrEmpty(local))throw new InvalidOperationException("The local texture cache is unavailable.");
          string path=CachePhoto(photo.Asset,Path.Combine(local,"vTools",BackgroundCacheFolder));
          id=doc.Objects.AddPictureFrame(plane,path,asMesh:false,photo.Frame.Width,photo.Frame.Height,
            selfIllumination:true,embedBitmap:true);
          if(id==Guid.Empty||doc.Objects.FindId(id) is not {} picture)
            throw new InvalidOperationException("Rhino refused the PatternSmith picture.");
          using var attributes=picture.Attributes.Duplicate();
          attributes.LayerIndex=layer;attributes.Name=BackgroundLayerName;
          attributes.DisplayOrder=BackgroundDrawOrder;attributes.WireDensity=BackgroundWireDensity;
          if(!doc.Objects.ModifyAttributes(id,attributes,true))
            throw new InvalidOperationException("Could not place the PatternSmith picture on its background layer.");
          if(photo.Opacity<1&&attributes.MaterialIndex>=0&&doc.Materials[attributes.MaterialIndex] is {} material)
          {
            material.Transparency=1-photo.Opacity;
            material.CommitChanges();
          }
          added.Add(id);
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
          if(id!=Guid.Empty)doc.Objects.Delete(id,true);
          Log.Write(nameof(vPasteFromPatternSmith),"background omitted for {0}: {1}",pattern.Name,ex.Message);
          project.Warn("background images could not be added to Rhino");
        }
      }
      int NewGroup()
      {
        int group=doc.Groups.Add();
        if(group<0)throw new InvalidOperationException("Could not create an imported part group.");
        groups.Add(group);return group;
      }
      BoundingBox imported=BoundingBox.Empty;
      var documentBounds=doc.Objects.BoundingBox;
      var ordinals=new Dictionary<string,int>(StringComparer.Ordinal);
      BoundingBox AddPart(Pattern pattern,Transform pose,int tableGroup=-1,Copy? copy=null,Material? tableMaterial=null,bool tableFaceDown=false,
        int tableIndex=-1)
      {
        int group=project.EditorClipboard?-1:NewGroup();
        string key=group<0?"":PartGroupData.GroupKey(doc.Groups[group].Id);
        var ids=new List<Guid>();var bounds=BoundingBox.Empty;var settings=tableMaterial??maps.Materials.GetValueOrDefault(pattern.Material);
        bool hasNotches=pattern.Items.Any(item=>item.Notch!=null);
        var seamItems=pattern.Items.Where(item=>item.SeamSources!=null).ToArray();
        var itemIds=hasNotches||seamItems.Length>0?new Dictionary<Item,Guid>(ReferenceEqualityComparer.Instance):null;
        var notchGroups=hasNotches?new Dictionary<ImportedNotch,Guid>(ReferenceEqualityComparer.Instance):null;
        bool faceDown=pattern.State?.CuttingFaceDown??(pattern.FaceDown||pattern.State==null&&tableFaceDown);
        bool mirrored=copy is {Flip:not "None"};
        var origin=copy!=null?original.GetValueOrDefault(pattern.State is {} linked?sources.GetValueOrDefault(linked.Source,pattern):pattern):null;
        var sourceKey=origin?.Key??pattern.Key;
        int copyNumber=copy==null?0:ordinals[sourceKey]=ordinals.GetValueOrDefault(sourceKey)+1;
        if(group>=0)
        {
          partKeys.Add(key);
          NestWorkflow.WriteImportedPartData(doc,key,CleanName(pattern.Name),settings,copy==null?
            Math.Clamp(pattern.Copies,NestWorkflow.ConfiguredDefaultCopies,NestWorkflow.ConfiguredMaximumCopies):NestWorkflow.ConfiguredDefaultCopies,
            copy==null&&(pattern.State?.Settings?.StrictCopies??pattern.Strict),faceDown,pattern.Nestable,pattern.State?.Settings,scale,pattern.RotationStep);
        }
        foreach(var item in pattern.Items)
        {
          using var geometry=paste?.DuplicatePlaced(item)??PrepareGeometry(doc,item,pose,scale,mirrored&&copy!.Flip=="X");
          int layer=GetLayer(item);
          if(item.Notch is {} notch&&!notchGroups!.ContainsKey(notch))notchGroups.Add(notch,Guid.NewGuid());
          using var attributes=item.Notch is {} mark
            ?ImportedNotchAttributes(doc,item,itemIds![mark.Source],layer,pose,scale,paste,notchGroups![mark])
            :new ObjectAttributes{LayerIndex=layer};
          if(group>=0)
          {
            attributes.AddToGroup(group);
            attributes.SetUserString("PatternSmith.patternId",pattern.Key);
            attributes.SetUserString("PatternSmith.allowNesting",pattern.Nestable.ToString());
            attributes.SetUserString(PartGroupData.FaceDownKey,faceDown.ToString());
          }
          if(pattern.Material.Length>0)attributes.SetUserString(PatternSmithPartContract.MaterialEnforcedKey,pattern.MaterialEnforced.ToString());
          attributes.SetUserString("PatternSmith.sourceLayer",item.Layer.Name);
          attributes.SetUserString("PatternSmith.tool",item.Layer.Tool);
          if(item.Template.Length>0)attributes.SetUserString("PatternSmith.entityId",item.Template);
          if(tableGroup>=0)
          {
            attributes.AddToGroup(tableGroup);
            attributes.SetUserString("vNest.session",session);
            attributes.SetUserString("vNest.table",(tableIndex+1).ToString(Invariant));
            attributes.SetUserString("vNest.part",origin?.Key??group.ToString(Invariant));
            attributes.SetUserString("vNest.instance",copyCount.ToString(Invariant));
            attributes.SetUserString("vNest.copy",copyNumber.ToString(Invariant));
            attributes.SetUserString("vNest.rotation",NestWorkflow.NormalizeRotation(RhinoMath.ToDegrees(copy!.Angle)+(copy.Flip=="X"?180:0)).ToString("R",Invariant));
            attributes.SetUserString("vNest.flipped",mirrored.ToString());
            attributes.SetUserString("vNest.material",settings?.Name);
            if(origin!=null)
            {
              attributes.SetUserString(PartGroupData.SourceSettingsKey,origin.Key);
              attributes.SetUserString("vNest.sourceObjects",System.Text.Json.JsonSerializer.Serialize(origin.Objects));
            }
          }
          Guid id=geometry is TextEntity annotation?doc.Objects.AddText(annotation,attributes):doc.Objects.Add(geometry,attributes);
          if(id==Guid.Empty)throw new InvalidOperationException("Rhino refused imported "+item.Kind+" geometry.");
          ids.Add(id);added.Add(id);
          itemIds?.Add(item,id);
          if(item.Notch!=null&&id!=attributes.ObjectId)
          {
            using var updated=doc.Objects.FindId(id)!.Attributes.Duplicate();
            MatchingNotches.SetMember(updated,id,notchGroups![item.Notch],"notch");
            if(!doc.Objects.ModifyAttributes(id,updated,true))throw new InvalidOperationException("Could not save imported notch metadata.");
          }
          var box=geometry.GetBoundingBox(true);bounds.Union(box);
        }
        if(seamItems.Length>0)
        {
          var members=seamItems.SelectMany(item=>item.SeamSources!).DistinctBy(member=>member.Source,ReferenceEqualityComparer.Instance).ToArray();
          var widths=members.ToDictionary(member=>itemIds![member.Source],member=>Math.Abs(member.SignedWidthInches)*scale);
          var styles=members.ToDictionary(member=>itemIds![member.Source],member=>
            new vSeam.ImportedStyle(member.SignedWidthInches*scale*(mirrored?-1:1),member.Start,member.End));
          var boundary=pattern.Items.Where(item=>item.BoundaryCandidate)
            .ToDictionary(item=>itemIds![item],item=>item.Layer.Process=="Cut");
          var allowances=seamItems.Select(item=>new vSeam.ImportedAllowance(itemIds![item],
            item.SeamSources!.Select(member=>itemIds[member.Source]).ToArray())).ToArray();
          vSeam.RegisterImportedAllowances(doc,widths,boundary,allowances,group<0?NewGroup:null,styles);
        }
        if(copy==null)
        {
          AddBackground(pattern);
          if(group>=0){original.Add(pattern,new(key,ids.ToArray()));partCount++;}
          imported.Union(bounds);
        }
        return bounds;
      }
      var tableInfo=new List<(Pattern Pattern,Transform Pose,Item Border,Rectangle3d Frame)>();
      var occupiedWorkspace=new List<BoundingBox>();
      var tableNames=project.Tables.Select(table=>TableMatchName(table.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
      foreach(var pattern in selection.Workspace)
      {
        var pose=Placement(pattern.Location,pattern.Angle,"None",scale,Point3d.Origin);
        if(paste==null&&TryTableInfoFrame(pattern,pose,doc.ModelAbsoluteTolerance,tableNames,out var border,out var frame))
          tableInfo.Add((pattern,pose,border!,frame));
        else
        {
          var box=AddPart(pattern,pose);
          if(box.IsValid)occupiedWorkspace.Add(box);
        }
      }
      if(!opening&&tableInfo.Count>0)
      {
        var newIds=added.ToHashSet();
        foreach(var obj in doc.Objects.GetObjectList(ObjectType.AnyObject))
          if(obj.Visible&&!newIds.Contains(obj.Id)&&obj.Geometry.GetBoundingBox(false) is {IsValid:true} box)
            occupiedWorkspace.Add(box);
      }
      foreach(var entry in tableInfo)
      {
        AddPart(entry.Pattern,entry.Pose);
        var members=original[entry.Pattern].Objects.Select(doc.Objects.FindId).OfType<RhinoObject>().ToArray();
        var texts=members.Where(item=>item.Geometry is TextEntity).ToArray();
        var sized=FitTableInfoFrame(entry.Frame,texts.Select(item=>(TextEntity)item.Geometry),doc.ModelAbsoluteTolerance);
        using var outline=sized.ToNurbsCurve();
        var shift=TableInfoShift(outline.GetBoundingBox(true),occupiedWorkspace,Math.Max(0,ConfiguredTableInfoClearanceInches)*scale);
        if(shift.SquareLength>0)
        {
          if(!outline.Translate(shift))throw new InvalidDataException("Could not position imported table information.");
          foreach(var item in texts)
          {
            using var moved=(TextEntity)item.Geometry.Duplicate();
            var plane=moved.Plane;plane.Origin+=shift;moved.Plane=plane;
            if(!doc.Objects.Replace(item.Id,moved,false))throw new InvalidDataException("Could not move an imported table label.");
          }
        }
        if(!sized.X.Equals(entry.Frame.X)||!sized.Y.Equals(entry.Frame.Y)||shift.SquareLength>0)
        {
          var borderId=original[entry.Pattern].Objects[entry.Pattern.Items.IndexOf(entry.Border)];
          if(!doc.Objects.Replace(borderId,outline,false))throw new InvalidDataException("Could not resize an imported table-info frame.");
        }
        var box=outline.GetBoundingBox(true);
        if(box.IsValid){occupiedWorkspace.Add(box);imported.Union(box);}
      }
      if(!opening&&documentBounds.IsValid)imported.Union(documentBounds);
      double clearance=NestWorkflow.ImportTableSpacing(scale,AutomaticTableClearanceInches);
      double x=imported.IsValid?imported.Min.X:0,columnTop=imported.IsValid?imported.Min.Y-clearance:0;
      var tables=selection.Tables.Select(table=>(Table:table,Copies:table.Copies.Where(copy=>copy.Pattern.Items.Count>0).ToArray()))
        .Select((entry,index)=>(entry.Table,entry.Copies,Index:index,
          Recipe:maps.Materials.GetValueOrDefault(entry.Table.Material.Id)??ProjectRecipe(entry.Table.Material,doc))).ToArray();
      foreach(var column in tables.GroupBy(entry=>entry.Recipe.Name.Trim(),StringComparer.OrdinalIgnoreCase))
      {
        double top=columnTop,right=x;
        foreach(var (table,copies,index,mapped) in column)
        {
          var localBounds=BoundingBox.Empty;
          foreach(var copy in copies)
          {
            var pose=Placement(copy.Location,copy.Angle,copy.Flip,scale,Point3d.Origin);
            var items=copy.Pattern.Items;var cutting=items.Where(item=>item.Layer.Process=="Cut"&&item.Geometry is Curve).ToArray();
            foreach(var item in cutting.Length>0?(IEnumerable<Item>)cutting:items)localBounds.Union(item.Geometry.GetBoundingBox(pose));
          }
          double occupied=localBounds.IsValid&&localBounds.Max.X>0?localBounds.Max.X:(table.LabelUsedSize?.Length??0)*scale;
          double occupiedWidth=localBounds.IsValid&&localBounds.Max.Y>0?localBounds.Max.Y:(table.LabelUsedSize?.Width??0)*scale;
          double width=table.Material.Width>0?table.Material.Width*scale:occupiedWidth>0?occupiedWidth:UnspecifiedTableWidthInches*scale;
          double length=table.Length>0?table.Length*scale:occupied>0?occupied:UnspecifiedTableLengthInches*scale;
          var recipe=mapped with {Width=width,Gap=table.Material.Gap*scale};
          var written=NestWorkflow.NestWindow.WriteImportedTable(doc,index,session,CleanName(table.Name),recipe,length,occupied,occupiedWidth,
            copies.Length,table.FaceDown,new Point3d(x,top,0),added,groups,layers);
          var bounds=written.Bounds;
          foreach(var copy in copies)
          {
            bounds.Union(AddPart(copy.Pattern,Placement(copy.Location,copy.Angle,copy.Flip,scale,written.Origin),written.Group,copy,
              recipe,table.FaceDown,tableIndex:index));
            copyCount++;
          }
          top=bounds.Min.Y-clearance;
          right=Math.Max(right,bounds.Max.X);
        }
        x=right+clearance;
      }
      if(added.Count==0)throw new InvalidDataException("The project has no objects on mapped layers.");
      if(paste!=null&&!doc.IsHeadless)
      {
        doc.Objects.UnselectAll();
        foreach(var id in added)doc.Objects.Select(id,select:true,syncHighlight:true,persistentSelect:true);
      }
      if(!opening)doc.Views.Redraw();
      return new(partCount,tables.Length,copyCount);
    }
    catch
    {
      foreach(var id in added.AsEnumerable().Reverse())doc.Objects.Delete(id,true);
      foreach(string key in partKeys){doc.Strings.Delete(PartGroupData.NestSection,key);doc.Strings.Delete(PartGroupData.NamesSection,key);}
      foreach(int group in groups.AsEnumerable().Reverse())doc.Groups.Delete(group);
      foreach(int layer in layers.AsEnumerable().Reverse())doc.Layers.Delete(layer,true);
      doc.Modified=initialModified;
      throw;
    }
    finally{if(undo!=0)doc.EndUndoRecord(undo);}
  }
}
