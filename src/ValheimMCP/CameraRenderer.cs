using System;
using UnityEngine;

namespace ValheimMCP
{
    /// <summary>Result of an off-screen render: PNG bytes or an error.</summary>
    internal sealed class RenderResult
    {
        public byte[] Png;
        public string Error;
    }

    /// <summary>
    ///     Renders an arbitrary viewpoint with a dedicated off-screen camera, fully
    ///     independent of the player's view. The camera is created once (persistent,
    ///     hidden, disabled) and driven manually via <c>Camera.Render()</c> into a
    ///     temporary RenderTexture, then read back to a PNG. The player's camera is
    ///     never touched.
    ///
    ///     MUST be called on the Unity main thread (via <see cref="MainThreadDispatcher" />).
    /// </summary>
    internal static class CameraRenderer
    {
        private const string CamName = "valheimmcp_render_cam";
        private static Camera _sCam;

        /// <summary>
        ///     Render a view aimed at world point (x, y, z), positioned on a sphere
        ///     around it: <paramref name="pitch" /> = elevation above horizon
        ///     (0 = level, 90 = straight down), <paramref name="yaw" /> = compass
        ///     azimuth, <paramref name="dist" /> = distance in meters. If
        ///     <paramref name="y" /> is null, the terrain ground height at (x, z) is
        ///     used — pass an explicit y for interiors/elevated floors.
        /// </summary>
        public static RenderResult Render(float x, float z, float? y,
            float yaw, float pitch, float dist, int size)
        {
            try
            {
                size = ModConfig.ClampRenderSize(size);
                var groundY = y ?? SampleGround(x, z);
                var target = new Vector3(x, groundY, z);

                var pitchRad = pitch * Mathf.Deg2Rad;
                var yawRad = yaw * Mathf.Deg2Rad;
                var dir = new Vector3(
                    Mathf.Cos(pitchRad) * Mathf.Sin(yawRad),
                    Mathf.Sin(pitchRad),
                    Mathf.Cos(pitchRad) * Mathf.Cos(yawRad));
                var camPos = target + dir * Mathf.Max(1f, dist);

                var cam = EnsureCamera();
                cam.transform.position = camPos;
                cam.transform.rotation = Quaternion.LookRotation(target - camPos, Vector3.up);
                cam.nearClipPlane = 0.1f;
                cam.farClipPlane = dist + 1000f;

                var rt = RenderTexture.GetTemporary(size, size, 24, RenderTextureFormat.ARGB32);
                var prevActive = RenderTexture.active;
                Texture2D tex = null;
                try
                {
                    cam.targetTexture = rt;
                    cam.Render();

                    RenderTexture.active = rt;
                    tex = new Texture2D(size, size, TextureFormat.RGB24, false);
                    tex.ReadPixels(new Rect(0, 0, size, size), 0, 0);
                    tex.Apply();

                    var png = tex.EncodeToPNG();
                    return new RenderResult { Png = png };
                }
                finally
                {
                    cam.targetTexture = null;
                    RenderTexture.active = prevActive;
                    RenderTexture.ReleaseTemporary(rt);
                    if (tex != null) UnityEngine.Object.Destroy(tex);
                }
            }
            catch (Exception ex)
            {
                return new RenderResult { Error = ex.Message };
            }
        }

        private static Camera EnsureCamera()
        {
            if (_sCam != null) return _sCam;

            var go = new GameObject(CamName) { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(go);
            _sCam = go.AddComponent<Camera>();
            _sCam.enabled = false; // driven manually via Render()
            _sCam.clearFlags = CameraClearFlags.Skybox;
            _sCam.cullingMask = ~0;
            _sCam.fieldOfView = 60f;
            return _sCam;
        }

        private static float SampleGround(float x, float z)
        {
            var zs = ZoneSystem.instance;
            if (zs != null && zs.GetGroundHeight(new Vector3(x, 5000f, z), out var h))
                return h;
            return 0f;
        }
    }
}
