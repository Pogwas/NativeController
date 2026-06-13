using System;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace NativeController;

// Push-to-talk status indicator (vanilla has NO feedback at all -- user 2026-06-12: "i couldnt
// tell"). Clones vanilla's mute-icon widget (ToggleMuteUI, a crossed-out mic) and keeps it at
// the SAME authored spot, then shows it whenever the mic is COLD in PTT mode -- i.e. PTT is on,
// you're not muted, and you're NOT holding the talk button. Holding push-to-talk makes the icon
// disappear: crossed mic visible = silent, empty spot = live (user 2026-06-12 -- the first
// build showed the muted-look icon WHILE talking, which read backwards). Together with vanilla
// that spot now reads: vanilla's icon while muted, our identical clone while PTT-idle, nothing
// while transmitting. Deliberately NOT gated on microphoneEnabled, so the feedback works even
// on a PC with no recording device. Solo (voiceChat null) / PTT off -> never shows. The clone
// lives on the per-scene HUD canvas and is lazily re-created; this component is recreated per
// scene by Plugin (REPO wipes DontDestroyOnLoad at boot).
internal class VoiceIndicator : MonoBehaviour
{
    // All vanilla state is internal -- guarded cctor per the ChatLog lesson (a renamed field
    // degrades the indicator to hidden + warn-once, never faults the type).
    private static readonly AccessTools.FieldRef<PlayerAvatar, PlayerVoiceChat> VoiceChatRef;
    private static readonly AccessTools.FieldRef<AudioManager, bool> PushToTalkRef;
    private static readonly AccessTools.FieldRef<DataDirector, bool> ToggleMuteRef;
    private static readonly AccessTools.FieldRef<SemiUI, Vector3> InitialPositionRef;
    private static readonly bool RefsOk;

    static VoiceIndicator()
    {
        try
        {
            VoiceChatRef = AccessTools.FieldRefAccess<PlayerAvatar, PlayerVoiceChat>("voiceChat");
            PushToTalkRef = AccessTools.FieldRefAccess<AudioManager, bool>("pushToTalk");
            ToggleMuteRef = AccessTools.FieldRefAccess<DataDirector, bool>("toggleMute");
            InitialPositionRef = AccessTools.FieldRefAccess<SemiUI, Vector3>("initialPosition");
            RefsOk = true;
        }
        catch { RefsOk = false; } // warned on first Update
    }

    private GameObject _icon;    // our clone; dies with the scene's canvas -> lazily re-created
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

        bool cold;
        try
        {
            PlayerVoiceChat voice = VoiceChatRef(avatar);
            if (voice == null) { Hide(); return; } // solo: no voice chat exists (runtime quirk)
            // Show the (muted-look) icon while the mic is COLD in PTT mode; holding the talk
            // input clears it. Not gated on microphoneEnabled -- works as input feedback even
            // with no recording device (user request 2026-06-12). Muted is excluded because
            // vanilla's own identical icon already shows then (same spot, seamless handoff).
            cold = PushToTalkRef(audio)
                   && !ToggleMuteRef(data)
                   && !SemiFunc.InputHold(InputKey.PushToTalk);
        }
        catch (Exception e) { WarnOnce(e.Message); return; }

        if (!cold) { Hide(); return; }
        if (_icon == null && !TryCreateClone()) return;

        if (!_icon.activeSelf) _icon.SetActive(true);
    }

    private void Hide()
    {
        if (_icon != null && _icon.activeSelf) _icon.SetActive(false);
    }

    // Clone vanilla's mute widget and park it at the SAME authored spot (muted, PTT-idle and
    // talking are mutually exclusive states, so one spot serves all three). SemiUI widgets are
    // positioned by transform.localPosition around an authored home (SemiUI.initialPosition,
    // captured in SemiUI.Start) -- NOT by anchors -- and the source is usually in its HIDDEN
    // state at clone time (unmuted -> SemiUI.Hide() disables uiText, deactivates children and
    // offsets the transform). So: clone, kill the logic component, re-activate the visuals, and
    // place the clone at the source's authored home (discarding any baked hide offset).
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

            _icon.transform.localPosition = home;

            _icon.SetActive(false); // shown by Update while PTT-idle
            Plugin.Log.LogDebug("[VoiceIndicator] Cloned the mute widget (shared spot).");
            return true;
        }
        catch (Exception e)
        {
            if (_icon != null) Destroy(_icon); // never leave a half-built (still-active) clone behind
            _icon = null;
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
