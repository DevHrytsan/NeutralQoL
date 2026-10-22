using System.Collections.Generic;
using NeutralQoL.Shared.Hardpoints;

namespace NeutralQoL.Shared.BayDoorRules
{
    internal static class BayDoorRules
    {
        private static readonly List<Weapon> s_WeaponCache = new List<Weapon>();

        public static bool UsesDoors(Weapon weapon)
        {
            if (weapon is Gun gun)
                return gun.info != null && gun.info.useWeaponDoors;
            return weapon is MountedMissile || weapon is MountedCargo || weapon is MountedTroops
                || weapon is SpecialFlareEjector || weapon is SpecialSmokeEjector;
        }

        // Empty hardpoints count as opening (the bay is shown empty). Loaded ones
        // open if any weapon would. A mount with no weapon (TailHook) never does.
        public static bool OpensDoors(Hardpoint hardpoint, bool loaded)
        {
            if (hardpoint == null)
                return false;
            if (!loaded)
                return true;
            // Mount only: the hardpoint transform can be an aircraft part that
            // also holds other hardpoints' weapons.
            var mount = HardpointMount.Root(hardpoint);
            if (mount == null)
                return true;
            mount.GetComponentsInChildren(true, s_WeaponCache);
            bool opens = false;
            foreach (var weapon in s_WeaponCache)
            {
                if (weapon != null && UsesDoors(weapon))
                {
                    opens = true;
                    break;
                }
            }
            s_WeaponCache.Clear();
            return opens;
        }
    }
}
