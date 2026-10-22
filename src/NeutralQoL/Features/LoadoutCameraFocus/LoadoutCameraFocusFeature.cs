using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using NeutralQoL.Core;
using NeutralQoL.Features.LoadoutPreview;
using NeutralQoL.Shared.BayDoorRules;
using NeutralQoL.Shared.Hardpoints;
using NeutralQoL.Shared.LoadoutDropdown;
using TMPro;
using UnityEngine;

namespace NeutralQoL.Features.LoadoutCameraFocus
{
    internal sealed class LoadoutCameraFocusFeature : Feature
    {
        public override string Section => "Menus";
        public override string Name => "Move camera to weapon";
        public override string Description =>
            "Moves the camera to frame the hardpoint's weapon, low beside the aircraft for weapons underneath, and zooms in on it. Hold Free Look and move the mouse to turn round it. Ground equipment in the way fades out.";
        public override Type PartOf => typeof(LoadoutPreviewFeature);
        protected override bool DefaultOn => true;

        protected override Type[] PatchClasses => new[] { typeof(ShowPatch), typeof(UpdateStatePatch) };

        private const float k_BlendSmoothing = 0.25f;
        // Ghost copies of the weapon overlay sit under its renderers; skip them
        // when sizing the weapon.
        private const string k_ModObjectPrefix = "NeutralQoL ";
        private const float k_EquipmentFadeSeconds = 0.3f;
        // With zoom, the weapon's bounding sphere fills this share of the view
        // height. Any narrower and the image gets too shaky.
        private const float k_ZoomShare = 0.6f;
        private const float k_MinFov = 12f;
        // Vanilla selection camera's input names and orbit rate
        // (MoveCamera: Pan View * sensitivity * -0.2 * -150 degrees a second).
        private const string k_FreeLookAction = "Free Look";
        private const string k_PanAction = "Pan View";
        private const string k_TiltAction = "Tilt View";
        private const float k_LookDegreesPerSecond = 30f;
        private const float k_MinLookElevation = -15f;
        private const float k_MaxLookElevation = 75f;
        private const float k_CameraHeight = 0.4f;
        // Share of the view height the weapon's bounding sphere fills.
        private const float k_FitShare = 0.45f;
        private const float k_MinDistance = 3f;
        // Gap kept between camera and aircraft box: the 1 m near clip plus room
        // for parts the box rounds off.
        private const float k_BoxMargin = 1.5f;
        private const float k_MinWeaponRadius = 0.5f;
        // Camera sits slightly ahead of the beam, looking back at the weapon.
        private const float k_AheadDegrees = 20f;
        // A weapon this close to the centre line counts as a centre bay.
        private const float k_CentreHalfWidth = 0.5f;
        // The ground probe starts this far above the weapon (under any hangar
        // roof) and reaches well below the wheels.
        private const float k_GroundProbeUp = 2f;
        private const float k_GroundProbeLength = 20f;
        private const float k_ClearanceMargin = 0.3f;
        // Surfaces facing up more than this are ground, not obstacles.
        private const float k_FloorNormalY = 0.7f;

        private static ConfigEntry<bool> s_BaysOnly;
        private static ConfigEntry<bool> s_OneSide;
        private static AccessTools.FieldRef<CameraSelectionState, float> s_CameraHeight;
        private static AccessTools.FieldRef<CameraSelectionState, bool> s_ManualView;

        private static AccessTools.FieldRef<CameraSelectionState, float> s_OrbitalAngle;
        private static AccessTools.FieldRef<MountedMissile, BayDoor[]> s_MissileDoors;

        private static readonly RaycastHit[] s_Hits = new RaycastHit[16];
        private static readonly List<Renderer> s_WeaponRenderers = new List<Renderer>();
        private static readonly List<Renderer> s_RendererCache = new List<Renderer>();

        private static float s_Weight;
        private static float s_WeightVelocity;
        private static bool s_Applied;
        private static Quaternion s_VanillaRotation;
        private static Vector3 s_FocusPosition;
        private static Quaternion s_FocusRotation;
        private static int s_Version = -1;
        private static bool s_Eligible;
        private static float s_Side = 1f;
        private static bool s_SideSwitched;
        // The side the weapon was measured on with One side only, or 0 when
        // nothing was left out.
        private static float s_MeasuredSide;

        // The side One side only frames, or 0 for both. The weapon ghost
        // (Show weapon through aircraft) follows it, so it only appears on
        // the weapon the camera shows.
        internal static float FramedSide => s_MeasuredSide;

