using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using NeutralQoL.Core;
using NeutralQoL.Features.LoadoutPreview;
using NeutralQoL.Shared.BayDoorRules;
using NeutralQoL.Shared.Hardpoints;
using NeutralQoL.Shared.LoadoutDropdown;
using TMPro;
using UnityEngine;

namespace NeutralQoL.Features.LoadoutBayDoors
{
    internal sealed class LoadoutBayDoorsFeature : Feature
    {
        public override string Section => "Menus";
        public override string Name => "Open bay doors";
        public override string Description =>
            "Opens the hardpoint's weapon bay doors or cargo ramp while its weapon list is open, so you can see what is loaded inside.";
        public override Type PartOf => typeof(LoadoutPreviewFeature);
        protected override bool DefaultOn => true;

        protected override Type[] PatchClasses => new[] { typeof(ShowPatch) };

        // Refreshed every frame while the list is open; this only covers a
        // frame where Tick runs first.
        private const float k_HoldSeconds = 0.3f;
        // A ramp opens once and its own timer holds it: CargoRamp.OpenDoor
        // restarts the opening and its stop sound on every call.
        private const float k_RampHoldSeconds = 3600f;
        // A ramp not latched shut by then gets its speed back anyway.
        private const float k_RampRestoreTimeout = 60f;
        // How much faster than vanilla a menu ramp moves, so it is open before
        // a weapon is picked.
        private const float k_RampSpeed = 3.5f;

        private static AccessTools.FieldRef<MountedMissile, BayDoor[]> s_MissileDoors;
        private static AccessTools.FieldRef<BayDoor, float> s_OpenSpeed;
        private static AccessTools.FieldRef<BayDoor, float> s_CloseSpeed;
        private static AccessTools.FieldRef<BayDoor, float> s_OpenTimer;

        private static readonly List<BayDoor> s_Doors = new List<BayDoor>();
        private static readonly List<CargoRamp> s_Ramps = new List<CargoRamp>();
        // Ramps we opened and have not told to close yet.
        private static readonly List<CargoRamp> s_OpenRamps = new List<CargoRamp>();
        // Ramps at the raised speed, with their own speeds to restore.
        private static readonly List<CargoRamp> s_FastRamps = new List<CargoRamp>();
        private static readonly List<Vector2> s_FastRampSpeeds = new List<Vector2>();
        private static readonly List<float> s_FastRampSince = new List<float>();
        private static readonly HashSet<string> s_LoggedSkipped = new HashSet<string>();
        private static int s_Version = -1;
        private static bool s_Holding;

        protected override void Resolve()
        {
            LoadoutDropdown.Resolve();
            HardpointMount.Resolve();
            Targets.Method(typeof(BayDoor), nameof(BayDoor.OpenDoor));
            Targets.Method(typeof(CargoRamp), nameof(CargoRamp.OpenDoor));
            s_MissileDoors = Targets.Field<MountedMissile, BayDoor[]>("bayDoors");
            s_OpenSpeed = Targets.Field<BayDoor, float>("openSpeed");
            s_CloseSpeed = Targets.Field<BayDoor, float>("closeSpeed");
            s_OpenTimer = Targets.Field<BayDoor, float>("openTimer");
        }

        // Open doors close on their own timer, as after firing; ramps are told
        // to close now, at normal speed.
        protected override void OnDisable()
        {
            CloseRamps(null, 0.01f);
            for (int i = 0; i < s_FastRamps.Count; i++)
                RestoreSpeed(i);
            s_FastRamps.Clear();
            s_FastRampSpeeds.Clear();
            s_FastRampSince.Clear();
            s_Doors.Clear();
            s_Ramps.Clear();
            s_Version = -1;
            s_Holding = false;
        }

        public override void Tick()
        {
            LoadoutDropdown.Update();
            if (LoadoutDropdown.IsActive)
            {
                if (LoadoutDropdown.Version != s_Version)
                {
                    s_Version = LoadoutDropdown.Version;
                    Collect();
                    // Another hardpoint's list: its ramp, if any, stays; the
                    // others close.
                    CloseRamps(s_Ramps, CloseDelay());
                }
                OpenAll(k_HoldSeconds);
                OpenRamps();
                s_Holding = true;
            }
            else if (s_Holding)
            {
                OpenAll(CloseDelay());
                CloseRamps(null, CloseDelay());
                s_Doors.Clear();
                s_Ramps.Clear();
                s_Version = -1;
                s_Holding = false;
            }
            RestoreClosedRamps();
        }

        // Even a zero delay needs one short timer to start the close.
        private static float CloseDelay() => Mathf.Max(LoadoutPreviewFeature.KeepSeconds, 0.01f);

        // Runs when a list opens or its weapon changes, not per frame.
        private static void Collect()
        {
            s_Doors.Clear();
            s_Ramps.Clear();
            AddSet(LoadoutDropdown.Set);
            AddSet(LoadoutDropdown.TwinSet);
        }

