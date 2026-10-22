using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using NeutralQoL.Core;
using TMPro;
using UnityEngine;

namespace NeutralQoL.Features.FuelTime
{
    internal sealed class FuelTimeFeature : Feature
    {
        public override string Section => "HUD";
        public override string Name => "Fuel time and range";
        protected override bool DefaultOn => true;
        public override string Description =>
            "Adds the flight time left at the current fuel burn under the FUEL label of the HUD fuel gauge (\"14min\", or \"1h05\" above an hour), optionally the distance that covers at the current speed, and turns it yellow or red when fuel runs low.";

        protected override Type[] PatchClasses => new[] { typeof(UpdateFuelGaugePatch) };

        // Burn rate is averaged over about this many seconds, so throttle changes settle
        // fast without the time jumping on every reading.
        private const float k_SmoothingSeconds = 10f;
        private const float k_MinSampleSeconds = 0.5f;
        private const int k_SamplesBeforeShowing = 3;
        private const float k_RefuelJumpKg = 1f;
        private const float k_MinBurnKgPerSecond = 1e-4f;
        private const float k_MaxShownSeconds = 10f * 3600f;
        private const string k_Unknown = "--min";
        private const float k_GapEm = 0.5f;

        private static ConfigEntry<bool> s_ShowRange;
        private static ConfigEntry<int> s_LowMinutes;
        private static ConfigEntry<int> s_BingoMinutes;

        private static AccessTools.FieldRef<FuelGauge, Aircraft> s_Aircraft;
        private static AccessTools.FieldRef<FuelGauge, TextMeshProUGUI> s_Label;
        private static AccessTools.FieldRef<FuelGauge, Gradient> s_Gradient;

        // One copy of the FUEL label per gauge, made on its first reading.
        private static readonly Dictionary<FuelGauge, TextMeshProUGUI> s_Lines = new Dictionary<FuelGauge, TextMeshProUGUI>();

        private static Aircraft s_LastAircraft;
        private static float s_LastTime;
        private static float s_LastQuantity;
        private static float s_BurnRate;
        private static int s_Samples;
        private static string s_Suffix = k_Unknown;

        protected override void BindOptions()
        {
            s_ShowRange = Bind("Show range", true,
                "Also show how far the fuel left takes you at your current speed, in the game's units.");
            s_LowMinutes = Bind("Low fuel (minutes)", 7,
                "The time turns yellow at this many minutes left. 0 turns the warning off.",
                new AcceptableValueRange<int>(0, 30), inMenu: false);
            s_BingoMinutes = Bind("Bingo fuel (minutes)", 3,
                "The time turns red at this many minutes left. 0 turns the warning off.",
                new AcceptableValueRange<int>(0, 30), inMenu: false);
        }

        protected override void Resolve()
        {
            Targets.Method(typeof(FuelGauge), "UpdateFuelGauge");
            s_Aircraft = Targets.Field<FuelGauge, Aircraft>("aircraft");
            s_Label = Targets.Field<FuelGauge, TextMeshProUGUI>("fuelLabel");
            s_Gradient = Targets.Field<FuelGauge, Gradient>("redYellowGreenGradient");
        }

        protected override void OnEnable()
        {
            s_LastAircraft = null;
        }

        // The game's own texts are never touched, so removing our copies gives back vanilla.
        protected override void OnDisable()
        {
            s_LastAircraft = null;
            foreach (var line in s_Lines.Values)
            {
                if (line != null)
                    UnityEngine.Object.Destroy(line.gameObject);
            }
            s_Lines.Clear();
        }

        // Copy of the FUEL label, so the line gets the gauge's font, size and colour and
        // sits one line below it.
        private static TextMeshProUGUI GetLine(FuelGauge gauge, TextMeshProUGUI label)
        {
            if (s_Lines.TryGetValue(gauge, out var line) && line != null)
                return line;

            // Gauges die with the HUD on respawn; drop their entries here, since a new one is
            // made anyway.
            var dead = s_Lines.Keys.Where(g => g == null || s_Lines[g] == null).ToList();
            foreach (var g in dead)
                s_Lines.Remove(g);

            var copy = UnityEngine.Object.Instantiate(label.gameObject, label.transform.parent);
            copy.name = "NeutralQoL FuelTime";
            line = copy.GetComponent<TextMeshProUGUI>();
            line.enableWordWrapping = false;
            line.overflowMode = TextOverflowModes.Overflow;
            // Left pivot and alignment, so the text starts where it is placed and grows away
            // from FUEL.
            line.horizontalAlignment = HorizontalAlignmentOptions.Left;
            line.rectTransform.pivot = new Vector2(0f, label.rectTransform.pivot.y);
            s_Lines[gauge] = line;
            return line;
        }

