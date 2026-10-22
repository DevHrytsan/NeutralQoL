using System;
using BepInEx.Configuration;
using HarmonyLib;
using NeutralQoL.Core;
using NeutralQoL.Features.CargoDropAid;
using NeutralQoL.Shared.CargoDrop;
using NuclearOption.UIStyleSystem;
using TMPro;
using UnityEngine;

namespace NeutralQoL.Features.CargoDropHeight
{
    internal sealed class CargoDropHeightFeature : Feature
    {
        public override string Section => "HUD";
        public override string Name => "Unsafe drop text";
        public override string Description =>
            "Shows [UNSAFE DROP] in yellow when cargo dropped now would hit the ground too fast and be destroyed, for example because it is too low for the parachute to open and fill.";
        public override Type PartOf => typeof(CargoDropAidFeature);
        protected override bool DefaultOn => true;

        protected override Type[] PatchClasses => new[] { typeof(HUDFixedUpdatePatch), typeof(ContainerSpawnPatch), typeof(VehicleSpawnPatch) };

        // "Hard landing" reads as the aircraft's own; brackets match other HUD notices.
        private const string k_Text = "[UNSAFE DROP]";
        // Warning distance below the HUD centre, in lines of its font size.
        private const float k_LabelLines = 6f;
        // A new cargo unit this close to the local aircraft is taken as its drop.
        private const float k_SpawnMatchDistance = 150f;
        // Height above ground at which a tracked drop counts as landed.
        private const float k_LandedHeight = 0.5f;
        // Descent speed is read above this height, before wheels or ground slow it and
        // before Parachute.Update fades the drag (below 2 m).
        private const float k_ApproachHeight = 3f;
        // A drop that stops this low without reaching k_LandedHeight (pivot sits higher
        // than the spawn offset says) also counts.
        private const float k_StoppedHeight = 5f;
        private const float k_StoppedSpeed = 0.5f;
        // How long a landed drop is watched for destruction before logging.
        private const float k_AfterLandingSeconds = 3f;
        private const float k_TrackTimeout = 600f;

        private enum Warning { None, Landing, Chute }

        public enum WarningSensitivity { Low, Normal, High }

        private static ConfigEntry<WarningSensitivity> s_Sensitivity;
        private static ConfigEntry<bool> s_LogDrops;

        private static AccessTools.FieldRef<HUDCargoState, WeaponStation> s_Station;
        private static AccessTools.FieldRef<HUDCargoState, TMP_Text> s_Title;

        private static HUDCargoState s_LabelOwner;
        private static TMP_Text s_Label;
        private static Warning s_Shown;
        private static bool s_LoggedTemplate;

        // The drop being followed for the log, one at a time.
        private static Unit s_Tracked;
        private static CargoDropModel s_TrackedModel;
        private static CargoDropPrediction s_TrackedPrediction;
        private static GlobalPosition s_TrackedPredictedPoint;
        private static GlobalPosition s_LastPosition;
        private static float s_TrackedSince;
        private static float s_LastTime;
        private static Vector3 s_LastVelocity;
        private static float s_LandedAt;
        private static GlobalPosition s_LandedPoint;
        private static float s_LandedSpeed;
        private static float s_LandedSlope;
        private static float s_LandedTilt;
        private static float s_ApproachSpeed;
        private static float s_ApproachHorizontal;
        private static string s_Release;
        private static float s_LastHeight;
        private static float s_WarningOnAt = float.NegativeInfinity;
        private static bool s_HasFirePoint;
        private static GlobalPosition s_FirePoint;

        protected override void BindOptions()
        {
            s_Sensitivity = Bind("Sensitivity", WarningSensitivity.Normal,
                "Low: warn only when the cargo would land clearly too fast. Normal: warn when it would land faster than the game allows, mostly drops too low for the parachute. High: also warn for high drops under a full parachute, which land right at the limit and break about half the time.");
            s_LogDrops = Bind("Log drops", false,
                "Write each of your drops to the BepInEx log: predicted and actual landing point and speed, and whether the cargo survived. For checking the prediction.", inMenu: false);
        }

        protected override void Resolve()
        {
            Targets.Method(typeof(HUDCargoState), nameof(HUDCargoState.HUDFixedUpdate));
            Targets.Method(typeof(Container), nameof(Container.InitializeUnit));
            Targets.Method(typeof(GroundVehicle), nameof(GroundVehicle.InitializeUnit));
            s_Station = Targets.Field<HUDCargoState, WeaponStation>("weaponStation");
            s_Title = Targets.Field<HUDCargoState, TMP_Text>("currentAirbaseTitle");
            CargoDropModel.Resolve();
        }

