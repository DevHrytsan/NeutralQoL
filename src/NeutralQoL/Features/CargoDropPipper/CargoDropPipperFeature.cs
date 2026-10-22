using System;
using BepInEx.Configuration;
using HarmonyLib;
using NeutralQoL.Core;
using NeutralQoL.Features.CargoDropAid;
using NeutralQoL.Shared.CargoDrop;
using NuclearOption.UIStyleSystem;
using UnityEngine;
using UnityEngine.UI;

namespace NeutralQoL.Features.CargoDropPipper
{
    internal sealed class CargoDropPipperFeature : Feature
    {
        public override string Section => "HUD";
        public override string Name => "Drop marker";
        public override string Description =>
            "Marks on the ground where the cargo would land if dropped now: a square with a cross, drawn like the runway outline on approach. It turns yellow when the drop would destroy the cargo.";
        public override Type PartOf => typeof(CargoDropAidFeature);
        protected override bool DefaultOn => true;

        protected override Type[] PatchClasses => new[] { typeof(CargoStatePatch), typeof(OverlayLateUpdatePatch) };

        // Side of the ground square. The prediction assumes steady inputs through the
        // release delay, so the square grows with the input rate to show how far to trust it.
        private const float k_MinSize = 10f;
        private const float k_MaxSize = 100f;
        // Metres of side per full deflection per second of input change, per second of
        // release delay: a full stick move over 1 s with a 1.6 s delay adds about 16 m.
        private const float k_InputGrowth = 10f;
        // Metres of side per second of predicted flight; wind and chute errors add up.
        // Logged drops landed 4 to 28 m off after 3 to 9 s, 48 m after 19 s.
        private const float k_SizePerFlightSecond = 2f;
        // Grows when a manoeuvre starts, shrinks slower once it ends, so it changes calmly.
        private const float k_GrowSeconds = 0.4f;
        private const float k_ShrinkSeconds = 1.5f;
        // Four sides, then the two diagonals.
        private const int k_LineCount = 6;
        // The prediction refreshes every 0.1 s; the marker eases over about this long
        // instead of jumping.
        private const float k_EaseSeconds = 0.15f;
        // Hidden when the landing point is within this many aircraft sizes (2 * maxRadius):
        // hovering or flying low, it would sit around the aircraft and only clutter.
        private const float k_HideWithinSizes = 3f;
        // Past this distance (new station, floating origin shift) it jumps.
        private const float k_SnapDistance = 200f;

        // The drop mark holds, then fades over this last part of its time.
        private const float k_DropFadeSeconds = 1f;
        private const float k_DropSeconds = 3.5f;
        private static readonly Color k_DropColor = new Color(1f, 0.15f, 0.1f, 0.5f);

        private static ConfigEntry<bool> s_MarkDrop;

        private static AccessTools.FieldRef<HUDCargoState, WeaponStation> s_Station;
        private static AccessTools.FieldRef<AirbaseOverlay, Image[]> s_RunwayBorders;

        // Cargo mode on the HUD, destroyed when another station is selected.
        private static HUDCargoState s_CargoState;
        private static readonly Image[] s_Lines = new Image[k_LineCount];
        private static readonly Vector3[] s_Corners = new Vector3[4];
        private static Color s_NormalColor;
        private static bool s_Shown;
        private static Vector3 s_Point;
        private static Vector3 s_Normal;
        private static float s_Size = k_MinSize;
        private static Vector3 s_Forward = Vector3.forward;

        private static readonly Image[] s_DropLines = new Image[k_LineCount];
        private static bool s_DropShown;
        private static float s_SeenFireTime = float.NegativeInfinity;
        private static float s_DropAt = float.NegativeInfinity;
        private static GlobalPosition s_DropPoint;
        private static Vector3 s_DropNormal;
        private static Vector3 s_DropForward;
        private static float s_DropSize;

        protected override void BindOptions()
        {
            s_MarkDrop = Bind("Red drop mark", true,
                "When you drop cargo, leaves a red see-through copy of the marker where it pointed, which fades out after a few seconds.");
        }

        protected override void Resolve()
        {
            Targets.Method(typeof(HUDCargoState), nameof(HUDCargoState.HUDFixedUpdate));
            Targets.Method(typeof(AirbaseOverlay), "LateUpdate");
            s_Station = Targets.Field<HUDCargoState, WeaponStation>("weaponStation");
            s_RunwayBorders = Targets.Field<AirbaseOverlay, Image[]>("runwayBorders");
            CargoDropModel.Resolve();
        }

