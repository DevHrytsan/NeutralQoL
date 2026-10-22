using System;
using BepInEx.Configuration;
using HarmonyLib;
using NeutralQoL.Core;
using NuclearOption.UIStyleSystem;
using Rewired;
using TMPro;
using UnityEngine;

namespace NeutralQoL.Features.JammerSwitch
{
    internal sealed class JammerSwitchFeature : Feature
    {
        public override string Section => "Weapons";
        public override string Name => "Jamming pod as a switch";
        public override string Description =>
            "With the jamming pod selected, tap Fire to start jamming and tap it again to stop, instead of holding the button. Jamming also stops when you select another weapon, leave the aircraft or open a menu with weapon safety on. Other weapons fire as usual.";

        protected override Type[] PatchClasses => new[] { typeof(UpdateStatePatch), typeof(LeaveStatePatch) };

        // Same threshold PilotPlayerState.PlayerControls uses for G-LOC.
        private const float k_MinPilotStrength = 0.2f;
        private const string k_FireAction = "Fire";
        private const string k_LabelText = "[JAMMER ACTIVE]";
        private const string k_HintField = "hint";
        // Label distance below the hint text, in lines of its font size.
        private const float k_LabelLines = 1.2f;

        private static ConfigEntry<bool> s_ShowLabel;

        private static AccessTools.FieldRef<PilotPlayerState, Player> s_Player;
        private static AccessTools.FieldRef<PilotPlayerState, float> s_PilotStrength;
        private static AccessTools.FieldRef<CombatHUD, HUDWeaponState> s_WeaponState;

        // The hint field is declared on each HUD weapon state type, not the base, so look
        // it up per type when the label is made, never per frame.
        private static Type s_HintOwner;
        private static System.Reflection.FieldInfo s_HintField;
        private static bool s_LoggedNoHint;

        private static WeaponStation s_LatchedStation;
        // Copy of the weapon mode's hint text (SHOOT, OUT OF RANGE), made under it when
        // first needed. It goes away with the weapon mode (station change, HUD respawn)
        // and is made again.
        private static TextMeshProUGUI s_Label;

        protected override void BindOptions()
        {
            s_ShowLabel = Bind("Show JAMMER ACTIVE", true,
                "Show [JAMMER ACTIVE] in yellow under the SHOOT hint while the switch keeps the jamming pod on.");
        }

        protected override void Resolve()
        {
            Targets.Method(typeof(PilotPlayerState), nameof(PilotPlayerState.UpdateState));
            Targets.Method(typeof(PilotPlayerState), nameof(PilotPlayerState.LeaveState));
            s_Player = Targets.Field<PilotPlayerState, Player>("player");
            s_PilotStrength = Targets.Field<PilotPlayerState, float>("pilotStrength");
            s_WeaponState = Targets.Field<CombatHUD, HUDWeaponState>("weaponState");
        }

        // Without the latch nothing calls Fire, so the pod's LateUpdate switches it off
        // next frame, as in vanilla.
        protected override void OnDisable()
        {
            s_LatchedStation = null;
            if (s_Label != null)
                UnityEngine.Object.Destroy(s_Label.gameObject);
            s_Label = null;
        }

        private static bool IsJammer(WeaponStation station)
        {
            return station != null && station.WeaponInfo != null && station.WeaponInfo.jammer;
        }

        private static void UpdateLatch(PilotPlayerState state, Pilot pilot)
        {
            var aircraft = pilot.aircraft;
            var player = s_Player(state);
            if (aircraft == null || player == null)
            {
                s_LatchedStation = null;
                return;
            }

            var station = aircraft.weaponManager.currentWeaponStation;
            bool safetyBlocks = PlayerSettings.menuWeaponSafety && Cursor.visible;
            if (station != s_LatchedStation || safetyBlocks || aircraft.cockpit.IsDetached())
                s_LatchedStation = null;

            // PlayerControls skips all input in these cases, so a press would be ignored
            // anyway, same as a held button.
            if (!GameManager.flightControlsEnabled || s_PilotStrength(state) < k_MinPilotStrength)
                return;

            if (!safetyBlocks && IsJammer(station) && player.GetButtonDown(k_FireAction))
                s_LatchedStation = s_LatchedStation == null ? station : null;

            // While Fire is held, vanilla has already fired this frame.
            if (s_LatchedStation != null && !player.GetButton(k_FireAction))
                pilot.Fire();
        }

        private static void ShowLabel(bool on)
        {
            if (!on || !s_ShowLabel.Value)
            {
                if (s_Label != null && s_Label.enabled)
                    s_Label.enabled = false;
                return;
            }
            if (s_Label == null && !CreateLabel())
                return;
            // Read every frame so a theme change applies at once; Color is a struct.
            s_Label.color = ThemeManager.Active.ColorTheme.Warning;
            s_Label.enabled = true;
        }

        // Copy of the hint, for its font, size and scaling. As a child of the hint it
        // follows it if a weapon mode moves it, and still shows when the mode hides the
        // hint (only the component is disabled).
        private static bool CreateLabel()
        {
            var hud = SceneSingleton<CombatHUD>.i;
            var state = hud != null ? s_WeaponState(hud) : null;
            if (state == null)
                return false;

            var stateType = state.GetType();
            if (stateType != s_HintOwner)
            {
                s_HintOwner = stateType;
                s_HintField = AccessTools.Field(stateType, k_HintField);
                if (s_HintField == null || s_HintField.FieldType != typeof(TextMeshProUGUI))
                    s_HintField = null;
            }
            var hint = s_HintField != null ? (TextMeshProUGUI)s_HintField.GetValue(state) : null;
            if (hint == null)
            {
                if (!s_LoggedNoHint)
                {
                    s_LoggedNoHint = true;
                    Plugin.Log.LogInfo($"Controls.Jamming pod as a switch: {stateType.Name} has no hint text, JAMMER ACTIVE not shown");
                }
                return false;
            }

            var copy = UnityEngine.Object.Instantiate(hint.gameObject, hint.transform);
            copy.name = "NeutralQoL JammerActive";
            for (int i = copy.transform.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(copy.transform.GetChild(i).gameObject);
            s_Label = copy.GetComponent<TextMeshProUGUI>();
            s_Label.enableWordWrapping = false;
            s_Label.overflowMode = TextOverflowModes.Overflow;
            s_Label.text = k_LabelText;
            var hintRect = hint.rectTransform;
            var rect = s_Label.rectTransform;
            rect.anchorMin = hintRect.pivot;
            rect.anchorMax = hintRect.pivot;
            rect.pivot = hintRect.pivot;
            rect.sizeDelta = hintRect.rect.size;
            rect.localRotation = Quaternion.identity;
            rect.localScale = Vector3.one;
            rect.anchoredPosition = new Vector2(0f, -hint.fontSize * k_LabelLines);
            return true;
        }

        [HarmonyPatch(typeof(PilotPlayerState), nameof(PilotPlayerState.UpdateState))]
        private static class UpdateStatePatch
        {
            private static void Postfix(PilotPlayerState __instance, Pilot pilot)
            {
                UpdateLatch(__instance, pilot);
                ShowLabel(s_LatchedStation != null);
            }
        }

        [HarmonyPatch(typeof(PilotPlayerState), nameof(PilotPlayerState.LeaveState))]
        private static class LeaveStatePatch
        {
            private static void Postfix()
            {
                s_LatchedStation = null;
                ShowLabel(false);
            }
        }
    }
}
