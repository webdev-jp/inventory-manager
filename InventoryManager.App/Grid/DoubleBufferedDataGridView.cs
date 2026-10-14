namespace InventoryManager.App.Grid;

/// <summary>
/// Port of the PowerShell build's grid double-buffering fix, ported the easy way: PS had to reach
/// DoubleBuffered via reflection (it's a protected property on Control, out of reach from script
/// code), but a real subclass can just set it directly. Without this, scrolling a grid with
/// hundreds of real rows visibly flickers/stutters - the PS build's own comment called this out
/// as the reason it was added, not just cosmetic polish.
/// </summary>
public sealed class DoubleBufferedDataGridView : DataGridView
{
    public DoubleBufferedDataGridView()
    {
        DoubleBuffered = true;
    }
}