        internal static bool IsFramed(Hardpoint hardpoint)
        {
            if (s_MeasuredSide == 0f || s_MeasuredAircraft == null || hardpoint == null || hardpoint.transform == null)
                return true;
            return OnSide(s_MeasuredAircraft.transform, hardpoint, s_MeasuredSide);
        }
        private static bool s_IsCargo;
        private static WeaponSelector s_SideSelector;
        private static float s_OrbitAngleBefore;
        private static bool s_HoldOrbit;
        private static float s_VanillaFov = 50f;
        private static float s_CameraHeightBefore;
        private static bool s_ManualViewBefore;
        private static float s_LookYaw;
        private static float s_LookPitch;
        // Measured when the list opens or its weapon changes, and again when a
        // measured renderer is destroyed (mounts respawn on change).
        private static Aircraft s_MeasuredAircraft;
        private static Bounds s_AircraftBox;
        private static Vector3 s_WeaponCentre;
        private static float s_WeaponRadius;
        // What was measured. The weapon above follows it, so picking another
        // weapon glides the camera and zoom instead of jumping.
        private static Vector3 s_TargetCentre;
        private static float s_TargetRadius;
        private static Vector3 s_CentreVelocity;
        private static float s_RadiusVelocity;

        protected override void BindOptions()
        {
            s_BaysOnly = Bind("Bays only", false,
                "Only move the camera for hardpoints with bay doors. Off: move it for every hardpoint.", inMenu: false);
            s_OneSide = Bind("One side only", false,
                "When a weapon is loaded on both sides of the aircraft, frame only the side the camera is on, closer up. Off: frame both sides together.");
        }

        protected override void Resolve()
        {
            LoadoutDropdown.Resolve();
            HardpointMount.Resolve();
            Targets.Method(typeof(CameraSelectionState), nameof(CameraSelectionState.UpdateState));
            EquipmentFader.Resolve();
            s_OrbitalAngle = Targets.Field<CameraSelectionState, float>("orbitalAngle");
            s_CameraHeight = Targets.Field<CameraSelectionState, float>("cameraHeight");
            s_ManualView = Targets.Field<CameraSelectionState, bool>("manualViewMode");
            s_MissileDoors = Targets.Field<MountedMissile, BayDoor[]>("bayDoors");
        }

        // Vanilla sets the camera position from scratch each frame and eases
        // only rotation, so handing back its last rotation is enough.
        protected override void OnDisable()
        {
            var manager = SceneSingleton<CameraStateManager>.i;
            if (s_Applied && manager != null)
            {
                manager.transform.rotation = s_VanillaRotation;
                if (manager.mainCamera != null && CameraStateManager.cameraMode == CameraMode.selection)
                    manager.mainCamera.fieldOfView = s_VanillaFov;
            }
            s_Applied = false;
            s_Weight = 0f;
            s_WeightVelocity = 0f;
            s_MeasuredSide = 0f;
            s_CentreVelocity = Vector3.zero;
            s_RadiusVelocity = 0f;
            s_Version = -1;
            s_MeasuredAircraft = null;
            s_SideSelector = null;
            s_HoldOrbit = false;
            // Only this feature sets it; left on, the other loadout features would
            // keep their doors and ghost open.
            LoadoutDropdown.Held = false;
            s_LookYaw = 0f;
            s_LookPitch = 0f;
            s_WeaponRenderers.Clear();
            EquipmentFader.Reset();
        }

        public override void Tick()
        {
            LoadoutDropdown.Update();
            // Uses last frame's camera (LateUpdate moves it); a frame late is
            // invisible in a fade.
            var manager = SceneSingleton<CameraStateManager>.i;
            bool active = s_HoldOrbit && manager != null
                && CameraStateManager.cameraMode == CameraMode.selection;
            var camera = manager != null ? manager.transform.position : Vector3.zero;
            EquipmentFader.Update(LoadoutDropdown.Selector, LoadoutDropdown.Aircraft, camera, s_WeaponCentre, active, k_EquipmentFadeSeconds);
        }

        private static void BeforeUpdate(CameraSelectionState state, CameraStateManager cam)
        {
            // Vanilla eases from the current rotation, so give it its own and the
            // orbit underneath carries on as if no focus happened.
            if (s_Applied)
                cam.transform.rotation = s_VanillaRotation;
            s_OrbitAngleBefore = s_OrbitalAngle(state);
            s_CameraHeightBefore = s_CameraHeight(state);
            s_ManualViewBefore = s_ManualView(state);
        }

