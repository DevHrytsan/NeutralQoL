using System;
using BepInEx.Configuration;
using HarmonyLib;
using NeutralQoL.Core;
using UnityEngine;

namespace NeutralQoL.Features.ZoomLookSensitivity
{
    internal sealed class ZoomLookSensitivityFeature : Feature
    {
        // Orbit and free camera already multiply look speed by min(fov / 20, 1).
        private const float k_VanillaExternalFov = 20f;
        private const float k_MinFov = 1f;

        public override string Section => "Camera";
        public override string Name => "Zoom-consistent look";
        protected override bool DefaultOn => true;
        public override string Description =>
            "Free look slows down while zoomed in (and speeds up while zoomed out), so a mouse move turns the view by the same share of the screen at every zoom. Cockpit always, orbit and free camera as an option.";

        protected override Type[] PatchClasses => new[] { typeof(CockpitLookPatch), typeof(OrbitLookPatch), typeof(FreeLookPatch) };

        private static ConfigEntry<float> s_Strength;
        private static ConfigEntry<bool> s_ThirdPerson;

        protected override void BindOptions()
        {
            s_Strength = Bind("Strength (ratio)", 1f,
                "1 keeps the look speed fully in step with the zoom, 0 is the game's normal behaviour.",
                new AcceptableValueRange<float>(0f, 1f));
            s_ThirdPerson = Bind("Third person views", true,
                "Also in the orbit view and the free camera, measured from the default external field of view in the Gameplay settings.");
        }

        protected override void Resolve()
        {
            Targets.Method(typeof(CameraCockpitState), nameof(CameraCockpitState.UpdateState));
            Targets.Method(typeof(CameraOrbitState), nameof(CameraOrbitState.UpdateState));
            Targets.Method(typeof(CameraFreeState), nameof(CameraFreeState.UpdateState));
            Targets.StaticField(typeof(PlayerSettings), nameof(PlayerSettings.viewSensitivity));
            Targets.StaticField(typeof(PlayerSettings), nameof(PlayerSettings.defaultFoV));
            Targets.StaticField(typeof(PlayerSettings), nameof(PlayerSettings.defaultExternalFoV));
        }

        // Look speed factor at this zoom, relative to the zoom where look speed is the
        // player's own setting.
        private static float Factor(CameraStateManager cam, float baseline)
        {
            if (baseline <= 0f || cam == null || cam.mainCamera == null)
                return 1f;
            return Mathf.Lerp(1f, cam.mainCamera.fieldOfView / baseline, s_Strength.Value);
        }

        // Replaces the game's min(fov / 20, 1) instead of stacking on it.
        private static float ExternalFactor(CameraStateManager cam)
        {
            if (!s_ThirdPerson.Value || cam == null || cam.mainCamera == null)
                return 1f;
            float fov = Mathf.Max(cam.mainCamera.fieldOfView, k_MinFov);
            return Factor(cam, PlayerSettings.defaultExternalFoV) / Mathf.Min(fov / k_VanillaExternalFov, 1f);
        }

        [HarmonyPatch(typeof(CameraCockpitState), nameof(CameraCockpitState.UpdateState))]
        private static class CockpitLookPatch
        {
            private static void Prefix(CameraStateManager cam, out float __state)
            {
                __state = PlayerSettings.viewSensitivity;
                PlayerSettings.viewSensitivity = __state * Factor(cam, PlayerSettings.defaultFoV);
            }

            private static void Finalizer(float __state)
            {
                PlayerSettings.viewSensitivity = __state;
            }
        }

        [HarmonyPatch(typeof(CameraOrbitState), nameof(CameraOrbitState.UpdateState))]
        private static class OrbitLookPatch
        {
            private static void Prefix(CameraStateManager cam, out float __state)
            {
                __state = PlayerSettings.viewSensitivity;
                PlayerSettings.viewSensitivity = __state * ExternalFactor(cam);
            }

            private static void Finalizer(float __state)
            {
                PlayerSettings.viewSensitivity = __state;
            }
        }

        [HarmonyPatch(typeof(CameraFreeState), nameof(CameraFreeState.UpdateState))]
        private static class FreeLookPatch
        {
            private static void Prefix(CameraStateManager cam, out float __state)
            {
                __state = PlayerSettings.viewSensitivity;
                PlayerSettings.viewSensitivity = __state * ExternalFactor(cam);
            }

            private static void Finalizer(float __state)
            {
                PlayerSettings.viewSensitivity = __state;
            }
        }
    }
}
