using System;
using BepInEx.Configuration;
using HarmonyLib;
using NeutralQoL.Core;
using Rewired;
using UnityEngine;

namespace NeutralQoL.Features.IdleStop
{
    // Input conditioning like a real idle detent. The idle value (just above 0)
    // is one an analog axis can send too, so peers get nothing new and no simulation changes.
    internal sealed class IdleStopFeature : Feature
    {
        public override string Section => "Controls";
        public override string Name => "Idle stop before airbrake";
        public override string Description =>
            "Throttle down to 0% opens the airbrake. Click: press again to close it at idle. Hold: open only while held.";

        protected override bool DefaultOn => true;
        protected override Type[] PatchClasses => new[] { typeof(ThrottlePatch), typeof(LeaveStatePatch) };

        private const string k_ThrottleMethod = "PlayerThrottleAxis1Controls";
        private const string k_ThrottleAxis = "Throttle";
        private const string k_ModifierButton = "Axis Modifier";
        // Far enough above 0 to survive the half float the input goes out as, small
        // enough that the gauge (F0) still reads 0% and thrust is the same as at 0.
        private const float k_IdleThrottle = 0.001f;
        private const float k_IdleTolerance = 0.0001f;
        // Keys and buttons read exactly +-1; an analog axis rarely sits there while it
        // jumps, and the stop leaves it alone.
        private const float k_DigitalInput = 0.999f;

        private static AccessTools.FieldRef<PilotPlayerState, Player> s_Player;
        private static AccessTools.FieldRef<PilotPlayerState, float> s_SimulatedThrottle;
        private static AccessTools.FieldRef<PilotPlayerState, bool> s_Collective;
        private static AccessTools.FieldRef<PilotPlayerState, ControlInputs> s_ControlInputs;

        public enum OpenMode { Click, Hold }

        private static ConfigEntry<OpenMode> s_OpenMode;
        private static ConfigEntry<bool> s_DownCloses;
        private static int s_PrevDirection;

        private static PilotPlayerState s_Owner;
        // True while the down key that stopped at idle is still held; it keeps the
        // throttle at idle until that key is released.
        private static bool s_HoldAtIdle;
        // Hold mode: the down key held at idle opened the airbrake, and releasing it
        // goes back to idle.
        private static bool s_BrakeHeld;
        // The down key held now was pressed above idle. It goes on to 0 as in vanilla,
        // and the airbrake it opens stays open after release.
        private static bool s_PassThrough;

        protected override void BindOptions()
        {
            s_OpenMode = Bind("Open airbrake by", OpenMode.Click,
                "Click: at idle, release throttle down and press it again to open the airbrake. Hold: at idle, the airbrake is open only while you hold throttle down, and closes when you release it.");
            s_DownCloses = Bind("Throttle down closes airbrake", true,
                "Click mode: with the airbrake open, a new throttle down press closes it and goes back to idle, so throttle down works as an airbrake switch.");
        }

        protected override void Resolve()
        {
            Targets.Method(typeof(PilotPlayerState), k_ThrottleMethod);
            Targets.Method(typeof(PilotPlayerState), nameof(PilotPlayerState.LeaveState));
            s_Player = Targets.Field<PilotPlayerState, Player>("player");
            s_SimulatedThrottle = Targets.Field<PilotPlayerState, float>("simulatedThrottle");
            s_Collective = Targets.Field<PilotPlayerState, bool>("collective");
            s_ControlInputs = Targets.Field<PilotPlayerState, ControlInputs>("controlInputs");
        }

        // Nothing is held after this, so the next frame is vanilla.
        protected override void OnDisable()
        {
            Reset();
        }

        private static void Reset()
        {
            s_Owner = null;
            s_HoldAtIdle = false;
            s_BrakeHeld = false;
            s_PassThrough = false;
            s_PrevDirection = 0;
        }

        // Same steps as PlayerThrottleAxis1Controls: clamp, snap for relative throttle,
        // and the Axis Modifier sends the throttle keys to Custom Axis 1 instead.
        private static float ReadInput(Player player)
        {
            float input = Mathf.Clamp(player.GetAxisRaw(k_ThrottleAxis), -1f, 1f);
            if (PlayerSettings.throttleUseRelative)
                input = Mathf.Abs(input) > 0.1f ? Mathf.Sign(input) : 0f;
            if (player.GetButton(k_ModifierButton))
                input = 0f;
            return input;
        }

