using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace NeutralQoL.Features.HudContrast
{
    internal sealed class HudOutline : BaseMeshEffect, IMaterialModifier
    {
        private enum Verdict { Pending, Safe, Backdrop }

        private sealed class Copy
        {
            public Material Material;
            public Shader Original;
            public Shader Blended;
            public bool Text;
            // The font material's own outline, put back when ours goes.
            public float BaseOutlineWidth;
            public Color BaseOutlineColor;
        }

        private static readonly int k_OutlineWidth = Shader.PropertyToID("_OutlineWidth");
        private static readonly int k_OutlineColor = Shader.PropertyToID("_OutlineColor");
        private static readonly Color k_Dark = new Color(0f, 0f, 0f, 1f);
        // TMP outline width (distance field units) per pixel of image outline;
        // 1.5 px gives about 0.2, a clear edge without filling small glyphs.
        private const float k_TextWidthPerPixel = 0.13f;
        private const float k_MaxTextWidth = 0.4f;
        // A texture is a backdrop when this share of it is dark and opaque:
        // invisible when added, a black box when blended.
        private const float k_BackdropShare = 0.15f;
        private const int k_ProbeSize = 16;

        private static readonly Vector2[] s_Directions =
        {
            new Vector2(1f, 0f), new Vector2(-1f, 0f), new Vector2(0f, 1f), new Vector2(0f, -1f),
            new Vector2(0.7071f, 0.7071f), new Vector2(-0.7071f, 0.7071f), new Vector2(0.7071f, -0.7071f), new Vector2(-0.7071f, -0.7071f),
        };

        private static readonly Dictionary<Material, Copy> s_Copies = new Dictionary<Material, Copy>();
        private static readonly List<Material> s_Dead = new List<Material>();
        private static readonly HashSet<string> s_LoggedShaders = new HashSet<string>();
        private static readonly List<UIVertex> s_Source = new List<UIVertex>();
        private static readonly List<UIVertex> s_Output = new List<UIVertex>();
        private static Shader[] s_LoadedShaders;
        private static float s_Strength;
        private static float s_WidthPixels = 1.5f;

        // Texture checks, one GPU readback at a time.
        private static readonly Dictionary<UnityEngine.Object, Verdict> s_Verdicts = new Dictionary<UnityEngine.Object, Verdict>();
        private static readonly Queue<UnityEngine.Object> s_ProbeQueue = new Queue<UnityEngine.Object>();
        private static RenderTexture s_Probe;
        private static UnityEngine.Object s_Probing;
        private static Action<AsyncGPUReadbackRequest> s_OnProbe;
        // Set when a verdict arrives, so the feature refreshes graphics that
        // were waiting for it.
        public static bool VerdictsChanged;

        private Verdict m_Verdict = Verdict.Pending;

        public static float Strength => s_Strength;

        protected override void OnEnable()
        {
            base.OnEnable();
            if (graphic != null)
                graphic.SetMaterialDirty();
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            if (graphic != null)
                graphic.SetMaterialDirty();
        }

        // After a strength change (the image outline is in the mesh) or a
        // texture verdict this graphic may have been waiting for.
        public void Refresh()
        {
            if (graphic == null)
                return;
            if (m_Verdict == Verdict.Pending)
                graphic.SetMaterialDirty();
            if (!(graphic is TMP_Text))
                graphic.SetVerticesDirty();
        }

        // Whether this graphic can switch to a blended shader and get an
        // outline without drawing a box.
        private Verdict Check()
        {
            if (m_Verdict != Verdict.Pending)
                return m_Verdict;
            var g = graphic;
            if (g is TMP_Text)
                return m_Verdict = Verdict.Safe;
            UnityEngine.Object key = null;
            if (g is Image image)
            {
                if (image.sprite == null)
                {
                    // A plain rectangle in its own colour: a dark one is a
                    // backdrop that additive drawing hides.
                    var c = image.color;
                    return m_Verdict = c.a > 0.5f && c.r * 0.2126f + c.g * 0.7152f + c.b * 0.0722f < 0.15f ? Verdict.Backdrop : Verdict.Safe;
                }
                key = image.sprite;
            }
            else
            {
                var texture = g.mainTexture;
                // Live pictures (cameras, the status display) may be black
                // anywhere at any time.
                if (texture is RenderTexture)
                    return m_Verdict = Verdict.Backdrop;
                if (texture is Texture2D t2 && t2.format == TextureFormat.Alpha8)
                    return m_Verdict = Verdict.Safe;
                key = texture;
            }
            if (key == null)
                return m_Verdict = Verdict.Safe;
            if (key is Sprite sprite && sprite.texture != null && sprite.texture.format == TextureFormat.Alpha8)
                return m_Verdict = Verdict.Safe;
            if (s_Verdicts.TryGetValue(key, out var verdict))
            {
                if (verdict != Verdict.Pending)
                    m_Verdict = verdict;
                return verdict;
            }
            s_Verdicts[key] = Verdict.Pending;
            s_ProbeQueue.Enqueue(key);
            NextProbe();
            return Verdict.Pending;
        }

        public Material GetModifiedMaterial(Material baseMaterial)
        {
            if (!enabled || baseMaterial == null || Check() != Verdict.Safe)
                return baseMaterial;
            if (!s_Copies.TryGetValue(baseMaterial, out var copy))
            {
                copy = Create(baseMaterial, graphic is TMP_Text);
                s_Copies[baseMaterial] = copy;
            }
            return copy != null ? copy.Material : baseMaterial;
        }

        public override void ModifyMesh(VertexHelper vh)
        {
            if (!IsActive() || s_Strength <= 0f || graphic is TMP_Text || m_Verdict != Verdict.Safe)
                return;
            var canvas = graphic.canvas;
            if (canvas == null)
                return;
            // Pixels to this graphic's local units.
            var root = canvas.rootCanvas;
            float scale = root.scaleFactor * (transform.lossyScale.x / Mathf.Max(1e-5f, root.transform.lossyScale.x));
            float width = s_WidthPixels / Mathf.Max(1e-5f, scale);

            vh.GetUIVertexStream(s_Source);
            int count = s_Source.Count;
            s_Output.Clear();
            for (int d = 0; d < s_Directions.Length; d++)
            {
                var offset = (Vector3)(s_Directions[d] * width);
                for (int i = 0; i < count; i++)
                {
                    var v = s_Source[i];
                    v.position += offset;
                    var c = v.color;
                    v.color = new Color32(0, 0, 0, (byte)(c.a * s_Strength));
                    s_Output.Add(v);
                }
            }
            s_Output.AddRange(s_Source);
            vh.Clear();
            vh.AddUIVertexTriangleStream(s_Output);
            s_Source.Clear();
            s_Output.Clear();
        }

        private static bool IsAdditive(string shaderName) => shaderName.IndexOf("Additive", StringComparison.Ordinal) >= 0;

        private static Copy Create(Material source, bool text)
        {
            var shader = source.shader;
            var shaderName = shader != null ? shader.name : "none";
            Shader blended;
            if (text)
                blended = shaderName.StartsWith("TextMeshPro/", StringComparison.Ordinal)
                    // The full distance field shader draws its outline without
                    // a keyword; the Mobile one needs a variant that may be
                    // stripped, so it comes last.
                    ? FindShader("TextMeshPro/Distance Field") ?? FindShader("TextMeshPro/Distance Field Overlay") ?? FindShader(shaderName.Replace(" Additive", ""))
                    : null;
            else
                blended = IsAdditive(shaderName) ? FindShader("UI/Default") : shader;
            bool usable = blended != null && (!text || blended.name.StartsWith("TextMeshPro/", StringComparison.Ordinal));
            if (s_LoggedShaders.Add(shaderName + (text ? " (text)" : "")))
                Plugin.Log.LogInfo(usable
                    ? $"HUD.Contrast with the view: {(text ? "text" : "images")} with shader {shaderName} outlined with {blended.name}"
                    : $"HUD.Contrast with the view: {(text ? "text" : "images")} with shader {shaderName} have no blended stand-in, left as is");
            if (!usable)
                return null;
            var copy = new Copy
            {
                Material = new Material(source) { name = source.name + " (NeutralQoL outline)", hideFlags = HideFlags.HideAndDontSave },
                Original = shader,
                Blended = blended,
                Text = text,
            };
            if (text && source.HasProperty(k_OutlineWidth))
            {
                copy.BaseOutlineWidth = source.GetFloat(k_OutlineWidth);
                copy.BaseOutlineColor = source.GetColor(k_OutlineColor);
            }
            Apply(copy);
            return copy;
        }

        private static Shader FindShader(string name)
        {
            var shader = Shader.Find(name);
            if (shader != null)
                return shader;
            // Shader.Find misses shaders that are loaded but not always
            // included.
            if (s_LoadedShaders == null)
                s_LoadedShaders = Resources.FindObjectsOfTypeAll<Shader>();
            for (int i = 0; i < s_LoadedShaders.Length; i++)
                if (s_LoadedShaders[i] != null && s_LoadedShaders[i].name == name)
                    return s_LoadedShaders[i];
            return null;
        }

        private static void Apply(Copy copy)
        {
            // The vanilla look (additive) whenever no outline shows.
            var shader = s_Strength > 0f ? copy.Blended : copy.Original;
            if (copy.Material.shader != shader)
                copy.Material.shader = shader;
            if (copy.Text && copy.Material.HasProperty(k_OutlineWidth))
            {
                // The distance field outline straddles the glyph edge, so it
                // also thins the face; the vanilla look needs the font's own
                // width back.
                bool on = s_Strength > 0f;
                copy.Material.SetColor(k_OutlineColor, on ? new Color(k_Dark.r, k_Dark.g, k_Dark.b, s_Strength) : copy.BaseOutlineColor);
                copy.Material.SetFloat(k_OutlineWidth, on ? Mathf.Min(k_MaxTextWidth, s_WidthPixels * k_TextWidthPerPixel) : copy.BaseOutlineWidth);
                ShaderUtilities.UpdateShaderRatios(copy.Material);
            }
        }

        // Returns true when image meshes must be rebuilt (their outline is in
        // the mesh); text and shaders update through the material copies.
        public static bool SetStrength(float strength, float widthPixels)
        {
            if (strength == s_Strength && widthPixels == s_WidthPixels)
                return false;
            s_Strength = strength;
            s_WidthPixels = widthPixels;
            foreach (var copy in s_Copies.Values)
                if (copy != null && copy.Material != null)
                    Apply(copy);
            return true;
        }

        private static void NextProbe()
        {
            if (s_Probing != null)
                return;
            while (s_ProbeQueue.Count > 0)
            {
                var key = s_ProbeQueue.Dequeue();
                Texture texture;
                var scale = Vector2.one;
                var offset = Vector2.zero;
                if (key is Sprite sprite)
                {
                    texture = sprite.texture;
                    if (texture != null)
                    {
                        // Only the sprite's own part of an atlas.
                        var r = sprite.textureRect;
                        scale = new Vector2(r.width / texture.width, r.height / texture.height);
                        offset = new Vector2(r.x / texture.width, r.y / texture.height);
                    }
                }
                else
                {
                    texture = key as Texture;
                }
                if (texture == null)
                {
                    s_Verdicts[key] = Verdict.Safe;
                    VerdictsChanged = true;
                    continue;
                }
                if (s_Probe == null)
                {
                    s_Probe = new RenderTexture(k_ProbeSize, k_ProbeSize, 0, RenderTextureFormat.ARGB32) { name = "NeutralQoL.HudOutlineProbe", hideFlags = HideFlags.HideAndDontSave };
                    s_Probe.Create();
                    s_OnProbe = OnProbe;
                }
                Graphics.Blit(texture, s_Probe, scale, offset);
                s_Probing = key;
                AsyncGPUReadback.Request(s_Probe, 0, TextureFormat.RGBA32, s_OnProbe);
                return;
            }
        }

        private static void OnProbe(AsyncGPUReadbackRequest request)
        {
            var key = s_Probing;
            s_Probing = null;
            if (key == null)
                return;
            var verdict = Verdict.Backdrop;
            if (!request.hasError)
            {
                var pixels = request.GetData<Color32>();
                int dark = 0;
                for (int i = 0; i < pixels.Length; i++)
                {
                    var p = pixels[i];
                    if (p.a > 128 && 0.2126f * p.r + 0.7152f * p.g + 0.0722f * p.b < 26f)
                        dark++;
                }
                if (dark <= pixels.Length * k_BackdropShare)
                    verdict = Verdict.Safe;
            }
            s_Verdicts[key] = verdict;
            VerdictsChanged = true;
            if (verdict == Verdict.Backdrop)
                Plugin.Log.LogInfo($"HUD.Contrast with the view: '{key.name}' is drawn on black, left without outline");
            NextProbe();
        }

        public static void Prune()
        {
            foreach (var pair in s_Copies)
                if (pair.Key == null)
                    s_Dead.Add(pair.Key);
            for (int i = 0; i < s_Dead.Count; i++)
            {
                var copy = s_Copies[s_Dead[i]];
                if (copy != null && copy.Material != null)
                    Destroy(copy.Material);
                s_Copies.Remove(s_Dead[i]);
            }
            s_Dead.Clear();
        }

        // Only once no outline component is left: a graphic may still render
        // with a copy until its next canvas rebuild.
        public static void Clear()
        {
            foreach (var copy in s_Copies.Values)
                if (copy != null && copy.Material != null)
                    Destroy(copy.Material);
            s_Copies.Clear();
            s_Verdicts.Clear();
            s_ProbeQueue.Clear();
            s_Probing = null;
            if (s_Probe != null)
            {
                s_Probe.Release();
                Destroy(s_Probe);
            }
            s_Probe = null;
            s_Strength = 0f;
            s_LoadedShaders = null;
            VerdictsChanged = false;
        }
    }
}
