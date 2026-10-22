using UnityEngine;
using UnityEngine.Rendering;

namespace NeutralQoL.Shared.Transparency
{
    internal static class TransparentMaterial
    {
        private const string k_FallbackShader = "Universal Render Pipeline/Lit";
        private const string k_TransparentKeyword = "_SURFACE_TYPE_TRANSPARENT";

        private static readonly int s_BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int s_ColorId = Shader.PropertyToID("_Color");
        private static readonly int s_BaseMapId = Shader.PropertyToID("_BaseMap");
        private static readonly int s_SurfaceId = Shader.PropertyToID("_Surface");
        private static readonly int s_BlendId = Shader.PropertyToID("_Blend");
        private static readonly int s_SrcBlendId = Shader.PropertyToID("_SrcBlend");
        private static readonly int s_DstBlendId = Shader.PropertyToID("_DstBlend");
        private static readonly int s_SrcBlendAlphaId = Shader.PropertyToID("_SrcBlendAlpha");
        private static readonly int s_DstBlendAlphaId = Shader.PropertyToID("_DstBlendAlpha");
        private static readonly int s_ZWriteId = Shader.PropertyToID("_ZWrite");
        // Texture properties to try, in order, for the fallback.
        private static readonly string[] s_TextureNames = { "_BaseMap", "_MainTex", "_BaseColorMap", "_Albedo", "_AlbedoMap", "_MainTexture", "_Diffuse", "_Texture" };

        private static Shader s_Fallback;

        // Null if there is no source and no fallback shader.
        public static Material Make(Material source, string name, bool depthWrite, int queue)
        {
            Material material;
            if (source != null && source.HasProperty(s_SurfaceId) && source.HasProperty(s_SrcBlendId))
            {
                material = new Material(source);
            }
            else
            {
                if (s_Fallback == null)
                    s_Fallback = Shader.Find(k_FallbackShader);
                if (s_Fallback == null)
                    return null;
                material = new Material(s_Fallback);
                if (source != null)
                {
                    var texture = FindTexture(source);
                    if (texture != null)
                        material.SetTexture(s_BaseMapId, texture);
                    // Livery alpha is used for other things, so take only the colour.
                    if (source.HasProperty(s_BaseColorId))
                        material.SetColor(s_BaseColorId, source.GetColor(s_BaseColorId));
                    else if (source.HasProperty(s_ColorId))
                        material.SetColor(s_BaseColorId, source.GetColor(s_ColorId));
                }
            }

            material.name = name;
            SetFloat(material, s_SurfaceId, 1f);
            SetFloat(material, s_BlendId, 0f);
            SetFloat(material, s_SrcBlendId, (float)BlendMode.SrcAlpha);
            SetFloat(material, s_DstBlendId, (float)BlendMode.OneMinusSrcAlpha);
            SetFloat(material, s_SrcBlendAlphaId, (float)BlendMode.One);
            SetFloat(material, s_DstBlendAlphaId, (float)BlendMode.OneMinusSrcAlpha);
            SetFloat(material, s_ZWriteId, depthWrite ? 1f : 0f);
            material.SetOverrideTag("RenderType", "Transparent");
            material.EnableKeyword(k_TransparentKeyword);
            material.renderQueue = queue;
            return material;
        }

        public static int ColorId(Material material)
        {
            return material.HasProperty(s_BaseColorId) ? s_BaseColorId : s_ColorId;
        }

        public static Color BaseColor(Material material, int colorId)
        {
            return material.HasProperty(colorId) ? material.GetColor(colorId) : Color.white;
        }

        // Set, not multiplied: opaque materials ignore alpha and liveries often store 0.
        public static void SetAlpha(Material material, int colorId, Color baseColor, float alpha)
        {
            if (!material.HasProperty(colorId))
                return;
            baseColor.a = alpha;
            material.SetColor(colorId, baseColor);
        }

        private static Texture FindTexture(Material source)
        {
            foreach (var name in s_TextureNames)
            {
                if (source.HasProperty(name))
                {
                    var texture = source.GetTexture(name);
                    if (texture != null)
                        return texture;
                }
            }
            return source.mainTexture;
        }

        private static void SetFloat(Material material, int id, float value)
        {
            if (material.HasProperty(id))
                material.SetFloat(id, value);
        }
    }
}
