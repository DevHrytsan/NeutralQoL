#if DEBUG
using BepInEx.Configuration;
using NeutralQoL.Core;
using UnityEngine;

namespace NeutralQoL.Features.DevSample
{
    internal sealed class DevSampleFeature : Feature
    {
        public enum Units { Metric, Imperial, Nautical }

        public override string Section => "Dev";
        public override string Name => "Sample feature";
        public override string Description => "Debug build only: one option of every type, for testing the QoL tab. Does nothing.";
        // Set to true to check the option rows in game; copies of this file should drop this line.
        public override bool ShowInMenu => false;

        protected override void BindOptions()
        {
            Bind("Show extra info", true, "Sample on/off option.");
            Bind("Zoom step", 1.5f, "Sample decimal slider.", new AcceptableValueRange<float>(0.5f, 5f));
            Bind("Line count", 8, "Sample whole number slider.", new AcceptableValueRange<int>(1, 20));
            Bind("Units", Units.Metric, "Sample list.");
            Bind("Hotkey", new KeyboardShortcut(KeyCode.F8, KeyCode.LeftControl), "Sample keybind, edited in the config file.");
        }
    }
}
#endif
