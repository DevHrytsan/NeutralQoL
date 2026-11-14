using System;
using HarmonyLib;
using NeutralQoL.Core;
using TMPro;

namespace NeutralQoL.Features.ThrottleReadingFix
{
    // Fixes a vanilla gauge bug (-1%, no AIRBRAKE) in local HUD code; AIRBRAKE
    // follows the airbrake parts the player already sees on the model.
    internal sealed class ThrottleReadingFixFeature : Feature
    {
        public override string Section => "HUD";
        public override string Name => "Throttle reading never below 0%";
        public override string Description =>
            "Throttle gauge shows 0% instead of -1%, and AIRBRAKE only while the airbrake is open.";

        // A vanilla bug fix with nothing to choose, like Refresh weapon lists.
        public override bool AlwaysOn => true;
        protected override Type[] PatchClasses => new[] { typeof(UpdateDisplayPatch), typeof(RefreshPatch) };

        private const string k_UpdateMethod = "UpdateThrottleDisplay";
        private const string k_RegionType = "ThrottleRegion";
        // The text vanilla shows at throttle 0 on gauges without regions.
        private const string k_AirbrakeText = "AIRBRAKE";
        private const string k_ZeroText = "0%";

        private static AccessTools.FieldRef<ThrottleGauge, object> s_CurrentRegion;
        private static AccessTools.FieldRef<ThrottleGauge, TextMeshProUGUI> s_Reading;
        private static AccessTools.FieldRef<ThrottleGauge, bool> s_HasAirbrake;
        private static AccessTools.FieldRef<ThrottleGauge, ControlInputs> s_Inputs;
        private static AccessTools.FieldRef<ThrottleGauge, Aircraft> s_Aircraft;
        private static AccessTools.FieldRef<ThrottleGauge, float> s_ThrottlePrev;
        private static AccessTools.FieldRef<ThrottleGauge, System.Collections.IEnumerable> s_Regions;
        private static AccessTools.FieldRef<Airbrake, float> s_OpenAmount;
        private static AccessTools.FieldRef<object, float> s_Percent;
        private static AccessTools.FieldRef<object, bool> s_ShowName;
        private static AccessTools.FieldRef<object, bool> s_ShowPercent;
        private static AccessTools.FieldRef<object, string> s_RegionName;

        // Airbrake parts of the gauge's aircraft, found once per aircraft.
        private static Aircraft s_CachedAircraft;
        private static Airbrake[] s_Airbrakes = new Airbrake[0];

        protected override void Resolve()
        {
            Targets.Method(typeof(ThrottleGauge), k_UpdateMethod);
            Targets.Method(typeof(ThrottleGauge), nameof(ThrottleGauge.Refresh));
            var regionType = AccessTools.Inner(typeof(ThrottleGauge), k_RegionType)
                ?? throw new MissingMemberException($"ThrottleGauge.{k_RegionType} missing");
            s_CurrentRegion = Targets.Field<ThrottleGauge, object>("currentRegion");
            s_Reading = Targets.Field<ThrottleGauge, TextMeshProUGUI>("throttleReading");
            s_HasAirbrake = Targets.Field<ThrottleGauge, bool>("airbrake");
            s_Inputs = Targets.Field<ThrottleGauge, ControlInputs>("inputs");
            s_Aircraft = Targets.Field<ThrottleGauge, Aircraft>("aircraft");
            s_ThrottlePrev = Targets.Field<ThrottleGauge, float>("throttlePrev");
            s_Regions = Targets.Field<ThrottleGauge, System.Collections.IEnumerable>("throttleRegions");
            s_OpenAmount = Targets.Field<Airbrake, float>("openAmount");
            s_Percent = RegionField<float>(regionType, "percent");
            s_ShowName = RegionField<bool>(regionType, "showName");
            s_ShowPercent = RegionField<bool>(regionType, "showPercent");
            s_RegionName = RegionField<string>(regionType, "name");
        }

        private static AccessTools.FieldRef<object, T> RegionField<T>(Type regionType, string name)
        {
            var field = AccessTools.Field(regionType, name);
            if (field == null || field.FieldType != typeof(T))
                throw new MissingMemberException($"ThrottleGauge.{k_RegionType}.{name} missing or not {typeof(T).Name}");
            return AccessTools.FieldRefAccess<T>(regionType, name);
        }

