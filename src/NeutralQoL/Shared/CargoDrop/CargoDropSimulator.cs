using UnityEngine;

namespace NeutralQoL.Shared.CargoDrop
{
    internal struct CargoDropPrediction
    {
        public bool Valid;
        public Vector3 Point;
        public Vector3 Normal;
        // Velocity into the ground at touchdown, the jump ImpactDetector sees.
        public float ImpactSpeed;
        // Fastest landing that always survived; infinity without an ImpactDetector.
        public float SafeSpeed;
        public bool ChuteOpened;
        public bool InWater;
        // The chute's opening pull alone exceeds the impact limit.
        public bool BreaksInAir;
        // ImpactDetector removes itself once the cargo has been slow for 5 s.
        public bool DetectorGone;
        public float Time;
        // When the chute opens after release; negative if it stays shut.
        public float ChuteOpenTime;
        // Angle of the ground at the landing point, in degrees.
        public float Slope;
        // The cargo lands on suspension, which absorbs the landing on flat ground.
        public bool OnWheels;
        // The path passed a near-vertical surface on the way down.
        public bool PassedObstacle;

        // ImpactAccelLimit * fixedDeltaTime: the detector's own limit. Between SafeSpeed
        // and this a landing is a coin flip (logged trucks at 13.4 to 14.3 m/s both broke
        // and survived); above it the cargo breaks as a rule. Vehicles land on
        // suspension, which sometimes saves them, so the same limits apply.
        public float BreakSpeed;

        public bool Breaks => BreaksInAir || (!InWater && !DetectorGone && ImpactSpeed >= BreakSpeed);
    }

    internal static class CargoDropSimulator
    {
        private const float k_MaxSeconds = 60f;
        // Ground is checked every few steps with a line between checkpoints, so nothing
        // slips through.
        private const int k_CheckEvery = 5;
        private const float k_MaxRay = 20000f;
        // ImpactDetector.FixedUpdate removes itself after 5 s while |vy| < 5 and
        // speed^2 < 10.
        private const float k_DetectorSettleTime = 5f;
        private const float k_DetectorSettleVy = 5f;
        private const float k_DetectorSettleSpeedSq = 10f;
        // Landing at v changes velocity by more than v in the impact step (a small bounce),
        // so it breaks below limit * dt. Measured with LightTruck1_AT (limit
        // 15.2 m/s): 12.8, 13.0 and 13.4 m/s survived, 14.0 was destroyed, so the factor
        // is 1.09 to 1.13.
        private const float k_ImpactBounce = 1.1f;
        // Surfaces steeper than 60 deg count as obstacles, not ground.
        private const float k_MinGroundNormalY = 0.5f;
        private const int k_MaxObstacles = 4;
        private const float k_ObstacleSkip = 0.1f;

