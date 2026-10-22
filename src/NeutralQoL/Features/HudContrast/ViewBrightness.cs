using System;
using System.Collections;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace NeutralQoL.Features.HudContrast
{
    internal sealed class ViewBrightness : MonoBehaviour
    {
        private const int k_TargetWidth = 32;
        // Gaussian falloff from the area's middle: at the edge a pixel counts
        // about a tenth as much as one in the centre.
        private const float k_CentreFalloff = 2.3f;

        // Set by the feature: the area to average, in screen pixels.
        public Rect Area;
        public bool Wanted;
        public float Interval = 0.2f;

        public float Brightness { get; private set; }
        public int Samples { get; private set; }

        private RenderTexture m_Frame;
        private int m_Mip;
        private bool m_Pending;
        private float m_NextTime;
        private Action<AsyncGPUReadbackRequest> m_OnReadback;
        private WaitForEndOfFrame m_EndOfFrame;
        private Coroutine m_Loop;
        // The area as it was when the frame was captured; Area moves on.
        private Rect m_RequestArea;
        private int m_RequestWidth;
        private int m_RequestHeight;
        private bool m_LoggedError;

        public static ViewBrightness Create()
        {
            var go = new GameObject("NeutralQoL.HudContrast") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            return go.AddComponent<ViewBrightness>();
        }

        private void OnEnable()
        {
            m_OnReadback = OnReadback;
            m_EndOfFrame = new WaitForEndOfFrame();
            m_Loop = StartCoroutine(Loop());
        }

        private void OnDisable()
        {
            if (m_Loop != null)
                StopCoroutine(m_Loop);
            m_Loop = null;
            Wanted = false;
            Samples = 0;
        }

        private void OnDestroy()
        {
            if (m_Frame != null)
            {
                m_Frame.Release();
                Destroy(m_Frame);
            }
            m_Frame = null;
        }

        private IEnumerator Loop()
        {
            while (true)
            {
                yield return m_EndOfFrame;
                if (!Wanted || m_Pending || Time.unscaledTime < m_NextTime)
                    continue;
                m_NextTime = Time.unscaledTime + Interval;
                Capture();
            }
        }

        private void Capture()
        {
            int width = Screen.width;
            int height = Screen.height;
            if (width < 1 || height < 1)
                return;
            // CaptureScreenshotIntoRenderTexture needs a target the size of the
            // screen.
            if (m_Frame == null || m_Frame.width != width || m_Frame.height != height)
            {
                if (m_Frame != null)
                {
                    m_Frame.Release();
                    Destroy(m_Frame);
                }
                m_Frame = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
                {
                    name = "NeutralQoL.HudContrast",
                    useMipMap = true,
                    autoGenerateMips = false,
                    hideFlags = HideFlags.HideAndDontSave,
                };
                m_Frame.Create();
                m_Mip = Mathf.Clamp(Mathf.FloorToInt(Mathf.Log(width / (float)k_TargetWidth, 2f)), 0, m_Frame.mipmapCount - 1);
            }

            ScreenCapture.CaptureScreenshotIntoRenderTexture(m_Frame);
            m_Frame.GenerateMips();
            m_RequestArea = Area;
            m_RequestWidth = width;
            m_RequestHeight = height;
            m_Pending = true;
            AsyncGPUReadback.Request(m_Frame, m_Mip, TextureFormat.RGBA32, m_OnReadback);
        }

        private void OnReadback(AsyncGPUReadbackRequest request)
        {
            m_Pending = false;
            if (!Wanted)
                return;
            if (request.hasError)
            {
                if (!m_LoggedError)
                    Plugin.Log.LogWarning("HUD.Contrast with the view: screen readback failed, brightness not updated");
                m_LoggedError = true;
                return;
            }

            int w = request.width;
            int h = request.height;
            NativeArray<Color32> pixels = request.GetData<Color32>();
            float sx = w / (float)m_RequestWidth;
            float sy = h / (float)m_RequestHeight;
            int x0 = Mathf.Clamp(Mathf.FloorToInt(m_RequestArea.xMin * sx), 0, w - 1);
            int x1 = Mathf.Clamp(Mathf.CeilToInt(m_RequestArea.xMax * sx), x0 + 1, w);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(m_RequestArea.yMin * sy), 0, h - 1);
            int y1 = Mathf.Clamp(Mathf.CeilToInt(m_RequestArea.yMax * sy), y0 + 1, h);
            // Where the first row is the top of the screen, the capture is
            // upside down compared to screen pixels (which start at the bottom).
            bool flip = SystemInfo.graphicsUVStartsAtTop;

            // Pixels near the middle of the area count most: that is where the
            // pitch ladder, flight path marker and weapon reticle sit, and a
            // bright patch at the edge matters less than one behind them.
            float cx = (x0 + x1) * 0.5f;
            float cy = (y0 + y1) * 0.5f;
            float rx = Mathf.Max(1f, (x1 - x0) * 0.5f);
            float ry = Mathf.Max(1f, (y1 - y0) * 0.5f);
            float sum = 0f;
            float weights = 0f;
            for (int y = y0; y < y1; y++)
            {
                int row = (flip ? h - 1 - y : y) * w;
                float dy = (y + 0.5f - cy) / ry;
                for (int x = x0; x < x1; x++)
                {
                    var p = pixels[row + x];
                    float dx = (x + 0.5f - cx) / rx;
                    float weight = Mathf.Exp(-(dx * dx + dy * dy) * k_CentreFalloff);
                    // Rec. 709 weights on the stored (gamma encoded) values,
                    // close to how bright the pixel looks.
                    sum += weight * (0.2126f * p.r + 0.7152f * p.g + 0.0722f * p.b);
                    weights += weight;
                }
            }
            if (weights <= 0f)
                return;
            Brightness = sum / (weights * 255f);
            Samples++;
        }
    }
}
