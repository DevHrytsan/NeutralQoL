using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using NeutralQoL.Core;
using UnityEngine;
using UnityEngine.UI;

namespace NeutralQoL.Features.HudContrast
{
    internal sealed class HudContrastFeature : Feature
    {
        // Brightness has to fall this far below the threshold before the
        // colours go back, so a cloud edge does not make them flicker.
        private const float k_Hysteresis = 0.1f;
        private const float k_FadeSeconds = 0.5f;
        private const float k_ScanInterval = 1f;
        // Half the sampled area around the HUD centre, as a share of the screen.
        private const float k_AreaHalfWidth = 0.22f;
        private const float k_AreaHalfHeight = 0.3f;

        public override string Section => "HUD";
        public override string Name => "Contrast with the view";
        // On hold: nothing looked right on the additive HUD.
        public override bool Shelved => true;
        public override string Description =>
            "Gives the flight HUD's lines and text a dark outline while the view behind it is bright (clouds, snow, sunlit sky), so they stay readable, and removes it over darker scenes. Unit markers and the minimap are not changed.";

        private static ConfigEntry<float> s_Threshold;
        private static ConfigEntry<float> s_Width;
        private static AccessTools.FieldRef<FlightHud, Canvas> s_HudCanvas;
        private static AccessTools.FieldRef<FlightHud, Transform> s_HudCenter;

        private readonly List<Graphic> m_Graphics = new List<Graphic>();
        private readonly List<HudOutline> m_Outlines = new List<HudOutline>();
        private ViewBrightness m_Brightness;
        private FlightHud m_Hud;
        private float m_NextScan;
        private bool m_Dark;
        private float m_Blend;
        private int m_LastSamples;

        protected override void BindOptions()
        {
            s_Threshold = Bind("Bright view threshold", 0.7f,
                "How bright the view behind the HUD must be before the outline shows, from 0 (black) to 1 (white). Lower shows it sooner.",
                new AcceptableValueRange<float>(0.4f, 0.95f));
            s_Width = Bind("Outline width (pixels)", 1.5f,
                "How thick the dark outline round the HUD's lines and text is.",
                new AcceptableValueRange<float>(0.5f, 3f));
        }

        protected override void Resolve()
        {
            s_HudCanvas = Targets.Field<FlightHud, Canvas>("canvas");
            s_HudCenter = Targets.Field<FlightHud, Transform>("HUDCenter");
            Targets.Field<CombatHUD, Transform>(nameof(CombatHUD.iconLayer));
        }

        protected override void OnEnable()
        {
            if (!SystemInfo.supportsAsyncGPUReadback)
                throw new System.NotSupportedException("this graphics device has no async GPU readback");
            m_Brightness = ViewBrightness.Create();
            m_Dark = false;
            m_Blend = 0f;
            m_LastSamples = 0;
            m_NextScan = 0f;
        }

        protected override void OnDisable()
        {
            RemoveOutlines();
            HudOutline.Clear();
            if (m_Brightness != null)
                Object.Destroy(m_Brightness.gameObject);
            m_Brightness = null;
            m_Hud = null;
        }

        private void RemoveOutlines()
        {
            for (int i = 0; i < m_Outlines.Count; i++)
            {
                var outline = m_Outlines[i];
                if (outline == null)
                    continue;
                outline.enabled = false;
                Object.Destroy(outline);
            }
            m_Outlines.Clear();
        }

        public override void Tick()
        {
            var hud = SceneSingleton<FlightHud>.i;
            var canvas = hud != null ? s_HudCanvas(hud) : null;
            bool shown = canvas != null && canvas.isActiveAndEnabled;
            m_Brightness.Wanted = shown;
            if (hud != m_Hud)
            {
                m_Hud = hud;
                m_Outlines.Clear();
                m_NextScan = 0f;
            }
            if (!shown)
                return;

            if (Time.unscaledTime >= m_NextScan)
            {
                m_NextScan = Time.unscaledTime + k_ScanInterval;
                Scan(canvas);
            }

            m_Brightness.Area = SampleArea(canvas, s_HudCenter(hud));
            if (m_Brightness.Samples != m_LastSamples)
            {
                m_LastSamples = m_Brightness.Samples;
                float b = m_Brightness.Brightness;
                bool dark = m_Dark ? b > s_Threshold.Value - k_Hysteresis : b > s_Threshold.Value;
                if (dark != m_Dark)
                {
                    m_Dark = dark;
                    Plugin.Log.LogInfo($"HUD.Contrast with the view: view brightness {b:0.00}, outline {(dark ? "on" : "off")}");
                }
            }

            float target = m_Dark ? 1f : 0f;
            m_Blend = Mathf.MoveTowards(m_Blend, target, Time.unscaledDeltaTime / k_FadeSeconds);
            // Image outlines live in the mesh, so those rebuild while the
            // strength changes (only during a fade).
            if (HudOutline.SetStrength(m_Blend, s_Width.Value) || HudOutline.VerdictsChanged)
            {
                HudOutline.VerdictsChanged = false;
                for (int i = 0; i < m_Outlines.Count; i++)
                {
                    var outline = m_Outlines[i];
                    if (outline == null)
                        continue;
                    outline.Refresh();
                }
            }
        }

        // Weapon modes instantiate their HUD under HUDCenter when selected, so
        // the HUD is scanned again now and then for new graphics.
        private void Scan(Canvas canvas)
        {
            HudOutline.Prune();
            for (int i = m_Outlines.Count - 1; i >= 0; i--)
                if (m_Outlines[i] == null)
                    m_Outlines.RemoveAt(i);

            var combat = SceneSingleton<CombatHUD>.i;
            var markers = combat != null ? combat.iconLayer : null;
            var map = SceneSingleton<DynamicMap>.i;
            var mapRoot = map != null ? map.transform : null;

            canvas.GetComponentsInChildren(true, m_Graphics);
            int added = 0;
            for (int i = 0; i < m_Graphics.Count; i++)
            {
                var graphic = m_Graphics[i];
                if (graphic.TryGetComponent<HudOutline>(out _))
                    continue;
                var t = graphic.transform;
                if ((markers != null && t.IsChildOf(markers)) || (mapRoot != null && t.IsChildOf(mapRoot)))
                    continue;
                m_Outlines.Add(graphic.gameObject.AddComponent<HudOutline>());
                added++;
            }
            m_Graphics.Clear();
            if (added > 0)
                Plugin.Log.LogInfo($"HUD.Contrast with the view: outlining {added} new HUD graphic(s), {m_Outlines.Count} in all");
        }

        private static Rect SampleArea(Canvas canvas, Transform center)
        {
            float w = Screen.width;
            float h = Screen.height;
            var pos = new Vector2(w * 0.5f, h * 0.5f);
            if (center != null)
            {
                var root = canvas.rootCanvas;
                var cam = root.renderMode == RenderMode.ScreenSpaceOverlay ? null : root.worldCamera;
                pos = RectTransformUtility.WorldToScreenPoint(cam, center.position);
                // HUDCenter follows the point ahead of the nose, which can be off
                // screen; the HUD readout is then mostly near the middle.
                pos.x = Mathf.Clamp(pos.x, 0f, w);
                pos.y = Mathf.Clamp(pos.y, 0f, h);
            }
            float hw = w * k_AreaHalfWidth;
            float hh = h * k_AreaHalfHeight;
            return Rect.MinMaxRect(Mathf.Max(0f, pos.x - hw), Mathf.Max(0f, pos.y - hh), Mathf.Min(w, pos.x + hw), Mathf.Min(h, pos.y + hh));
        }
    }
}
