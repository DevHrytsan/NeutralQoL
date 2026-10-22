using System.Collections.Generic;
using HarmonyLib;
using NeutralQoL.Core;
using NeutralQoL.Shared.BayDoorRules;
using UnityEngine;

namespace NeutralQoL.Features.BayDoorsOnSelect
{
    internal sealed class BayDoorsOnSelectFeature : Feature
    {
        public override string Section => "Weapons";
        public override string Name => "Open bay doors on weapon select";
        public override string Description =>
            "Keeps the weapon bay doors of the selected weapon open while it is selected, instead of opening them only for the moment you fire. Only you see the doors open; it does not change how the aircraft flies or how fast weapons launch. Cargo doors and ramps are left alone.";

        // Refreshed every frame, so this only sets how soon the doors start closing after deselect.
        private const float k_HoldSeconds = 0.3f;

        private static AccessTools.FieldRef<Weapon, Hardpoint> s_Hardpoint;
        private static AccessTools.FieldRef<MountedMissile, BayDoor[]> s_MissileDoors;

        private static readonly List<BayDoor> s_Doors = new List<BayDoor>();
        private static readonly HashSet<string> s_LoggedSkipped = new HashSet<string>();
        private static Aircraft s_Aircraft;
        private static WeaponStation s_Station;

        protected override void Resolve()
        {
            Targets.Method(typeof(BayDoor), nameof(BayDoor.OpenDoor));
            s_Hardpoint = Targets.Field<Weapon, Hardpoint>("hardpoint");
            s_MissileDoors = Targets.Field<MountedMissile, BayDoor[]>("bayDoors");
        }

        protected override void OnDisable()
        {
            s_Doors.Clear();
            s_Aircraft = null;
            s_Station = null;
        }

        public override void Tick()
        {
            WeaponStation station = null;
            if (GameManager.GetLocalAircraft(out var aircraft) && aircraft != null && !aircraft.disabled && aircraft.weaponManager != null)
                station = aircraft.weaponManager.currentWeaponStation;
            if (aircraft != s_Aircraft || station != s_Station)
            {
                s_Aircraft = aircraft;
                s_Station = station;
                Collect(station);
            }
            if (station == null || station.Ammo <= 0)
                return;
            for (int i = 0; i < s_Doors.Count; i++)
            {
                var door = s_Doors[i];
                // The door disables its own component once closed and OpenDoor enables it again,
                // so only the object has to be active.
                if (door != null && door.gameObject.activeInHierarchy)
                    door.OpenDoor(k_HoldSeconds);
            }
        }

        // Runs only when the selection changes, keeping lookups and list growth out of the frame path.
        private static void Collect(WeaponStation station)
        {
            s_Doors.Clear();
            if (station == null || station.Cargo || station.Weapons == null)
                return;
            foreach (var weapon in station.Weapons)
            {
                // Vanilla never opens the doors for this weapon (a gun without useWeaponDoors),
                // so we don't hold them open either.
                if (weapon == null || !BayDoorRules.UsesDoors(weapon))
                    continue;
                if (weapon is MountedMissile missile)
                    AddAll(s_MissileDoors(missile));
                var hardpoint = s_Hardpoint(weapon);
                if (hardpoint != null)
                    AddAll(hardpoint.bayDoors);
            }
        }

        private static void AddAll(BayDoor[] doors)
        {
            if (doors == null)
                return;
            foreach (var door in doors)
            {
                if (door != null && !s_Doors.Contains(door) && IsVisualOnly(door))
                    s_Doors.Add(door);
            }
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
                Plugin.Log.LogInfo($"Weapons.Open bay doors on weapon select: door '{door.name}' skipped ({reason})");
            return false;
        }
    }
}
