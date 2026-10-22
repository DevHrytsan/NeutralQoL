using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using NeutralQoL.Core;
using NeutralQoL.Core.SettingsTab;
using UnityEngine;

namespace NeutralQoL
{
    [BepInPlugin(Guid, "NeutralQoL", MyPluginInfo.PLUGIN_VERSION)]
    [BepInProcess("NuclearOption.exe")]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.devhrytsan.neutralqol";

        internal static ManualLogSource Log;
        internal static ConfigFile Cfg;

        private static PatchSet s_Core;

        private void Awake()
        {
            Log = Logger;
            Cfg = Config;

            Log.LogInfo($"NeutralQoL {MyPluginInfo.PLUGIN_VERSION} loading");
            s_Core = new PatchSet(Guid + ".core");
            SettingsTabPatch.Install(s_Core);
            FeatureRegistry.Init(Config);

            // Some games destroy BepInEx's manager object while loading their first scene,
            // which would stop Update and every ticking feature. Patches stay applied for
            // the whole session, so nothing depends on this component surviving.
            var runner = new GameObject("NeutralQoL.Runner") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(runner);
            runner.AddComponent<Runner>();
            Log.LogInfo($"NeutralQoL loaded, {FeatureRegistry.All.Count} feature(s)");
        }

        private void OnDestroy()
        {
            Log.LogInfo("BepInEx manager object destroyed; NeutralQoL keeps running");
        }

        private sealed class Runner : MonoBehaviour
        {
            private void Update()
            {
                FeatureRegistry.Tick();
            }

            private void OnApplicationQuit()
            {
                FeatureRegistry.Shutdown();
                s_Core?.UnpatchAll();
            }
        }
    }
}
