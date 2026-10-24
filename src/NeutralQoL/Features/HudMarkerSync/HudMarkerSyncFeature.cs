using System;
using HarmonyLib;
using NeutralQoL.Core;
using UnityEngine;

namespace NeutralQoL.Features.HudMarkerSync
{
    internal sealed class HudMarkerSyncFeature : Feature
    {
        public override string Section => "HUD";
        public override string Name => "Markers follow the view";
        public override string Description =>
            "Places unit markers, objective pointers and landing markers after the camera has moved each frame, so they stay on their targets while you look around or turn instead of trailing behind.";

        public override bool AlwaysOn => true;
        protected override Type[] PatchClasses => new[] { typeof(CombatLatePatch), typeof(ObjectiveUpdatePatch), typeof(AirbaseLatePatch), typeof(FlightHudUpdatePatch), typeof(CameraLatePatch), typeof(OriginShiftPatch) };

        private static readonly Held<CombatHUD> s_Combat = new Held<CombatHUD>();
        private static readonly Held<ObjectiveOverlayManager> s_Objectives = new Held<ObjectiveOverlayManager>();
        private static readonly Held<AirbaseOverlay> s_Airbase = new Held<AirbaseOverlay>();
        // HUD center and velocity vector. Weapon sights draw lines to them.
        private static readonly Held<FlightHud> s_Flight = new Held<FlightHud>();
        private static int s_CameraFrame = -1;
        // True while CameraStateManager.LateUpdate runs.
        private static bool s_InCameraLate;

        protected override void Resolve()
        {
            s_Combat.Call = AccessTools.MethodDelegate<Action<CombatHUD>>(Targets.Method(typeof(CombatHUD), "LateUpdate"));
            s_Objectives.Call = AccessTools.MethodDelegate<Action<ObjectiveOverlayManager>>(Targets.Method(typeof(ObjectiveOverlayManager), "Update"));
            s_Airbase.Call = AccessTools.MethodDelegate<Action<AirbaseOverlay>>(Targets.Method(typeof(AirbaseOverlay), "LateUpdate"));
            s_Flight.Call = AccessTools.MethodDelegate<Action<FlightHud>>(Targets.Method(typeof(FlightHud), "Update"));
            Targets.Method(typeof(CameraStateManager), "LateUpdate");
            Targets.Method(typeof(CameraStateManager), nameof(CameraStateManager.CheckOriginShift));
        }

        protected override void OnDisable()
        {
            s_Combat.Drop();
            s_Objectives.Drop();
            s_Airbase.Drop();
            s_Flight.Drop();
            s_InCameraLate = false;
        }

        // Runs every held HUD update once, HUD center first.
        private static void ReleaseAll()
        {
            s_CameraFrame = Time.frameCount;
            s_Flight.Release();
            s_Objectives.Release();
            s_Combat.Release();
            s_Airbase.Release();
        }

        // Whether the camera still moves later this frame. Once it has, or with
        // no camera to wait for, the game's call runs at its usual time.
        private static bool CameraPending()
        {
            var cam = SceneSingleton<CameraStateManager>.i;
            return cam != null && cam.isActiveAndEnabled && s_CameraFrame != Time.frameCount;
        }

        private sealed class Held<T> where T : MonoBehaviour
        {
            public Action<T> Call;
            private T m_Instance;
            private bool m_Pending;
            private bool m_Running;

            // Prefix result: true lets the game's call run now.
            public bool Hold(T instance)
            {
                if (m_Running || !CameraPending())
                    return true;
                m_Instance = instance;
                m_Pending = true;
                return false;
            }

            public void Release()
            {
                if (!m_Pending)
                    return;
                m_Pending = false;
                // Destroyed or switched off since Update held it back.
                if (m_Instance == null || !m_Instance.isActiveAndEnabled)
                    return;
                m_Running = true;
                try
                {
                    Call(m_Instance);
                }
                catch (Exception e)
                {
                    // As Unity would for the game's own message.
                    Debug.LogException(e);
                }
                finally
                {
                    m_Running = false;
                }
            }

            public void Drop()
            {
                m_Pending = false;
                m_Instance = null;
            }
        }

        [HarmonyPatch(typeof(CombatHUD), "LateUpdate")]
        private static class CombatLatePatch
        {
            private static bool Prefix(CombatHUD __instance) => s_Combat.Hold(__instance);
        }

        [HarmonyPatch(typeof(ObjectiveOverlayManager), "Update")]
        private static class ObjectiveUpdatePatch
        {
            private static bool Prefix(ObjectiveOverlayManager __instance) => s_Objectives.Hold(__instance);
        }

        [HarmonyPatch(typeof(AirbaseOverlay), "LateUpdate")]
        private static class AirbaseLatePatch
        {
            private static bool Prefix(AirbaseOverlay __instance) => s_Airbase.Hold(__instance);
        }

        [HarmonyPatch(typeof(FlightHud), "Update")]
        private static class FlightHudUpdatePatch
        {
            private static bool Prefix(FlightHud __instance) => s_Flight.Hold(__instance);
        }

        [HarmonyPatch(typeof(CameraStateManager), "LateUpdate")]
        private static class CameraLatePatch
        {
            private static void Prefix() => s_InCameraLate = true;

            // Fallback if the camera update threw before the origin shift.
            private static void Finalizer()
            {
                s_InCameraLate = false;
                ReleaseAll();
            }
        }

        // The camera has moved, the origin has not shifted yet. The shift moves
        // the HUD canvas for a frame, so the HUD is placed before it.
        [HarmonyPatch(typeof(CameraStateManager), nameof(CameraStateManager.CheckOriginShift))]
        private static class OriginShiftPatch
        {
            private static void Prefix()
            {
                // Skips the call from LevelInfo.SetStartingCamera.
                if (s_InCameraLate)
                    ReleaseAll();
            }
        }
    }
}