        private static void Sample(Aircraft aircraft, float now, float quantity)
        {
            float dt = now - s_LastTime;
            bool refuelled = quantity > s_LastQuantity + k_RefuelJumpKg;
            if (aircraft != s_LastAircraft || refuelled || dt < 0f)
            {
                s_LastAircraft = aircraft;
                s_LastTime = now;
                s_LastQuantity = quantity;
                s_Samples = 0;
                return;
            }
            // A second gauge or theme change reads again at once: no useful rate, keep the last.
            if (dt < k_MinSampleSeconds)
                return;

            float rate = Mathf.Max(0f, (s_LastQuantity - quantity) / dt);
            s_BurnRate = s_Samples == 0 ? rate : Mathf.Lerp(s_BurnRate, rate, 1f - Mathf.Exp(-dt / k_SmoothingSeconds));
            s_Samples++;
            s_LastTime = now;
            s_LastQuantity = quantity;
        }

        // Rebuilt once per reading (about once a second, like the game's own text), never per frame.
        private static string BuildSuffix(Aircraft aircraft, float quantity, Gradient gradient)
        {
            if (quantity <= 0f)
                return Colored("0min", gradient, 0f);
            if (s_Samples < k_SamplesBeforeShowing || s_BurnRate < k_MinBurnKgPerSecond)
                return k_Unknown;

            float seconds = quantity / s_BurnRate;
            if (seconds >= k_MaxShownSeconds)
                return ">10h";

            // Rounded down, so the gauge never promises a minute the fuel doesn't have.
            int totalMinutes = (int)(seconds / 60f);
            string time = totalMinutes >= 60
                ? $"{totalMinutes / 60}h{totalMinutes % 60:D2}"
                : totalMinutes >= 1 ? $"{totalMinutes}min" : "<1min";
            if (s_ShowRange.Value)
                time += " " + UnitConverter.DistanceReading(aircraft.speed * seconds);

            float minutes = seconds / 60f;
            if (s_BingoMinutes.Value > 0 && minutes <= s_BingoMinutes.Value)
                return Colored(time, gradient, 0f);
            if (s_LowMinutes.Value > 0 && minutes <= s_LowMinutes.Value)
                return Colored(time, gradient, 0.2f);
            return time;
        }

        // The gauge's own gradient is red at 0 and yellow at 0.2 in the theme's colours,
        // so the warnings match the bar.
        private static string Colored(string text, Gradient gradient, float at)
        {
            if (gradient == null)
                return text;
            return $"<color=#{ColorUtility.ToHtmlStringRGB(gradient.Evaluate(at))}>{text}</color>";
        }

        [HarmonyPatch(typeof(FuelGauge), "UpdateFuelGauge")]
        private static class UpdateFuelGaugePatch
        {
            private static void Postfix(FuelGauge __instance)
            {
                var aircraft = s_Aircraft(__instance);
                var label = s_Label(__instance);
                if (aircraft == null || label == null)
                    return;

                float now = Time.timeSinceLevelLoad;
                float quantity = aircraft.GetFuelQuantity();
                bool fresh = aircraft != s_LastAircraft || now - s_LastTime >= k_MinSampleSeconds;
                Sample(aircraft, now, quantity);
                if (fresh)
                    s_Suffix = BuildSuffix(aircraft, quantity, s_Gradient(__instance));
                // Size, colour and visibility follow the label every reading, since the HUD text
                // size setting and the gauges toggle change it.
                var line = GetLine(__instance, label);
                line.fontSize = label.fontSize;
                line.color = label.color;
                line.enabled = label.enabled;
                // Right after the FUEL text (textBounds is in the label's own space), one space
                // apart, same baseline.
                var labelRect = label.rectTransform;
                float x = (label.textBounds.max.x + label.fontSize * k_GapEm) * labelRect.localScale.x;
                line.rectTransform.localPosition = labelRect.localPosition + labelRect.localRotation * new Vector3(x, 0f, 0f);
                line.text = s_Suffix;
            }
        }
    }
}
