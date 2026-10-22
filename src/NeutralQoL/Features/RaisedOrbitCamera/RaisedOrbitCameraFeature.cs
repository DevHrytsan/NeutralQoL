using System;
using BepInEx.Configuration;
using HarmonyLib;
using NeutralQoL.Core;
using UnityEngine;

namespace NeutralQoL.Features.RaisedOrbitCamera
{
    internal sealed class RaisedOrbitCameraFeature : Feature
    {
        public override string Section => "Camera";
        public override string Name => "Raised orbit camera";
        protected override bool DefaultOn => true;
        public override string Description =>
            "Lifts the third person orbit camera above your aircraft, by an amount that grows with the aircraft's size and the zoom, so the aircraft sits lower on screen and the HUD aim marker shows above it instead of behind it.";

        protected override Type[] PatchClasses => new[] { typeof(OrbitUpdatePatch) };

        private const float k_HitMargin = 0.8f;

        private static ConfigEntry<float> s_Height;
        private static AccessTools.FieldRef<CameraOrbitState, int> s_LayerMask;

        protected override void BindOptions()
        {
            s_Height = Bind("Height (ratio)", 0.25f,
                "How far the camera is lifted, as a share of its distance to the aircraft. 0 is the game's normal position.",
                new AcceptableValueRange<float>(0f, 0.5f));
        }

        protected override void Resolve()
        {
            Targets.Method(typeof(CameraOrbitState), nameof(CameraOrbitState.UpdateState));
            s_LayerMask = Targets.Field<CameraOrbitState, int>("layerMask");
        }

        [HarmonyPatch(typeof(CameraOrbitState), nameof(CameraOrbitState.UpdateState))]
        private static class OrbitUpdatePatch
        {
            // CameraMotion sets the position from the pivot every frame, so the lift never
            // piles up.
            private static void Postfix(CameraOrbitState __instance, CameraStateManager cam)
            {
                if (cam == null || cam.currentState != __instance || cam.followingUnit == null)
                    return;
                var camTransform = cam.transform;
                var pivot = cam.cameraPivot.position;
                var from = camTransform.position;
                float distance = Vector3.Distance(pivot, from);
                var to = from + camTransform.up * (distance * s_Height.Value);
                // Stop short of terrain and buildings like the game does for its own position, so
                // the lift shrinks smoothly near the ground.
                if (Physics.Linecast(from, to, out var hit, s_LayerMask(__instance)))
                    to = from + (hit.point - from) * k_HitMargin;
                camTransform.position = to;
            }
        }
    }
}