        private static void AfterUpdate(CameraSelectionState state, CameraStateManager cam)
        {
            // Hold the vanilla orbit angle (and Free Look) while focused; on exit set it
            // to the focus camera so the circling carries on from there.
            if (s_HoldOrbit)
            {
                s_OrbitalAngle(state) = s_OrbitAngleBefore;
                s_CameraHeight(state) = s_CameraHeightBefore;
                s_ManualView(state) = s_ManualViewBefore;
            }

            // Airbases with a fixed selection camera (hangars, shelters) work the
            // same way: vanilla sets that pose from scratch each frame.
            if (CameraStateManager.cameraMode != CameraMode.selection)
            {
                LoadoutDropdown.Held = false;
                s_Applied = false;
                s_Weight = 0f;
                s_WeightVelocity = 0f;
                return;
            }
            LoadoutDropdown.Update();
            var camTransform = cam.transform;
            camTransform.GetPositionAndRotation(out var vanillaPosition, out var vanillaRotation);
            s_VanillaRotation = vanillaRotation;

            var aircraft = LoadoutDropdown.Aircraft;
            bool wanted = aircraft != null
                && (LoadoutDropdown.IsActive || Time.unscaledTime - LoadoutDropdown.ClosedAt < LoadoutPreviewFeature.KeepSeconds);
            // A side switch for lack of room (TryFocusPose) moves the camera, so
            // with One side only the weapon on the new side is measured.
            bool sideMoved = s_MeasuredSide != 0f && s_MeasuredSide != s_Side;
            if (wanted && (LoadoutDropdown.Version != s_Version || aircraft != s_MeasuredAircraft || WeaponRendererGone() || sideMoved))
            {
                bool newList = LoadoutDropdown.Version != s_Version;
                s_Version = LoadoutDropdown.Version;
                // An empty hardpoint has nothing to look at. Picking a weapon changes
                // weaponMount, which bumps the version and starts the focus.
                s_Eligible = (IsLoaded(LoadoutDropdown.Set) || IsLoaded(LoadoutDropdown.TwinSet))
                    && (IsEligible(LoadoutDropdown.Set) || IsEligible(LoadoutDropdown.TwinSet));
                s_IsCargo = HasRamp(LoadoutDropdown.Set) || HasRamp(LoadoutDropdown.TwinSet);
                // Picking Empty ends the focus and blends back from the old weapon
                // (measuring the empty hardpoint made the zoom snap in first).
                // A fresh list frames the camera's side; a weapon change keeps the
                // focus side.
                float side = !s_OneSide.Value ? 0f
                    : newList && s_Weight <= 0f ? CameraSide(aircraft, vanillaPosition)
                    : s_Side;
                if (s_Eligible || aircraft != s_MeasuredAircraft)
                    Measure(aircraft, side);
                else
                {
                    s_WeaponRenderers.Clear();
                    s_MeasuredSide = 0f;
                }
                // Side is chosen once per hardpoint, so a weapon change in the same
                // list or the orbit moving does not swing it round.
                if (newList && (LoadoutDropdown.Selector != s_SideSelector || s_Weight <= 0f))
                {
                    s_SideSelector = LoadoutDropdown.Selector;
                    s_Side = PickSide(aircraft, vanillaPosition);
                    s_SideSwitched = false;
                    s_LookYaw = 0f;
                    s_LookPitch = 0f;
                }
            }
            // The selection state sets 50 degrees on entry and never changes it.
            // Read while focus is off, put back when it ends.
            var lens = cam.mainCamera;
            if (!s_Applied && lens != null)
                s_VanillaFov = lens.fieldOfView;
            FollowWeapon();
            wanted = wanted && s_Eligible && TryFocusPose(aircraft, s_VanillaFov);
            UpdateLook(wanted);
            if (wanted)
                ApplyLook();
            if (s_HoldOrbit && !wanted && s_Applied)
                AlignOrbit(state, cam);
            s_HoldOrbit = wanted;

            s_Weight = Mathf.SmoothDamp(s_Weight, wanted ? 1f : 0f, ref s_WeightVelocity, k_BlendSmoothing, float.MaxValue, Time.unscaledDeltaTime);
            if (!wanted && s_Weight < 0.001f)
            {
                if (s_Applied && lens != null)
                    lens.fieldOfView = s_VanillaFov;
                s_Weight = 0f;
                s_Applied = false;
                s_LookYaw = 0f;
                s_LookPitch = 0f;
                return;
            }
            float t = Mathf.SmoothStep(0f, 1f, s_Weight);
            var position = BlendPosition(cam, vanillaPosition, vanillaRotation, t, out var look);
            camTransform.SetPositionAndRotation(position, BlendRotation(vanillaPosition, vanillaRotation, position, look, t));
            if (lens != null)
                lens.fieldOfView = Mathf.Lerp(s_VanillaFov, FocusFov(), t);
            s_Applied = true;
        }

