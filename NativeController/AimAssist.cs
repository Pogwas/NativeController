using HarmonyLib;
using UnityEngine;

namespace NativeController;

// Selects a soft aim-assist target (armed -> nearest enemy, else nearest grabbable item; cone+range+LOS)
// with sticky-target hysteresis + point smoothing, and PUBLISHES it statically. The actual nudge is
// applied by LookPatch as a bounded additive look-delta injected into GetMouseX/GetMouseY — it is added
// to (never substituted for) the player's own input, and is always clamped below the player's own
// per-frame turn, so it CANNOT lock the view. Local-only; it never calls AimTargetSoftSet (that path
// overwrites playerAim toward the target and normalizes away mouse sensitivity = the old lock-on).
internal class AimAssist : MonoBehaviour
{
    // Published state read by LookPatch.
    internal static bool HasTarget;
    internal static Vector3 TargetPosition;

    private const float CastRadius = 0.4f;        // item spherecast thickness
    private const float LosMargin  = 0.5f;        // stop the line-of-sight ray short of the target
    private const float AcquireFraction = 0.6f;   // a NEW target must be inside MaxAngle*this (retain out to MaxAngle)
    private const float SwitchMarginDeg = 4f;     // a rival must beat the current target by this many deg to flip
    private const float MinDwellSeconds = 0.15f;  // min time before another switch
    private const float PointSmoothTime = 0.08f;  // SmoothDamp time for the published point

    // Internal game fields (different assembly) → reflection field refs.
    private static readonly AccessTools.FieldRef<PhysGrabber, PhysGrabObject> GrabbedRef =
        AccessTools.FieldRefAccess<PhysGrabber, PhysGrabObject>("grabbedPhysGrabObject");
    private static readonly AccessTools.FieldRef<EnemyParent, Enemy> EnemyRef =
        AccessTools.FieldRefAccess<EnemyParent, Enemy>("Enemy");
    private static readonly AccessTools.FieldRef<EnemyParent, bool> SpawnedRef =
        AccessTools.FieldRefAccess<EnemyParent, bool>("Spawned");

    private int _itemMask;
    private int _losMask;
    private bool _masksReady;

    private GameObject _lastTarget;
    private float _dwellTimer;
    private Vector3 _smoothedPoint;
    private Vector3 _pointVel;

    private void Update()
    {
        if (!Plugin.Enabled.Value || !Plugin.AimAssistEnabled.Value) { Forget(); return; }

        var aim = CameraAim.Instance;
        var cam = Camera.main;
        if (aim == null || cam == null) { Forget(); return; }

        EnsureMasks();
        if (_dwellTimer > 0f) _dwellTimer -= Time.deltaTime;

        Vector3 origin = cam.transform.position;
        Vector3 fwd = cam.transform.forward;

        Vector3 targetPos;
        GameObject targetGo;

        if (Plugin.AimAssistEnemies.Value && IsArmed() && TryFindEnemy(origin, fwd, out targetPos, out targetGo))
        {
            // enemy wins — combat priority while armed
        }
        else if (Plugin.AimAssistItems.Value && TryFindItem(origin, fwd, out targetPos, out targetGo))
        {
            // item assist
        }
        else { Forget(); return; }

        if (targetGo != _lastTarget)
        {
            _lastTarget = targetGo;
            _smoothedPoint = targetPos;          // reset smoother on identity change (no sweep)
            _pointVel = Vector3.zero;
            _dwellTimer = MinDwellSeconds;
            Plugin.Log.LogDebug($"[AimAssist] target -> {targetGo.name}");
        }
        else
        {
            _smoothedPoint = Vector3.SmoothDamp(_smoothedPoint, targetPos, ref _pointVel, PointSmoothTime);
        }

        // PUBLISH — LookPatch applies the bounded correction. No AimTargetSoftSet (no lock, no input-normalization).
        TargetPosition = _smoothedPoint;
        HasTarget = true;
    }

    private void OnDisable() => Forget();

    private void Forget()
    {
        HasTarget = false;
        if (_lastTarget != null)
        {
            _lastTarget = null;
            Plugin.Log.LogDebug("[AimAssist] target -> none");
        }
    }

    private static bool IsArmed()
    {
        var grabber = PhysGrabber.instance;
        if (grabber == null) return false;
        var held = GrabbedRef(grabber);
        if (held == null) return false;
        var go = held.gameObject;
        return go.GetComponentInChildren<ItemGun>() != null
            || go.GetComponentInChildren<ItemMelee>() != null
            || go.GetComponentInChildren<ValuableWizardStaff>() != null;
    }

