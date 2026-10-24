using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;

namespace NeutralQoL.Core
{
    internal static class FeatureRegistry
    {
        public static IReadOnlyList<Feature> All { get; private set; } = Array.Empty<Feature>();

        // Rebuilt only when the enabled set changes, so Tick never allocates.
        private static Feature[] s_Ticking = Array.Empty<Feature>();

        public static void Init(ConfigFile config)
        {
            All = Discover();
            // A part's entries go between its owner's toggle and the owner's own options,
            // so the tab lists them in that order.
            foreach (var owner in All.Where(f => f.PartOf == null))
            {
                owner.BindEnabled(config, null);
                foreach (var part in All.Where(f => f.PartOf == owner.GetType()))
                {
                    part.BindEnabled(config, owner);
                    part.Init();
                }
                owner.Init();
            }
            foreach (var feature in All.Where(f => !f.Bound))
                Plugin.Log.LogWarning($"{feature.Section}.{feature.Name} skipped: owner {feature.PartOf.Name} not found");
            All = All.Where(f => f.Bound).ToList();
            foreach (var feature in All.Where(f => f.Enabled != null))
            {
                var f = feature;
                feature.Enabled.SettingChanged += (_, __) => Apply(f);
            }
            foreach (var feature in All)
                feature.SetActive(feature.Wanted);
            RebuildTicking();

            foreach (var feature in All)
            {
                var state = feature.State == FeatureState.Broken ? $"broken: {feature.BrokenReason}" : feature.State.ToString().ToLowerInvariant();
                Plugin.Log.LogInfo($"  {feature.Section}.{feature.Name}: {state}");
            }
        }

        public static void Tick()
        {
            var features = s_Ticking;
            for (int i = 0; i < features.Length; i++)
            {
                try
                {
                    features[i].Tick();
                }
                catch (Exception e)
                {
                    features[i].Fail(e);
                    RebuildTicking();
                }
            }
        }

        public static void Shutdown()
        {
            foreach (var feature in All)
                feature.SetActive(false);
            s_Ticking = Array.Empty<Feature>();
        }

        private static void Apply(Feature feature)
        {
            feature.SetActive(feature.Wanted);
            foreach (var part in All)
                if (part.Owner == feature)
                    part.SetActive(part.Wanted);
            RebuildTicking();
        }

        private static void RebuildTicking()
        {
            s_Ticking = All.Where(f => f.Ticks && f.State == FeatureState.On).ToArray();
        }

        private static List<Feature> Discover()
        {
            Type[] types;
            try
            {
                types = typeof(Feature).Assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                // A game update can remove a type one feature uses; keep the others running.
                types = e.Types.Where(t => t != null).ToArray();
            }

            var features = new List<Feature>();
            foreach (var type in types.Where(t => !t.IsAbstract && typeof(Feature).IsAssignableFrom(t)))
            {
                try
                {
                    var feature = (Feature)Activator.CreateInstance(type);
                    if (feature.Shelved)
                    {
                        Plugin.Log.LogInfo($"{feature.Section}.{feature.Name}: shelved, not loaded");
                        continue;
                    }
                    features.Add(feature);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"{type.Name} skipped: {e.Message}");
                }
            }
            return features.OrderBy(f => f.Section).ThenBy(f => f.Name).ToList();
        }
    }
}