        protected override void OnEnable()
        {
            CargoDropService.Reset();
        }

        protected override void OnDisable()
        {
            if (s_Label != null)
                UnityEngine.Object.Destroy(s_Label.gameObject);
            s_Label = null;
            s_LabelOwner = null;
            s_Tracked = null;
        }

        public override void Tick()
        {
            // The label lives on the flight HUD, which outlasts the cargo mode, so hide it
            // here once that mode is gone (another station selected).
            if (s_Label != null && s_Label.enabled && s_LabelOwner == null)
                s_Label.enabled = false;
            if (s_Tracked != null || s_TrackedModel != null)
                Track();
        }

        private static Warning Evaluate(Aircraft aircraft, WeaponStation station)
        {
            if (!CargoDropService.TryGet(aircraft, station, out var prediction, out _))
                return Warning.None;
            if (prediction.BreaksInAir)
                return Warning.Chute;
            if (prediction.InWater || prediction.DetectorGone)
                return Warning.None;
            // A full chute lands a truck between the safe speed and the limit, a coin
            // flip. Normal warns from the limit, High also for the coin flip, Low only
            // when clearly too fast.
            float warnAt;
            switch (s_Sensitivity.Value)
            {
                case WarningSensitivity.Low:
                    warnAt = prediction.BreakSpeed * 1.1f;
                    break;
                case WarningSensitivity.High:
                    warnAt = prediction.SafeSpeed * 0.95f;
                    break;
                default:
                    warnAt = prediction.BreakSpeed;
                    break;
            }
            return prediction.ImpactSpeed >= warnAt ? Warning.Landing : Warning.None;
        }