        // A straight blend cuts through the aircraft when the poses are on opposite
        // sides. Heading, distance and height blend separately instead, giving an arc.
        private static Vector3 BlendPosition(CameraStateManager cam, Vector3 vanillaPosition, Quaternion vanillaRotation, float t, out Vector3 look)
        {
            var vanillaLook = VanillaLook(cam, vanillaPosition, vanillaRotation);
            look = Vector3.Lerp(vanillaLook, s_WeaponCentre, t);
            var from = vanillaPosition - vanillaLook;
            var to = s_FocusPosition - s_WeaponCentre;
            float heading = Mathf.LerpAngle(Heading(from), Heading(to), t) * Mathf.Deg2Rad;
            float horizontal = Mathf.Lerp(Horizontal(from), Horizontal(to), t);
            float height = Mathf.Lerp(from.y, to.y, t);
            var direction = new Vector3(Mathf.Sin(heading), 0f, Mathf.Cos(heading));

            // Push the middle of the arc out too, so it passes the nose or tail.
            var aircraft = s_MeasuredAircraft;
            if (aircraft != null)
            {
                var aircraftTransform = aircraft.transform;
                var directionLocal = aircraftTransform.InverseTransformDirection(direction);
                directionLocal.y = 0f;
                float exit = ExitDistance(aircraftTransform.InverseTransformPoint(look), directionLocal,
                    s_AircraftBox.min - Vector3.one * k_BoxMargin, s_AircraftBox.max + Vector3.one * k_BoxMargin);
                float middle = 2f * t - 1f;
                float bump = 1f - middle * middle * middle * middle;
                horizontal = Mathf.Max(horizontal, exit * bump);
            }
            return look + direction * horizontal + Vector3.up * height;
        }

        // The orbit eases its rotation toward the aircraft, so the look-at is
        // taken on its own forward line, level with the pivot.
        private static Vector3 VanillaLook(CameraStateManager cam, Vector3 vanillaPosition, Quaternion vanillaRotation)
        {
            var forward = vanillaRotation * Vector3.forward;
            var pivot = cam.cameraPivot != null ? cam.cameraPivot.position : s_WeaponCentre;
            return vanillaPosition + forward * Mathf.Max(Vector3.Dot(pivot - vanillaPosition, forward), 1f);
        }

        // Looks at the sliding point throughout; the orbit's small lag and roll
        // fade out with the blend so both ends match exactly.
        private static Quaternion BlendRotation(Vector3 vanillaPosition, Quaternion vanillaRotation, Vector3 position, Vector3 look, float t)
        {
            var toLook = look - position;
            if (toLook.sqrMagnitude < 0.0001f)
                return Quaternion.Slerp(vanillaRotation, s_FocusRotation, t);
            var vanillaLookRotation = Quaternion.LookRotation(vanillaRotation * Vector3.forward, Vector3.up);
            var lag = vanillaRotation * Quaternion.Inverse(vanillaLookRotation);
            return Quaternion.Slerp(lag, Quaternion.identity, t) * Quaternion.LookRotation(toLook, Vector3.up);
        }

        // The first focus starts on the measured weapon (the blend-in already
        // eases). Later changes glide at the blend's own rate.
        private static void FollowWeapon()
        {
            if (!s_Applied)
            {
                s_WeaponCentre = s_TargetCentre;
                s_WeaponRadius = s_TargetRadius;
                s_CentreVelocity = Vector3.zero;
                s_RadiusVelocity = 0f;
                return;
            }
            float dt = Time.unscaledDeltaTime;
            s_WeaponCentre = Vector3.SmoothDamp(s_WeaponCentre, s_TargetCentre, ref s_CentreVelocity, k_BlendSmoothing, float.MaxValue, dt);
            s_WeaponRadius = Mathf.SmoothDamp(s_WeaponRadius, s_TargetRadius, ref s_RadiusVelocity, k_BlendSmoothing, float.MaxValue, dt);
        }

        private static float Heading(Vector3 offset) => Mathf.Atan2(offset.x, offset.z) * Mathf.Rad2Deg;

        private static float Horizontal(Vector3 offset) => Mathf.Sqrt(offset.x * offset.x + offset.z * offset.z);

        // Holding Free Look orbits the camera round the weapon (vanilla axes and rate).
        // The list counts as open until release (LoadoutDropdown.Held).
        private static void UpdateLook(bool focused)
        {
            var input = GameManager.playerInput;
            bool held = focused && input != null && input.GetButton(k_FreeLookAction);
            LoadoutDropdown.Held = held;
            if (!held)
                return;
            float dt = Time.unscaledDeltaTime;
            float sensitivity = PlayerSettings.viewSensitivity;
            s_LookYaw += input.GetAxis(k_PanAction) * sensitivity * k_LookDegreesPerSecond * dt;
            s_LookPitch += input.GetAxis(k_TiltAction) * sensitivity * k_LookDegreesPerSecond * dt;
        }

