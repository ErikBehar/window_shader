using System.Collections.Generic;
using UnityEngine;

namespace RoomWindow
{
    /// <summary>
    /// Flat grid on the glass of a captured room. Local +Z points into the room, matching the capture camera.
    /// The material ray-marches the captured depth photos.
    /// </summary>
    [DisallowMultipleComponent]
    [ExecuteAlways]
    [AddComponentMenu("RoomWindow/Room Window")]
    public class RoomWindow : MonoBehaviour
    {
        [Tooltip("Camera that framed this capture. Used by the capture tool when recapturing.")]
        [SerializeField] Camera _capturedFrom;

        [Tooltip("Distance from the capture camera to the glass, in meters.")]
        [SerializeField] float _windowDistance = 0.3f;

        [SerializeField] int _textureWidth = 1024;
        [SerializeField] int _textureHeight = 1024;
        [SerializeField] int _meshResolution = 1;

        [Tooltip("Depth gap, in meters, treated as a silhouette. The shader opens a crack there and the back layer fills it.")]
        [SerializeField] float _edgeThresholdMeters = 0.25f;

        [SerializeField] string _assetFolder = "Assets/_Content/Rendering/RoomWindow/Captures";
        [SerializeField] LayerMask _layerMask = ~0;
        [SerializeField] Matrix4x4 _projection = Matrix4x4.identity;
        [SerializeField] float _nearClip = 0.3f;
        [SerializeField] float _farClip = 1000f;

        [Tooltip("Hide the real meshes inside this window so they are not drawn again beside the captured picture.")]
        [SerializeField] bool _hideSourceGeometry = true;

        readonly List<Renderer> _hiddenSources = new List<Renderer>();
        int _sourceHidePasses;
        Vector3 _hidePosition;
        Quaternion _hideRotation;

        public Camera CapturedFrom => _capturedFrom;
        public float WindowDistance => _windowDistance;
        public int TextureWidth => _textureWidth;
        public int TextureHeight => _textureHeight;
        public int MeshResolution => _meshResolution;
        public float EdgeThresholdMeters => _edgeThresholdMeters;
        public string AssetFolder => _assetFolder;
        public LayerMask LayerMask => _layerMask;
        public Matrix4x4 Projection => _projection;
        public float NearClip => _nearClip;
        public float FarClip => _farClip;
        public bool HideSourceGeometry => _hideSourceGeometry;

        void OnEnable()
        {
            _sourceHidePasses = 0;
            HideSources();
        }

        void OnDisable()
        {
            ShowSources();
        }

        void Update()
        {
            bool moved = transform.position != _hidePosition || transform.rotation != _hideRotation;
            if (moved)
                _sourceHidePasses = 0;
            if (_sourceHidePasses >= 8)
                return;

            _sourceHidePasses++;
            _hidePosition = transform.position;
            _hideRotation = transform.rotation;
            HideSources();
        }

        void OnValidate()
        {
            _sourceHidePasses = 0;
            if (!isActiveAndEnabled)
                return;

            if (_hideSourceGeometry)
                HideSources();
            else
                ShowSources();
        }

        /// <summary>
        /// Turns the photographed meshes back on. The capture tool calls this so it can see the room.
        /// </summary>
        public void ShowSources()
        {
            for (int i = 0; i < _hiddenSources.Count; i++)
            {
                if (_hiddenSources[i] != null)
                    _hiddenSources[i].forceRenderingOff = false;
            }

            _hiddenSources.Clear();
        }

        /// <summary>
        /// Disables renderers inside the captured frustum. The window keeps their picture.
        /// </summary>
        public void HideSources()
        {
            ShowSources();
            if (!_hideSourceGeometry || !gameObject.scene.IsValid() || _projection == Matrix4x4.identity)
                return;

            Plane[] planes = SourcePlanes();
            if (planes == null)
                return;

            Renderer[] renderers = FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer source = renderers[i];
                if (!IsSource(source))
                    continue;
                if (!GeometryUtility.TestPlanesAABB(planes, source.bounds))
                    continue;

                source.forceRenderingOff = true;
                _hiddenSources.Add(source);
            }

#if UNITY_EDITOR
            UnityEditor.SceneView.RepaintAll();
#endif
        }

