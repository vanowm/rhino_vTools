using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace vTools.Commands;

/// <summary>Selects an innermost group without expanding its parent groups.</summary>
[CommandStyle(Style.Transparent | Style.NotUndoable)]
public sealed class vSelGroup : vToolsCommand
{
  // Defaults and customizable constants
  private const SelectionMode DefaultMode = SelectionMode.Select; // Select replaces selection; Add retains other selections; Deselect removes only the picked group.
  private static readonly string[] ModeNames = ["Select", "Add", "Deselect"]; // Command-line values in SelectionMode order.
  private const string PickPrompt = "Pick child groups. Press Enter when done"; // Repeated group-picking prompt; Enter or Esc returns to the interrupted command.

  internal enum SelectionMode { Select, Add, Deselect }

  public override string EnglishName => "vSelGroup";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    // This is a borrowed wrapper around the interrupted native/managed getter, not an owned input getter.
    var objectGetter = GetObject.ActiveGetObject(doc);
    var targets = new GroupTargets(doc);
    if (!targets.HasGroups)
    {
      RhinoApp.WriteLine("vSelGroup: no selectable groups.");
      return Result.Nothing;
    }
    using var highlight = new PreviewDisplay.ObjectHighlighter(doc);
    using var point = new GetPoint();
    point.AcceptNothing(true);
    point.PermitObjectSnap(false);
    point.PermitOrthoSnap(false);
    point.PermitConstraintOptions(false);
    point.MouseMove += (_, e) =>
      highlight.SetObjects(targets.Pick(e.Viewport.ParentView, e.WindowPoint)?.Select(obj => obj.Id) ?? []);
    var selectionMode = DefaultMode;
    bool applied = false;
    Log.Write(EnglishName, "selection start groups={0}", targets.Count);
    while (true)
    {
      point.SetCommandPrompt(PickPrompt);
      point.ClearCommandOptions();
      int modeOption = point.AddOptionList("Mode", ModeNames, (int)selectionMode);
      var result = point.Get();
      if (result == GetResult.Option)
      {
        if (point.OptionIndex() == modeOption)
          selectionMode = (SelectionMode)point.Option().CurrentListOptionIndex;
        continue;
      }
      if (result is GetResult.Nothing or GetResult.Cancel)
      {
        // Hand the interrupted getter only the final selection, not intermediate loop picks.
        if (applied) UpdateObjectGetter(objectGetter, doc.Objects.GetSelectedObjects(false, false).ToArray());
        return applied ? Result.Success : result == GetResult.Nothing ? Result.Nothing : Result.Cancel;
      }
      if (result != GetResult.Point) return point.CommandResult();
      var view = point.View() ?? doc.Views.ActiveView;
      if (view == null) return Result.Cancel;
      var location = view.ActiveViewport.WorldToClient(point.Point());
      var members = targets.Pick(view, new System.Drawing.Point((int)Math.Round(location.X), (int)Math.Round(location.Y)));
      if (members == null)
      {
        highlight.SetObjects([]);
        RhinoApp.WriteLine("vSelGroup: click an object belonging to a group.");
        continue;
      }
      var modifiers = System.Windows.Forms.Control.ModifierKeys;
      var action = ResolveMode(selectionMode,
        (modifiers & System.Windows.Forms.Keys.Shift) != 0,
        (modifiers & System.Windows.Forms.Keys.Control) != 0);
      Log.Write(EnglishName, "mode={0} members={1}", action, members.Length);
      highlight.SetObjects([]);
      var selectable = action == SelectionMode.Deselect ? members : members.Where(obj => AcceptsObject(objectGetter, obj)).ToArray();
      ApplySelection(doc, selectable, action);
      applied = true;
    }
  }

  internal static SelectionMode ResolveMode(SelectionMode mode, bool shift, bool control) =>
    control ? SelectionMode.Deselect : shift ? SelectionMode.Add : mode;

  private static void UpdateObjectGetter(GetObject? getter, RhinoObject[] members)
  {
    if (getter == null) return;
    var existing = new HashSet<Guid>();
    for (int i = 0; i < getter.ObjectCount; i++)
    {
      using var reference = getter.Object(i);
      existing.Add(reference.ObjectId);
    }
    foreach (var obj in members)
    {
      if (existing.Contains(obj.Id) || !AcceptsObject(getter, obj)) continue;
      using var reference = new ObjRef(obj);
      getter.AppendToPickList(reference);
    }
  }

  private static bool AcceptsObject(GetObject? getter, RhinoObject obj) => getter == null ||
    (obj.ObjectType & getter.GeometryFilter) != 0 &&
    getter.PassesGeometryAttributeFilter(obj, obj.Geometry, ComponentIndex.Unset) &&
    getter.CustomGeometryFilter(obj, obj.Geometry, ComponentIndex.Unset);

  internal static void ApplySelection(RhinoDoc doc, RhinoObject[] members, SelectionMode mode)
  {
    if (mode == SelectionMode.Select) doc.Objects.UnselectAll();
    foreach (var obj in members)
      obj.Select(mode != SelectionMode.Deselect, true, true, false, false, false);
    doc.Views.Redraw();
  }

  internal static int InnermostGroup(IEnumerable<int> groups, IReadOnlyDictionary<int, int> sizes) =>
    groups.Where(sizes.ContainsKey).OrderBy(group => sizes[group]).ThenBy(group => group).FirstOrDefault(-1);

  internal sealed class GroupTargets
  {
    private readonly RhinoDoc _doc;
    private readonly Dictionary<int, RhinoObject[]> _members = [];
    private readonly Dictionary<Guid, int> _targets = [];
    private readonly RhinoObject[] _objects;

    internal bool HasGroups => _targets.Count > 0;
    internal int Count => _members.Count;

    internal GroupTargets(RhinoDoc doc)
    {
      _doc = doc;
      var sizes = new Dictionary<int, int>();
      foreach (var group in doc.Groups.Where(group => !group.IsDeleted))
      {
        var members = doc.Groups.GroupMembers(group.Index) ?? [];
        sizes[group.Index] = members.Length;
        var selectable = members.Where(obj => obj.IsSelectable(true, false, false, false)).ToArray();
        if (selectable.Length > 0) _members[group.Index] = selectable;
      }
      _objects = _members.Values.SelectMany(members => members).DistinctBy(obj => obj.Id).ToArray();
      foreach (var obj in _objects)
      {
        int group = InnermostGroup(obj.Attributes.GetGroupList() ?? [], sizes);
        if (_members.ContainsKey(group)) _targets[obj.Id] = group;
      }
    }

    internal RhinoObject[]? Pick(RhinoView? view, System.Drawing.Point location)
    {
      if (view == null) return null;
      view.ActiveViewport.GetFrustumLine(location.X, location.Y, out var ray);
      using var context = new PickContext
      {
        View = view,
        PickStyle = PickStyle.PointPick,
        PickMode = PickMode.Shaded,
        PickLine = ray,
        PickGroupsEnabled = false
      };
      context.SetPickTransform(view.ActiveViewport.GetPickTransform(location));
      context.UpdateClippingPlanes();
      var references = _doc.Objects.PickObjects(context);
      try
      {
        var hit = references?.Where(reference => _targets.ContainsKey(reference.ObjectId))
          .OrderBy(reference => reference.SelectionPoint().IsValid
            ? reference.SelectionPoint().DistanceTo(view.ActiveViewport.CameraLocation) : double.MaxValue).FirstOrDefault();
        Guid id = hit?.ObjectId ?? Guid.Empty;
        double distance = hit?.SelectionPoint().IsValid == true ? hit.SelectionPoint().DistanceTo(ray.From) : double.MaxValue;
        // Rhino's normal picker can omit selected objects; deselection must still be able to hit them.
        foreach (var obj in _objects.Where(obj => !obj.IsDeleted && obj.IsSelected(false) != 0 && obj.IsSelectable(true, false, false, false)))
        {
          if (!context.PickFrustumTest(obj.Geometry.GetBoundingBox(true), out _)) continue;
          var picked = PickSelected(context, obj.Geometry, ray);
          if (!picked.IsValid || picked.DistanceTo(ray.From) >= distance) continue;
          id = obj.Id; distance = picked.DistanceTo(ray.From);
        }
        return id == Guid.Empty ? null : _members[_targets[id]];
      }
      finally
      {
        if (references != null) foreach (var reference in references) reference.Dispose();
      }
    }

    private static Point3d PickSelected(PickContext context, GeometryBase geometry, Line ray)
    {
      if (geometry is Curve curve)
      {
        using var nurbs = curve.ToNurbsCurve();
        return context.PickFrustumTest(nurbs, out double parameter, out _, out _) ? nurbs.PointAt(parameter) : Point3d.Unset;
      }
      if (geometry is Rhino.Geometry.Point point)
        return context.PickFrustumTest(point.Location, out _, out _) ? point.Location : Point3d.Unset;
      if (geometry is TextDot dot)
        return context.PickFrustumTest(dot.Point, out _, out _) ? dot.Point : Point3d.Unset;
      if (geometry is Mesh mesh)
        return context.PickFrustumTest(mesh, PickContext.MeshPickStyle.ShadedModePicking, out Point3d meshPoint, out _, out _, out _, out _) ? meshPoint : Point3d.Unset;
      if (geometry is Brep or Surface)
        return Intersection.RayShoot([geometry], new Ray3d(ray.From, ray.Direction), 1).Select(hit => hit.Point).FirstOrDefault(Point3d.Unset);
      if (geometry is Extrusion extrusion)
      {
        using var brep = extrusion.ToBrep();
        return brep == null ? Point3d.Unset : PickSelected(context, brep, ray);
      }
      return Point3d.Unset;
    }

  }
}