        // Aircraft without Airbrake parts (split control surfaces, for example) keep the
        // vanilla rule: the gauge's airbrake flag and throttle at 0.
        private static bool IsAirbrakeOpen(ThrottleGauge gauge, ControlInputs inputs)
        {
            var aircraft = s_Aircraft(gauge);
            if (aircraft == null)
                return false;
            if (aircraft != s_CachedAircraft)
            {
                s_CachedAircraft = aircraft;
                s_Airbrakes = aircraft.GetComponentsInChildren<Airbrake>(true);
                LogGauge(gauge, aircraft);
            }
            if (s_Airbrakes.Length == 0)
                return s_HasAirbrake(gauge) && inputs.throttle == 0f;
            for (int i = 0; i < s_Airbrakes.Length; i++)
            {
                var airbrake = s_Airbrakes[i];
                if (airbrake != null && s_OpenAmount(airbrake) > 0f)
                    return true;
            }
            return false;
        }

        // Once per aircraft: what the gauge text can come from, for bug reports.
        private static void LogGauge(ThrottleGauge gauge, Aircraft aircraft)
        {
            var names = new System.Text.StringBuilder();
            var regions = s_Regions(gauge);
            if (regions != null)
            {
                foreach (var region in regions)
                {
                    if (region == null)
                        continue;
                    names.Append(names.Length == 0 ? "" : ", ")
                        .Append('"').Append(s_RegionName(region)).Append('"')
                        .Append(s_ShowName(region) ? " name" : "")
                        .Append(s_ShowPercent(region) ? " percent" : "");
                }
            }
            Plugin.Log.LogInfo($"HUD.Throttle reading: {aircraft.name}: {s_Airbrakes.Length} airbrake part(s), gauge airbrake flag {s_HasAirbrake(gauge)}, regions [{names}]");
        }

        // Only throttle 0 can show AIRBRAKE, as in vanilla; above 0 the percent shows
        // while the panels close.
        private static bool WantsAirbrakeText(ThrottleGauge gauge, ControlInputs inputs)
        {
            return inputs.throttle == 0f && IsAirbrakeOpen(gauge, inputs);
        }

        [HarmonyPatch(typeof(ThrottleGauge), k_UpdateMethod)]
        private static class UpdateDisplayPatch
        {
            private static void Postfix(ThrottleGauge __instance)
            {
                var reading = s_Reading(__instance);
                var inputs = s_Inputs(__instance);
                if (reading == null || inputs == null)
                    return;

                if (WantsAirbrakeText(__instance, inputs))
                {
                    reading.text = k_AirbrakeText;
                    return;
                }

                var region = s_CurrentRegion(__instance);
                if (region != null && s_Percent(region) < 0f)
                {
                    // GetText with the percent at 0. Strings are made only on a change.
                    string text = s_ShowName(region) ? s_RegionName(region) : string.Empty;
                    if (s_ShowPercent(region))
                        text += k_ZeroText;
                    reading.text = text;
                }
                // Vanilla writes AIRBRAKE at 0 before the panels open, and a region can
                // carry that name over the bottom of the throttle; with the panels closed
                // show the percent as the gauge without regions does.
                if (ShowsAirbrake(reading))
                    reading.text = string.Format("{0:F0}%", inputs.throttle * 100f);
            }
        }

        private static bool ShowsAirbrake(TextMeshProUGUI reading)
        {
            string text = reading.text;
            return text != null && text.IndexOf(k_AirbrakeText, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Refresh runs every frame (HUDAppManager.Update) but redraws only when the
        // throttle changes. When the panels open or close without a throttle change,
        // reset throttlePrev so the next Refresh redraws the reading. The check reads
        // each gauge's own text, so the HUD and helmet gauges stay apart.
        [HarmonyPatch(typeof(ThrottleGauge), nameof(ThrottleGauge.Refresh))]
        private static class RefreshPatch
        {
            private static void Postfix(ThrottleGauge __instance)
            {
                var inputs = s_Inputs(__instance);
                var reading = s_Reading(__instance);
                if (inputs == null || reading == null || s_Aircraft(__instance) == null)
                    return;
                if (WantsAirbrakeText(__instance, inputs) != ShowsAirbrake(reading))
                    s_ThrottlePrev(__instance) = -1f;
            }
        }
    }
}