        // Turns the focus pose round the weapon by the looked angles, keeping
        // distance, and lifts it off the ground below.
        private static void ApplyLook()
        {
            if (s_LookYaw == 0f && s_LookPitch == 0f)
                return;
            var offset = s_FocusPosition - s_WeaponCentre;
            float distance = offset.magnitude;
            if (distance < 0.01f)
                return;
            float heading = Mathf.Atan2(offset.x, offset.z) * Mathf.Rad2Deg;
            float elevation = Mathf.Asin(Mathf.Clamp(offset.y / distance, -1f, 1f)) * Mathf.Rad2Deg;
            float lookedElevation = Mathf.Clamp(elevation + s_LookPitch, k_MinLookElevation, k_MaxLookElevation);
            // Stops the pitch running past the limit, so turning back answers at once.
            s_LookPitch = lookedElevation - elevation;
            var rotation = Quaternion.Euler(-lookedElevation, heading + s_LookYaw, 0f);
            var position = s_WeaponCentre + rotation * Vector3.forward * distance;

            int groundMask = PhysicsLayers.StaticsMask | PhysicsLayers.ShipsMask;
            var probe = new Vector3(position.x, Mathf.Max(position.y, s_WeaponCentre.y) + k_GroundProbeUp, position.z);
            if (Physics.Raycast(probe, Vector3.down, out var ground, k_GroundProbeLength + distance, groundMask, QueryTriggerInteraction.Ignore))
                position.y = Mathf.Max(position.y, ground.point.y + k_CameraHeight);

            s_FocusPosition = position;
            s_FocusRotation = Quaternion.LookRotation(s_WeaponCentre - position, Vector3.up);
        }

        // The camera often has to stay well out (outside the aircraft's box,
        // beyond a wing for underwing stores), so the lens narrows to keep the
        // weapon the same share of the view at any distance.
        private static float FocusFov()
        {
            float distance = Vector3.Distance(s_FocusPosition, s_WeaponCentre);
            if (distance < 0.01f)
                return s_VanillaFov;
            float fov = 2f * Mathf.Atan(s_WeaponRadius / (distance * k_ZoomShare)) * Mathf.Rad2Deg;
            return Mathf.Clamp(fov, k_MinFov, s_VanillaFov);
        }

        // Vanilla puts the camera along cameraPivot.forward (UpdateOrbit). Set the
        // angle that points at the focus camera so the blend back starts from there.
        private static void AlignOrbit(CameraSelectionState state, CameraStateManager cam)
        {
            var pivot = cam.cameraPivot;
            if (pivot == null)
                return;
            var toFocus = Vector3.ProjectOnPlane(s_FocusPosition - pivot.position, Vector3.up);
            if (toFocus.sqrMagnitude < 0.01f)
                return;
            float angle = s_OrbitalAngle(state);
            var proxyRotation = pivot.rotation * Quaternion.Euler(0f, -angle, 0f);
            var local = Quaternion.Inverse(proxyRotation) * toFocus;
            float aligned = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
            s_OrbitalAngle(state) = aligned;
            s_OrbitAngleBefore = aligned;
        }

        private static bool IsLoaded(HardpointSet set) => set != null && set.weaponMount != null;

        // A cargo hardpoint's first bay door is its ramp (Hardpoint.GetCargoDoor).
        private static bool HasRamp(HardpointSet set)
        {
            if (set == null || set.hardpoints == null)
                return false;
            foreach (var hardpoint in set.hardpoints)
            {
                if (hardpoint == null || hardpoint.bayDoors == null)
                    continue;
                foreach (var door in hardpoint.bayDoors)
                {
                    if (door is CargoRamp)
                        return true;
                }
            }
            return false;
        }

        private static bool IsEligible(HardpointSet set)
        {
            if (set == null || set.hardpoints == null || set.hardpoints.Count == 0)
                return false;
            if (!s_BaysOnly.Value)
                return true;
            foreach (var hardpoint in set.hardpoints)
            {
                // Only a bay whose doors open for this weapon counts; a gun pod hung
                // outside a bay is an ordinary external store.
                if (hardpoint == null || !BayDoorRules.OpensDoors(hardpoint, set.weaponMount != null))
                    continue;
                if (hardpoint.bayDoors != null && hardpoint.bayDoors.Length > 0)
                    return true;
                var mount = HardpointMount.Root(hardpoint);
                if (mount == null)
                    continue;
                foreach (var missile in mount.GetComponentsInChildren<MountedMissile>(true))
                {
                    var doors = s_MissileDoors(missile);
                    if (doors != null && doors.Length > 0)
                        return true;
                }
            }
            return false;
        }

        private static bool WeaponRendererGone()
        {
            for (int i = 0; i < s_WeaponRenderers.Count; i++)
            {
                if (s_WeaponRenderers[i] == null)
                    return true;
            }
            return false;
        }

