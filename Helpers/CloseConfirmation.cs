using Eto.Drawing;
using Eto.Forms;

namespace vTools;

/// <summary>Explicit save/place, discard and cancel choices for windows with unfinished work.</summary>
internal static class CloseConfirmation
{
  // Shared confirmation appearance defaults
  [CommandDefault("CloseConfirmation","vTools.CloseConfirmation.Padding","Window-edge padding in logical pixels; zero or greater.")]
  private const int Padding = 10; // Nonnegative logical-pixel dialog padding.
  private static readonly int ConfiguredPadding = CommandDefaults.Get("CloseConfirmation","vTools.CloseConfirmation.Padding",Padding); // Resolved padding; restart after advanced edits.
  [CommandDefault("CloseConfirmation","vTools.CloseConfirmation.Spacing","Space between confirmation controls in logical pixels; zero or greater.")]
  private const int Spacing = 6; // Nonnegative logical-pixel control spacing.
  private static readonly int ConfiguredSpacing = CommandDefaults.Get("CloseConfirmation","vTools.CloseConfirmation.Spacing",Spacing); // Resolved spacing; restart after advanced edits.

  internal enum Decision { Cancel, Save, Discard }

  internal static Decision Show(Window owner,string title,string message,string action,bool actionAvailable=true,string? details=null)
  {
    using var dialog=Create(title,message,action,actionAvailable,details);
    return dialog.ShowModal(owner);
  }

  internal static Dialog<Decision> Create(string title,string message,string action,bool actionAvailable,string? details)
  {
    var dialog=new Dialog<Decision>{Title=title,Padding=new Padding(ConfiguredPadding),Resizable=false};
    var save=new Button{Text=action,Enabled=actionAvailable,ToolTip=actionAvailable?action+" and close":"No valid result is ready to "+action.ToLowerInvariant()};
    var discard=new Button{Text="Discard",ToolTip="Close without saving this window's unfinished changes"};
    var cancel=new Button{Text="Cancel",ToolTip="Keep the window open"};
    dialog.DefaultButton=cancel;dialog.AbortButton=cancel;
    var content=new TableLayout{Spacing=new Size(ConfiguredSpacing,ConfiguredSpacing),Rows={new TableRow(new Label{Text=message})}};
    if(!string.IsNullOrWhiteSpace(details))content.Rows.Add(new TableRow(new Label{Text=details}));
    content.Rows.Add(new TableRow(new TableLayout{Spacing=new Size(ConfiguredSpacing,ConfiguredSpacing),Rows=
      {new TableRow(new TableCell(null,true),save,discard,cancel)}}));
    dialog.Content=content;
    save.Click+=(_,_)=>dialog.Close(Decision.Save);
    discard.Click+=(_,_)=>dialog.Close(Decision.Discard);
    cancel.Click+=(_,_)=>dialog.Close(Decision.Cancel);
    return dialog;
  }
}