        public static bool Predict(CargoDropModel model, Vector3 position, Vector3 velocity, out CargoDropPrediction result)
        {
            result = default;
            result.OnWheels = model.HasSuspension;
            result.BreakSpeed = model.ImpactAccelLimit > 0f ? model.ImpactAccelLimit * Time.fixedDeltaTime : float.PositiveInfinity;
            result.SafeSpeed = result.BreakSpeed / k_ImpactBounce;

            var level = NetworkSceneSingleton<LevelInfo>.i;
            var assets = GameAssets.i;
            if (level == null || assets == null)
                return false;

            float dt = Time.fixedDeltaTime;
            var gravity = Physics.gravity;
            int groundMask = (int)PhysicsLayers.StaticsMask | (int)PhysicsLayers.ShipsMask;
            var bottom = Vector3.up * model.BottomOffset;

            float groundY = GroundY(position, groundMask);
            bool chute = model.HasChute && !Physics.Linecast(position, position - Vector3.up * CargoDropModel.NoChuteHeight, model.NoChuteMask);
            // ImpactDetector.Awake removes itself when the cargo spawns below 1 m.
            bool detector = model.ImpactAccelLimit > 0f && position.y - groundY - model.BottomOffset >= 1f;

            var p = position;
            var v = velocity;
            var checkpoint = p;
            bool open = false;
            float openAmount = 0f;
            var canopyP = Vector3.zero;
            var canopyV = Vector3.zero;
            float t = 0f;
            int steps = Mathf.CeilToInt(k_MaxSeconds / dt);

            for (int step = 1; step <= steps; step++)
            {
                var force = Vector3.zero;
                if (chute)
                {
                    float chuteTime = t - model.ChuteStartDelay;
                    if (!open)
                    {
                        // The game deploys and returns, so the pull starts next step.
                        float radarAlt = p.y - groundY - model.BottomOffset;
                        float speed = v.magnitude;
                        if (chuteTime > model.DelayMin && chuteTime < model.DelayMax && radarAlt > model.AltitudeMin && radarAlt < model.AltitudeMax
                            && speed > model.SpeedMin && speed < model.SpeedMax)
                        {
                            open = true;
                            result.ChuteOpened = true;
                            result.ChuteOpenTime = t;
                            canopyV = v;
                            canopyP = p - v.normalized * 2f;
                        }
                    }
                    else
                    {
                        float density = assets.airDensityAltitude.Evaluate(p.GlobalY() * 0.001f);
                        var line = canopyP - p;
                        float lineLength = line.magnitude;
                        var lineDir = lineLength > 1e-4f ? line / lineLength : Vector3.zero;
                        float tension = Mathf.Max(lineLength - model.LineSlack, 0f) * model.LineSpring;
                        var rel = canopyV - level.GetWind(p.ToGlobalPosition());
                        float drag = model.MaxDrag * model.ChuteDrag.Evaluate(openAmount) * density;
                        var canopyForce = -Vector3.up * model.CanopyMass * 9.81f - lineDir * tension - rel * drag;
                        openAmount += Mathf.Min(rel.magnitude * 0.03f, 1f) * dt;
                        // The cargo feels the clamped tension, the canopy the full one.
                        force += lineDir * (Mathf.Min(tension, model.Mass * 200f) * model.ChuteCount);
                        canopyV += canopyForce / model.CanopyMass * dt;
                        canopyP += canopyV * dt;
                    }
                }
                if (model.QuadraticDrag > 0f && p.y - model.BottomOffset > Datum.LocalSeaY)
                {
                    var rel = v - level.GetWind(p.ToGlobalPosition());
                    force -= rel * (rel.magnitude * model.QuadraticDrag);
                }

                var next = (v + (gravity + force / model.Mass) * dt) / (1f + model.RigidbodyDrag * dt);
                if (detector && (next - v).sqrMagnitude > model.ImpactAccelLimit * model.ImpactAccelLimit * dt * dt)
                    result.BreaksInAir = true;
                v = next;
                p += v * dt;
                t += dt;
                if (detector && t > k_DetectorSettleTime && Mathf.Abs(v.y) < k_DetectorSettleVy && v.sqrMagnitude < k_DetectorSettleSpeedSq)
                {
                    detector = false;
                    result.DetectorGone = true;
                }

                if (step % k_CheckEvery != 0)
                    continue;
                if (Touchdown(checkpoint - bottom, p - bottom, v, groundMask, ref result))
                {
                    result.Time = t;
                    result.Valid = true;
                    return true;
                }
                if (!open)
                    groundY = GroundY(p, groundMask);
                checkpoint = p;
            }

            // Still airborne after the detailed part: a long drop under an open chute has
            // settled at its descent rate, so continue in a straight line.
            if (!open || v.sqrMagnitude < 1e-4f)
                return false;
            var from = p - bottom;
            float speedNow = v.magnitude;
            if (Touchdown(from, from + v / speedNow * k_MaxRay, v, groundMask, ref result))
            {
                result.Time = t + Vector3.Distance(from, result.Point) / speedNow;
                result.Valid = true;
                return true;
            }
            return false;
        }

        private static float GroundY(Vector3 p, int mask)
        {
            // Same line as Unit.CheckRadarAlt, and like it never below sea level.
            float ground = Physics.Linecast(p, p - Vector3.up * 10000f, out var hit, mask) ? hit.point.y : float.NegativeInfinity;
            return Mathf.Max(ground, Datum.LocalSeaY);
        }

        private static bool Touchdown(Vector3 from, Vector3 to, Vector3 v, int mask, ref CargoDropPrediction result)
        {
            float sea = Datum.LocalSeaY;
            bool hitGround = Physics.Linecast(from, to, out var hit, mask);
            // A wall or building side: such drops landed on flat ground about 20 m away
            // and the speed into it was only sideways, so look for the ground past it.
            var path = to - from;
            float length = path.magnitude;
            for (int i = 0; i < k_MaxObstacles && hitGround && hit.normal.y < k_MinGroundNormalY && length > 1e-3f; i++)
            {
                result.PassedObstacle = true;
                var past = hit.point + path / length * k_ObstacleSkip;
                if (Vector3.Dot(to - past, path) <= 0f)
                {
                    hitGround = false;
                    break;
                }
                hitGround = Physics.Linecast(past, to, out hit, mask);
            }
            // Sea first if the line reaches the water before any ground.
            if (to.y < sea && from.y >= sea)
            {
                float k = (from.y - sea) / (from.y - to.y);
                var water = Vector3.Lerp(from, to, k);
                if (!hitGround || hit.point.y < sea)
                {
                    result.Point = water;
                    result.Normal = Vector3.up;
                    result.InWater = true;
                    result.ImpactSpeed = Mathf.Abs(v.y);
                    return true;
                }
            }
            if (!hitGround)
                return false;
            result.Point = hit.point;
            result.Normal = hit.normal;
            result.Slope = Vector3.Angle(hit.normal, Vector3.up);
            result.ImpactSpeed = Mathf.Max(-Vector3.Dot(v, hit.normal), 0f);
            return true;
        }
    }
}
