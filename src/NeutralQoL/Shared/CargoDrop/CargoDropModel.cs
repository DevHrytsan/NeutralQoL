using System.Collections.Generic;
using HarmonyLib;
using NeutralQoL.Core;
using UnityEngine;

namespace NeutralQoL.Shared.CargoDrop
{
    internal sealed class CargoDropModel
    {
        // ImpactDetector compares accel.sqrMagnitude with gLimit^2 * 82.81, so the limit
        // is gLimit * 9.1 m/s2, not 9.81.
        private const float k_DetectorG = 9.1f;
        // Container.InitializeUnit and GroundVehicle.CheckAirdrop skip the chute when a
        // 10 m line down from the spawn point hits the ground.
        public const float NoChuteHeight = 10f;
        // GroundVehicle.CheckAirdrop waits this long before attaching the chute.
        private const float k_VehicleChuteDelay = 0.5f;
        // Fallback when the ramp geometry can't be read: share of the swing after which
        // IsOpen was true in logged drops.
        private const float k_RampOpenShare = 0.43f;
        private const float k_RampSearchStep = 0.5f;
        private const float k_RampPollSeconds = 0.05f;

        public UnitDefinition Definition;
        public float Mass;
        public float RigidbodyDrag;
        // Container.FixedUpdate: force = -rel * |rel| * airDragCoef * height^2.
        public float QuadraticDrag;
        // Unit.CheckRadarAlt subtracts spawnOffset.y, so touchdown is when the pivot is
        // this far above the ground.
        public float BottomOffset;

        public bool HasChute;
        public int NoChuteMask;
        public float ChuteStartDelay;
        public int ChuteCount;
        public float CanopyMass;
        public float LineSpring;
        public float LineSlack;
        public float MaxDrag;
        public AnimationCurve ChuteDrag;
        public float AltitudeMin;
        public float AltitudeMax;
        public float SpeedMin;
        public float SpeedMax;
        public float DelayMin;
        public float DelayMax;

        // 0 without an ImpactDetector: nothing breaks on landing.
        public float ImpactAccelLimit;
        // GroundVehicle suspension (GroundVehicleJob_Math2.SuspensionPhysics): spring and
        // damper from this height up, spreading a landing over many steps. 0 for containers.
        public float SuspensionTravel;
        public bool HasSuspension => SuspensionTravel > 0f;

        private static bool s_Resolved;
        private static AccessTools.FieldRef<Container, GameObject> s_ContainerChute;
        private static AccessTools.FieldRef<Container, float> s_AirDragCoef;
        private static AccessTools.FieldRef<GroundVehicle, GameObject> s_VehicleChute;
        private static AccessTools.FieldRef<GroundVehicle, float> s_SuspensionTravel;
        private static AccessTools.FieldRef<CargoDeploymentSystem, List<Parachute>> s_Parachutes;
        private static AccessTools.FieldRef<Parachute, float> s_CanopyMass;
        private static AccessTools.FieldRef<Parachute, float> s_LineSpring;
        private static AccessTools.FieldRef<Parachute, float> s_LineSlack;
        private static AccessTools.FieldRef<Parachute, float> s_MaxDrag;
        private static AccessTools.FieldRef<Parachute, AnimationCurve> s_ChuteDrag;
        private static AccessTools.FieldRef<Parachute, float> s_AltitudeMin;
        private static AccessTools.FieldRef<Parachute, float> s_AltitudeMax;
        private static AccessTools.FieldRef<Parachute, float> s_SpeedMin;
        private static AccessTools.FieldRef<Parachute, float> s_SpeedMax;
        private static AccessTools.FieldRef<Parachute, float> s_DelayMin;
        private static AccessTools.FieldRef<Parachute, float> s_DelayMax;
        private static AccessTools.FieldRef<ImpactDetector, float> s_GLimit;
        private static AccessTools.FieldRef<MountedCargo, Vector3> s_RailVector;
        private static AccessTools.FieldRef<MountedCargo, float> s_RailSpeed;
        private static AccessTools.FieldRef<MountedCargo, float> s_RailDelay;
        private static AccessTools.FieldRef<MountedCargo, BayDoor> s_CargoDoor;
        private static AccessTools.FieldRef<BayDoor, float> s_HingeAngle;
        private static AccessTools.FieldRef<BayDoor, float> s_OpenSpeed;
        private static AccessTools.FieldRef<CargoRamp, float> s_RampAngle;
        private static AccessTools.FieldRef<CargoRamp, Transform> s_RampSurfaceNormal;
        private static AccessTools.FieldRef<CargoRamp, AeroPart> s_RampAttachedPart;
        private static AccessTools.FieldRef<CargoRamp, AeroPart> s_RampPart;

