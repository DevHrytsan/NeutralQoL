using System;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NeutralQoL.Core.SettingsTab
{
    [HarmonyPatch(typeof(SettingsMenu), nameof(SettingsMenu.Start))]
    internal static class SettingsTabPatch
    {
        private const string k_Label = "QoL";

        private static AccessTools.FieldRef<SettingsMenu, SettingsMenuButton> s_DefaultMenu;
        private static GameObject s_MenuTemplate;
        private static bool s_Failed;

        public static void Install(PatchSet core)
        {
            try
            {
                s_DefaultMenu = Targets.Field<SettingsMenu, SettingsMenuButton>("defaultMenu");
                core.Patch(typeof(SettingsTabPatch));
                Plugin.Log.LogInfo("QoL settings tab installed");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"QoL settings tab disabled ({e.Message}); edit BepInEx/config/{Plugin.Guid}.cfg instead");
            }
        }

        private static void Prefix(SettingsMenu __instance)
        {
            if (s_Failed)
                return;
            try
            {
                AddTab(__instance);
            }
            catch (Exception e)
            {
                s_Failed = true;
                Plugin.Log.LogWarning($"QoL settings tab disabled: {e}");
            }
        }

        private static void AddTab(SettingsMenu menu)
        {
            var source = s_DefaultMenu(menu) ?? throw new InvalidOperationException("SettingsMenu has no default tab");
            var bar = source.transform.parent;
            if (s_MenuTemplate == null)
                s_MenuTemplate = QoLMenu.CreateTemplate(PickSourcePage(bar));

            // The clone still points at this SettingsMenu, so the game's OpenMenu shows our
            // page and destroys it on the next tab switch.
            var button = UnityEngine.Object.Instantiate(source, bar);
            button.name = "QoLTab";
            button.MenuPrefab = s_MenuTemplate;
            button.transform.SetAsLastSibling();
            Plugin.Log.LogInfo($"QoL tab added next to {source.name} under {bar.name}");

            var tmp = button.GetComponentInChildren<TMP_Text>(true);
            if (tmp != null)
                tmp.text = k_Label;
            var text = button.GetComponentInChildren<Text>(true);
            if (text != null)
                text.text = k_Label;
        }

        // The Gameplay page has a toggle, a slider and a dropdown, every row type the QoL
        // page needs. Any page with all three would do.
        private static GameObject PickSourcePage(Transform bar)
        {
            var pages = bar.GetComponentsInChildren<SettingsMenuButton>(true).Select(b => b.MenuPrefab).Where(p => p != null).ToList();
            return pages.FirstOrDefault(p => p.GetComponentInChildren<GameplayMenu>(true) != null)
                ?? pages.FirstOrDefault(p => p.GetComponentInChildren<Toggle>(true) != null && p.GetComponentInChildren<Slider>(true) != null && p.GetComponentInChildren<Dropdown>(true) != null)
                ?? throw new InvalidOperationException("no settings page with toggle, slider and dropdown rows");
        }
    }
}
