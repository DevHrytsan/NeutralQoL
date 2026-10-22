using BepInEx.Configuration;
using NeutralQoL.Core;
using UnityEngine;

namespace NeutralQoL.Features.LoadoutPreview
{
    internal sealed class LoadoutPreviewFeature : Feature
    {
        public override string Section => "Menus";
        public override string Name => "Loadout weapon preview";
        public override string Description =>
            "In the aircraft selection menu, while a hardpoint's weapon list is open, shows what that hardpoint carries: opens its bay doors or cargo ramp, moves the camera to it and draws the weapon through the aircraft.";

        protected override bool DefaultOn => true;

        private static ConfigEntry<float> s_KeepAfter;

        // How long the preview lingers after the weapon list closes.
        public static float KeepSeconds => s_KeepAfter != null ? Mathf.Max(s_KeepAfter.Value, 0f) : 2f;

        protected override void BindOptions()
        {
            s_KeepAfter = Bind("Keep after close (seconds)", 2f,
                "How long the doors stay open, the camera stays on the weapon and the weapon stays drawn after the weapon list closes.",
                new AcceptableValueRange<float>(0f, 10f));
        }
    }
}
