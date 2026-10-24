using System.Collections.Generic;
using HarmonyLib;
using NeutralQoL.Core;
using NuclearOption.UIStyleSystem;
using TMPro;
using UnityEngine;

namespace NeutralQoL.Features.BombArmingReminder
{
    /// <summary>
    /// Reason why it's bomb arming reminder rather than nuke arming reminder, 
    /// because some mods might have some own implementation of arming delay, and this feature is intended to cover that case.
    /// I hope so :D
    /// </summary>
    internal sealed class BombArmingReminderFeature : Feature 
    {
        public override string Section => "Weapons";
        public override string Name => "Bomb arming reminder";
        public override string Description =>
            "While a bomb with an arming delay is selected, shows [TOO LOW TO ARM] in red when a bomb released now would break on hitting the ground or water before it arms, and [LOW BURST] in yellow when it would go off below its normal height, including on the ground or under water after a soft landing.";
        protected override bool DefaultOn => true;
        private const string k_TooLowText = "[TOO LOW TO ARM]";
        private const string k_LowBurstText = "[LOW BURST]";
        // Same spot as the cargo warning, they never show together.
        private const float k_LabelLines = 6f;
        // Matches the physics step.
        private const float k_Step = 0.02f;
        private const float k_MaxFallSeconds = 180f;
        private const float k_SimulateEvery = 0.1f;
        // Leaves some margin, a normal burst lands slightly under the fuse height.
        private const float k_LowBurstShare = 0.9f;
        // Same cap as Parachute.FixedUpdate.
        private const float k_MaxLineAcceleration = 200f;
        private const float k_GToImpactAcceleration = 9.1f;
        private const float k_WaterDrag = 0.2f;
        private const float k_BounceSpeed = 10f;
        private const float k_BounceShare = 0.25f;
        private const float k_GroundRayLength = 20000f;

        private enum Warning { None, TooLow, LowBurst }

        private sealed class Model
        {
            public float Delay;
            public float FuseHeight;
            // Same formula as the CCIP sight.
            public float DragPerMass;
            public float Mass;
            public float MuzzleVelocity;
            public bool HasChute;
            public float ChuteMaxDrag;
            public AnimationCurve ChuteDrag;
            public float OpenDelayMin;
            public float OpenDelayMax;
            public float OpenAltitudeMin;
            public float OpenAltitudeMax;
            public float OpenSpeedMin;
            public float OpenSpeedMax;
            // 0 when the bomb has no ImpactDetector.
            public float ImpactLimit;
        }

        private static AccessTools.FieldRef<OpticalSeekerBomb, float> s_ArmDelay;
        private static AccessTools.FieldRef<OpticalSeekerBomb, float> s_FuseHeight;
        private static AccessTools.FieldRef<OpticalSeekerBomb, float> s_GuidanceDelay;
        private static AccessTools.FieldRef<Parachute, float> s_MaxDrag;
        private static AccessTools.FieldRef<Parachute, AnimationCurve> s_ChuteDrag;
        private static AccessTools.FieldRef<Parachute, float> s_OpenDelayMin;
        private static AccessTools.FieldRef<Parachute, float> s_OpenDelayMax;
        private static AccessTools.FieldRef<Parachute, float> s_OpenAltitudeMin;
        private static AccessTools.FieldRef<Parachute, float> s_OpenAltitudeMax;
        private static AccessTools.FieldRef<Parachute, float> s_OpenSpeedMin;
        private static AccessTools.FieldRef<Parachute, float> s_OpenSpeedMax;
        private static AccessTools.FieldRef<ImpactDetector, float> s_GLimit;

        // Null for weapons without an arm delay.
        private static readonly Dictionary<WeaponInfo, Model> s_Models = new Dictionary<WeaponInfo, Model>();
        private static TMP_Text s_Label;
        private static Warning s_Shown;
        private static Warning s_Last;
        private static float s_NextSimulation;
        private static bool s_LoggedTemplate;

        protected override void Resolve()
        {
            s_ArmDelay = Targets.Field<OpticalSeekerBomb, float>("armDelay");
            s_FuseHeight = Targets.Field<OpticalSeekerBomb, float>("altitudeFuseHeight");
            s_GuidanceDelay = Targets.Field<OpticalSeekerBomb, float>("guidanceDelay");
            s_MaxDrag = Targets.Field<Parachute, float>("maxDrag");
            s_ChuteDrag = Targets.Field<Parachute, AnimationCurve>("chuteDrag");
            s_OpenDelayMin = Targets.Field<Parachute, float>("openDelayMin");
            s_OpenDelayMax = Targets.Field<Parachute, float>("openDelayMax");
            s_OpenAltitudeMin = Targets.Field<Parachute, float>("openAltitudeMin");
            s_OpenAltitudeMax = Targets.Field<Parachute, float>("openAltitudeMax");
            s_OpenSpeedMin = Targets.Field<Parachute, float>("openSpeedMin");
            s_OpenSpeedMax = Targets.Field<Parachute, float>("openSpeedMax");
            s_GLimit = Targets.Field<ImpactDetector, float>("gLimit");
            Targets.Method(typeof(CombatHUD), nameof(CombatHUD.GetWeaponStation));
            Targets.Method(typeof(LevelInfo), nameof(LevelInfo.GetAirDensity));
        }

