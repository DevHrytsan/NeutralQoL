using NeutralQoL.Core;

namespace NeutralQoL.Features.CargoDropAid
{
    internal sealed class CargoDropAidFeature : Feature
    {
        public override string Section => "HUD";
        public override string Name => "Cargo drop aid";
        public override string Description =>
            "While cargo is selected, shows where it would land if dropped now and warns when the drop would destroy it. Works out the drop with the game's own parachute and cargo physics, including wind.";

        protected override bool DefaultOn => true;
    }
}
