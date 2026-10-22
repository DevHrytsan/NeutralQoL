using System.Collections.Generic;
using HarmonyLib;
using NeutralQoL.Core;
using NeutralQoL.Shared.Transparency;
using UnityEngine;

namespace NeutralQoL.Features.LoadoutCameraFocus
{
    internal static class EquipmentFader
    {
        private const string k_MaterialName = "NeutralQoL CartFade";
        private const int k_TransparentQueue = 3000;
        // Carts whose box gets this close to the camera fade.
        private const float k_NearDistance = 2.5f;

        private sealed class Cart
        {
            public Renderer[] Renderers;
            public Material[][] Originals;
            public readonly List<Material> Copies = new List<Material>();
            public readonly List<int> ColorIds = new List<int>();
            public readonly List<Color> BaseColors = new List<Color>();
            public Bounds Bounds;
            public float Alpha = 1f;
            public bool Swapped;
            public bool Hidden;
        }

        private static AccessTools.FieldRef<AircraftSelectionMenu, GameObject> s_Equipment;
        private static AccessTools.FieldRef<Aircraft, GameObject[]> s_AircraftEquipment;

        private static readonly List<Cart> s_Carts = new List<Cart>();
        private static readonly List<Renderer> s_RendererCache = new List<Renderer>();
        private static GameObject s_Root;
        private static Aircraft s_Aircraft;

        public static void Resolve()
        {
            s_Equipment = Targets.Field<AircraftSelectionMenu, GameObject>("groundSupportEquipment");
            s_AircraftEquipment = Targets.Field<Aircraft, GameObject[]>("groundEquipment");
        }

        // Runs every frame; active = focus is wanted.
        public static void Update(WeaponSelector selector, Aircraft aircraft, Vector3 camera, Vector3 target, bool active, float fadeSeconds)
        {
            if (active)
                Find(selector, aircraft);
            if (s_Carts.Count == 0)
                return;

            var toTarget = target - camera;
            float length = toTarget.magnitude;
            var ray = new Ray(camera, length > 0.01f ? toTarget / length : Vector3.forward);
            float step = Time.unscaledDeltaTime / Mathf.Max(fadeSeconds, 0.01f);
            for (int i = s_Carts.Count - 1; i >= 0; i--)
            {
                var cart = s_Carts[i];
                bool fade = active && InTheWay(cart.Bounds, camera, ray, length);
                float alpha = Mathf.MoveTowards(cart.Alpha, fade ? 0f : 1f, step);
                if (alpha == cart.Alpha)
                    continue;
                cart.Alpha = alpha;
                if (alpha >= 1f)
                {
                    Restore(cart);
                    continue;
                }
                if (!cart.Swapped)
                    Swap(cart);
                // Fully faded carts are disabled: even at alpha 0 their depth
                // writes would hide what is behind them, and they cast shadows.
                SetVisible(cart, alpha > 0f);
                for (int m = 0; m < cart.Copies.Count; m++)
                {
                    if (cart.Copies[m] != null)
                        TransparentMaterial.SetAlpha(cart.Copies[m], cart.ColorIds[m], cart.BaseColors[m], alpha);
                }
            }
        }

        // Carts are no obstacle for the camera and don't count toward the
        // aircraft's size. Reads the aircraft's list directly so it works
        // before the first fade has found the carts.
        public static bool IsEquipment(Transform transform, Aircraft aircraft)
        {
            if (transform == null)
                return false;
            if (s_Root != null && transform.IsChildOf(s_Root.transform))
                return true;
            var equipment = aircraft != null ? s_AircraftEquipment(aircraft) : null;
            if (equipment == null)
                return false;
            for (int i = 0; i < equipment.Length; i++)
            {
                if (equipment[i] != null && transform.IsChildOf(equipment[i].transform))
                    return true;
            }
            return false;
        }

        public static void Reset()
        {
            foreach (var cart in s_Carts)
                Restore(cart);
            s_Carts.Clear();
            s_Root = null;
            s_Aircraft = null;
        }

