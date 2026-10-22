using HarmonyLib;
using NeutralQoL.Core;
using UnityEngine;

namespace NeutralQoL.Shared.Hardpoints
{
    internal static class HardpointMount
    {
        private static AccessTools.FieldRef<Hardpoint, GameObject> s_Spawned;

        public static void Resolve()
        {
            if (s_Spawned == null)
                s_Spawned = Targets.Field<Hardpoint, GameObject>("spawnedPrefab");
        }

        // Null if the hardpoint is empty.
        public static Transform Root(Hardpoint hardpoint)
        {
            if (hardpoint == null || s_Spawned == null)
                return null;
            var mount = s_Spawned(hardpoint);
            return mount != null ? mount.transform : null;
        }
    }
}
