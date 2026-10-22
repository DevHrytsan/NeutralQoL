using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using NeutralQoL.Core;
using NeutralQoL.Features.LoadoutCameraFocus;
using NeutralQoL.Features.LoadoutPreview;
using NeutralQoL.Shared.Hardpoints;
using NeutralQoL.Shared.LoadoutDropdown;
using NeutralQoL.Shared.Transparency;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace NeutralQoL.Features.LoadoutXRay
{
    internal sealed class LoadoutXRayFeature : Feature
    {
        public override string Section => "Menus";
        public override string Name => "Show weapon through aircraft";
        public override string Description =>
            "Fades in a see-through copy of the hardpoint's missiles, bombs or cargo on top of the aircraft, so weapons in bays or under the fuselage can be seen from any angle.";
        public override Type PartOf => typeof(LoadoutPreviewFeature);
        protected override bool DefaultOn => true;

        protected override Type[] PatchClasses => new[] { typeof(ShowPatch) };

        private const string k_CameraName = "NeutralQoL LoadoutXRay";
        private const string k_CopyName = "NeutralQoL XRayCopy";
        private const string k_DepthName = "NeutralQoL XRayDepth";
        private const int k_TransparentQueue = 3000;
        // Just big enough that the two surfaces do not cut through each other.
        private const float k_CopySize = 1.01f;
        // Drawn before every colour copy, so the whole ghost's depth is already
        // there.
        private const int k_DepthQueue = 2990;
        private const float k_FadeSeconds = 0.4f;
        private const float k_Opacity = 0.75f;

        private static AccessTools.FieldRef<UniversalAdditionalCameraData, bool> s_ClearDepth;

        private static readonly List<Renderer> s_RendererCache = new List<Renderer>();
        private static readonly List<LODGroup> s_LodGroupCache = new List<LODGroup>();
        private static readonly List<BayDoor> s_DoorCache = new List<BayDoor>();
        private static readonly HashSet<Renderer> s_LowerLods = new HashSet<Renderer>();
        private static readonly List<MeshRenderer> s_Munitions = new List<MeshRenderer>();
        private static readonly List<MeshRenderer> s_Others = new List<MeshRenderer>();
        private static readonly string[] s_PylonWords = { "pylon", "rack", "adapter", "rail" };
        private static readonly HashSet<string> s_LoggedMounts = new HashSet<string>();
        private static readonly List<GameObject> s_Copies = new List<GameObject>();
        // Every faded colour material, with its colour property and colour.
        private static readonly List<Material> s_Materials = new List<Material>();
        private static readonly List<int> s_ColorIds = new List<int>();
        private static readonly List<Color> s_BaseColors = new List<Color>();
        // Depth-only materials, never faded.
        private static readonly List<Material> s_DepthMaterials = new List<Material>();
        private static readonly HashSet<string> s_LoggedShaders = new HashSet<string>();

        private static int s_Layer = -1;
        private static Camera s_Overlay;
        private static Camera s_Base;
        private static bool s_BaseHadLayer;
        private static int s_Version = -1;
        private static float s_CopiedSide;
        private static float s_Alpha;
        private static float s_AppliedAlpha = -1f;
        private static bool s_LoggedNoBase;

        protected override void Resolve()
        {
            LoadoutDropdown.Resolve();
            HardpointMount.Resolve();
            // Read-only in this URP version, so set it before the overlay first
            // renders.
            s_ClearDepth = Targets.Field<UniversalAdditionalCameraData, bool>("m_ClearDepth");
            s_Layer = FindFreeLayer();
        }

        protected override void OnDisable()
        {
            DestroyCopies();
            RemoveOverlay();
            s_Version = -1;
            s_Alpha = 0f;
        }

        public override void Tick()
        {
            LoadoutDropdown.Update();
            bool wanted = s_Layer >= 0
                && LoadoutDropdown.Aircraft != null
                && CameraStateManager.cameraMode == CameraMode.selection
                && (LoadoutDropdown.IsActive || Time.unscaledTime - LoadoutDropdown.ClosedAt < LoadoutPreviewFeature.KeepSeconds);

            float step = Time.unscaledDeltaTime / k_FadeSeconds;
            s_Alpha = Mathf.MoveTowards(s_Alpha, wanted ? 1f : 0f, step);
            if (!wanted && s_Alpha <= 0f)
            {
                if (s_Copies.Count > 0)
                    DestroyCopies();
                if (s_Overlay != null && s_Overlay.enabled)
                    s_Overlay.enabled = false;
                s_Version = -1;
                return;
            }
            if (!EnsureOverlay())
                return;
            // Mounts respawn on a weapon change and take their copies along, so copy
            // again and keep the fade. The camera picks its side later in LateUpdate.
            float framedSide = LoadoutCameraFocusFeature.FramedSide;
            if (wanted && (LoadoutDropdown.Version != s_Version || CopyGone() || framedSide != s_CopiedSide))
            {
                s_Version = LoadoutDropdown.Version;
                s_CopiedSide = framedSide;
                DestroyCopies();
                CopySet(LoadoutDropdown.Set);
                CopySet(LoadoutDropdown.TwinSet);
            }
            ApplyAlpha(Mathf.SmoothStep(0f, 1f, s_Alpha) * k_Opacity);

            if (!s_Overlay.enabled)
                s_Overlay.enabled = true;
            // Same lens as the main camera. The overlay is its child, so
            // position and rotation already follow.
            if (s_Overlay.fieldOfView != s_Base.fieldOfView)
                s_Overlay.fieldOfView = s_Base.fieldOfView;
            if (s_Overlay.nearClipPlane != s_Base.nearClipPlane)
                s_Overlay.nearClipPlane = s_Base.nearClipPlane;
        }

        // A layer nobody named is one the game does not use. PhysicsLayers
        // names 0 to 18 (GrassBlockerProxy), so search above those.
        private static int FindFreeLayer()
        {
            for (int i = 31; i > 18; i--)
            {
                if (string.IsNullOrEmpty(LayerMask.LayerToName(i)))
                    return i;
            }
            return -1;
        }

        private static bool EnsureOverlay()
        {
            var manager = SceneSingleton<CameraStateManager>.i;
            var main = manager != null ? manager.mainCamera : null;
            if (main == null)
                return false;
            if (s_Overlay != null && s_Base == main)
                return true;

            RemoveOverlay();
            var baseData = main.GetUniversalAdditionalCameraData();
            if (baseData == null || baseData.renderType != CameraRenderType.Base)
            {
                if (!s_LoggedNoBase)
                {
                    s_LoggedNoBase = true;
                    Plugin.Log.LogInfo("Menus.Show selected weapon through aircraft: main camera is not a URP base camera, not shown");
                }
                return false;
            }

            var go = new GameObject(k_CameraName);
            go.transform.SetParent(main.transform, false);
            var overlay = go.AddComponent<Camera>();
            overlay.CopyFrom(main);
            overlay.cullingMask = 1 << s_Layer;
            overlay.clearFlags = CameraClearFlags.Nothing;
            overlay.depth = main.depth + 1f;
            var data = overlay.GetUniversalAdditionalCameraData();
            data.renderType = CameraRenderType.Overlay;
            s_ClearDepth(data) = true;
            data.renderShadows = false;
            // Base camera tonemapping runs at the end of the stack, so the
            // overlay gets it too and matches the scene.
            data.renderPostProcessing = baseData.renderPostProcessing;
            baseData.cameraStack.Add(overlay);
            // The main camera would draw the copies inside the weapon's own
            // surface; only the overlay draws that layer.
            s_BaseHadLayer = (main.cullingMask & (1 << s_Layer)) != 0;
            main.cullingMask &= ~(1 << s_Layer);
            s_Overlay = overlay;
            s_Base = main;
            return true;
        }

        private static void RemoveOverlay()
        {
            if (s_Base != null && s_Overlay != null)
            {
                var baseData = s_Base.GetUniversalAdditionalCameraData();
                if (baseData != null)
                    baseData.cameraStack.Remove(s_Overlay);
            }
            if (s_Base != null && s_BaseHadLayer)
                s_Base.cullingMask |= 1 << s_Layer;
            s_BaseHadLayer = false;
            if (s_Overlay != null)
                UnityEngine.Object.Destroy(s_Overlay.gameObject);
            s_Overlay = null;
            s_Base = null;
        }

        // Runs when a list opens or its weapon changes, not per frame.
        private static void CopySet(HardpointSet set)
        {
            if (set == null || set.hardpoints == null)
                return;
            s_Munitions.Clear();
            s_Others.Clear();
            bool storage = false;
            foreach (var hardpoint in set.hardpoints)
            {
                // With One side only, the other wing's weapon gets no ghost.
                if (!LoadoutCameraFocusFeature.IsFramed(hardpoint))
                    continue;
                var mount = HardpointMount.Root(hardpoint);
                if (mount == null)
                    continue;
                bool ownDoor = HasOwnDoor(mount);
                storage |= ownDoor;
                CollectLowerLods(mount);
                mount.GetComponentsInChildren(false, s_RendererCache);
                foreach (var renderer in s_RendererCache)
                {
                    // Copies made a moment ago are only destroyed at the end of
                    // the frame, so they still show up here.
                    string name = renderer.gameObject.name;
                    if (name == k_CopyName || name == k_DepthName)
                        continue;
                    // Pylon variants are hidden by disabling their renderer
                    // (HardpointPylon.ShowPylon), not the object, so copy only
                    // what is drawn now.
                    if (!(renderer is MeshRenderer meshRenderer) || !renderer.enabled || s_LowerLods.Contains(renderer))
                        continue;
                    // Storage inside the aircraft keeps its rack and door: they
                    // show where the load sits. Only the aircraft's pylon goes.
                    if (ownDoor ? renderer == hardpoint.Pylon : IsPylon(renderer, hardpoint))
                        continue;
                    if (IsMunition(renderer))
                        s_Munitions.Add(meshRenderer);
                    else
                        s_Others.Add(meshRenderer);
                }
            }
            // Pods, racks and rails are left out when the mount has munitions to show.
            // A mount without any (gun pod, jammer) or stored behind its own door
            // (UH-90 AGM storage) is shown whole.
            bool munitionsOnly = s_Munitions.Count > 0 && !storage;
            LogSplit(set, munitionsOnly, storage);
            foreach (var renderer in s_Munitions)
                CopyMesh(renderer);
            if (!munitionsOnly)
            {
                foreach (var renderer in s_Others)
                    CopyMesh(renderer);
            }
            s_Munitions.Clear();
            s_Others.Clear();
            s_RendererCache.Clear();
            s_LowerLods.Clear();
            s_AppliedAlpha = -1f;
        }

        // Once per mount, so a mount whose parts land on the wrong side of the
        // split can be read from the log.
        private static void LogSplit(HardpointSet set, bool munitionsOnly, bool storage)
        {
            string mount = set.weaponMount != null ? set.weaponMount.mountName : "empty";
            if (!s_LoggedMounts.Add(mount))
                return;
            var shown = new List<string>();
            var hidden = new List<string>();
            foreach (var renderer in s_Munitions)
                shown.Add(renderer.gameObject.name);
            foreach (var renderer in s_Others)
                (munitionsOnly ? hidden : shown).Add(renderer.gameObject.name);
            Plugin.Log.LogInfo($"Menus.Show selected weapon through aircraft: '{mount}' {(storage ? "(internal storage, whole mount) " : "")}shows [{string.Join(", ", shown)}], leaves out [{string.Join(", ", hidden)}]");
        }

        // A mount with its own door (MountedMissile.bayDoors) sits inside the aircraft.
        // Doors on the aircraft (Hardpoint.bayDoors) are not under the mount.
        // A plain mesh door (no BayDoor) counts too, by name.
        private static bool HasOwnDoor(Transform mount)
        {
            mount.GetComponentsInChildren(true, s_DoorCache);
            bool any = s_DoorCache.Count > 0;
            s_DoorCache.Clear();
            if (any)
                return true;
            mount.GetComponentsInChildren(true, s_RendererCache);
            foreach (var renderer in s_RendererCache)
            {
                if (renderer != null && renderer.gameObject.name.IndexOf("door", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    any = true;
                    break;
                }
            }
            s_RendererCache.Clear();
            return any;
        }

        // A munition's meshes sit under its own Weapon; the pod or rack body above
        // them has none.
        private static bool IsMunition(Renderer renderer)
        {
            var weapon = renderer.GetComponentInParent<Weapon>();
            return weapon is MountedMissile || weapon is MountedCargo || weapon is MountedTroops;
        }

        // The aircraft's pylon and mount parts named pylon, rack, adapter or rail.
        // They hold the munition, they are not it.
        private static bool IsPylon(Renderer renderer, Hardpoint hardpoint)
        {
            if (renderer == hardpoint.Pylon)
                return true;
            // Compare name parts, not substrings: "tracks" contains "rack".
            string name = renderer.gameObject.name;
            int start = 0;
            while (start < name.Length)
            {
                while (start < name.Length && !char.IsLetter(name[start]))
                    start++;
                int end = start;
                while (end < name.Length && char.IsLetter(name[end]))
                    end++;
                if (end > start && PartIsPylon(name, start, end - start))
                    return true;
                start = end;
            }
            return false;
        }

        private static bool PartIsPylon(string name, int start, int length)
        {
            foreach (var word in s_PylonWords)
            {
                if (length >= word.Length && string.Compare(name, start, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) == 0)
                    return true;
            }
            return false;
        }

        // A LODGroup keeps every level enabled; copy only the most detailed or the
        // levels would stack.
        private static void CollectLowerLods(Transform root)
        {
            s_LowerLods.Clear();
            root.GetComponentsInChildren(false, s_LodGroupCache);
            foreach (var group in s_LodGroupCache)
            {
                var lods = group.GetLODs();
                for (int level = 1; level < lods.Length; level++)
                {
                    foreach (var renderer in lods[level].renderers)
                    {
                        if (renderer != null)
                            s_LowerLods.Add(renderer);
                    }
                }
                // A renderer in the most detailed level stays, even if a lower
                // level shares it.
                if (lods.Length > 0)
                {
                    foreach (var renderer in lods[0].renderers)
                    {
                        if (renderer != null)
                            s_LowerLods.Remove(renderer);
                    }
                }
            }
            s_LodGroupCache.Clear();
        }

        private static void CopyMesh(MeshRenderer source)
        {
            string name = source.gameObject.name;
            if (name == k_CopyName || name == k_DepthName)
                return;
            var filter = source.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null)
                return;

            var sources = source.sharedMaterials;
            var colours = new Material[sources.Length];
            var depths = new Material[sources.Length];
            for (int i = 0; i < sources.Length; i++)
            {
                colours[i] = MakeTransparent(sources[i], false);
                depths[i] = MakeTransparent(sources[i], true);
            }
            AddCopy(source, filter.sharedMesh, k_DepthName, depths);
            AddCopy(source, filter.sharedMesh, k_CopyName, colours);
        }

        private static void AddCopy(MeshRenderer source, Mesh mesh, string name, Material[] materials)
        {
            var go = new GameObject(name);
            go.layer = s_Layer;
            go.transform.SetParent(source.transform, false);
            // Grown about the mesh's own centre, so it wraps the weapon and
            // stays centred instead of cutting through it.
            go.transform.localScale = Vector3.one * k_CopySize;
            go.transform.localPosition = mesh.bounds.center * (1f - k_CopySize);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var copy = go.AddComponent<MeshRenderer>();
            copy.shadowCastingMode = ShadowCastingMode.Off;
            copy.receiveShadows = false;
            copy.sharedMaterials = materials;
            s_Copies.Add(go);
        }

        // The depth twin is the colour copy's material at alpha 0 with depth
        // writes on: it blends to nothing but fills the depth the colour copy
        // tests against.
        private static Material MakeTransparent(Material source, bool depthOnly)
        {
            var material = TransparentMaterial.Make(source, depthOnly ? k_DepthName : k_CopyName, depthOnly, depthOnly ? k_DepthQueue : k_TransparentQueue);
            if (material == null)
                return source;
            if (!depthOnly && source != null && s_LoggedShaders.Add(source.shader.name))
                Plugin.Log.LogInfo($"Menus.Show selected weapon through aircraft: weapon shader '{source.shader.name}' -> '{material.shader.name}'");

            int colorId = TransparentMaterial.ColorId(material);
            var colour = TransparentMaterial.BaseColor(material, colorId);
            if (depthOnly)
            {
                TransparentMaterial.SetAlpha(material, colorId, colour, 0f);
                s_DepthMaterials.Add(material);
            }
            else
            {
                s_Materials.Add(material);
                s_ColorIds.Add(colorId);
                s_BaseColors.Add(colour);
            }
            return material;
        }

        private static void ApplyAlpha(float alpha)
        {
            if (Mathf.Abs(alpha - s_AppliedAlpha) < 0.002f)
                return;
            s_AppliedAlpha = alpha;
            for (int i = 0; i < s_Materials.Count; i++)
            {
                if (s_Materials[i] != null)
                    TransparentMaterial.SetAlpha(s_Materials[i], s_ColorIds[i], s_BaseColors[i], alpha);
            }
        }

        private static bool CopyGone()
        {
            for (int i = 0; i < s_Copies.Count; i++)
            {
                if (s_Copies[i] == null)
                    return true;
            }
            return false;
        }

        private static void DestroyCopies()
        {
            foreach (var copy in s_Copies)
            {
                if (copy != null)
                    UnityEngine.Object.Destroy(copy);
            }
            foreach (var material in s_Materials)
            {
                if (material != null)
                    UnityEngine.Object.Destroy(material);
            }
            foreach (var material in s_DepthMaterials)
            {
                if (material != null)
                    UnityEngine.Object.Destroy(material);
            }
            s_Copies.Clear();
            s_Materials.Clear();
            s_ColorIds.Clear();
            s_BaseColors.Clear();
            s_DepthMaterials.Clear();
            s_AppliedAlpha = -1f;
        }

        [HarmonyPatch(typeof(TMP_Dropdown), nameof(TMP_Dropdown.Show))]
        private static class ShowPatch
        {
            private static void Postfix(TMP_Dropdown __instance) => LoadoutDropdown.OnShow(__instance);
        }
    }
}