        protected override void OnDisable()
        {
            if (s_Label != null)
                Object.Destroy(s_Label.gameObject);
            s_Label = null;
            s_Shown = Warning.None;
            s_Last = Warning.None;
            s_NextSimulation = 0f;
        }

        public override void Tick()
        {
            if (Time.time >= s_NextSimulation)
            {
                s_NextSimulation = Time.time + k_SimulateEvery;
                s_Last = Evaluate();
            }
            Show(s_Last);
        }

        private static Warning Evaluate()
        {
            var hud = SceneSingleton<CombatHUD>.i;

            var aircraft = hud != null ? hud.aircraft : null;
            if (aircraft == null || aircraft.disabled || aircraft.rb == null || aircraft.gearDeployed){
                return Warning.None;
            }

            var station = hud.GetWeaponStation();
            if (station == null || station.Ammo <= 0 || station.WeaponInfo == null)
            {
                return Warning.None;
            }

            var model = GetModel(station.WeaponInfo);
            if (model == null)
            {
                return Warning.None;
            }
            // Same release velocity as the CCIP sight.
            var velocity = aircraft.rb.velocity - Vector3.up * 9.81f * 0.25f + model.MuzzleVelocity * aircraft.transform.forward;
            int mask = (int)PhysicsLayers.StaticsMask | (int)PhysicsLayers.ShipsMask;
            bool water = !Physics.Raycast(aircraft.transform.position, Vector3.down, out var hit, k_GroundRayLength, mask) || hit.point.y <= Datum.LocalSeaY;
            return Simulate(model, aircraft.radarAlt, aircraft.GlobalPosition().y, velocity, water);
        }

        // Assumes flat ground or water at the current radar altitude.
        private static Warning Simulate(Model model, float height, float altitude, Vector3 velocity, bool water)
        {
            float groundAltitude = altitude - height;
            bool open = false;
            float openAmount = 0f;

            for (float t = k_Step; t < k_MaxFallSeconds; t += k_Step)
            {
                float density = LevelInfo.GetAirDensity(groundAltitude + height);
                float speed = velocity.magnitude;
                if (model.HasChute && !open && height > model.OpenAltitudeMin && height < model.OpenAltitudeMax
                    && t > model.OpenDelayMin && t < model.OpenDelayMax && speed > model.OpenSpeedMin && speed < model.OpenSpeedMax)
                    open = true;

                var acceleration = Vector3.down * 9.81f;
                if (speed > 0f)
                {
                    var along = velocity / speed;
                    acceleration -= along * (model.DragPerMass * density * speed * speed);
                    if (open)
                    {
                        // Canopy drag applied straight to the bomb.
                        openAmount += Mathf.Min(speed * 0.03f, 1f) * k_Step;
                        float pull = Mathf.Min(model.ChuteMaxDrag * model.ChuteDrag.Evaluate(openAmount) * density * speed, model.Mass * k_MaxLineAcceleration);
                        acceleration -= along * (pull / model.Mass);
                    }
                }
                velocity += acceleration * k_Step;
                height += velocity.y * k_Step;

                if (t > model.Delay && (model.FuseHeight <= 0f || height < model.FuseHeight))
                    return model.FuseHeight > 0f && height < model.FuseHeight * k_LowBurstShare ? Warning.LowBurst : Warning.None;
                if (height <= 0f)
                    return Land(model, velocity, water);
            }
            return Warning.None;
        }

        private static Warning Land(Model model, Vector3 velocity, bool water)
        {
            if (model.ImpactLimit <= 0f)
                return Warning.LowBurst;
            float speed = velocity.magnitude;
            float change;
            if (water)
            {
                change = Mathf.Min(k_WaterDrag * k_Step * speed * speed, speed);
            }
            else if (speed > k_BounceSpeed)
            {
                float across = new Vector2(velocity.x, velocity.z).magnitude;
                change = new Vector2(across * (1f - k_BounceShare), velocity.y * (1f + k_BounceShare)).magnitude;
            }
            else
            {
                change = speed;
            }
            return change / k_Step > model.ImpactLimit ? Warning.TooLow : Warning.LowBurst;
        }