        // Copy of a text on the fixed flight HUD for its font, size and depth. Its
        // scripts are removed so the original readout can't overwrite it. The cargo
        // HUD's airbase title is the fallback.
        private static bool CreateLabel(HUDCargoState state)
        {
            var hud = SceneSingleton<FlightHud>.i;
            var center = hud != null ? hud.GetHUDCenter() : null;
            TMP_Text template = null;
            if (center != null)
            {
                foreach (var text in center.GetComponentsInChildren<TMP_Text>())
                {
                    if (text.enabled && text.fontSize > 0f)
                    {
                        template = text;
                        break;
                    }
                }
            }
            var parent = center;
            if (template == null)
            {
                template = s_Title(state);
                parent = state.transform;
            }
            if (template == null || parent == null)
                return false;
            if (!s_LoggedTemplate)
            {
                s_LoggedTemplate = true;
                Plugin.Log.LogInfo($"HUD.Low cargo drop warning: text copied from '{template.name}' under '{parent.name}'");
            }

            var copy = UnityEngine.Object.Instantiate(template.gameObject, parent, false);
            copy.name = "NeutralQoL CargoDropHeight";
            copy.SetActive(true);
            for (int i = copy.transform.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(copy.transform.GetChild(i).gameObject);
            s_Label = copy.GetComponent<TMP_Text>();
            foreach (var behaviour in copy.GetComponents<MonoBehaviour>())
            {
                if (behaviour != s_Label)
                    UnityEngine.Object.Destroy(behaviour);
            }
            s_Label.enableWordWrapping = false;
            s_Label.overflowMode = TextOverflowModes.Overflow;
            s_Label.horizontalAlignment = HorizontalAlignmentOptions.Center;
            // Same on-screen size as the template, whatever scale its parent had.
            var rect = s_Label.rectTransform;
            var parentScale = parent.lossyScale;
            var templateScale = template.transform.lossyScale;
            rect.localScale = new Vector3(templateScale.x / parentScale.x, templateScale.y / parentScale.y, 1f);
            rect.localRotation = Quaternion.identity;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.localPosition = new Vector3(0f, -s_Label.fontSize * k_LabelLines * rect.localScale.y, 0f);
            return true;
        }

        private static void Show(HUDCargoState state, Warning warning)
        {
            s_LabelOwner = state;
            if (s_Label == null)
            {
                s_Shown = Warning.None;
                if (warning == Warning.None || !CreateLabel(state))
                    return;
            }
            if (warning != s_Shown)
            {
                s_Shown = warning;
                if (warning != Warning.None)
                    s_Label.text = k_Text;
            }
            bool on = warning != Warning.None;
            if (on)
                s_WarningOnAt = Time.time;
            if (s_Label.enabled != on)
                s_Label.enabled = on;
            // Read every step so a theme change applies at once; Color is a struct.
            if (on)
                s_Label.color = ThemeManager.Active.ColorTheme.Warning;
        }

        // Predicted again from the cargo's real spawn point and the aircraft's velocity
        // there, so the log measures the simulation, not the pilot's flying during the
        // rail delay.
        private static void OnCargoSpawned(Unit unit)
        {
            if (!s_LogDrops.Value || unit == null || unit.definition == null || unit.definition != CargoDropService.LastDefinition || !GameManager.GetLocalAircraft(out var aircraft) || aircraft.rb == null)
                return;
            var position = unit.transform.position;
            if (Vector3.Distance(position, aircraft.transform.position) > k_SpawnMatchDistance)
                return;
            var model = CargoDropModel.Get(unit.definition);
            if (model == null)
                return;

            var velocity = aircraft.rb.GetPointVelocity(position);
            CargoDropSimulator.Predict(model, position, velocity, out s_TrackedPrediction);
            // What the HUD showed just before release, so a missed warning can be told
            // from a wrong simulation. After Fire the HUD moves on to the next cargo.
            var hud = CargoDropService.LastFirePrediction;
            float warningAge = Time.time - s_WarningOnAt;
            float fireAge = Time.time - CargoDropService.LastFireTime;
            bool atFire = fireAge < 30f && hud.Valid;
            s_HasFirePoint = atFire;
            s_FirePoint = hud.Point.ToGlobalPosition();
            s_Release = (fireAge < 30f ? $"spawned {fireAge:F1} s after fire (predicted {CargoDropService.LastFireDelay:F1} s, ramp {CargoDropService.LastFireRampWait:F2} s{(CargoDropService.LastFireRampSwing >= 0f ? $" for {CargoDropService.LastFireRampSwing:F1} deg at {CargoDropService.LastFireFramesPerSecond:F0} fps" : "")}), " : "")
                + $"released at {aircraft.radarAlt:F0} m, {velocity.magnitude:F0} m/s, vertical {velocity.y:F1} m/s; "
                + (atFire ? $"at fire predicted {hud.ImpactSpeed:F1} m/s{(hud.Breaks ? " BREAKS" : "")} after {hud.Time:F1} s on a {hud.Slope:F0} deg slope{(hud.PassedObstacle ? " past an obstacle" : "")} with acceleration {CargoDropService.LastFireAcceleration.magnitude:F1} m/s2 (vertical {CargoDropService.LastFireAcceleration.y:F1}) held, inputs changing {CargoDropService.LastFireInputRate:F2}/s" : "no prediction at fire")
                + (warningAge < 10f ? $", warning last on {warningAge:F1} s before" : ", warning not shown");
            s_Tracked = unit;
            s_TrackedModel = model;
            s_TrackedPredictedPoint = s_TrackedPrediction.Point.ToGlobalPosition();
            s_TrackedSince = Time.time;
            s_LastTime = Time.time;
            s_LastPosition = unit.GlobalPosition();
            s_LastVelocity = velocity;
            s_ApproachSpeed = -1f;
            s_ApproachHorizontal = -1f;
            s_LandedAt = -1f;
        }

        // Velocity comes from position each frame: on a client the server moves the
        // cargo, so its Rigidbody velocity means nothing.
        private static void Track()
        {
            float now = Time.time;
            bool gone = s_Tracked == null || s_Tracked.disabled;
            if (gone || now - s_TrackedSince > k_TrackTimeout)
            {
                LogDrop(destroyed: gone);
                return;
            }

            var position = s_Tracked.GlobalPosition();
            float dt = now - s_LastTime;
            if (dt > 0f)
            {
                if (s_LandedAt < 0f)
                    s_LastVelocity = (position - s_LastPosition) / dt;
                s_LastPosition = position;
                s_LastTime = now;
            }

            if (s_LandedAt < 0f)
            {
                var local = s_Tracked.transform.position;
                int mask = (int)PhysicsLayers.StaticsMask | (int)PhysicsLayers.ShipsMask;
                float ground = Physics.Raycast(local, Vector3.down, out var hit, 10000f, mask) ? hit.point.y : float.NegativeInfinity;
                float height = local.y - Mathf.Max(ground, Datum.LocalSeaY) - s_TrackedModel.BottomOffset;
                s_LastHeight = height;
                if (height >= k_ApproachHeight)
                {
                    s_ApproachSpeed = Mathf.Max(-s_LastVelocity.y, 0f);
                    s_ApproachHorizontal = new Vector2(s_LastVelocity.x, s_LastVelocity.z).magnitude;
                }
                bool stopped = height < k_StoppedHeight && s_LastVelocity.sqrMagnitude < k_StoppedSpeed * k_StoppedSpeed && now - s_TrackedSince > 1f;
                if (height < k_LandedHeight || stopped)
                {
                    s_LandedAt = now;
                    s_LandedPoint = position;
                    s_LandedSpeed = s_ApproachSpeed;
                    var normal = ground > float.NegativeInfinity && hit.point.y >= Datum.LocalSeaY ? hit.normal : Vector3.up;
                    s_LandedSlope = Vector3.Angle(normal, Vector3.up);
                    s_LandedTilt = Vector3.Angle(s_Tracked.transform.up, normal);
                }
            }
            else if (now - s_LandedAt > k_AfterLandingSeconds)
            {
                LogDrop(destroyed: false);
            }
        }

        private static string FirePointDistance(GlobalPosition point) =>
            s_HasFirePoint ? $", {FastMath.Distance(point, s_FirePoint):F0} m from the point at fire" : "";

        private static void LogDrop(bool destroyed)
        {
            var p = s_TrackedPrediction;
            string predicted = p.Valid
                ? $"predicted {p.ImpactSpeed:F1} m/s after {p.Time:F1} s on a {p.Slope:F0} deg slope (breaks at {p.BreakSpeed:F1}, chute {(p.ChuteOpened ? $"opens at {p.ChuteOpenTime:F1} s" : "stays shut")}{(p.InWater ? ", water" : "")}{(p.PassedObstacle ? ", passes an obstacle" : "")}{(p.DetectorGone ? ", detector off" : "")}{(p.BreaksInAir ? ", breaks on opening" : "")}{(p.Breaks ? ", BREAKS" : "")})"
                : "no prediction";
            // Destroyed on impact before a frame saw it on the ground: the last descent
            // reading is still the approach speed.
            string actual = s_LandedAt < 0f && destroyed
                ? $"destroyed after {Time.time - s_TrackedSince:F1} s, last seen {s_LastHeight:F1} m up at {s_ApproachSpeed:F1} m/s down, {s_ApproachHorizontal:F1} m/s across, {FastMath.Distance(s_LastPosition, s_TrackedPredictedPoint):F0} m from the predicted point{FirePointDistance(s_LastPosition)}"
                : s_LandedAt >= 0f
                ? $"landed at {s_LandedSpeed:F1} m/s down, {s_ApproachHorizontal:F1} m/s across (above {k_ApproachHeight:F0} m) after {s_LandedAt - s_TrackedSince:F1} s on a {s_LandedSlope:F0} deg slope, tilted {s_LandedTilt:F0} deg to it, {FastMath.Distance(s_LandedPoint, s_TrackedPredictedPoint):F0} m from the predicted point{FirePointDistance(s_LandedPoint)}"
                : "did not land";
            Plugin.Log.LogInfo($"HUD.Low cargo drop warning: drop of {s_TrackedModel.Definition.name}: {s_Release}; {predicted}; {actual}; {(destroyed ? "destroyed" : "survived")}");
            s_Tracked = null;
            s_TrackedModel = null;
        }

        [HarmonyPatch(typeof(HUDCargoState), nameof(HUDCargoState.HUDFixedUpdate))]
        private static class HUDFixedUpdatePatch
        {
            private static void Postfix(HUDCargoState __instance, Aircraft aircraft)
            {
                var station = s_Station(__instance);
                var warning = aircraft != null && station != null ? Evaluate(aircraft, station) : Warning.None;
                Show(__instance, warning);
            }
        }

        [HarmonyPatch(typeof(Container), nameof(Container.InitializeUnit))]
        private static class ContainerSpawnPatch
        {
            private static void Postfix(Container __instance) => OnCargoSpawned(__instance);
        }

        [HarmonyPatch(typeof(GroundVehicle), nameof(GroundVehicle.InitializeUnit))]
        private static class VehicleSpawnPatch
        {
            private static void Postfix(GroundVehicle __instance) => OnCargoSpawned(__instance);
        }
    }
}
