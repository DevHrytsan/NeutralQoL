using System;
using HarmonyLib;
using NeutralQoL.Core;
using UnityEngine;

namespace NeutralQoL.Features.WorldOrbitCamera
{
    internal sealed class WorldOrbitCameraFeature : Feature
    {
        public override string Section => "Camera";
        public override string Name => "World fixed orbit camera";
        protected override bool DefaultOn => true;
        public override string Description =>
            "Keeps the third person orbit camera pointing the same compass direction when your aircraft turns, instead of swinging round behind it. Looking around with the view controls still turns the camera.";

        protected override Type[] PatchClasses => new[] { typeof(OrbitUpdatePatch) };

        private static AccessTools.FieldRef<CameraOrbitState, float> s_PanView;
        private static AccessTools.FieldRef<CameraOrbitState, Vector3> s_FlatVelSmoothed;

        private static CameraOrbitState s_Tracked;
        private static float s_WorldYaw;
        private static float s_SetPan;

        protected override void Resolve()
        {
            Targets.Method(typeof(CameraOrbitState), nameof(CameraOrbitState.UpdateState));
            s_PanView = Targets.Field<CameraOrbitState, float>("panView");
            s_FlatVelSmoothed = Targets.Field<CameraOrbitState, Vector3>("flatVelSmoothed");
        }

        protected override void OnEnable()
        {
            s_Tracked = null;
        }

        // The leftover pan is a plain vanilla offset from the heading, so the camera just
        // starts following turns again.
        protected override void OnDisable()
        {
            s_Tracked = null;
        }

        [HarmonyPatch(typeof(CameraOrbitState), nameof(CameraOrbitState.UpdateState))]
        private static class OrbitUpdatePatch
        {
            private static void Prefix(CameraOrbitState __instance, CameraStateManager cam)
            {
                if (cam == null || cam.currentState != __instance || cam.followingRB == null)
                {
                    s_Tracked = null;
                    return;
                }

                // Same smoothing CameraMotion applies to the heading right after, so the pan
                // cancels this very frame's heading.
                var rb = cam.followingRB;
                var heading = s_FlatVelSmoothed(__instance);
                var target = rb.velocity + rb.transform.forward * 10f;
                target.y = 0f;
                if (target.sqrMagnitude > 0.001f)
                    heading = Vector3.Lerp(heading, target, 10f * Time.unscaledDeltaTime);
                if (heading.sqrMagnitude < 1e-6f)
                    return;
                float headingYaw = Mathf.Atan2(heading.x, heading.z) * Mathf.Rad2Deg;

                ref float pan = ref s_PanView(__instance);
                if (s_Tracked != __instance)
                {
                    s_Tracked = __instance;
                    s_WorldYaw = headingYaw + pan;
                }
                else
                {
                    // Anything the game's Inputs added to the pan since last frame is the player looking around.
                    s_WorldYaw = Mathf.DeltaAngle(0f, s_WorldYaw + Mathf.DeltaAngle(s_SetPan, pan));
                }
                pan = Mathf.DeltaAngle(0f, s_WorldYaw - headingYaw);
                s_SetPan = pan;
            }
        }
    }
}