        Plane[] SourcePlanes()
        {
            if (Mathf.Abs(_projection.m00) < 1e-6f || Mathf.Abs(_projection.m11) < 1e-6f)
                return null;

            Vector3 cameraPosition = transform.TransformPoint(new Vector3(0f, 0f, -_windowDistance));
            Matrix4x4 cameraToWorld = Matrix4x4.TRS(cameraPosition, transform.rotation, Vector3.one);
            Matrix4x4 worldToView = Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) * cameraToWorld.inverse;
            return GeometryUtility.CalculateFrustumPlanes(_projection * worldToView);
        }

        bool IsSource(Renderer source)
        {
            if (source == null || !source.enabled || !source.gameObject.activeInHierarchy)
                return false;
            if (source.GetComponentInParent<RoomWindow>() != null)
                return false;
            if (((1 << source.gameObject.layer) & _layerMask.value) == 0)
                return false;
            if ((source.hideFlags & HideFlags.HideAndDontSave) != 0)
                return false;

            return true;
        }

        /// <summary>
        /// 0 uses the first captured lighting, 1 uses the second. Values in between blend shadows and light.
        /// </summary>
        public void SetLightBlend(float blend)
        {
            MeshRenderer meshRenderer = GetComponent<MeshRenderer>();
            if (meshRenderer == null)
                return;

            Material material = Application.isPlaying ? meshRenderer.material : meshRenderer.sharedMaterial;
            if (material != null && material.HasProperty("_LightBlend"))
                material.SetFloat("_LightBlend", Mathf.Clamp01(blend));
        }

        /// <summary>
        /// Point on the capture ray through uv (0-1), in camera space.
        /// Camera space here is +X right, +Y up, +Z forward (Unity camera local axes), not Unity view space.
        /// Keep this in sync with RoomWindowPass.hlsl.
        /// </summary>
        public static Vector3 CameraSpacePosition(float u, float v, float eyeDepth, Vector4 frustum, Vector4 orthoTerms, bool orthographic)
        {
            float ndcX = u * 2f - 1f;
            float ndcY = v * 2f - 1f;

            if (orthographic)
            {
                float x = (ndcX - orthoTerms.x) / frustum.x;
                float y = (ndcY - orthoTerms.y) / frustum.y;
                return new Vector3(x, y, eyeDepth);
            }

            float px = (ndcX * eyeDepth + eyeDepth * frustum.z) / frustum.x;
            float py = (ndcY * eyeDepth + eyeDepth * frustum.w) / frustum.y;
            return new Vector3(px, py, eyeDepth);
        }

        /// <summary>
        /// Same point in mesh space. The mesh origin is the center of the glass, so the camera sits at z = -windowDistance.
        /// </summary>
        public static Vector3 MeshPosition(float u, float v, float eyeDepth, float windowDistance, Vector4 frustum, Vector4 orthoTerms, bool orthographic)
        {
            Vector3 cameraSpace = CameraSpacePosition(u, v, eyeDepth, frustum, orthoTerms, orthographic);
            cameraSpace.z -= windowDistance;
            return cameraSpace;
        }

        void OnDrawGizmosSelected()
        {
            MeshRenderer renderer = GetComponent<MeshRenderer>();
            if (renderer == null || renderer.sharedMaterial == null)
                return;

            Material material = renderer.sharedMaterial;
            if (!material.HasProperty("_DepthRange") || !material.HasProperty("_Frustum"))
                return;

            Vector4 range = material.GetVector("_DepthRange");
            Vector4 frustum = material.GetVector("_Frustum");
            Vector4 orthoTerms = material.HasProperty("_OrthoTerms") ? material.GetVector("_OrthoTerms") : Vector4.zero;
            bool orthographic = range.w > 0.5f;
            float windowDistance = range.z;
            float farDepth = range.y;

            if (Mathf.Abs(frustum.x) < 1e-6f || Mathf.Abs(frustum.y) < 1e-6f)
                return;

            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = new Color(0.35f, 0.85f, 1f, 0.9f);

            Vector3[] near =
            {
                MeshPosition(0f, 0f, windowDistance, windowDistance, frustum, orthoTerms, orthographic),
                MeshPosition(1f, 0f, windowDistance, windowDistance, frustum, orthoTerms, orthographic),
                MeshPosition(1f, 1f, windowDistance, windowDistance, frustum, orthoTerms, orthographic),
                MeshPosition(0f, 1f, windowDistance, windowDistance, frustum, orthoTerms, orthographic)
            };
            Vector3[] far =
            {
                MeshPosition(0f, 0f, farDepth, windowDistance, frustum, orthoTerms, orthographic),
                MeshPosition(1f, 0f, farDepth, windowDistance, frustum, orthoTerms, orthographic),
                MeshPosition(1f, 1f, farDepth, windowDistance, frustum, orthoTerms, orthographic),
                MeshPosition(0f, 1f, farDepth, windowDistance, frustum, orthoTerms, orthographic)
            };

            DrawLoop(near);
            DrawLoop(far);
            for (int i = 0; i < 4; i++)
                Gizmos.DrawLine(near[i], far[i]);
        }

        static void DrawLoop(Vector3[] corners)
        {
            for (int i = 0; i < 4; i++)
                Gizmos.DrawLine(corners[i], corners[(i + 1) % 4]);
        }

#if UNITY_EDITOR
        public void SetCaptureSettings(Camera camera, float windowDistance, int textureWidth, int textureHeight, int meshResolution, float edgeThresholdMeters, string assetFolder, LayerMask layerMask, Matrix4x4 projection, float nearClip, float farClip)
        {
            _capturedFrom = camera;
            _windowDistance = windowDistance;
            _textureWidth = textureWidth;
            _textureHeight = textureHeight;
            _meshResolution = meshResolution;
            _edgeThresholdMeters = edgeThresholdMeters;
            _assetFolder = assetFolder;
            _layerMask = layerMask;
            _projection = projection;
            _nearClip = nearClip;
            _farClip = farClip;
        }
#endif
    }
}