        private static readonly Dictionary<UnitDefinition, CargoDropModel> s_Cache = new Dictionary<UnitDefinition, CargoDropModel>();

        // Called from each feature's Resolve, so a game update that renames a field only
        // breaks the features that use the model.
        public static void Resolve()
        {
            if (s_Resolved)
                return;
            s_ContainerChute = Targets.Field<Container, GameObject>("parachuteSystem");
            s_AirDragCoef = Targets.Field<Container, float>("airDragCoef");
            s_VehicleChute = Targets.Field<GroundVehicle, GameObject>("parachuteSystem");
            s_SuspensionTravel = Targets.Field<GroundVehicle, float>("suspensionTravel");
            s_Parachutes = Targets.Field<CargoDeploymentSystem, List<Parachute>>("listParachutes");
            s_CanopyMass = Targets.Field<Parachute, float>("canopyMass");
            s_LineSpring = Targets.Field<Parachute, float>("lineSpring");
            s_LineSlack = Targets.Field<Parachute, float>("lineSlackLength");
            s_MaxDrag = Targets.Field<Parachute, float>("maxDrag");
            s_ChuteDrag = Targets.Field<Parachute, AnimationCurve>("chuteDrag");
            s_AltitudeMin = Targets.Field<Parachute, float>("openAltitudeMin");
            s_AltitudeMax = Targets.Field<Parachute, float>("openAltitudeMax");
            s_SpeedMin = Targets.Field<Parachute, float>("openSpeedMin");
            s_SpeedMax = Targets.Field<Parachute, float>("openSpeedMax");
            s_DelayMin = Targets.Field<Parachute, float>("openDelayMin");
            s_DelayMax = Targets.Field<Parachute, float>("openDelayMax");
            s_GLimit = Targets.Field<ImpactDetector, float>("gLimit");
            s_RailVector = Targets.Field<MountedCargo, Vector3>("railVector");
            s_RailSpeed = Targets.Field<MountedCargo, float>("railSpeed");
            s_RailDelay = Targets.Field<MountedCargo, float>("railDelay");
            s_CargoDoor = Targets.Field<MountedCargo, BayDoor>("cargoDoor");
            s_HingeAngle = Targets.Field<BayDoor, float>("hingeAngle");
            s_OpenSpeed = Targets.Field<BayDoor, float>("openSpeed");
            s_RampAngle = Targets.Field<CargoRamp, float>("currentAngle");
            s_RampSurfaceNormal = Targets.Field<CargoRamp, Transform>("rampSurfaceNormal");
            s_RampAttachedPart = Targets.Field<CargoRamp, AeroPart>("attachedPart");
            s_RampPart = Targets.Field<CargoRamp, AeroPart>("rampPart");
            s_Resolved = true;
        }

        // Fire to spawn, as MountedCargo.RailLaunch runs it: rail delay, wait for a cargo
        // ramp to open, then slide along the rail until the cargo passes the door.
        // railOffset is how far the slide moves it relative to the aircraft.
        public static float ReleaseDelay(MountedCargo mount, out Vector3 railOffset)
        {
            railOffset = Vector3.zero;
            float delay = Mathf.Max(s_RailDelay(mount), 0f);
            var door = s_CargoDoor(mount);
            if (door == null)
                return delay;

            LastRampWait = 0f;
            LastRampSwing = -1f;
            if (door is CargoRamp ramp && !ramp.IsOpen())
            {
                LastRampWait = RampWait(ramp);
                delay += LastRampWait;
            }

            // RailLaunch moves localPosition by railSpeed * railVector each frame while the
            // cargo is still ahead of the door along its forward axis.
            var t = mount.transform;
            var space = t.parent != null ? t.parent : t;
            var step = space.TransformVector(s_RailVector(mount)) * s_RailSpeed(mount);
            float closing = -Vector3.Dot(step, t.forward);
            float distance = Vector3.Dot(t.position - door.transform.position, t.forward);
            if (distance > 0f && closing > 1e-3f)
            {
                float slide = distance / closing;
                delay += slide;
                railOffset = step * slide;
            }
            return delay;
        }