        // Only does work when the list, its weapon or the aircraft changes.
        private static void Measure(Aircraft aircraft, float side)
        {
            s_MeasuredAircraft = aircraft;
            var aircraftTransform = aircraft.transform;

            // The aircraft's box in its own space. AeroPart.CreateRB unparents every
            // part to the scene root, so walk the parts one by one.
            bool any = false;
            var box = new Bounds();
            AddRenderers(aircraftTransform, aircraftTransform.root, ref box, ref any);
            var parts = aircraft.partLookup;
            if (parts != null)
            {
                for (int i = 0; i < parts.Count; i++)
                {
                    if (parts[i] != null)
                        AddRenderers(aircraftTransform, parts[i].transform, ref box, ref any);
                }
            }
            if (!any)
                box = new Bounds(Vector3.zero, new Vector3(aircraft.definition.width, 3f, aircraft.definition.length));
            s_AircraftBox = box;

            // The weapon's mount renderers, or the hardpoints for an empty selection.
            // A pair is one set or two linked by TwinSet, so read the side per hardpoint.
            // Hardpoints all on the other side are framed anyway.
            if (side != 0f && !AnyOnSide(aircraftTransform, side))
                side = 0f;
            s_MeasuredSide = side != 0f && AnyOnSide(aircraftTransform, -side) ? side : 0f;
            s_WeaponRenderers.Clear();
            AddWeaponRenderers(aircraftTransform, LoadoutDropdown.Set, side);
            AddWeaponRenderers(aircraftTransform, LoadoutDropdown.TwinSet, side);
            any = false;
            var weapon = new Bounds();
            foreach (var renderer in s_WeaponRenderers)
            {
                if (any)
                    weapon.Encapsulate(renderer.bounds);
                else
                    weapon = renderer.bounds;
                any = true;
            }
            if (!any && TryGetHardpointCentre(aircraftTransform, side, out var centre))
            {
                weapon = new Bounds(centre, Vector3.zero);
                any = true;
            }
            s_TargetCentre = any ? weapon.center : aircraftTransform.position;
            s_TargetRadius = Mathf.Max(weapon.extents.magnitude, k_MinWeaponRadius);
            s_RendererCache.Clear();
        }

        // A part's own renderers. Child parts are walked separately, and overlap
        // is harmless for a box.
        private static void AddRenderers(Transform space, Transform part, ref Bounds box, ref bool any)
        {
            part.GetComponentsInChildren(false, s_RendererCache);
            foreach (var renderer in s_RendererCache)
            {
                if (IsModel(renderer) && !EquipmentFader.IsEquipment(renderer.transform, s_MeasuredAircraft))
                    EncapsulateLocal(space, renderer.bounds, ref box, ref any);
            }
        }

        // Unparented parts are no longer under the preview root, so a collider
        // counts as the aircraft when its part belongs to it.
        private static bool IsAircraft(Collider collider, Aircraft aircraft)
        {
            if (collider.transform.IsChildOf(aircraft.transform.root))
                return true;
            var part = collider.GetComponentInParent<UnitPart>();
            return part != null && part.parentUnit == aircraft;
        }

        private static bool IsModel(Renderer renderer)
        {
            return renderer != null && renderer.enabled && (renderer is MeshRenderer || renderer is SkinnedMeshRenderer);
        }

        private static void EncapsulateLocal(Transform space, Bounds world, ref Bounds box, ref bool any)
        {
            var min = world.min;
            var max = world.max;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3((i & 1) == 0 ? min.x : max.x, (i & 2) == 0 ? min.y : max.y, (i & 4) == 0 ? min.z : max.z);
                var local = space.InverseTransformPoint(corner);
                if (any)
                {
                    box.Encapsulate(local);
                }
                else
                {
                    box = new Bounds(local, Vector3.zero);
                    any = true;
                }
            }
        }

        // The side of the aircraft (-1 left, 1 right) a point is on, or 0 near
        // the centre line. Aircraft are built symmetric about their local x = 0.
        private static float SideOf(Transform aircraftTransform, Vector3 position)
        {
            float x = aircraftTransform.InverseTransformPoint(position).x;
            return Mathf.Abs(x) <= k_CentreHalfWidth ? 0f : Mathf.Sign(x);
        }

        private static float CameraSide(Aircraft aircraft, Vector3 cameraPosition)
        {
            return aircraft.transform.InverseTransformPoint(cameraPosition).x < 0f ? -1f : 1f;
        }

        // Centre line hardpoints belong to both sides.
        private static bool OnSide(Transform aircraftTransform, Hardpoint hardpoint, float side)
        {
            if (side == 0f)
                return true;
            float own = SideOf(aircraftTransform, hardpoint.transform.position);
            return own == 0f || own == side;
        }

        private static bool AnyOnSide(Transform aircraftTransform, float side)
        {
            return AnyOnSide(aircraftTransform, LoadoutDropdown.Set, side) || AnyOnSide(aircraftTransform, LoadoutDropdown.TwinSet, side);
        }

