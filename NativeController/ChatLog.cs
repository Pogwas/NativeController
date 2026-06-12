using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace NativeController;

// Visible chat log ("regular chat system" scrollback, user feedback 2026-06-12): captures every
// chat/TTS message at PlayerAvatar.ChatMessageSpeak -- the one method all paths share (solo
// direct call from ChatMessageSend, MP ChatMessageSendRPC delivered on every client incl. the
// sender; PlayerAvatar.cs:1618-1652) -- and draws the recent conversation bottom-left, newest at
// the bottom. Vanilla has no chat log UI at all (messages only float above heads briefly).
// Read-only; independent of the chat-keyboard flick-recall. Entries persist across levels
// (static store); the GameObject is recreated per scene by Plugin (REPO wipes DontDestroyOnLoad
// at boot). Visible while chat is open, or for VisibleSeconds after a message (1s end-fade).
internal class ChatLog : MonoBehaviour
{
    // Display strings are prebuilt at append time ("Name:" / " text") so OnGUI does no string
    // work; layout widths are cached and recomputed only when entries/scale/count change.
    private struct Entry { public string Name; public string Text; }

    private const int MaxEntries = 30;     // storage cap (display cap is the MaxVisible config)
    private const float FadeSeconds = 1f;  // alpha fade at the end of the VisibleSeconds window

    private static readonly List<Entry> Entries = new List<Entry>();
    private static float _lastMessageTime = -1000f;
    private static int _entriesVersion;

    private GUIStyle _line, _name;
    private float _styleScale = -1f;

    // Layout cache (instance: rebuilt cheaply on scene-recreate).
    private int _layoutVersion = -1, _layoutCount;
    private float _layoutScale = -1f, _panelW;
    private float _layoutScreenW = -1f;
    private float[] _nameWidths;

    // [Chat] FontSizeMultiplier applier state (vanilla typed-message text, ChatManager.chatText).
    private TMPro.TextMeshProUGUI _sizedChatText; // the instance the multiplier was applied to
    private float _chatTextBaseSize;              // vanilla's own size, captured per instance
    private float _appliedMultiplier = -1f;

    internal static void Append(string name, string text)
    {
        Entries.Add(new Entry
        {
            Name = (string.IsNullOrEmpty(name) ? "???" : name) + ":",
            Text = " " + text,
        });
        if (Entries.Count > MaxEntries) Entries.RemoveAt(0);
        _lastMessageTime = Time.unscaledTime;
        _entriesVersion++;
    }

    // Scales the game's own chat text (the message being typed). chatText is public on
    // ChatManager; the HUD is a world-space canvas (quirk 8) so TMP fontSize is canvas-local
    // and safe to scale. Re-applies automatically when the scene swaps the instance or the
    // config changes; vanilla never touches fontSize at runtime (flash/shake only).
    private void Update()
    {
        if (!Plugin.Enabled.Value) return;
        var chat = ChatManager.instance;
        var text = chat != null ? chat.chatText : null;
        if (text == null) return;
        if (!ReferenceEquals(text, _sizedChatText))
        {
            _sizedChatText = text;
            _chatTextBaseSize = text.fontSize;
            _appliedMultiplier = -1f;
        }
        float mult = Plugin.ChatFontSizeMultiplier.Value;
        if (!Mathf.Approximately(mult, _appliedMultiplier))
        {
            _appliedMultiplier = mult;
            text.fontSize = _chatTextBaseSize * mult;
        }
    }

