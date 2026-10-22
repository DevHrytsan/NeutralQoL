using BepInEx.Configuration;
using NeutralQoL.Core;
using NuclearOption.UIStyleSystem;
using TMPro;
using UnityEngine;

namespace NeutralQoL.Features.AutoHoverReminder
{
    internal sealed class AutoHoverReminderFeature : Feature
    {
        public override string Section => "HUD";
        public override string Name => "Auto Hover reminder";
        public override string Description =>
            "Shows [AUTO HOVER OFF] in yellow when you fly an aircraft that has Auto Hover (such as the Tarantula) slowly and close to the ground with Auto Hover switched off. It only reminds you; it never switches Auto Hover on or off.";

        private const string k_Text = "[AUTO HOVER OFF]";
        // Below the cargo warning (6 lines), which can show at the same time.
        private const float k_LabelLines = 7.5f;
        // Vanilla refuses to switch Auto Hover on below 1 m radar altitude
        // (ControlsFilter.ToggleAutoHover), so show nothing down there.
        private const float k_MinRadarAlt = 1f;
        // The condition must hold this long, so a takeoff or quick pass through the band
        // doesn't flash the text.
        private const float k_DelaySeconds = 1.5f;
        private const float k_KmhToMs = 1f / 3.6f;

        private static ConfigEntry<int> s_MaxSpeedKmh;
        private static ConfigEntry<int> s_MaxHeight;

        private static TMP_Text s_Label;
        private static float s_ConditionSince = float.PositiveInfinity;
        private static bool s_LoggedTemplate;

        protected override void BindOptions()
        {
            s_MaxSpeedKmh = Bind("Below speed (km/h)", 80,
                "The reminder shows only below this speed.",
                new AcceptableValueRange<int>(10, 300));
            s_MaxHeight = Bind("Below height (meters)", 40,
                "The reminder shows only below this height above the ground or water.",
                new AcceptableValueRange<int>(2, 500));
        }

        protected override void OnDisable()
        {
            if (s_Label != null)
                Object.Destroy(s_Label.gameObject);
            s_Label = null;
            s_ConditionSince = float.PositiveInfinity;
        }

        public override void Tick()
        {
            bool condition = ShouldRemind();
            float now = Time.time;
            if (!condition)
                s_ConditionSince = float.PositiveInfinity;
            else if (float.IsPositiveInfinity(s_ConditionSince))
                s_ConditionSince = now;
            Show(condition && now - s_ConditionSince >= k_DelaySeconds);
        }

        private static bool ShouldRemind()
        {
            // Player.Aircraft can outlive a crash or an ejection.
            if (!GameManager.GetLocalAircraft(out var aircraft) || aircraft == null || aircraft.disabled
                || aircraft.cockpit == null || aircraft.cockpit.IsDetached())
                return false;
            var filter = aircraft.GetControlsFilter();
            if (filter == null || !filter.HasAutoHover() || filter.IsAutoHoverEnabled())
                return false;
            float alt = aircraft.radarAlt;
            return alt >= k_MinRadarAlt && alt < s_MaxHeight.Value && aircraft.speed < s_MaxSpeedKmh.Value * k_KmhToMs;
        }

        private static void Show(bool on)
        {
            if (!on)
            {
                if (s_Label != null && s_Label.enabled)
                    s_Label.enabled = false;
                return;
            }
            if (s_Label == null && !CreateLabel())
                return;
            if (!s_Label.enabled)
                s_Label.enabled = true;
            // Read every frame so a theme change applies at once; Color is a struct.
            s_Label.color = ThemeManager.Active.ColorTheme.Warning;
        }

        // Copy of a text on the HUD centre, for the HUD's font, size and depth (like the
        // cargo warning). It goes with the HUD on respawn and is made again.
        private static bool CreateLabel()
        {
            var hud = SceneSingleton<FlightHud>.i;
            var center = hud != null ? hud.GetHUDCenter() : null;
            if (center == null)
                return false;
            TMP_Text template = null;
            foreach (var text in center.GetComponentsInChildren<TMP_Text>())
            {
                if (text.enabled && text.fontSize > 0f)
                {
                    template = text;
                    break;
                }
            }
            if (template == null)
                return false;
            if (!s_LoggedTemplate)
            {
                s_LoggedTemplate = true;
                Plugin.Log.LogInfo($"HUD.Auto Hover reminder: text copied from '{template.name}' under '{center.name}'");
            }

            var copy = Object.Instantiate(template.gameObject, center, false);
            copy.name = "NeutralQoL AutoHoverReminder";
            copy.SetActive(true);
            for (int i = copy.transform.childCount - 1; i >= 0; i--)
                Object.Destroy(copy.transform.GetChild(i).gameObject);
            s_Label = copy.GetComponent<TMP_Text>();
            foreach (var behaviour in copy.GetComponents<MonoBehaviour>())
            {
                if (behaviour != s_Label)
                    Object.Destroy(behaviour);
            }
            s_Label.enableWordWrapping = false;
            s_Label.overflowMode = TextOverflowModes.Overflow;
            s_Label.horizontalAlignment = HorizontalAlignmentOptions.Center;
            s_Label.text = k_Text;
            var rect = s_Label.rectTransform;
            var parentScale = center.lossyScale;
            var templateScale = template.transform.lossyScale;
            rect.localScale = new Vector3(templateScale.x / parentScale.x, templateScale.y / parentScale.y, 1f);
            rect.localRotation = Quaternion.identity;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.localPosition = new Vector3(0f, -s_Label.fontSize * k_LabelLines * rect.localScale.y, 0f);
            return true;
        }
    }
}