    private bool TryFindEnemy(Vector3 origin, Vector3 fwd, out Vector3 pos, out GameObject go)
    {
        pos = Vector3.zero; go = null;
        var dir = EnemyDirector.instance;
        if (dir == null || dir.enemiesSpawned == null) return false;

        float maxAngle    = Plugin.AimAssistMaxAngle.Value;
        float acquireCone = maxAngle * AcquireFraction;
        float range       = Plugin.AimAssistEnemyRange.Value;

        GameObject bestGo = null; float bestAngle = maxAngle; Vector3 bestPos = Vector3.zero;
        GameObject curGo = null;  float curAngle = maxAngle;  Vector3 curPos = Vector3.zero;

        foreach (var ep in dir.enemiesSpawned)
        {
            if (ep == null || !SpawnedRef(ep)) continue;
            var enemy = EnemyRef(ep);
            if (enemy == null) continue;
            Transform ct = enemy.CenterTransform != null ? enemy.CenterTransform : enemy.transform;
            var gobj = enemy.gameObject;
            // Retain the current target out to MaxAngle; only acquire NEW targets inside the tighter cone.
            float cone = (gobj == _lastTarget) ? maxAngle : acquireCone;
            if (!Passes(origin, fwd, ct.position, range, cone, out float angle)) continue;
            if (gobj == _lastTarget) { curGo = gobj; curAngle = angle; curPos = ct.position; }
            if (angle < bestAngle) { bestAngle = angle; bestGo = gobj; bestPos = ct.position; }
        }
        return Resolve(bestGo, bestAngle, bestPos, curGo, curAngle, curPos, out pos, out go);
    }

    private bool TryFindItem(Vector3 origin, Vector3 fwd, out Vector3 pos, out GameObject go)
    {
        pos = Vector3.zero; go = null;
        float maxAngle    = Plugin.AimAssistMaxAngle.Value;
        float acquireCone = maxAngle * AcquireFraction;
        float range       = Plugin.AimAssistItemRange.Value;

        var hits = Physics.SphereCastAll(origin, CastRadius, fwd, range, _itemMask, QueryTriggerInteraction.Collide);

        GameObject bestGo = null; float bestAngle = maxAngle; Vector3 bestPos = Vector3.zero;
        GameObject curGo = null;  float curAngle = maxAngle;  Vector3 curPos = Vector3.zero;

        foreach (var h in hits)
        {
            if (!h.collider.CompareTag("Phys Grab Object")) continue;
            var pgo = h.collider.GetComponentInParent<PhysGrabObject>();
            if (pgo == null) continue;
            var gobj = pgo.gameObject;
            Vector3 p = pgo.centerPoint;
            float cone = (gobj == _lastTarget) ? maxAngle : acquireCone;
            if (!Passes(origin, fwd, p, range, cone, out float angle)) continue;
            if (gobj == _lastTarget) { curGo = gobj; curAngle = angle; curPos = p; }
            if (angle < bestAngle) { bestAngle = angle; bestGo = gobj; bestPos = p; }
        }
        return Resolve(bestGo, bestAngle, bestPos, curGo, curAngle, curPos, out pos, out go);
    }

    // Sticky-target resolution: keep the current target unless a rival beats it by the switch margin AND
    // the min-dwell has elapsed. The incumbent is identified during the scan (gobj == _lastTarget), so no
    // fragile component re-resolution is needed. Prevents frame-to-frame flicker between similar targets.
    private bool Resolve(GameObject bestGo, float bestAngle, Vector3 bestPos,
                         GameObject curGo, float curAngle, Vector3 curPos,
                         out Vector3 pos, out GameObject go)
    {
        if (curGo != null && (bestGo == curGo || _dwellTimer > 0f || bestAngle > curAngle - SwitchMarginDeg))
        {
            pos = curPos; go = curGo; return true;
        }
        pos = bestPos; go = bestGo;
        return go != null;
    }

    // cone + range + line-of-sight (not blocked by world geometry before the target)
    private bool Passes(Vector3 origin, Vector3 fwd, Vector3 target, float range, float maxAngle, out float angle)
    {
        angle = 999f;
        Vector3 to = target - origin;
        float dist = to.magnitude;
        if (dist < 0.05f || dist > range) return false;
        angle = Vector3.Angle(fwd, to);
        if (angle > maxAngle) return false;
        if (Physics.Raycast(origin, to / dist, dist - LosMargin, _losMask)) return false;
        return true;
    }

    private void EnsureMasks()
    {
        if (_masksReady) return;
        int vision = (int)SemiFunc.LayerMaskGetVisionObstruct();
        _losMask = vision;
        _itemMask = vision & ~LayerMask.GetMask("Player");
        _masksReady = true;
    }
}