        private static bool AnyOnSide(Transform aircraftTransform, HardpointSet set, float side)
        {
            if (set == null || set.hardpoints == null)
                return false;
            foreach (var hardpoint in set.hardpoints)
            {
                if (hardpoint != null && hardpoint.transform != null && SideOf(aircraftTransform, hardpoint.transform.position) == side)
                    return true;
            }
            return false;
        }

        private static void AddWeaponRenderers(Transform aircraftTransform, HardpointSet set, float side)
        {
            if (set == null || set.hardpoints == null)
                return;
            foreach (var hardpoint in set.hardpoints)
            {
                if (hardpoint == null || hardpoint.transform == null || !OnSide(aircraftTransform, hardpoint, side))
                    continue;
                // Mount only: a hardpoint's own transform can be a whole aircraft part
                // (the tail hook's rear fuselage).
                var mount = HardpointMount.Root(hardpoint);
                if (mount == null)
                    continue;
                mount.GetComponentsInChildren(false, s_RendererCache);
                foreach (var renderer in s_RendererCache)
                {
                    string name = renderer != null ? renderer.gameObject.name : null;
                    if (name != null && name.StartsWith(k_ModObjectPrefix, StringComparison.Ordinal))
                        continue;
                    if (IsModel(renderer) && !s_WeaponRenderers.Contains(renderer))
                        s_WeaponRenderers.Add(renderer);
                }
            }
        }

        // The side the weapon is on. For a centre bay (or both sides with
        // symmetry) keep the side the camera is on, so it does not swing round.
        private static float PickSide(Aircraft aircraft, Vector3 cameraPosition)
        {
            var local = aircraft.transform.InverseTransformPoint(s_TargetCentre);
            if (Mathf.Abs(local.x - s_AircraftBox.center.x) > k_CentreHalfWidth)
                return Mathf.Sign(local.x - s_AircraftBox.center.x);
            float camX = aircraft.transform.InverseTransformPoint(cameraPosition).x;
            return camX < 0f ? -1f : 1f;
        }

        private static bool TryGetHardpointCentre(Transform aircraftTransform, float side, out Vector3 centre)
        {
            centre = Vector3.zero;
            int count = 0;
            AddPositions(aircraftTransform, LoadoutDropdown.Set, side, ref centre, ref count);
            AddPositions(aircraftTransform, LoadoutDropdown.TwinSet, side, ref centre, ref count);
            if (count == 0)
                return false;
            centre /= count;
            return true;
        }

        private static void AddPositions(Transform aircraftTransform, HardpointSet set, float side, ref Vector3 sum, ref int count)
        {
            if (set == null || set.hardpoints == null)
                return;
            var hardpoints = set.hardpoints;
            for (int i = 0; i < hardpoints.Count; i++)
            {
                var hardpoint = hardpoints[i];
                if (hardpoint != null && hardpoint.transform != null && OnSide(aircraftTransform, hardpoint, side))
                {
                    sum += hardpoint.transform.position;
                    count++;
                }
            }
        }

        private static bool TryFocusPose(Aircraft aircraft, float fieldOfView)
        {
            var aircraftTransform = aircraft.transform;
            float halfFov = Mathf.Max(fieldOfView, 10f) * 0.5f * Mathf.Deg2Rad;
            float fit = Mathf.Max(k_MinDistance, s_WeaponRadius / (Mathf.Tan(halfFov) * k_FitShare));

            // Cargo sits inside the fuselage behind the ramp, so view it from
            // straight behind, level with the load.
            if (s_IsCargo)
            {
                Pose(aircraft, aircraftTransform, Vector3.back, false, fit, out var rear, out _);
                s_FocusPosition = rear;
                s_FocusRotation = Quaternion.LookRotation(s_WeaponCentre - rear, Vector3.up);
                return true;
            }

            // Keep the weapon's side if the camera fits there; otherwise (hangar wall)
            // use the other side if it has more room, and stay on it.
            float room = Pose(aircraft, aircraftTransform, SideDirection(s_Side), IsBelowMiddle(aircraftTransform), fit, out var position, out var blocker);
            if (room < 0f && !s_SideSwitched)
            {
                // Once per list, so two cramped sides cannot flip it every frame.
                s_SideSwitched = true;
                float otherRoom = Pose(aircraft, aircraftTransform, SideDirection(-s_Side), IsBelowMiddle(aircraftTransform), fit, out var otherPosition, out _);
                if (otherRoom > room)
                {
                    Plugin.Log.LogInfo($"Menus.Loadout camera focus: no room on the weapon's side ('{blocker}' in the way), using the other side");
                    s_Side = -s_Side;
                    position = otherPosition;
                }
            }

            s_FocusPosition = position;
            s_FocusRotation = Quaternion.LookRotation(s_WeaponCentre - position, Vector3.up);
            return true;
        }