        // Velocity the rail adds to the aircraft's, as in MountedCargo.RailLaunch.
        public static Vector3 RailVelocity(MountedCargo mount) => mount.transform.TransformVector(s_RailVector(mount)) * s_RailSpeed(mount);

        // RailLaunch waits for CargoRamp.IsOpen (Dot(rampSurfaceNormal.forward,
        // attachedPart.up) > 0). The hinge target moves at openSpeed deg/s and the ramp
        // trails it on the joint spring, so the wait is the swing still needed, minus the
        // target's lead, over the opening rate, plus half of RailLaunch's 100 ms polling.
        private static float RampWait(CargoRamp ramp)
        {
            float openSpeed = s_OpenSpeed(ramp);
            if (openSpeed <= 0f)
                return 0f;
            float remaining = Mathf.Abs(s_HingeAngle(ramp) - s_RampAngle(ramp));
            var normal = s_RampSurfaceNormal(ramp);
            var attached = s_RampAttachedPart(ramp);
            var part = s_RampPart(ramp);
            if (normal == null || attached == null || part == null)
                return k_RampOpenShare * remaining / openSpeed;

            if (!SwingToOpen(normal.forward, attached.transform.up, part.transform.right, out float swing))
                return k_RampOpenShare * remaining / openSpeed;
            LastRampSwing = swing;

            float lead = 0f;
            if (part.TryGetComponent<HingeJoint>(out var joint) && joint.connectedBody != null)
            {
                float opening = Mathf.Sign(s_HingeAngle(ramp) - s_RampAngle(ramp));
                lead = Mathf.Max((joint.spring.targetPosition - joint.angle) * opening, 0f);
            }
            // CargoRamp.Update steps by openSpeed * fixedDeltaTime per frame, not per physics
            // step, so it opens faster at higher frame rates (a third shorter waits at 80 fps).
            float degreesPerSecond = openSpeed * Time.fixedDeltaTime / Mathf.Max(FrameSeconds(), 1e-4f);
            return Mathf.Max(swing - lead, 0f) / degreesPerSecond + k_RampPollSeconds;
        }

        private static int s_FrameAt;
        private static float s_RealAt;
        private static float s_FrameSeconds = 1f / 60f;

        // Game seconds per rendered frame. Time.deltaTime is the physics step inside
        // FixedUpdate, where the prediction runs, so frames are counted here.
        private static float FrameSeconds()
        {
            int frame = Time.frameCount;
            float real = Time.realtimeSinceStartup;
            int frames = frame - s_FrameAt;
            float elapsed = real - s_RealAt;
            if (frames > 0 && elapsed >= 0.05f)
            {
                if (elapsed < 2f && Time.timeScale > 0f)
                    s_FrameSeconds = elapsed / frames * Time.timeScale;
                s_FrameAt = frame;
                s_RealAt = real;
            }
            return s_FrameSeconds;
        }

        // Smallest rotation of the surface normal about the hinge axis, toward raising the
        // IsOpen dot product, that makes it positive.
        private static bool SwingToOpen(Vector3 normal, Vector3 up, Vector3 axis, out float degrees)
        {
            degrees = 0f;
            float start = Vector3.Dot(normal, up);
            if (start > 0f)
                return true;
            // d/dtheta of Dot(rotated normal, up) at 0 is Dot(axis x normal, up).
            float direction = Vector3.Dot(Vector3.Cross(axis, normal), up) >= 0f ? 1f : -1f;
            for (float angle = k_RampSearchStep; angle <= 180f; angle += k_RampSearchStep)
            {
                var rotated = Quaternion.AngleAxis(direction * angle, axis) * normal;
                if (Vector3.Dot(rotated, up) > 0f)
                {
                    degrees = angle;
                    return true;
                }
            }
            return false;
        }