        private static float ToThrottle(float simulated, bool invert)
        {
            float value = PlayerSettings.throttleUseNegative ? 0.5f * (simulated + 1f) : simulated;
            if (invert)
                value = 1f - value;
            return Mathf.Clamp01(value);
        }

        private static float ToSimulated(float throttle, bool invert)
        {
            float value = invert ? 1f - throttle : throttle;
            return PlayerSettings.throttleUseNegative ? 2f * value - 1f : value;
        }

        private static void Apply(PilotPlayerState state, float simulatedBefore)
        {
            if (state != s_Owner)
            {
                Reset();
                s_Owner = state;
            }

            var player = s_Player(state);
            var inputs = s_ControlInputs(state);
            if (player == null || inputs == null)
                return;

            // With an inverted collective the down key raises the throttle.
            bool invert = s_Collective(state) && PlayerSettings.invertCollective;
            float input = ReadInput(player);
            int direction = Mathf.Abs(input) < k_DigitalInput ? 0 : (int)Mathf.Sign(invert ? -input : input);
            bool newPress = direction != 0 && direction != s_PrevDirection;
            s_PrevDirection = direction;
            bool hold = s_OpenMode.Value == OpenMode.Hold;
            if (direction >= 0)
                s_PassThrough = false;

            if (s_BrakeHeld && direction >= 0)
            {
                // Hold mode: down released with the airbrake open goes back to idle. With
                // up pressed instead, vanilla already moves the throttle up.
                s_BrakeHeld = false;
                if (direction == 0 && inputs.throttle <= 0f)
                    SetIdle(state, inputs, invert);
            }
            if (s_HoldAtIdle)
            {
                if (direction < 0)
                {
                    SetIdle(state, inputs, invert);
                    return;
                }
                s_HoldAtIdle = false;
            }
            // Up is never stopped: a pause there felt like input lag.
            if (direction >= 0)
                return;

            float before = ToThrottle(simulatedBefore, invert);
            float after = inputs.throttle;
            bool aboveIdle = before > k_IdleThrottle + k_IdleTolerance;
            bool atZero = before <= 0f;
            // Down held from above idle goes on to 0 (airbrake) as in vanilla, in both
            // modes. Click mode keeps it open after release as vanilla does; hold mode
            // closes it on release, the same as a hold that starts at idle.
            if (aboveIdle)
                s_PassThrough = true;
            if (s_PassThrough)
            {
                if (hold && after <= 0f)
                    s_BrakeHeld = true;
                return;
            }
            // At idle a new down press goes on to 0 (airbrake) at once. In click mode a new
            // down press at 0 closes the airbrake when that option is on; in hold mode
            // releasing the key closes it.
            bool closeDown = !hold && atZero && newPress && s_DownCloses.Value;
            if (closeDown)
            {
                s_HoldAtIdle = true;
                SetIdle(state, inputs, invert);
                return;
            }
            if (hold && after <= 0f)
                s_BrakeHeld = true;
        }

        // Writes the simulated throttle back as well, so the next frame moves on from idle.
        private static void SetIdle(PilotPlayerState state, ControlInputs inputs, bool invert)
        {
            inputs.throttle = k_IdleThrottle;
            s_SimulatedThrottle(state) = ToSimulated(k_IdleThrottle, invert);
        }

        [HarmonyPatch(typeof(PilotPlayerState), k_ThrottleMethod)]
        private static class ThrottlePatch
        {
            private static void Prefix(PilotPlayerState __instance, out float __state)
            {
                __state = s_SimulatedThrottle(__instance);
            }

            private static void Postfix(PilotPlayerState __instance, float __state)
            {
                Apply(__instance, __state);
            }
        }

        [HarmonyPatch(typeof(PilotPlayerState), nameof(PilotPlayerState.LeaveState))]
        private static class LeaveStatePatch
        {
            private static void Postfix()
            {
                Reset();
            }
        }
    }
}