        // Slightly ahead of the beam on one side, looking back at the weapon.
        private static Vector3 SideDirection(float side)
        {
            float ahead = k_AheadDegrees * Mathf.Deg2Rad;
            return new Vector3(side * Mathf.Cos(ahead), 0f, Mathf.Sin(ahead));
        }

        // Weapons under the middle of the aircraft are viewed from low down,
        // looking up; weapons above it from their own height. Decided by the
        // measured weapon so it does not flip mid-glide.
        private static bool IsBelowMiddle(Transform aircraftTransform)
        {
            return aircraftTransform.InverseTransformPoint(s_TargetCentre).y < s_AircraftBox.center.y;
        }

        // Places the camera along a horizontal direction in aircraft space and
        // returns the room left between the aircraft's box and the nearest
        // obstacle there (negative: none).
        private static float Pose(Aircraft aircraft, Transform aircraftTransform, Vector3 directionLocal, bool fromBelow, float fit, out Vector3 position, out string blocker)
        {
            blocker = null;
            var target = s_WeaponCentre;
            var targetLocal = aircraftTransform.InverseTransformPoint(target);

            // Far enough to fit the weapon and outside the aircraft's box plus the
            // margin, measured in the aircraft's own space.
            float exit = ExitDistance(targetLocal, directionLocal, s_AircraftBox.min - Vector3.one * k_BoxMargin, s_AircraftBox.max + Vector3.one * k_BoxMargin);
            float distance = Mathf.Max(fit, exit);
            var direction = Vector3.ProjectOnPlane(aircraftTransform.TransformDirection(directionLocal), Vector3.up).normalized;
            position = target + direction * distance;

            // Both heights stay above the ground under the camera.
            float groundY = float.NegativeInfinity;
            int groundMask = PhysicsLayers.StaticsMask | PhysicsLayers.ShipsMask;
            var probe = new Vector3(position.x, target.y + k_GroundProbeUp, position.z);
            if (Physics.Raycast(probe, Vector3.down, out var ground, k_GroundProbeLength, groundMask, QueryTriggerInteraction.Ignore))
                groundY = ground.point.y + k_CameraHeight;
            if (fromBelow)
                position.y = groundY > float.NegativeInfinity ? groundY : target.y - 1f;
            else
                position.y = Mathf.Max(target.y + s_WeaponRadius * 0.5f, groundY);

            // Pull in front of anything that is not the aircraft (ground equipment,
            // a hangar wall), never into its box. The low camera's line grazes the
            // ground, so floor-like faces (normal mostly up) are not obstacles.
            var toCamera = position - target;
            float length = toCamera.magnitude;
            if (length < 0.01f)
                return 0f;
            int hits = Physics.RaycastNonAlloc(target, toCamera / length, s_Hits, length, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            float nearest = length;
            for (int i = 0; i < hits; i++)
            {
                var hit = s_Hits[i];
                if (hit.collider == null || IsAircraft(hit.collider, aircraft) || hit.normal.y > k_FloorNormalY)
                    continue;
                if (EquipmentFader.IsEquipment(hit.collider.transform, aircraft))
                    continue;
                if (hit.distance < nearest)
                {
                    nearest = hit.distance;
                    blocker = hit.collider.name;
                }
            }
            float minimum = Mathf.Min(exit, length);
            if (nearest >= length)
                return length - minimum;
            position = target + toCamera / length * Mathf.Max(nearest - k_ClearanceMargin, minimum);
            return nearest - k_ClearanceMargin - minimum;
        }

        // How far a ray from p along d (horizontal, aircraft space) runs before
        // it leaves the box sideways or lengthways; 0 if p is already outside.
        private static float ExitDistance(Vector3 p, Vector3 d, Vector3 min, Vector3 max)
        {
            float exit = float.MaxValue;
            if (Mathf.Abs(d.x) > 1e-4f)
                exit = Mathf.Min(exit, ((d.x > 0f ? max.x : min.x) - p.x) / d.x);
            if (Mathf.Abs(d.z) > 1e-4f)
                exit = Mathf.Min(exit, ((d.z > 0f ? max.z : min.z) - p.z) / d.z);
            return exit == float.MaxValue ? 0f : Mathf.Max(exit, 0f);
        }

        [HarmonyPatch(typeof(TMP_Dropdown), nameof(TMP_Dropdown.Show))]
        private static class ShowPatch
        {
            private static void Postfix(TMP_Dropdown __instance) => LoadoutDropdown.OnShow(__instance);
        }

        [HarmonyPatch(typeof(CameraSelectionState), nameof(CameraSelectionState.UpdateState))]
        private static class UpdateStatePatch
        {
            private static void Prefix(CameraSelectionState __instance, CameraStateManager cam) => BeforeUpdate(__instance, cam);

            private static void Postfix(CameraSelectionState __instance, CameraStateManager cam) => AfterUpdate(__instance, cam);
        }
    }
}