        private static bool InTheWay(Bounds bounds, Vector3 camera, Ray ray, float length)
        {
            if (bounds.SqrDistance(camera) < k_NearDistance * k_NearDistance)
                return true;
            return bounds.IntersectRay(ray, out float distance) && distance < length;
        }

        // Only does work when the menu's equipment or the preview changes.
        private static void Find(WeaponSelector selector, Aircraft aircraft)
        {
            if (selector == null)
                return;
            var menu = selector.GetComponentInParent<AircraftSelectionMenu>();
            var root = menu != null ? s_Equipment(menu) : null;
            if (root == s_Root && aircraft == s_Aircraft)
                return;
            Reset();
            s_Root = root;
            s_Aircraft = aircraft;
            if (root != null)
            {
                var rootTransform = root.transform;
                for (int i = 0; i < rootTransform.childCount; i++)
                    AddCart(rootTransform.GetChild(i));
                if (s_Carts.Count == 0)
                    AddCart(rootTransform);
            }
            var equipment = aircraft != null ? s_AircraftEquipment(aircraft) : null;
            if (equipment == null)
                return;
            foreach (var item in equipment)
            {
                if (item != null && item.activeInHierarchy)
                    AddCart(item.transform);
            }
        }

        private static void AddCart(Transform part)
        {
            part.GetComponentsInChildren(false, s_RendererCache);
            var renderers = new List<Renderer>();
            bool any = false;
            var bounds = new Bounds();
            foreach (var renderer in s_RendererCache)
            {
                if (renderer == null || !renderer.enabled || !(renderer is MeshRenderer || renderer is SkinnedMeshRenderer))
                    continue;
                renderers.Add(renderer);
                if (any)
                {
                    bounds.Encapsulate(renderer.bounds);
                }
                else
                {
                    bounds = renderer.bounds;
                    any = true;
                }
            }
            s_RendererCache.Clear();
            if (!any)
                return;
            var cart = new Cart { Renderers = renderers.ToArray(), Bounds = bounds };
            cart.Originals = new Material[cart.Renderers.Length][];
            s_Carts.Add(cart);
        }

        private static void Swap(Cart cart)
        {
            for (int r = 0; r < cart.Renderers.Length; r++)
            {
                var renderer = cart.Renderers[r];
                if (renderer == null)
                    continue;
                var originals = renderer.sharedMaterials;
                cart.Originals[r] = originals;
                var copies = new Material[originals.Length];
                for (int m = 0; m < originals.Length; m++)
                {
                    // Depth writes stay on so the cart hides its own far side.
                    var copy = TransparentMaterial.Make(originals[m], k_MaterialName, true, k_TransparentQueue);
                    if (copy == null)
                    {
                        copies[m] = originals[m];
                        continue;
                    }
                    int colorId = TransparentMaterial.ColorId(copy);
                    cart.Copies.Add(copy);
                    cart.ColorIds.Add(colorId);
                    cart.BaseColors.Add(TransparentMaterial.BaseColor(copy, colorId));
                    copies[m] = copy;
                }
                renderer.sharedMaterials = copies;
            }
            cart.Swapped = true;
        }

        private static void SetVisible(Cart cart, bool visible)
        {
            if (cart.Hidden == !visible)
                return;
            cart.Hidden = !visible;
            foreach (var renderer in cart.Renderers)
            {
                if (renderer != null)
                    renderer.enabled = visible;
            }
        }

        private static void Restore(Cart cart)
        {
            SetVisible(cart, true);
            if (cart.Swapped)
            {
                for (int r = 0; r < cart.Renderers.Length; r++)
                {
                    if (cart.Renderers[r] != null && cart.Originals[r] != null)
                        cart.Renderers[r].sharedMaterials = cart.Originals[r];
                    cart.Originals[r] = null;
                }
            }
            foreach (var copy in cart.Copies)
            {
                if (copy != null)
                    Object.Destroy(copy);
            }
            cart.Copies.Clear();
            cart.ColorIds.Clear();
            cart.BaseColors.Clear();
            cart.Swapped = false;
            cart.Alpha = 1f;
        }
    }
}
