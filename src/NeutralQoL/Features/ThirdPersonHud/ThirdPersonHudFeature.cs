using System;
using BepInEx.Configuration;
using HarmonyLib;
using NeutralQoL.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NeutralQoL.Features.ThirdPersonHud
{
    internal sealed class ThirdPersonHudFeature : Feature
    {
        // CheckHUD's own rule for chase camera positions that can show the HUD.
        private static readonly string[] s_HudPositions = { "Back", "Tail", "Belly", "WingRootL", "WingRootR" };

        public override string Section => "HUD";
        public override string Name => "Flight HUD in third person";
        public override string Description =>
            "Shows your flight HUD and minimap outside the cockpit, in the chase and orbit views, for your own aircraft. The chase view only shows it while the camera sits behind or under the aircraft, as the Mission Editor does.";

        protected override Type[] PatchClasses => new[] { typeof(ChaseInputPatch), typeof(OrbitUpdatePatch), typeof(HudUpdatePatch), typeof(FuelGaugePatch), typeof(ThrottleGaugePatch), typeof(CombatLatePatch), typeof(CameraLatePatch) };

        private static ConfigEntry<bool> s_InChase;
        private static ConfigEntry<bool> s_InOrbit;
        private static ConfigEntry<bool> s_LevelHud;
        private static ConfigEntry<bool> s_FixedCompass;
        private static AccessTools.FieldRef<FlightHud, Transform> s_HudCenter;
        private static AccessTools.FieldRef<FlightHud, RawImage> s_Compass;
        private static AccessTools.FieldRef<FlightHud, GameObject> s_PitchCenter;
        private static AccessTools.FieldRef<FuelGauge, TextMeshProUGUI> s_FuelReading;
        private static AccessTools.FieldRef<ThrottleGauge, TextMeshProUGUI> s_ThrottleReading;
        // Compass pose before we moved it, restored on leaving third person.
        private static Transform s_MovedCompass;
        private static Vector3 s_CompassLocalPos;
        private static Quaternion s_CompassLocalRot;
        private static bool s_CompassChecked;
        private static Graphic[] s_CompassGraphics;
        private static readonly Vector3[] s_Corners = new Vector3[4];
        // Gap between the compass and the top screen edge, as a share of the
        // height.
        private const float k_CompassTopMargin = 0.02f;
        private static Func<CameraChaseState, int> s_CurrentPos;
        private static AccessTools.FieldRef<CameraChaseState, bool> s_ShowHud;
        private static AccessTools.FieldRef<FlightHud, Canvas> s_HudCanvas;
        private static AccessTools.FieldRef<FlightHud, Aircraft> s_HudAircraft;
        private static bool[] s_HudPosition;
        // Camera state whose HUD we switched on, so we only hide a HUD we showed.
        private static CameraBaseState s_Driven;
        // Minimap state before we showed the HUD.
        private static bool s_MapWasOn;
        private static CameraBaseState s_MapOwner;

        protected override void BindOptions()
        {
            s_InChase = Bind("Chase view", true,
                "Show the HUD in the chase view (Center key from the orbit view), while the camera sits behind or under the aircraft.");
            s_InOrbit = Bind("Orbit view", true,
                "Show the HUD in the orbit view (Switch View from the cockpit).");
            s_LevelHud = Bind("Level with horizon", false,
                "Keep the HUD level with the horizon instead of tilting it as the aircraft banks.");
            s_FixedCompass = Bind("Fixed compass", false,
                "Keep the heading compass at the top middle of the screen instead of moving it with the point ahead of the aircraft.");
        }

        protected override void Resolve()
        {
            Targets.Method(typeof(CameraChaseState), nameof(CameraChaseState.CheckInput));
            Targets.Method(typeof(CameraChaseState), nameof(CameraChaseState.CheckHUD));
            Targets.Method(typeof(CameraOrbitState), nameof(CameraOrbitState.UpdateState));
            s_CurrentPos = Targets.EnumField<CameraChaseState>("currentPos");
            s_ShowHud = Targets.Field<CameraChaseState, bool>("showHUD");
            s_HudCanvas = Targets.Field<FlightHud, Canvas>("canvas");
            s_HudAircraft = Targets.Field<FlightHud, Aircraft>("aircraft");
            Targets.Method(typeof(FlightHud), "Update");
            s_HudCenter = Targets.Field<FlightHud, Transform>("HUDCenter");
            s_Compass = Targets.Field<FlightHud, RawImage>("compass");
            s_PitchCenter = Targets.Field<FlightHud, GameObject>("pitchCompassCenter");
            Targets.Method(typeof(FuelGauge), "UpdateFuelGauge");
            Targets.Method(typeof(ThrottleGauge), "UpdateThrottleDisplay");
            s_FuelReading = Targets.Field<FuelGauge, TextMeshProUGUI>("fuelReading");
            s_ThrottleReading = Targets.Field<ThrottleGauge, TextMeshProUGUI>("throttleReading");
            s_CombatLateUpdate = AccessTools.MethodDelegate<Action<CombatHUD>>(Targets.Method(typeof(CombatHUD), "LateUpdate"));
            Targets.Method(typeof(CameraStateManager), "LateUpdate");

            var positions = AccessTools.Field(typeof(CameraChaseState), "currentPos").FieldType;
            int max = 0;
            foreach (int value in Enum.GetValues(positions))
                max = Math.Max(max, value);
            s_HudPosition = new bool[max + 1];
            foreach (var name in s_HudPositions)
            {
                if (!Enum.IsDefined(positions, name))
                    throw new MissingMemberException($"chase camera position {name} missing");
                s_HudPosition[(int)Enum.Parse(positions, name)] = true;
            }
        }

        protected override void OnDisable()
        {
            var cam = SceneSingleton<CameraStateManager>.i;
            if (s_Driven != null && cam != null && cam.currentState == s_Driven)
                Hide(s_Driven);
            s_Driven = null;
            s_MapOwner = null;
            RestoreCompass();
        }

        private static void RestoreCompass()
        {
            if (s_MovedCompass == null)
                return;
            s_MovedCompass.localPosition = s_CompassLocalPos;
            s_MovedCompass.localRotation = s_CompassLocalRot;
            s_MovedCompass = null;
        }

        // FlightHud.Update rolls HUDCenter by bank and puts it 4000 m ahead on screen.
        // Only the drawing changes: same readings, steadier place.
        private static void Arrange(FlightHud hud)
        {
            var cam = SceneSingleton<CameraStateManager>.i;
            bool driving = s_Driven != null && cam != null && cam.currentState == s_Driven;
            var center = s_HudCenter(hud);
            if (!driving || center == null)
            {
                RestoreCompass();
                return;
            }

            if (s_LevelHud.Value)
            {
                // Update already placed the ladder and path marker; rolling their
                // parent would tilt them, so keep their pose.
                var ladder = s_PitchCenter(hud) != null ? s_PitchCenter(hud).transform : null;
                var marker = hud.velocityVector != null ? hud.velocityVector.transform : null;
                var ladderRot = ladder != null ? ladder.rotation : Quaternion.identity;
                var markerPos = marker != null ? marker.position : Vector3.zero;
                // As the game does for the pitch ladder (pitchCompassCenter).
                center.eulerAngles = new Vector3(0f, 0f, -cam.mainCamera.transform.eulerAngles.z);
                if (ladder != null)
                    ladder.rotation = ladderRot;
                if (marker != null)
                    marker.position = markerPos;
            }

            var compass = s_Compass(hud) != null ? s_Compass(hud).transform : null;
            if (!s_FixedCompass.Value || compass == null)
            {
                RestoreCompass();
                return;
            }
            if (!compass.IsChildOf(center))
            {
                // Then the compass never followed the aircraft and needs no
                // fix.
                if (!s_CompassChecked)
                    Plugin.Log.LogInfo("HUD.Flight HUD in third person: compass is not under HUDCenter, Fixed compass does nothing");
                s_CompassChecked = true;
                return;
            }

            // Move the whole compass branch so the heading box and caret come along.
            var group = compass;
            while (group.parent != center)
                group = group.parent;

            if (s_MovedCompass != group)
            {
                RestoreCompass();
                s_MovedCompass = group;
                s_CompassLocalPos = group.localPosition;
                s_CompassLocalRot = group.localRotation;
                s_CompassGraphics = group.GetComponentsInChildren<Graphic>(true);
                Plugin.Log.LogInfo($"HUD.Flight HUD in third person: fixed compass moves '{group.name}' ({s_CompassGraphics.Length} graphics)");
            }
            else
            {
                group.localPosition = s_CompassLocalPos;
                group.localRotation = s_CompassLocalRot;
            }

            group.rotation = Quaternion.Inverse(center.rotation) * group.rotation;
            // Top of the branch, so the heading box sits just under the screen edge.
            // The canvas is screen space, so world corners are pixels.
            float top = float.MinValue;
            for (int i = 0; i < s_CompassGraphics.Length; i++)
            {
                var graphic = s_CompassGraphics[i];
                if (graphic == null || !graphic.isActiveAndEnabled)
                    continue;
                graphic.rectTransform.GetWorldCorners(s_Corners);
                for (int c = 0; c < 4; c++)
                    top = Mathf.Max(top, s_Corners[c].y);
            }
            if (top == float.MinValue)
                return;
            float dx = Screen.width * 0.5f - compass.position.x;
            float dy = Screen.height * (1f - k_CompassTopMargin) - top;
            // With the point ahead behind the camera, HUDCenter keeps a non-zero depth
            // that hides its children. Put the compass back on the canvas plane.
            var pos = group.position;
            group.position = new Vector3(pos.x + dx, pos.y + dy, s_HudCanvas(hud).transform.position.z);
        }

        [HarmonyPatch(typeof(FlightHud), "Update")]
        private static class HudUpdatePatch
        {
            private static void Postfix(FlightHud __instance)
            {
                Arrange(__instance);
            }
        }

        // Gauges rotate their text by camera roll minus cockpit roll, about 0 in the
        // cockpit. In third person the camera stays level, so the text took the
        // aircraft's bank. Keep the gauge's own rotation, as the cockpit shows.
        private static void AlignReading(HUDApp gauge, TextMeshProUGUI reading)
        {
            if (reading == null)
                return;
            var cam = SceneSingleton<CameraStateManager>.i;
            if (s_Driven == null || cam == null || cam.currentState != s_Driven)
                return;
            reading.transform.rotation = gauge.transform.rotation;
        }

        [HarmonyPatch(typeof(FuelGauge), "UpdateFuelGauge")]
        private static class FuelGaugePatch
        {
            private static void Postfix(FuelGauge __instance)
            {
                AlignReading(__instance, s_FuelReading(__instance));
            }
        }

        [HarmonyPatch(typeof(ThrottleGauge), "UpdateThrottleDisplay")]
        private static class ThrottleGaugePatch
        {
            private static void Postfix(ThrottleGauge __instance)
            {
                AlignReading(__instance, s_ThrottleReading(__instance));
            }
        }

        // CombatHUD.LateUpdate projects unit markers through the main camera, but the
        // chase and orbit cameras move in CameraStateManager.LateUpdate, in no set
        // order. If the HUD ran first, markers slid off their units. So the game's
        // LateUpdate is held back and run once, unchanged, right after the camera moves.
        private static Action<CombatHUD> s_CombatLateUpdate;
        private static int s_CombatFrame = -1;
        private static int s_HeldFrame = -1;
        private static bool s_RunningCombat;

        private static bool Driving()
        {
            var cam = SceneSingleton<CameraStateManager>.i;
            return s_Driven != null && cam != null && cam.currentState == s_Driven;
        }

        [HarmonyPatch(typeof(CombatHUD), "LateUpdate")]
        private static class CombatLatePatch
        {
            private static bool Prefix()
            {
                if (s_RunningCombat)
                    return true;
                int frame = Time.frameCount;
                // Already run after the camera this frame.
                if (s_CombatFrame == frame)
                    return false;
                if (Driving())
                {
                    s_HeldFrame = frame;
                    return false;
                }
                s_CombatFrame = frame;
                return true;
            }
        }

        [HarmonyPatch(typeof(CameraStateManager), "LateUpdate")]
        private static class CameraLatePatch
        {
            private static void Postfix()
            {
                int frame = Time.frameCount;
                // A call held back this frame still runs if the view just
                // ended.
                if (s_CombatFrame == frame || (!Driving() && s_HeldFrame != frame))
                    return;
                var hud = SceneSingleton<CombatHUD>.i;
                if (hud == null)
                    return;
                s_CombatFrame = frame;
                s_RunningCombat = true;
                try
                {
                    s_CombatLateUpdate(hud);
                }
                finally
                {
                    s_RunningCombat = false;
                }
            }
        }

        // Why the HUD stays hidden in any third person view, or null to show
        // it.
        private static string HiddenReason(CameraStateManager cam, FlightHud hud)
        {
            return !GameManager.flightControlsEnabled ? "hidden: flight controls off (paused or menu)"
                : DynamicMap.mapMaximized ? "hidden: big map open"
                : !GameManager.IsLocalAircraft(cam.followingUnit) ? "hidden: camera follows another unit"
                : s_HudAircraft(hud) != cam.followingUnit ? "hidden: flight HUD shows another aircraft"
                : null;
        }

        private static void Show(CameraBaseState state)
        {
            // Recorded once per view: after the big map closes it leaves the
            // minimap on, which says nothing about how the player had it.
            if (s_MapOwner != state)
            {
                s_MapWasOn = SceneSingleton<DynamicMap>.i != null && SceneSingleton<DynamicMap>.i.gameObject.activeSelf;
                s_MapOwner = state;
            }
            if (state is CameraChaseState chase)
            {
                s_ShowHud(chase) = true;
                chase.CheckHUD();
            }
            else
            {
                FlightHud.EnableCanvas(true);
                if (SceneSingleton<DynamicMap>.i != null)
                    DynamicMap.EnableCanvas(true);
            }
            s_Driven = state;
        }

        private static void Hide(CameraBaseState state)
        {
            if (state is CameraChaseState chase)
            {
                s_ShowHud(chase) = false;
                chase.CheckHUD();
            }
            else
            {
                FlightHud.EnableCanvas(false);
            }
            // The big map is the same object as the minimap: never switch it off while
            // open (opening it is what hides the HUD), or it closes at once.
            if (SceneSingleton<DynamicMap>.i != null && !s_MapWasOn && !DynamicMap.mapMaximized)
                DynamicMap.EnableCanvas(false);
            s_Driven = null;
        }

        // Every frame, so the HUD also returns after the pause menu or big map.
        private static void Drive(CameraBaseState state, CameraStateManager cam, bool enabled, bool positionShowsHud)
        {
            if (GameManager.gameState == GameState.Editor || cam.currentState != state)
                return;
            var hud = SceneSingleton<FlightHud>.i;
            if (hud == null)
            {
                Report("hidden: no flight HUD in this scene");
                return;
            }
            string reason = !enabled ? "hidden: off for this view"
                : HiddenReason(cam, hud)
                ?? (positionShowsHud ? null : "hidden: camera position without HUD");
            bool want = reason == null;
            Report(reason ?? "shown");
            bool shown = s_HudCanvas(hud).gameObject.activeSelf;
            if (want == shown || (!want && s_Driven != state))
                return;
            if (want)
                Show(state);
            else
                Hide(state);
            if (want && !s_HudCanvas(hud).gameObject.activeInHierarchy)
                Report("shown, but the HUD canvas is still inactive");
        }

        [HarmonyPatch(typeof(CameraChaseState), nameof(CameraChaseState.CheckInput))]
        private static class ChaseInputPatch
        {
            private static void Postfix(CameraChaseState __instance, CameraStateManager cam)
            {
                int pos = s_CurrentPos(__instance);
                Drive(__instance, cam, s_InChase.Value, pos >= 0 && pos < s_HudPosition.Length && s_HudPosition[pos]);
            }
        }

        [HarmonyPatch(typeof(CameraOrbitState), nameof(CameraOrbitState.UpdateState))]
        private static class OrbitUpdatePatch
        {
            private static void Postfix(CameraOrbitState __instance, CameraStateManager cam)
            {
                Drive(__instance, cam, s_InOrbit.Value, true);
            }
        }

        // Logs one line when the decision changes. Reasons are constants.
        private static string s_LastReport;

        private static void Report(string reason)
        {
            if (ReferenceEquals(reason, s_LastReport))
                return;
            s_LastReport = reason;
            Plugin.Log.LogInfo($"HUD.Flight HUD in third person: {reason}");
        }

        public override void Tick()
        {
            // Logs every camera view change, to show which view was reached.
            var cam = SceneSingleton<CameraStateManager>.i;
            var state = cam != null ? cam.currentState : null;
            if (state == s_LastState)
                return;
            s_LastState = state;
            s_LastReport = null;
            s_MapOwner = null;
            // Only cockpit view decides about the minimap, so put back ours.
            if (s_Driven != null && state != s_Driven)
            {
                if (!s_MapWasOn && !(state is CameraCockpitState) && SceneSingleton<DynamicMap>.i != null && !DynamicMap.mapMaximized)
                    DynamicMap.EnableCanvas(false);
                s_Driven = null;
            }
            if (state != null)
                Plugin.Log.LogInfo($"HUD.Flight HUD in third person: camera view is now {state.GetType().Name}");
        }

        private static CameraBaseState s_LastState;
    }
}