        private static Model GetModel(WeaponInfo info)
        {
            if (s_Models.TryGetValue(info, out var model))
                return model;

            var prefab = info.weaponPrefab;
            var bomb = prefab != null ? prefab.GetComponentInChildren<OpticalSeekerBomb>(true) : null;
            var missile = prefab != null ? prefab.GetComponentInChildren<Missile>(true) : null;
            if (bomb != null && missile != null && s_ArmDelay(bomb) > 0f)
            {
                float mass = info.massPerRound > 0f ? info.massPerRound : missile.GetMass();
                model = new Model
                {
                    // Arming is checked only once guidance starts.
                    Delay = Mathf.Max(s_ArmDelay(bomb), s_GuidanceDelay(bomb)),
                    FuseHeight = s_FuseHeight(bomb),
                    DragPerMass = 0.5f * missile.GetDragCoef(Mathf.PI / 360f) * missile.GetFinArea() / mass,
                    Mass = mass,
                    MuzzleVelocity = info.muzzleVelocity,
                };
                var chute = prefab.GetComponentInChildren<Parachute>(true);
                if (chute != null && s_ChuteDrag(chute) != null)
                {
                    model.HasChute = true;
                    model.ChuteMaxDrag = s_MaxDrag(chute);
                    model.ChuteDrag = s_ChuteDrag(chute);
                    model.OpenDelayMin = s_OpenDelayMin(chute);
                    model.OpenDelayMax = s_OpenDelayMax(chute);
                    model.OpenAltitudeMin = s_OpenAltitudeMin(chute);
                    model.OpenAltitudeMax = s_OpenAltitudeMax(chute);
                    model.OpenSpeedMin = s_OpenSpeedMin(chute);
                    model.OpenSpeedMax = s_OpenSpeedMax(chute);
                }
                var detector = prefab.GetComponentInChildren<ImpactDetector>(true);
                if (detector != null)
                    model.ImpactLimit = s_GLimit(detector) * k_GToImpactAcceleration;
                Plugin.Log.LogInfo($"Weapons.Bomb arming reminder: {info.name} arms after {model.Delay:F1} s, fuse height {model.FuseHeight:F0} m, mass {mass:F0} kg, "
                    + (model.HasChute ? $"parachute opens below {model.OpenAltitudeMax:F0} m between {model.OpenDelayMin:F1} and {model.OpenDelayMax:F1} s" : "no parachute")
                    + (model.ImpactLimit > 0f ? $", breaks above {model.ImpactLimit:F0} m/s2 on impact" : ", no impact limit"));
            }
            s_Models[info] = model;
            return model;
        }

        private static void Show(Warning warning)
        {
            if (warning == Warning.None)
            {
                if (s_Label != null && s_Label.enabled)
                    s_Label.enabled = false;
                return;
            }
            if (s_Label == null)
            {
                s_Shown = Warning.None;
                if (!CreateLabel())
                    return;
            }
            if (warning != s_Shown)
            {
                s_Shown = warning;
                s_Label.text = warning == Warning.TooLow ? k_TooLowText : k_LowBurstText;
            }
            if (!s_Label.enabled)
            {
                s_Label.enabled = true;
            }
            // Theme can change at runtime.
            var theme = ThemeManager.Active.ColorTheme;
            s_Label.color = warning == Warning.TooLow ? theme.Alert : theme.Warning;
        }

        // Copies an existing HUD text to get the right font and size.
        private static bool CreateLabel()
        {
            var hud = SceneSingleton<FlightHud>.i;
            
            var center = hud != null ? hud.GetHUDCenter() : null;
            if (center == null)
            {
                return false;
            }

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
            {
                return false;
            }

            if (!s_LoggedTemplate)
            {
                s_LoggedTemplate = true;
                Plugin.Log.LogInfo($"Weapons.Bomb arming reminder: text copied from '{template.name}' under '{center.name}'");
            }

            var copy = Object.Instantiate(template.gameObject, center, false);
            copy.name = "NeutralQoL BombArmingReminder";
            copy.SetActive(true);

            for (int i = copy.transform.childCount - 1; i >= 0; i--)
            {
                Object.Destroy(copy.transform.GetChild(i).gameObject);
            }

            s_Label = copy.GetComponent<TMP_Text>();
            foreach (var behaviour in copy.GetComponents<MonoBehaviour>())
            {
                if (behaviour != s_Label)
                    Object.Destroy(behaviour);
            }
            
            s_Label.enableWordWrapping = false;
            s_Label.overflowMode = TextOverflowModes.Overflow;
            s_Label.horizontalAlignment = HorizontalAlignmentOptions.Center;
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