        protected override void OnEnable()
        {
            CargoDropService.Reset();
            // Drops from before the feature was switched on are not marked.
            s_SeenFireTime = CargoDropService.LastFireTime;
            s_DropAt = float.NegativeInfinity;
        }

        protected override void OnDisable()
        {
            DestroyLines(s_Lines);
            DestroyLines(s_DropLines);
            s_CargoState = null;
            s_Shown = false;
            s_DropShown = false;
        }

        private static void DestroyLines(Image[] lines)
        {
            for (int i = 0; i < k_LineCount; i++)
            {
                if (lines[i] != null)
                    UnityEngine.Object.Destroy(lines[i].gameObject);
                lines[i] = null;
            }
        }

        private static bool EnsureLines(AirbaseOverlay overlay)
        {
            if (s_Lines[0] != null)
                return true;
            var borders = s_RunwayBorders(overlay);
            if (borders == null || borders.Length == 0 || borders[0] == null)
                return false;
            var template = borders[0];
            s_NormalColor = template.color;
            CreateLines(template, s_Lines, "NeutralQoL cargo drop marker");
            CreateLines(template, s_DropLines, "NeutralQoL cargo drop point");
            s_Shown = false;
            s_DropShown = false;
            return true;
        }

        private static void CreateLines(Image template, Image[] lines, string name)
        {
            for (int i = 0; i < k_LineCount; i++)
            {
                var line = UnityEngine.Object.Instantiate(template, template.transform.parent);
                line.name = name;
                line.enabled = false;
                lines[i] = line;
            }
        }

        private static void SetShown(Image[] lines, ref bool shown, bool value)
        {
            if (shown == value)
                return;
            shown = value;
            for (int i = 0; i < k_LineCount; i++)
                lines[i].enabled = value;
        }

        private static void SetShown(bool shown) => SetShown(s_Lines, ref s_Shown, shown);

        private static void Draw(AirbaseOverlay overlay)
        {
            if (!EnsureLines(overlay))
                return;
            var aircraft = SceneSingleton<CombatHUD>.i != null ? SceneSingleton<CombatHUD>.i.aircraft : null;
            var station = s_CargoState != null ? s_Station(s_CargoState) : null;
            var camera = SceneSingleton<CameraStateManager>.i != null ? SceneSingleton<CameraStateManager>.i.mainCamera : null;
            var prediction = default(CargoDropPrediction);
            bool predicted = aircraft != null && station != null && camera != null
                && CargoDropService.TryGet(aircraft, station, out prediction, out _);

            // TryGet already moved on to the next cargo; the marker still holds last frame's
            // point, which is where the pilot fired.
            if (CargoDropService.LastFireTime != s_SeenFireTime)
            {
                s_SeenFireTime = CargoDropService.LastFireTime;
                if (s_Shown && s_MarkDrop.Value)
                {
                    s_DropAt = Time.time;
                    s_DropPoint = s_Point.ToGlobalPosition();
                    s_DropNormal = s_Normal;
                    s_DropForward = s_Forward;
                    s_DropSize = s_Size;
                }
            }
            DrawDrop(camera);

            if (!predicted)
            {
                SetShown(false);
                return;
            }

            var target = prediction.Point;
            float hideWithin = k_HideWithinSizes * 2f * aircraft.maxRadius;
            if ((target - aircraft.transform.position).sqrMagnitude < hideWithin * hideWithin)
            {
                SetShown(false);
                return;
            }
            var targetNormal = prediction.Normal.sqrMagnitude > 0.5f ? prediction.Normal : Vector3.up;
            float targetSize = Mathf.Clamp(k_MinSize + k_SizePerFlightSecond * prediction.Time + k_InputGrowth * CargoDropService.InputRate * CargoDropService.ReleaseDelay, k_MinSize, k_MaxSize);
            if (!s_Shown || (s_Point - target).sqrMagnitude > k_SnapDistance * k_SnapDistance)
            {
                s_Point = target;
                s_Normal = targetNormal;
                s_Size = targetSize;
            }
            else
            {
                // The marker moves with the aircraft like the target, so easing only smooths the
                // refresh steps and adds no lag.
                s_Point += aircraft.rb.velocity * Time.deltaTime;
                float k = 1f - Mathf.Exp(-Time.deltaTime / k_EaseSeconds);
                s_Point = Vector3.Lerp(s_Point, target, k);
                s_Normal = Vector3.Slerp(s_Normal, targetNormal, k);
                float sizeSeconds = targetSize > s_Size ? k_GrowSeconds : k_ShrinkSeconds;
                s_Size = Mathf.Lerp(s_Size, targetSize, 1f - Mathf.Exp(-Time.deltaTime / sizeSeconds));
            }

            // Square lies on the ground, turned with the heading so its sides read along
            // and across the drop.
            var forward = Vector3.ProjectOnPlane(aircraft.transform.forward, s_Normal);
            if (forward.sqrMagnitude < 1e-4f)
                forward = Vector3.ProjectOnPlane(aircraft.transform.up, s_Normal);
            s_Forward = forward.normalized;
            var color = prediction.Breaks ? ThemeManager.Active.ColorTheme.Warning : s_NormalColor;
            SetShown(PlaceSquare(s_Lines, camera, s_Point, s_Normal, s_Forward, s_Size, color));
        }

