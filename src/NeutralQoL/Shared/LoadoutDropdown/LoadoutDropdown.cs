using HarmonyLib;
using NeutralQoL.Core;
using TMPro;
using UnityEngine;

namespace NeutralQoL.Shared.LoadoutDropdown
{
    internal static class LoadoutDropdown
    {
        private static AccessTools.FieldRef<WeaponSelector, Aircraft> s_Aircraft;
        private static AccessTools.FieldRef<WeaponSelector, HardpointSet> s_HardpointSet;
        private static AccessTools.FieldRef<WeaponSelector, TMP_Dropdown> s_Dropdown;
        private static AccessTools.FieldRef<WeaponSelector, WeaponSelector> s_Symmetry;

        private static TMP_Dropdown s_OpenDropdown;
        private static WeaponMount s_LastMount;
        private static int s_UpdatedFrame = -1;

        public static WeaponSelector Selector { get; private set; }
        public static Aircraft Aircraft { get; private set; }
        public static HardpointSet Set { get; private set; }
        // The symmetry twin's set when symmetry is on (the twin's selector is hidden and
        // this one loads both sides), otherwise null.
        public static HardpointSet TwinSet { get; private set; }
        public static bool IsOpen { get; private set; }
        // Set by the camera focus while the player holds Free Look to look round the
        // weapon: everything stays as if the list were still open, and the close timers
        // start from the release.
        public static bool Held { get; set; }
        public static bool IsActive => IsOpen || Held;
        public static float ClosedAt { get; private set; } = float.NegativeInfinity;
        // Goes up when another list opens or the open list's weapon changes (the mounts
        // are respawned then), so users know to look things up again.
        public static int Version { get; private set; }

        public static void Resolve()
        {
            Targets.Method(typeof(TMP_Dropdown), nameof(TMP_Dropdown.Show));
            s_Aircraft = Targets.Field<WeaponSelector, Aircraft>("aircraft");
            s_HardpointSet = Targets.Field<WeaponSelector, HardpointSet>("hardpointSet");
            s_Dropdown = Targets.Field<WeaponSelector, TMP_Dropdown>("dropdown");
            s_Symmetry = Targets.Field<WeaponSelector, WeaponSelector>("symmetryWeaponSelector");
        }

        public static void OnShow(TMP_Dropdown dropdown)
        {
            if (s_Dropdown == null || dropdown == s_OpenDropdown && IsOpen)
                return;
            var selector = dropdown.GetComponentInParent<WeaponSelector>();
            if (selector == null || s_Dropdown(selector) != dropdown)
                return;
            var aircraft = s_Aircraft(selector);
            if (aircraft == null || aircraft.networked)
                return;

            s_OpenDropdown = dropdown;
            Selector = selector;
            Aircraft = aircraft;
            Set = s_HardpointSet(selector);
            var twin = s_Symmetry(selector);
            TwinSet = twin != null && !twin.gameObject.activeSelf ? s_HardpointSet(twin) : null;
            s_LastMount = Set != null ? Set.weaponMount : null;
            IsOpen = true;
            Version++;
        }

        // Called from each user's Tick; runs once per frame whoever calls first.
        public static void Update()
        {
            if (s_UpdatedFrame == Time.frameCount)
                return;
            s_UpdatedFrame = Time.frameCount;
            if (Selector == null && Aircraft == null)
                return;
            if (Selector == null || Aircraft == null || s_OpenDropdown == null)
            {
                Clear();
                return;
            }
            if (Set != null && Set.weaponMount != s_LastMount)
            {
                s_LastMount = Set.weaponMount;
                Version++;
            }
            if (IsOpen && !s_OpenDropdown.IsExpanded)
            {
                IsOpen = false;
                ClosedAt = Time.unscaledTime;
            }
            if (Held && !IsOpen)
                ClosedAt = Time.unscaledTime;
        }

        public static void Clear()
        {
            if (IsOpen)
                ClosedAt = Time.unscaledTime;
            s_OpenDropdown = null;
            Selector = null;
            Aircraft = null;
            Set = null;
            TwinSet = null;
            s_LastMount = null;
            IsOpen = false;
            Held = false;
        }
    }
}