        // Last ramp wait and the swing it was based on, in degrees (-1 if the ramp was
        // open or its geometry unusable). For the log.
        public static float LastRampWait { get; private set; }
        public static float LastRampSwing { get; private set; } = -1f;
        public static float FramesPerSecond => 1f / Mathf.Max(s_FrameSeconds, 1e-4f);

        public static CargoDropModel Get(UnitDefinition definition)
        {
            if (definition == null)
                return null;
            if (s_Cache.TryGetValue(definition, out var model))
                return model;
            model = Read(definition);
            s_Cache[definition] = model;
            return model;
        }

        private static CargoDropModel Read(UnitDefinition definition)
        {
            var prefab = definition.unitPrefab;
            if (prefab == null || !prefab.TryGetComponent<Unit>(out var unit))
                return null;

            var model = new CargoDropModel
            {
                Definition = definition,
                Mass = Mathf.Max(unit.GetPrefabMass(), 1f),
                BottomOffset = definition.spawnOffset.y,
            };
            if (prefab.TryGetComponent<Rigidbody>(out var rb))
                model.RigidbodyDrag = rb.drag;
            if (prefab.TryGetComponent<ImpactDetector>(out var detector))
                model.ImpactAccelLimit = s_GLimit(detector) * k_DetectorG;

            GameObject system = null;
            if (unit is Container container)
            {
                system = s_ContainerChute(container);
                model.NoChuteMask = PhysicsLayers.StaticsMask;
                if (prefab.TryGetComponent<BoxCollider>(out var box))
                    model.QuadraticDrag = s_AirDragCoef(container) * box.size.y * box.size.y;
            }
            else if (unit is GroundVehicle vehicle)
            {
                system = s_VehicleChute(vehicle);
                model.NoChuteMask = (int)PhysicsLayers.StaticsMask | (int)PhysicsLayers.ExclusionZonesMask;
                model.ChuteStartDelay = k_VehicleChuteDelay;
                model.SuspensionTravel = Mathf.Max(s_SuspensionTravel(vehicle), 0f);
            }

            if (system != null && system.TryGetComponent<CargoDeploymentSystem>(out var deployment))
            {
                var parachutes = s_Parachutes(deployment);
                // Chutes of one frame share a prefab; the first stands for all.
                var parachute = parachutes != null && parachutes.Count > 0 ? parachutes[0] : null;
                if (parachute != null)
                {
                    model.HasChute = true;
                    model.ChuteCount = parachutes.Count;
                    model.CanopyMass = Mathf.Max(s_CanopyMass(parachute), 0.01f);
                    model.LineSpring = s_LineSpring(parachute);
                    model.LineSlack = s_LineSlack(parachute);
                    model.MaxDrag = s_MaxDrag(parachute);
                    model.ChuteDrag = s_ChuteDrag(parachute);
                    model.AltitudeMin = s_AltitudeMin(parachute);
                    model.AltitudeMax = s_AltitudeMax(parachute);
                    model.SpeedMin = s_SpeedMin(parachute);
                    model.SpeedMax = s_SpeedMax(parachute);
                    model.DelayMin = s_DelayMin(parachute);
                    model.DelayMax = s_DelayMax(parachute);
                }
            }

            Plugin.Log.LogInfo($"Cargo drop model {definition.name}: mass {model.Mass:F0} kg, drag {model.RigidbodyDrag:F3} + {model.QuadraticDrag:F3}, "
                + $"impact limit {model.ImpactAccelLimit:F0} m/s2, "
                + (model.HasSuspension ? $"suspension {model.SuspensionTravel:F2} m, " : "")
                + (model.HasChute
                    ? $"{model.ChuteCount} chutes (canopy {model.CanopyMass:F0} kg, spring {model.LineSpring:F0}, slack {model.LineSlack:F0} m, drag {model.MaxDrag:F0}), opens {model.AltitudeMin:F0} to {model.AltitudeMax:F0} m, {model.SpeedMin:F0} to {model.SpeedMax:F0} m/s, {model.DelayMin:F1} to {model.DelayMax:F1} s"
                    : "no chute"));
            return model;
        }
    }
}
