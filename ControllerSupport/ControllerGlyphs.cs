using UnityEngine;

namespace ControllerSupport;

// The game's IMGUI font renders the PlayStation outline shapes (○ □ △) tiny from a fallback font, while
// normal text and ✕ render full size. To make them match, draw just those three glyphs with a dynamic OS
// symbol font (Windows ships several that contain them at proper size), leaving all other text in the
// caller's style. Everything degrades gracefully: if no symbol font loads, it's a plain GUI.Label.
internal static class ControllerGlyphs
{
    private static Font _symbol;
    private static bool _tried;
    private static GUIStyle _shapeStyle;

    private static Font Symbol()
    {
        if (!_tried)
        {
            _tried = true;
            try
            {
                _symbol = Font.CreateDynamicFontFromOSFont(
                    new[] { "Arial Unicode MS", "Segoe UI Symbol", "Segoe UI", "Arial" }, 16);
            }
            catch { _symbol = null; }
        }
        return _symbol;
    }

    // The outline shapes and the d-pad arrows render small/thin from the game's fallback font; ✕ already
    // renders full size, so it's left to the caller's font. Route the rest through the symbol font.
    private static bool IsSymbol(char c) =>
        c == '○' || c == '□' || c == '△' || c == '←' || c == '↑' || c == '→' || c == '↓';

    private static bool HasSymbol(string s)
    {
        for (int i = 0; i < s.Length; i++) if (IsSymbol(s[i])) return true;
        return false;
    }

    // Draw `text` in `style`, but render the symbol glyphs (○ □ △ ← ↑ → ↓) with the OS symbol font so
    // they're full size. Lays the runs out left-to-right using CalcSize so text and symbols sit inline.
    internal static void DrawLabel(Rect rect, string text, GUIStyle style)
    {
        var font = Symbol();
        if (font == null || !HasSymbol(text)) { GUI.Label(rect, text, style); return; }

        if (_shapeStyle == null) _shapeStyle = new GUIStyle();
        _shapeStyle.font = font;
        _shapeStyle.fontSize = style.fontSize;
        _shapeStyle.fontStyle = style.fontStyle; // match the column's weight (bold)
        _shapeStyle.alignment = TextAnchor.UpperLeft;
        _shapeStyle.normal.textColor = style.normal.textColor;

        float x = rect.x;
        int i = 0;
        while (i < text.Length)
        {
            if (IsSymbol(text[i]))
            {
                string g = text[i].ToString();
                float w = _shapeStyle.CalcSize(new GUIContent(g)).x;
                GUI.Label(new Rect(x, rect.y, w, rect.height), g, _shapeStyle);
                x += w;
                i++;
            }
            else
            {
                int s = i;
                while (i < text.Length && !IsSymbol(text[i])) i++;
                string run = text.Substring(s, i - s);
                float w = style.CalcSize(new GUIContent(run)).x;
                GUI.Label(new Rect(x, rect.y, w, rect.height), run, style);
                x += w;
            }
        }
    }
}
