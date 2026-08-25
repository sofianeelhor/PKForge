namespace PKForge.Domain;

public enum FocusDirection { Up, Down, Left, Right }

/// <summary>Gamepad target for the box screen: L/R page boxes, d-pad moves the cursor.</summary>
public interface IBoxPager
{
    void PreviousBox();
    void NextBox();

    /// <summary>Moves the box cursor one cell; false when no save is open (input falls through).</summary>
    bool MoveCursor(FocusDirection direction);
}
