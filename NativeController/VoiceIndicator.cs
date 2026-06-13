using System;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace NativeController;

// Local "you're transmitting" indicator for Push-to-Talk (vanilla has NO feedback at all --
// user 2026-06-12: "i couldnt tell"). Clones vanilla's mute-icon widget (ToggleMuteUI) so the
// visual is pixel-identical, mirrors it to the LEFT side of the HUD canvas (vanilla's mute icon
// keeps its authored side -- opposite sides = no confusion), shows it while the mic is HOT in
// PTT mode (the exact transmit condition vanilla uses, PlayerVoiceChat.cs:511-524), and
// alpha-blinks it while voice is actually going out (clipLoudnessNoTTS -- vanilla zeroes it
// whenever transmit is off, so it's the honest signal). PTT off / muted / solo (voiceChat is
// null) -> never shows. The clone lives on the per-scene HUD canvas and is lazily re-created;
// this component is recreated per scene by Plugin (REPO wipes DontDestroyOnLoad at boot).
internal class VoiceIndicator : MonoBehaviour
{
    // All vanilla state is internal -- guarded cctor per the ChatLog lesson (a renamed field
    // degrades the indicator to hidden + warn-once, never faults the type).
    private static readonly AccessTools.FieldRef<PlayerAvatar, PlayerVoiceChat> VoiceChatRef;
    private static readonly AccessTools.FieldRef<PlayerVoiceChat, bool> MicEnabledRef;
    private static readonly AccessTools.FieldRef<PlayerVoiceChat, float> ClipLoudnessNoTTSRef;
    private static readonly AccessTools.FieldRef<AudioManager, bool> PushToTalkRef;
    private static readonly AccessTools.FieldRef<DataDirector, bool> ToggleMuteRef;
    private static readonly AccessTools.FieldRef<SemiUI, Vector3> InitialPositionRef;
    private static readonly bool RefsOk;

    static VoiceIndicator()
    {
        try
        {
            VoiceChatRef = AccessTools.FieldRefAccess<PlayerAvatar, PlayerVoiceChat>("voiceChat");
            MicEnabledRef = AccessTools.FieldRefAccess<PlayerVoiceChat, bool>("microphoneEnabled");
            ClipLoudnessNoTTSRef = AccessTools.FieldRefAccess<PlayerVoiceChat, float>("clipLoudnessNoTTS");
            PushToTalkRef = AccessTools.FieldRefAccess<AudioManager, bool>("pushToTalk");
            ToggleMuteRef = AccessTools.FieldRefAccess<DataDirector, bool>("toggleMute");
            InitialPositionRef = AccessTools.FieldRefAccess<SemiUI, Vector3>("initialPosition");
            RefsOk = true;
        }
        catch { RefsOk = false; } // warned on first Update
    }

    private const float BlinkHz = 0.75f; // |sin| doubles the perceived rate -> ~1.5 visible pulses/sec

    private GameObject _icon;    // our clone; dies with the scene's canvas -> lazily re-created
    private CanvasGroup _group;
    private static bool _cloneFailedThisScene; // no per-frame retry spam; reset per scene
    private static bool _warned;

    // Called by Plugin.OnSceneLoaded.
    internal static void ResetState()
    {
        _cloneFailedThisScene = false;
    }

    private void Update()
    {
        if (!Plugin.Enabled.Value || !Plugin.VoiceIndicatorEnabled.Value) { Hide(); return; }
        if (!RefsOk) { WarnOnce("vanilla voice fields not found"); return; }

        var avatar = PlayerAvatar.instance;
        var audio = AudioManager.instance;
        var data = DataDirector.instance;
        if (avatar == null || audio == null || data == null) { Hide(); return; }

        bool hot;
        float loudness;
        try
        {
            PlayerVoiceChat voice = VoiceChatRef(avatar);
            if (voice == null) { Hide(); return; } // solo: no voice chat exists (runtime quirk)
            hot = PushToTalkRef(audio)             // PTT mode only (open mic would be always-on)
                  && MicEnabledRef(voice)
                  && !ToggleMuteRef(data)          // muted: vanilla's own icon owns that story
                  && SemiFunc.InputHold(InputKey.PushToTalk);
            loudness = ClipLoudnessNoTTSRef(voice);
        }
        catch (Exception e) { WarnOnce(e.Message); return; }

        if (!hot) { Hide(); return; }
        if (_icon == null && !TryCreateClone()) return;

        _icon.SetActive(true);
        // Steady = channel open; blink = voice actually going out.
        bool speaking = loudness > Plugin.VoiceSpeakThreshold.Value;
        _group.alpha = speaking
            ? 0.45f + 0.55f * Mathf.Abs(Mathf.Sin(Time.unscaledTime * 2f * Mathf.PI * BlinkHz))
            : 1f;
    }

    private void Hide()
    {
        if (_icon != null && _icon.activeSelf) _icon.SetActive(false);
    }

    // Clone vanilla's mute widget and mirror it to the LEFT. SemiUI widgets are positioned by
    // transform.localPosition around an authored home (SemiUI.initialPosition, captured in
    // SemiUI.Start) -- NOT by anchors -- and the source is usually in its HIDDEN state at clone
    // time (unmuted -> SemiUI.Hide() disables uiText, deactivates children and offsets the
    // transform). So: clone, kill the logic component, re-activate the visuals, and place the
    // clone at the x-mirror of the source's authored home (the HUD canvas is center-origin).
    private bool TryCreateClone()
    {
        if (_cloneFailedThisScene) return false;
        try
        {
            ToggleMuteUI muteUi = FindObjectOfType<ToggleMuteUI>(true);
            if (muteUi == null)
            {
                _cloneFailedThisScene = true;
                Plugin.Log.LogDebug("[VoiceIndicator] No ToggleMuteUI widget in this scene -- no indicator here.");
                return false;
            }

            Vector3 home = InitialPositionRef(muteUi); // authored position, no hide offset
            // An inactive widget whose Start hasn't run yet has initialPosition = default --
            // fall back to its authored transform position rather than parking at canvas center.
            if (home == Vector3.zero && !muteUi.isActiveAndEnabled) home = muteUi.transform.localPosition;

            GameObject src = muteUi.gameObject;
            _icon = Instantiate(src, src.transform.parent);
            _icon.name = "NativeController.VoiceIndicator";
            Destroy(_icon.GetComponent<ToggleMuteUI>()); // kills mute logic + SemiUI base with it

            // Undo the cloned hidden state.
            foreach (Transform child in _icon.transform) child.gameObject.SetActive(true);
            foreach (TMP_Text text in _icon.GetComponentsInChildren<TMP_Text>(true)) text.enabled = true;

            _icon.transform.localPosition = new Vector3(-home.x, home.y, home.z);

            _group = _icon.GetComponent<CanvasGroup>();
            if (_group == null) _group = _icon.AddComponent<CanvasGroup>();
            _icon.SetActive(false); // shown by Update when hot
            Plugin.Log.LogDebug("[VoiceIndicator] Cloned the mute widget (left-mirrored).");
            return true;
        }
        catch (Exception e)
        {
            if (_icon != null) Destroy(_icon); // never leave a half-built (still-active) clone behind
            _icon = null;
            _group = null;
            _cloneFailedThisScene = true;
            WarnOnce(e.Message);
            return false;
        }
    }

    private static void WarnOnce(string why)
    {
        if (_warned) return;
        _warned = true;
        Plugin.Log.LogWarning($"[VoiceIndicator] Disabled ({why}) -- further warnings suppressed.");
    }
}
