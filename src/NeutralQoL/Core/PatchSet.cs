using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace NeutralQoL.Core
{
    internal sealed class PatchSet
    {
        private readonly Harmony m_Harmony;

        public PatchSet(string id)
        {
            m_Harmony = new Harmony(id);
        }

        public void Patch(Type patchClass)
        {
            m_Harmony.CreateClassProcessor(patchClass).Patch();
            foreach (var method in m_Harmony.GetPatchedMethods())
            {
                var reason = NetGuard.Check(method);
                if (reason != null)
                {
                    UnpatchAll();
                    throw new InvalidOperationException($"refusing to patch {method.DeclaringType?.FullName}.{method.Name}: {reason}");
                }
            }
        }

        public void UnpatchAll()
        {
            m_Harmony.UnpatchSelf();
        }
    }

    internal static class NetGuard
    {
        private static readonly string[] s_AttributeNames = { "ServerRpcAttribute", "ClientRpcAttribute", "ServerAttribute", "ClientAttribute" };

        public static string Check(MethodBase method)
        {
            var ns = method.DeclaringType?.Namespace ?? "";
            if (ns.StartsWith("Mirage", StringComparison.Ordinal))
                return "Mirage networking";
            if (ns.StartsWith("NuclearOption.Jobs", StringComparison.Ordinal))
                return "simulation jobs";
            if (method.Name.StartsWith("UserCode_", StringComparison.Ordinal) || method.Name.StartsWith("Skeleton_", StringComparison.Ordinal))
                return "RPC body";
            if (method.Name == "SerializeSyncVars" || method.Name == "DeserializeSyncVars")
                return "SyncVar serialization";
            var attribute = method.GetCustomAttributes(false).Select(a => a.GetType().Name).FirstOrDefault(n => s_AttributeNames.Contains(n));
            if (attribute != null)
                return $"marked [{attribute.Replace("Attribute", "")}]";
            return null;
        }
    }
}
