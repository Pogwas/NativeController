namespace ControllerSupport;

// Kind-aware TEXT labels for physical controls (no image assets). Generic falls back to Xbox names.
internal static class ButtonNames
{
    internal enum Control
    {
        South, East, West, North, LB, RB, LT, RT, L3, R3,
        DpadLeft, DpadUp, DpadRight, DpadDown, Start, Select, LStick, RStick
    }

    internal static string Of(Control c, ControllerDetect.Kind kind)
    {
        switch (kind)
        {
            case ControllerDetect.Kind.PlayStation: return Ps(c);
            case ControllerDetect.Kind.Switch: return Sw(c);
            default: return Xbox(c); // Xbox + Generic
        }
    }

    private static string Xbox(Control c) => c switch
    {
        Control.South => "A", Control.East => "B", Control.West => "X", Control.North => "Y",
        Control.LB => "LB", Control.RB => "RB", Control.LT => "LT", Control.RT => "RT",
        Control.L3 => "L3", Control.R3 => "R3",
        Control.DpadLeft => "D-pad ←", Control.DpadUp => "D-pad ↑",
        Control.DpadRight => "D-pad →", Control.DpadDown => "D-pad ↓",
        Control.Start => "Menu", Control.Select => "View",
        Control.LStick => "L-Stick", Control.RStick => "R-Stick", _ => "?"
    };

    private static string Ps(Control c) => c switch
    {
        Control.South => "Cross", Control.East => "Circle", Control.West => "Square", Control.North => "Triangle",
        Control.LB => "L1", Control.RB => "R1", Control.LT => "L2", Control.RT => "R2",
        Control.L3 => "L3", Control.R3 => "R3",
        Control.DpadLeft => "D-pad ←", Control.DpadUp => "D-pad ↑",
        Control.DpadRight => "D-pad →", Control.DpadDown => "D-pad ↓",
        Control.Start => "Options", Control.Select => "Create",
        Control.LStick => "L-Stick", Control.RStick => "R-Stick", _ => "?"
    };

    // Switch: Unity buttonSouth/East/West/North sit at the PHYSICAL B/A/Y/X positions on a Switch pad.
    private static string Sw(Control c) => c switch
    {
        Control.South => "B", Control.East => "A", Control.West => "Y", Control.North => "X",
        Control.LB => "L", Control.RB => "R", Control.LT => "ZL", Control.RT => "ZR",
        Control.L3 => "L3", Control.R3 => "R3",
        Control.DpadLeft => "D-pad ←", Control.DpadUp => "D-pad ↑",
        Control.DpadRight => "D-pad →", Control.DpadDown => "D-pad ↓",
        Control.Start => "+", Control.Select => "-",
        Control.LStick => "L-Stick", Control.RStick => "R-Stick", _ => "?"
    };
}
