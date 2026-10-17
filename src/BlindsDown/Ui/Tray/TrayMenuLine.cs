namespace BlindsDown.Ui.Tray;

/// <summary>
/// A read-only status line in the tray menu. A disabled item would be greyed out and hard to read;
/// this one keeps normal colours but can't be highlighted or reached with the arrow keys.
/// </summary>
internal sealed class TrayMenuLine : ToolStripMenuItem
{
    public override bool CanSelect => false;
}