        // The red copy stays where the marker was at Fire (moved with the floating
        // origin) and fades at the end of its time.
        private static void DrawDrop(Camera camera)
        {
            float age = Time.time - s_DropAt;
            float seconds = k_DropSeconds;
            if (camera == null || !s_MarkDrop.Value || age < 0f || age >= seconds)
            {
                SetShown(s_DropLines, ref s_DropShown, false);
                return;
            }
            var color = k_DropColor;
            color.a *= Mathf.Clamp01((seconds - age) / k_DropFadeSeconds);
            SetShown(s_DropLines, ref s_DropShown, PlaceSquare(s_DropLines, camera, s_DropPoint.ToLocalPosition(), s_DropNormal, s_DropForward, s_DropSize, color));
        }

        // Returns false and places nothing when the square is partly behind the camera.
        private static bool PlaceSquare(Image[] lines, Camera camera, Vector3 center, Vector3 normal, Vector3 direction, float size, Color color)
        {
            var forward = direction * (size * 0.5f);
            var right = Vector3.Cross(normal, forward);
            s_Corners[0] = center - forward - right;
            s_Corners[1] = center + forward - right;
            s_Corners[2] = center + forward + right;
            s_Corners[3] = center - forward + right;

            // As AirbaseOverlay.DrawRunwayBorders: hidden if any corner is behind the camera;
            // screen points in the overlay canvas.
            var cameraTransform = camera.transform;
            for (int i = 0; i < 4; i++)
            {
                if (Vector3.Dot(cameraTransform.forward, s_Corners[i] - cameraTransform.position) <= 0f)
                    return false;
                var screen = camera.WorldToScreenPoint(s_Corners[i]);
                screen.z = 0f;
                s_Corners[i] = screen;
            }

            float scale = 1080f / Screen.height;
            PlaceLine(lines[0], s_Corners[0], s_Corners[1], scale, color);
            PlaceLine(lines[1], s_Corners[1], s_Corners[2], scale, color);
            PlaceLine(lines[2], s_Corners[2], s_Corners[3], scale, color);
            PlaceLine(lines[3], s_Corners[3], s_Corners[0], scale, color);
            PlaceLine(lines[4], s_Corners[0], s_Corners[2], scale, color);
            PlaceLine(lines[5], s_Corners[1], s_Corners[3], scale, color);
            return true;
        }

        // The runway line is a 1x1 image pivoted at its bottom, stretched along its length.
        private static void PlaceLine(Image line, Vector3 from, Vector3 to, float scale, Color color)
        {
            var delta = to - from;
            var t = line.transform;
            t.position = from;
            t.eulerAngles = new Vector3(0f, 0f, -Mathf.Atan2(delta.x, delta.y) * Mathf.Rad2Deg);
            t.localScale = Vector3.one + Vector3.up * (delta.magnitude * scale);
            line.color = color;
        }

        [HarmonyPatch(typeof(HUDCargoState), nameof(HUDCargoState.HUDFixedUpdate))]
        private static class CargoStatePatch
        {
            private static void Postfix(HUDCargoState __instance)
            {
                s_CargoState = __instance;
            }
        }

        [HarmonyPatch(typeof(AirbaseOverlay), "LateUpdate")]
        private static class OverlayLateUpdatePatch
        {
            private static void Postfix(AirbaseOverlay __instance)
            {
                Draw(__instance);
            }
        }
    }
}
