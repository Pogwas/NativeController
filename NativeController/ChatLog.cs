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
    private float[] _nameWidths;

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

    private void OnGUI()
    {
        if (!Plugin.Enabled.Value || !Plugin.ChatLogEnabled.Value || Entries.Count == 0) return;
        float alpha = VisibleAlpha();
        if (alpha <= 0f) return;

        float s = Plugin.ChatLogScale.Value;
        EnsureStyles(s);

        int n = Mathf.Min(Plugin.ChatLogMaxVisible.Value, Entries.Count);
        float lineH = 20f * s, padX = 8f * s, padY = 5f * s, margin = 12f * s;

        if (_layoutVersion != _entriesVersion || !Mathf.Approximately(_layoutScale, s) || _layoutCount != n)
        {
            _layoutVersion = _entriesVersion;
            _layoutScale = s;
            _layoutCount = n;
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
        float x0 = margin, y0 = Screen.height - margin - panelH;

        GUI.color = new Color(0f, 0f, 0f, 0.55f * alpha); // backing strip, OSK panel style
        GUI.DrawTexture(new Rect(x0, y0, _panelW, panelH), Texture2D.whiteTexture);

        for (int i = 0; i < n; i++)
        {
            Entry e = Entries[Entries.Count - n + i];
            float y = y0 + padY + i * lineH;
            GUI.color = new Color(1f, 0.85f, 0.3f, alpha); // gold name (matches EmoteWheel/OSK hover)
            GUI.Label(new Rect(x0 + padX, y, _nameWidths[i], lineH), e.Name, _name);
            GUI.color = new Color(1f, 1f, 1f, alpha);
            GUI.Label(new Rect(x0 + padX + _nameWidths[i], y, _panelW - 2f * padX - _nameWidths[i], lineH), e.Text, _line);
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
    private static readonly AccessTools.FieldRef<PlayerAvatar, string> PlayerNameRef =
        AccessTools.FieldRefAccess<PlayerAvatar, string>("playerName");
    private static bool _warned;

    // Postfix runs even when the body no-ops (solo voiceChat is null), so capture works
    // wherever a message exists. /-commands eaten by DebugCommandHandler never get here.
    [HarmonyPatch(typeof(PlayerAvatar), "ChatMessageSpeak")]
    [HarmonyPostfix]
    private static void Postfix(PlayerAvatar __instance, string _message)
    {
        if (!Plugin.Enabled.Value || !Plugin.ChatLogEnabled.Value) return;
        try
        {
            ChatLog.Append(PlayerNameRef(__instance), _message);
        }
        catch (Exception e)
        {
            if (_warned) return;
            _warned = true;
            Plugin.Log.LogWarning($"[ChatLog] Capture error (further warnings suppressed): {e.Message}");
        }
    }
}
