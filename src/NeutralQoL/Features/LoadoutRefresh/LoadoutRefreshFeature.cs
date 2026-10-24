using System;
using System.Collections.Generic;
using HarmonyLib;
using NeutralQoL.Core;
using NuclearOption.Networking;
using TMPro;
using UnityEngine;

namespace NeutralQoL.Features.LoadoutRefresh
{
    internal sealed class LoadoutRefreshFeature : Feature
    {
        public override string Section => "Menus";
        public override string Name => "Refresh weapon lists";
        public override string Description =>
            "Updates the loadout weapon lists when nukes become available (first spawn, rank, warheads), so you don't have to go back to the airport screen.";
        public override bool AlwaysOn => true;

        protected override Type[] PatchClasses => new[] { typeof(ShowHardpointsPatch) };

        private const float k_CheckEvery = 0.5f;

        private static AccessTools.FieldRef<LoadoutSelector, Aircraft> s_Aircraft;
        private static AccessTools.FieldRef<LoadoutSelector, FactionHQ> s_Hq;
        private static AccessTools.FieldRef<LoadoutSelector, Airbase> s_Airbase;
        private static AccessTools.FieldRef<LoadoutSelector, List<WeaponSelector>> s_Selectors;
        private static AccessTools.FieldRef<WeaponSelector, TMP_Dropdown> s_Dropdown;

        private static readonly List<WeaponMount> s_Available = new List<WeaponMount>();
        private static LoadoutSelector s_Selector;
        private static int s_Signature;
        private static float s_NextCheck;
        private static bool s_Logged;

        protected override void Resolve()
        {
            Targets.Method(typeof(LoadoutSelector), nameof(LoadoutSelector.ShowHardpoints));
            Targets.Method(typeof(WeaponChecker), nameof(WeaponChecker.GetAvailableWeaponsNonAlloc));
            s_Aircraft = Targets.Field<LoadoutSelector, Aircraft>("aircraft");
            s_Hq = Targets.Field<LoadoutSelector, FactionHQ>("hq");
            s_Airbase = Targets.Field<LoadoutSelector, Airbase>("airbase");
            s_Selectors = Targets.Field<LoadoutSelector, List<WeaponSelector>>("weaponSelectors");
            s_Dropdown = Targets.Field<WeaponSelector, TMP_Dropdown>("dropdown");
        }

        protected override void OnDisable()
        {
            s_Selector = null;
        }

        public override void Tick()
        {
            if (s_Selector == null || !s_Selector.isActiveAndEnabled || Time.unscaledTime < s_NextCheck)
                return;
            s_NextCheck = Time.unscaledTime + k_CheckEvery;
            if (GameManager.gameState == GameState.Editor || AnyListOpen(s_Selector))
                return;
            if (Signature(s_Selector) != s_Signature)
                Rebuild(s_Selector);
        }

        // Rebuilding closes an open list, so wait until the player is done.
        private static bool AnyListOpen(LoadoutSelector selector)
        {
            var selectors = s_Selectors(selector);
            for (int i = 0; i < selectors.Count; i++)
            {
                var dropdown = selectors[i] != null ? s_Dropdown(selectors[i]) : null;
                if (dropdown != null && dropdown.IsExpanded)
                    return true;
            }
            return false;
        }

        // Changes whenever any hardpoint's list of allowed mounts would change.
        private static int Signature(LoadoutSelector selector)
        {
            var aircraft = s_Aircraft(selector);
            if (aircraft == null || aircraft.weaponManager == null)
                return 0;
            GameManager.GetLocalPlayer<Player>(out var player);
            var hq = s_Hq(selector);
            var airbase = s_Airbase(selector);
            int signature = 17;
            foreach (var set in aircraft.weaponManager.hardpointSets)
            {
                WeaponChecker.GetAvailableWeaponsNonAlloc(player, set, airbase, hq, false, s_Available);
                for (int i = 0; i < s_Available.Count; i++)
                    signature = signature * 31 + (s_Available[i] != null ? s_Available[i].GetInstanceID() : 0);
                signature = signature * 31 + s_Available.Count;
            }
            return signature;
        }

        private static void Rebuild(LoadoutSelector selector)
        {
            var aircraft = s_Aircraft(selector);
            var loadout = selector.GenerateLoadoutFromDropdowns();
            selector.ShowHardpoints();

            GameManager.GetLocalPlayer<Player>(out var player);
            var selectors = s_Selectors(selector);
            var sets = aircraft.weaponManager.hardpointSets;
            bool changed = false;
            for (int i = 0; i < selectors.Count && i < loadout.weapons.Count && i < sets.Length; i++)
            {
                var mount = loadout.weapons[i];
                // A mount no longer allowed (warheads taken) falls back to empty.
                WeaponChecker.GetAvailableWeaponsNonAlloc(player, sets[i], s_Airbase(selector), s_Hq(selector), false, s_Available);
                if (mount != null && !s_Available.Contains(mount))
                {
                    mount = null;
                    changed = true;
                }
                selectors[i].SetValue(mount);
            }
            foreach (var weaponSelector in selectors)
                weaponSelector.UpdateSymmetry();
            selector.UpdateWeapons(changed);

            if (!s_Logged)
            {
                s_Logged = true;
                Plugin.Log.LogInfo("Menus.Refresh weapon lists: allowed weapons changed while the loadout menu was open, lists rebuilt");
            }
        }

        [HarmonyPatch(typeof(LoadoutSelector), nameof(LoadoutSelector.ShowHardpoints))]
        private static class ShowHardpointsPatch
        {
            private static void Postfix(LoadoutSelector __instance)
            {
                s_Selector = __instance;
                s_Signature = Signature(__instance);
            }
        }
    }
}
