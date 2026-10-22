using UnityEngine;

namespace NeutralQoL.Shared.CargoDrop
{
    internal static class CargoDropService
    {
        private const float k_RefreshSeconds = 0.1f;

        private static WeaponStation s_Station;
        private static MountedCargo s_Mount;
        private static CargoDropModel s_Model;
        private static float s_ComputedAt = float.NegativeInfinity;
        private static Vector3 s_AircraftVelocity;
        private static CargoDropPrediction s_Prediction;

        // Cargo type last predicted for, kept after the last one leaves the station so
        // its spawn can still be matched.
        public static UnitDefinition LastDefinition { get; private set; }

        // Last valid prediction and when it was made, for the drop log. Outlives the
        // mount: when the cargo spawns the station has moved on.
        public static CargoDropPrediction LastValidPrediction { get; private set; }
        public static float LastValidTime { get; private set; } = float.NegativeInfinity;
        // When the mount was last seen empty: within 0.1 s of Fire.
        public static float LastFireTime { get; private set; } = float.NegativeInfinity;
        // The Fire to spawn delay predicted for the cargo that left at LastFireTime.
        public static float LastFireDelay { get; private set; }
        // The prediction for that cargo at the last refresh before it was fired.
        public static CargoDropPrediction LastFirePrediction { get; private set; }
        // The acceleration that prediction carried through the delay and the input rate
        // then, to see how much held inputs help or hurt.
        public static Vector3 LastFireAcceleration { get; private set; }
        public static float LastFireInputRate { get; private set; }
        public static float LastFireRampWait { get; private set; }
        public static float LastFireRampSwing { get; private set; }
        public static float LastFireFramesPerSecond { get; private set; }
        private static float s_RampWait;
        private static float s_RampSwing;
        private static float s_Delay;

        public static float ReleaseDelay => s_Delay;

        // Aircraft acceleration carried through the release delay: the prediction assumes
        // the pilot holds the current inputs, so a steady pull or turn is predicted too.
        private static Vector3 s_Acceleration;
        // How fast the pilot moves pitch, roll and yaw (sum of axes, full deflections per
        // second). Held inputs keep the prediction good; changing ones make the exit
        // point uncertain.
        public static float InputRate { get; private set; }
        private static float s_Pitch;
        private static float s_Roll;
        private static float s_Yaw;
        private static float s_SampledAt = float.NegativeInfinity;
        // Smoothing of both, so one noisy step doesn't swing the result.
        private const float k_SmoothSeconds = 0.3f;
        // Past this gap the last sample is too old to difference against.
        private const float k_MaxSampleGap = 0.5f;
        // 10 g: anything above is a floating origin shift or a crash.
        private const float k_MaxAcceleration = 100f;

        public static void Reset()
        {
            s_Station = null;
            s_Mount = null;
            s_Model = null;
            s_ComputedAt = float.NegativeInfinity;
            s_Prediction = default;
            s_Acceleration = Vector3.zero;
            InputRate = 0f;
            s_SampledAt = float.NegativeInfinity;
        }

        // The last refresh's prediction with the landing point moved along with the
        // aircraft by frame time, so a marker drawn every frame glides between refreshes.
        public static bool TryGet(Aircraft aircraft, WeaponStation station, out CargoDropPrediction prediction, out CargoDropModel model)
        {
            prediction = default;
            model = null;
            if (aircraft == null || station == null || aircraft.rb == null)
                return false;

            float now = Time.fixedTime;
            if (station != s_Station || s_Mount == null || s_Mount.GetAmmoLoaded() <= 0 || now - s_ComputedAt >= k_RefreshSeconds || now < s_ComputedAt)
                Refresh(aircraft, station, now);

            model = s_Model;
            if (s_Model == null || !s_Prediction.Valid)
                return false;
            prediction = s_Prediction;
            prediction.Point += s_AircraftVelocity * Mathf.Max(Time.time - s_ComputedAt, 0f);
            return true;
        }

        private static void Refresh(Aircraft aircraft, WeaponStation station, float now)
        {
            if (station != s_Station)
            {
                s_Station = station;
                s_Mount = null;
            }
            if (s_Mount != null && s_Mount.GetAmmoLoaded() <= 0)
            {
                LastFireTime = Time.time;
                LastFireDelay = s_Delay;
                LastFirePrediction = s_Prediction;
                LastFireAcceleration = s_Acceleration;
                LastFireInputRate = InputRate;
                LastFireRampWait = s_RampWait;
                LastFireRampSwing = s_RampSwing;
                LastFireFramesPerSecond = CargoDropModel.FramesPerSecond;
            }
            if (s_Mount == null || s_Mount.GetAmmoLoaded() <= 0)
                s_Mount = NextMount(station);
            Sample(aircraft, now);
            s_Model = s_Mount != null ? CargoDropModel.Get(s_Mount.cargo) : null;
            if (s_Model != null)
                LastDefinition = s_Model.Definition;
            s_ComputedAt = now;
            s_Prediction = default;
            if (s_Model == null)
                return;

            // Fired now, the cargo leaves after the release delay at the door: it starts
            // where the aircraft carries that point by then, with the aircraft's velocity
            // there plus the rail push, assuming the current acceleration is held.
            s_Delay = CargoDropModel.ReleaseDelay(s_Mount, out var railOffset);
            s_RampWait = CargoDropModel.LastRampWait;
            s_RampSwing = CargoDropModel.LastRampSwing;
            var mountPosition = s_Mount.transform.position;
            var velocity = aircraft.rb.GetPointVelocity(mountPosition);
            var start = mountPosition + railOffset + velocity * s_Delay + s_Acceleration * (0.5f * s_Delay * s_Delay);
            var releaseVelocity = velocity + s_Acceleration * s_Delay + CargoDropModel.RailVelocity(s_Mount);
            CargoDropSimulator.Predict(s_Model, start, releaseVelocity, out s_Prediction);
            if (s_Prediction.Valid)
            {
                LastValidPrediction = s_Prediction;
                LastValidTime = now;
            }
        }

        private static void Sample(Aircraft aircraft, float now)
        {
            var velocity = aircraft.rb.velocity;
            var inputs = aircraft.GetInputs();
            float dt = now - s_SampledAt;
            if (dt > 0f && dt <= k_MaxSampleGap)
            {
                float k = 1f - Mathf.Exp(-dt / k_SmoothSeconds);
                var acceleration = Vector3.ClampMagnitude((velocity - s_AircraftVelocity) / dt, k_MaxAcceleration);
                s_Acceleration = Vector3.Lerp(s_Acceleration, acceleration, k);
                float rate = inputs != null
                    ? (Mathf.Abs(inputs.pitch - s_Pitch) + Mathf.Abs(inputs.roll - s_Roll) + Mathf.Abs(inputs.yaw - s_Yaw)) / dt
                    : 0f;
                InputRate = Mathf.Lerp(InputRate, rate, k);
            }
            else if (dt > k_MaxSampleGap)
            {
                s_Acceleration = Vector3.zero;
                InputRate = 0f;
            }
            if (dt != 0f)
            {
                s_AircraftVelocity = velocity;
                s_SampledAt = now;
                if (inputs != null)
                {
                    s_Pitch = inputs.pitch;
                    s_Roll = inputs.roll;
                    s_Yaw = inputs.yaw;
                }
            }
        }

        private static MountedCargo NextMount(WeaponStation station)
        {
            var weapons = station.Weapons;
            for (int i = 0; i < weapons.Count; i++)
            {
                if (weapons[i] is MountedCargo mount && mount != null && mount.GetAmmoLoaded() > 0)
                    return mount;
            }
            return null;
        }
    }
}