    private void OnGUI()
    {
        if (!Plugin.Enabled.Value || !Plugin.ChatLogEnabled.Value || Entries.Count == 0) return;
        float alpha = VisibleAlpha();
        if (alpha <= 0f) return;

        float s = Plugin.ChatLogScale.Value;
        EnsureStyles(s);

        int n = Mathf.Min(Plugin.ChatLogMaxVisible.Value, Entries.Count);
        float lineH = 20f * s, padX = 8f * s, padY = 5f * s, margin = 12f * s;

        if (_layoutVersion != _entriesVersion || !Mathf.Approximately(_layoutScale, s) || _layoutCount != n || !Mathf.Approximately(_layoutScreenW, Screen.width))
        {
            _layoutVersion = _entriesVersion;
            _layoutScale = s;
            _layoutCount = n;
            _layoutScreenW = Screen.width;
            if (_nameWidths == null || _nameWidths.Length < n) _nameWidths = new float[n];
            _panelW = 0f;
            for (int i = 0; i < n; i++)
            {
                Entry e = Entries[Entries.Count - n + i];
                float nameW = _name.CalcSize(new GUIContent(e.Name)).x;
                float textW = _line.CalcSize(new GUIContent(e.Text)).x;
                _nameWidths[i] = nameW;
                _panelW = Mathf.Max(_panelW, nameW + textW);
            }
            _panelW = Mathf.Min(_panelW + 2f * padX, Screen.width - 2f * margin); // clip absurdly long lines
        }

        float panelH = n * lineH + 2f * padY;
        // Sit above the chat OSK while it's open (the log is forced visible exactly then, and
        // IMGUI draw order across components is undefined). PanelTop is 0 for the OSK's first
        // frame -- one frame at the bottom anchor is imperceptible.
        float bottom = Screen.height - margin;
        if (ChatKeyboard.Open && ChatKeyboard.PanelTop > 0f) bottom = ChatKeyboard.PanelTop - margin;
        float x0 = margin, y0 = bottom - panelH;

        GUI.color = new Color(0f, 0f, 0f, 0.55f * alpha); // backing strip, OSK panel style
        GUI.DrawTexture(new Rect(x0, y0, _panelW, panelH), Texture2D.whiteTexture);

        for (int i = 0; i < n; i++)
        {
            Entry e = Entries[Entries.Count - n + i];
            float y = y0 + padY + i * lineH;
            GUI.color = new Color(1f, 0.85f, 0.3f, alpha); // gold name (matches EmoteWheel/OSK hover)
            GUI.Label(new Rect(x0 + padX, y, _nameWidths[i], lineH), e.Name, _name);
            GUI.color = new Color(1f, 1f, 1f, alpha);
            GUI.Label(new Rect(x0 + padX + _nameWidths[i], y, Mathf.Max(0f, _panelW - 2f * padX - _nameWidths[i]), lineH), e.Text, _line);
        }
        GUI.color = Color.white;
    }

    // 1 while chat is open; otherwise fades out over the last FadeSeconds of the
    // VisibleSeconds-after-last-message window. VisibleSeconds 0 = only-while-chat-open.
    private static float VisibleAlpha()
    {
        ChatManager chat = ChatManager.instance;
        if (chat != null && !chat.StateIsInactive()) return 1f;
        float vis = Plugin.ChatLogVisibleSeconds.Value;
        if (vis <= 0f) return 0f;
        float remain = vis - (Time.unscaledTime - _lastMessageTime);
        if (remain <= 0f) return 0f;
        return remain >= FadeSeconds ? 1f : remain / FadeSeconds;
    }

    private void EnsureStyles(float scale)
    {
        if (_line != null && Mathf.Approximately(_styleScale, scale)) return;
        _styleScale = scale;
        _line = new GUIStyle(GUI.skin.label) { fontSize = (int)(14 * scale), alignment = TextAnchor.MiddleLeft };
        _line.normal.textColor = Color.white; // tinted per-label via GUI.color
        _name = new GUIStyle(_line) { fontStyle = FontStyle.Bold };
        _layoutScale = -1f; // styles changed -> widths are stale
    }
}

// Capture seam. Class-level [HarmonyPatch] so PatchAll sees the method attributes (quirk 7).
[HarmonyPatch]
internal static class ChatLogCapturePatch
{
    // Resolved in a guarded cctor: a renamed field must degrade the log to a warn-once no-op,
    // never fault the type -- a faulted type would make every patched ChatMessageSpeak call
    // throw TypeInitializationException into vanilla and break chat itself.
    private static readonly AccessTools.FieldRef<PlayerAvatar, string> PlayerNameRef;
    private static bool _warned;

    static ChatLogCapturePatch()
    {
        try { PlayerNameRef = AccessTools.FieldRefAccess<PlayerAvatar, string>("playerName"); }
        catch { PlayerNameRef = null; } // warned on first postfix call
    }

    // Postfix runs even when the body no-ops (solo voiceChat is null), so capture works
    // wherever a message exists. /-commands eaten by DebugCommandHandler never get here.
    [HarmonyPatch(typeof(PlayerAvatar), "ChatMessageSpeak")]
    [HarmonyPostfix]
    private static void Postfix(PlayerAvatar __instance, string _message)
    {
        // Capture honors the toggle too (off = the mod records nothing, per the config's
        // master-toggle wording) -- so re-enabling mid-session leaves a gap. Deliberate.
        if (!Plugin.Enabled.Value || !Plugin.ChatLogEnabled.Value) return;
        try
        {
            if (PlayerNameRef == null) { WarnOnce("playerName field not found"); return; }
            ChatLog.Append(PlayerNameRef(__instance), _message);
        }
        catch (Exception e) { WarnOnce(e.Message); }
    }

    private static void WarnOnce(string why)
    {
        if (_warned) return;
        _warned = true;
        Plugin.Log.LogWarning($"[ChatLog] Capture disabled (further warnings suppressed): {why}");
    }
}