        private static void AddSet(HardpointSet set)
        {
            if (set == null || set.hardpoints == null)
                return;
            foreach (var hardpoint in set.hardpoints)
            {
                // A gun pod outside a bay keeps the doors shut, as in vanilla.
                if (hardpoint == null || !BayDoorRules.OpensDoors(hardpoint, set.weaponMount != null))
                    continue;
                AddAll(hardpoint.bayDoors);
                var mount = HardpointMount.Root(hardpoint);
                if (mount != null)
                {
                    foreach (var missile in mount.GetComponentsInChildren<MountedMissile>(true))
                        AddAll(s_MissileDoors(missile));
                }
            }
        }

        private static void AddAll(BayDoor[] doors)
        {
            if (doors == null)
                return;
            foreach (var door in doors)
            {
                if (door == null)
                    continue;
                if (door is CargoRamp ramp)
                {
                    if (!s_Ramps.Contains(ramp))
                        s_Ramps.Add(ramp);
                }
                else if (!s_Doors.Contains(door) && IsVisualOnly(door))
                {
                    s_Doors.Add(door);
                }
            }
        }

        private static void OpenAll(float seconds)
        {
            for (int i = 0; i < s_Doors.Count; i++)
            {
                var door = s_Doors[i];
                if (door != null && door.gameObject.activeInHierarchy)
                    door.OpenDoor(seconds);
            }
        }

        private static void OpenRamps()
        {
            for (int i = 0; i < s_Ramps.Count; i++)
            {
                var ramp = s_Ramps[i];
                if (ramp == null || s_OpenRamps.Contains(ramp) || !ramp.gameObject.activeInHierarchy)
                    continue;
                SpeedUp(ramp);
                // CargoRamp.OpenDoor restarts the opening and replays its sound, so a
                // ramp that is opening or open just gets its timer extended.
                if (ramp.enabled && s_OpenTimer(ramp) > 0f)
                    s_OpenTimer(ramp) = k_RampHoldSeconds;
                else
                    ramp.OpenDoor(k_RampHoldSeconds);
                s_OpenRamps.Add(ramp);
            }
        }

        // Starts the close of every opened ramp not in keep, by cutting the
        // timer CargoRamp counts down once fully open.
        private static void CloseRamps(List<CargoRamp> keep, float seconds)
        {
            for (int i = s_OpenRamps.Count - 1; i >= 0; i--)
            {
                var ramp = s_OpenRamps[i];
                if (ramp != null && keep != null && keep.Contains(ramp))
                    continue;
                if (ramp != null)
                    s_OpenTimer(ramp) = seconds;
                s_OpenRamps.RemoveAt(i);
            }
        }

        private static void SpeedUp(CargoRamp ramp)
        {
            int index = s_FastRamps.IndexOf(ramp);
            if (index >= 0)
            {
                s_FastRampSince[index] = Time.unscaledTime;
                return;
            }
            float open = s_OpenSpeed(ramp);
            float close = s_CloseSpeed(ramp);
            s_FastRamps.Add(ramp);
            s_FastRampSpeeds.Add(new Vector2(open, close));
            s_FastRampSince.Add(Time.unscaledTime);
            float ratio = k_RampSpeed;
            s_OpenSpeed(ramp) = open * ratio;
            s_CloseSpeed(ramp) = close * ratio;
        }

        // A ramp disables itself once latched shut (CargoRamp.Closing).
        private static void RestoreClosedRamps()
        {
            for (int i = s_FastRamps.Count - 1; i >= 0; i--)
            {
                var ramp = s_FastRamps[i];
                bool gone = ramp == null;
                bool closed = !gone && !ramp.enabled && !s_OpenRamps.Contains(ramp);
                bool stale = !gone && !s_OpenRamps.Contains(ramp) && Time.unscaledTime - s_FastRampSince[i] > k_RampRestoreTimeout;
                if (!gone && !closed && !stale)
                    continue;
                RestoreSpeed(i);
                s_FastRamps.RemoveAt(i);
                s_FastRampSpeeds.RemoveAt(i);
                s_FastRampSince.RemoveAt(i);
            }
        }

        private static void RestoreSpeed(int index)
        {
            var ramp = s_FastRamps[index];
            if (ramp == null)
                return;
            s_OpenSpeed(ramp) = s_FastRampSpeeds[index].x;
            s_CloseSpeed(ramp) = s_FastRampSpeeds[index].y;
        }

        private static bool IsVisualOnly(BayDoor door)
        {
            string reason = door.GetType() != typeof(BayDoor) ? door.GetType().Name
                : door.GetComponentInChildren<Collider>(true) != null ? "collider"
                : door.GetComponentInChildren<UnitPart>(true) != null ? "unit part"
                : null;
            if (reason == null)
                return true;
            if (s_LoggedSkipped.Add(door.name + reason))
                Plugin.Log.LogInfo($"Menus.Open bay doors in loadout menu: door '{door.name}' skipped ({reason})");
            return false;
        }

        [HarmonyPatch(typeof(TMP_Dropdown), nameof(TMP_Dropdown.Show))]
        private static class ShowPatch
        {
            private static void Postfix(TMP_Dropdown __instance) => LoadoutDropdown.OnShow(__instance);
        }
    }
}
