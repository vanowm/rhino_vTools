using Rhino;
using Rhino.Commands;

namespace vTools.Commands;

/// <summary>
/// Places points on a selected surface normal from picked points in space.
/// </summary>
public sealed class vPointNormalToSurface : vToolsCommand
{
  public override string EnglishName => "vPointNormalToSurface";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode) =>
    PointNormalToSurfaceWorkflow.Run(doc);
}
