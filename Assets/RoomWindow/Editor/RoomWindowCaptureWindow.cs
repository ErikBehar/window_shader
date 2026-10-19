using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace RoomWindow.Editor
{
    using RoomWindow = global::RoomWindow.RoomWindow;

    public class RoomWindowCaptureWindow : EditorWindow
    {
        const string DefaultFolder = "Assets/_Content/Rendering/RoomWindow/Captures";

        [SerializeField] Camera _camera;
        [SerializeField] RoomWindow _existing;
        [SerializeField] int _width = 1024;
        [SerializeField] int _height = 1024;
        [SerializeField] int _meshResolution = 1;
        [SerializeField] float _windowDistance = 0.3f;
        [SerializeField] float _edgeThreshold = 0.25f;
        [SerializeField] LayerMask _layerMask = ~0;
        [SerializeField] string _assetFolder = DefaultFolder;
        [SerializeField] string _assetName = "RoomWindow";

        [MenuItem("Tools/Rendering/Room Window Capture")]
        public static void Open()
        {
            GetWindow<RoomWindowCaptureWindow>("Room Window");
        }

        [InitializeOnLoadMethod]
        static void ScheduleDepthPack()
        {
            EditorApplication.delayCall += MoveDepthIntoRoomColor;
        }

        static void MoveDepthIntoRoomColor()
        {
            string[] guids = AssetDatabase.FindAssets("t:Texture2D", new[] { "Assets/_Content/Rendering/RoomWindow" });
            foreach (string guid in guids)
            {
                string depthPath = AssetDatabase.GUIDToAssetPath(guid);
                if (!depthPath.EndsWith("_Depth.exr"))
                    continue;

                string colorPath = depthPath.Replace("_Depth.exr", "_Albedo.exr");
                Texture2D color = AssetDatabase.LoadAssetAtPath<Texture2D>(colorPath);
                Texture2D depth = AssetDatabase.LoadAssetAtPath<Texture2D>(depthPath);
                if (color == null || depth == null || color.width != depth.width || color.height != depth.height)
                    continue;

                int width = color.width;
                int height = color.height;
                Color[] albedo = ReadLinearTexture(color);
                Color[] depths = ReadLinearTexture(depth);
                if (albedo.Length != depths.Length || albedo.Length == 0)
                    continue;

                bool alreadyPacked = false;
                for (int i = 0; i < albedo.Length; i++)
                {
                    if (Mathf.Abs(albedo[i].a - 1f) > 0.001f)
                    {
                        alreadyPacked = true;
                        break;
                    }
                }

                if (!alreadyPacked)
                {
                    for (int i = 0; i < albedo.Length; i++)
                        albedo[i].a = depths[i].r;
                    WriteLinearExr(colorPath, width, height, albedo);
                }

                AssetDatabase.DeleteAsset(depthPath);
            }
        }

        [MenuItem("Tools/Rendering/Add Room Window At Near Clip")]
        public static void AddPlaneAtSelectedCamera()
        {
            Camera camera = SelectedCamera();
            if (camera == null)
            {
                EditorUtility.DisplayDialog("Room Window", "Select a camera first.", "OK");
                return;
            }

            RoomWindow window = CreatePlane(camera);
            if (window != null)
                Selection.activeGameObject = window.gameObject;
        }

        [MenuItem("Tools/Rendering/Add Room Window At Near Clip", true)]
        static bool ValidateAddPlaneAtSelectedCamera()
        {
            return SelectedCamera() != null;
        }

        [MenuItem("CONTEXT/Camera/Add Room Window At Near Clip")]
        static void AddPlaneFromCameraMenu(MenuCommand command)
        {
            Camera camera = command.context as Camera;
            if (camera == null)
                return;

            RoomWindow window = CreatePlane(camera);
            if (window != null)
                Selection.activeGameObject = window.gameObject;
        }

        static Camera SelectedCamera()
        {
            GameObject active = Selection.activeGameObject;
            if (active == null)
                return null;

            return active.GetComponent<Camera>();
        }

        public static void Open(RoomWindow existing)
        {
            RoomWindowCaptureWindow window = GetWindow<RoomWindowCaptureWindow>("Room Window");
            window.Load(existing);
            window.Show();
        }

        void Load(RoomWindow existing)
        {
            if (existing == null)
                return;

            _existing = existing;
            _camera = existing.CapturedFrom;
            _windowDistance = existing.WindowDistance;
            _width = Mathf.Max(16, existing.TextureWidth);
            _height = Mathf.Max(16, existing.TextureHeight);
            _meshResolution = Mathf.Max(1, existing.MeshResolution);
            _edgeThreshold = existing.EdgeThresholdMeters;
            _assetFolder = string.IsNullOrEmpty(existing.AssetFolder) ? DefaultFolder : existing.AssetFolder;
            _layerMask = existing.LayerMask;
            _assetName = existing.name;
        }

        void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Frame the room with a camera just outside the opening, looking in. " +
                "Capture stores a lit photo of the room, with depth packed into its alpha, and the same kind of photo for the walls and floor that furniture hid. " +
                "A flat grid sits on the glass. The shader ray-marches those depth photos, so the interior parallax as the camera moves. " +
                "After the lights change, Capture Other Lighting stores a second lit photo, and Light Blend on the material fades between them.",
                MessageType.Info);

            _camera = (Camera)EditorGUILayout.ObjectField("Camera", _camera, typeof(Camera), true);
            _existing = (RoomWindow)EditorGUILayout.ObjectField("Recapture", _existing, typeof(RoomWindow), true);

            EditorGUILayout.Space(4f);
            _width = Mathf.Clamp(EditorGUILayout.IntField("Texture Width", _width), 16, 4096);
            if (_camera != null)
            {
                _height = FitHeight(_width, NearClipAspect(_camera));
                EditorGUILayout.LabelField("Texture Height", _height.ToString());
            }
            else
            {
                _height = Mathf.Clamp(EditorGUILayout.IntField("Texture Height", _height), 16, 4096);
            }
            _meshResolution = EditorGUILayout.IntSlider("Mesh Resolution", _meshResolution, 1, 256);
            _windowDistance = Mathf.Max(0.01f, EditorGUILayout.FloatField("Window Distance", _windowDistance));
            if (_camera != null && GUILayout.Button("Match Camera Near Clip"))
                _windowDistance = _camera.nearClipPlane;

            _edgeThreshold = EditorGUILayout.Slider("Silhouette Threshold (m)", _edgeThreshold, 0.01f, 2f);
            _layerMask = LayerMaskField("Layers", _layerMask);

            EditorGUILayout.Space(4f);
            EditorGUILayout.BeginHorizontal();
            _assetFolder = EditorGUILayout.TextField("Output Folder", _assetFolder);
            if (GUILayout.Button("Browse", GUILayout.Width(70f)))
                BrowseFolder();
            EditorGUILayout.EndHorizontal();
            _assetName = EditorGUILayout.TextField("Name", _assetName);

            EditorGUILayout.Space(8f);
            using (new EditorGUI.DisabledScope(_camera == null && SelectedCamera() == null))
            {
                if (GUILayout.Button("Add Plane At Near Clip", GUILayout.Height(24f)))
                    AddPlaneFromTool();
            }

            using (new EditorGUI.DisabledScope(_camera == null))
            {
                if (GUILayout.Button("Capture", GUILayout.Height(28f)))
                    Capture();
            }

            using (new EditorGUI.DisabledScope(_existing == null))
            {
                if (GUILayout.Button("Capture Other Lighting", GUILayout.Height(24f)))
                    CaptureOtherLighting();
            }
        }

        void BrowseFolder()
        {
            string current = Directory.Exists(ToAbsolute(_assetFolder)) ? ToAbsolute(_assetFolder) : Application.dataPath;
            string picked = EditorUtility.OpenFolderPanel("Room Window Output", current, "");
            if (string.IsNullOrEmpty(picked))
                return;

            picked = picked.Replace('\\', '/');
            string data = Application.dataPath.Replace('\\', '/');
            if (!picked.StartsWith(data))
            {
                EditorUtility.DisplayDialog("Room Window", "Choose a folder inside this project's Assets directory.", "OK");
                return;
            }

            _assetFolder = "Assets" + picked.Substring(data.Length);
        }

        void AddPlaneFromTool()
        {
            Camera camera = _camera != null ? _camera : SelectedCamera();
            if (camera == null)
            {
                EditorUtility.DisplayDialog("Room Window", "Assign a camera, or select one in the scene.", "OK");
                return;
            }

            RoomWindow window = CreatePlane(camera);
            if (window == null)
                return;

            _camera = camera;
            _existing = window;
            _windowDistance = camera.nearClipPlane;
            _width = window.TextureWidth;
            _height = window.TextureHeight;
            _assetName = window.name;
            Selection.activeGameObject = window.gameObject;
        }

        void Capture()
        {
            RunGuarded(RunCapture);
        }

        void CaptureOtherLighting()
        {
            RunGuarded(RunLightingCapture);
        }

        void RunGuarded(Action capture)
        {
            if (Application.isPlaying)
            {
                EditorUtility.DisplayDialog("Room Window", "Exit play mode before capturing.", "OK");
                return;
            }

            try
            {
                capture();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("Room Window", exception.Message, "OK");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        void RunLightingCapture()
        {
            if (_existing == null)
                throw new InvalidOperationException("Assign the captured room window before capturing a second lighting.");

            Camera settings = _existing.CapturedFrom != null ? _existing.CapturedFrom : _camera;
            if (settings == null)
                throw new InvalidOperationException("Assign the camera that framed this room.");

            MeshRenderer meshRenderer = _existing.GetComponent<MeshRenderer>();
            Material material = meshRenderer != null ? meshRenderer.sharedMaterial : null;
            if (material == null || material.GetTexture("_BaseMap") == null)
                throw new InvalidOperationException("Capture the room once before capturing a second lighting.");
            if (_existing.Projection == Matrix4x4.identity)
                throw new InvalidOperationException("Capture the room again so the second lighting uses the same camera.");

            Shader depthShader = Shader.Find("Hidden/RoomWindow/Depth");
            Shader albedoShader = Shader.Find("Hidden/RoomWindow/Albedo");
            if (depthShader == null || albedoShader == null)
                throw new InvalidOperationException("Room Window shaders are not imported yet. Wait for compilation and capture again.");

            int width = Mathf.Max(16, _existing.TextureWidth);
            int height = Mathf.Max(16, _existing.TextureHeight);
            ShowCapturedSources();
            List<Renderer> hiddenWindows = HideRoomWindows();
            ScenePhotos photos;
            try
            {
                Vector3 position = _existing.transform.position - _existing.transform.forward * _existing.WindowDistance;
                int mask = _existing.LayerMask.value & settings.cullingMask;
                photos = CapturePhotos(settings, position, _existing.transform.rotation, width, height, mask, meshRenderer, true, _existing.Projection, _existing.NearClip, _existing.FarClip, depthShader, albedoShader);
            }
            finally
            {
                RestoreRenderers(hiddenWindows);
                HideCapturedSources();
            }

            if (photos.Lit.Length != width * height)
                throw new InvalidOperationException("The lighting capture was empty. Capture the room again, then capture the other lighting.");

            // Match the first capture: fill holes, then replace occluder rims with the
            // room behind them. Otherwise the object's edge stays in Lighting B.
            float farClip = _existing.FarClip;
            float[] eyeDepth = new float[photos.Depth.Length];
            for (int i = 0; i < eyeDepth.Length; i++)
                eyeDepth[i] = photos.Depth[i].r;
            FillEmptySurfaces(photos.Lit, null, eyeDepth, width, height, farClip);

            bool wantsBackground = material.HasProperty("_BackMap") && material.GetTexture("_BackMap") != null;
            BackgroundPhotos backgroundPhotos = photos.Background;
            bool backgroundReady = false;
            if (wantsBackground && backgroundPhotos.Valid && backgroundPhotos.Lit != null && backgroundPhotos.Depth != null
                && backgroundPhotos.Lit.Length == photos.Lit.Length && backgroundPhotos.Depth.Length == backgroundPhotos.Lit.Length)
            {
                float[] backgroundDepth = new float[backgroundPhotos.Depth.Length];
                for (int i = 0; i < backgroundDepth.Length; i++)
                    backgroundDepth[i] = backgroundPhotos.Depth[i].r;
                FillEmptySurfaces(backgroundPhotos.Lit, null, backgroundDepth, width, height, farClip);
                DilateOccluderEdges(eyeDepth, photos.Lit, null, backgroundDepth, backgroundPhotos.Lit, null, width, height, farClip);
                backgroundReady = true;
            }
            else if (wantsBackground)
            {
                Debug.LogWarning("No background layer was found this time, so Background Lighting B was left as it was.", _existing);
            }

            string folder = string.IsNullOrEmpty(_existing.AssetFolder) ? DefaultFolder : _existing.AssetFolder;
            EnsureFolder(folder);
            Texture2D lightTexture = SaveLinear(folder, _existing.name, width, height, photos.Lit, "_LitB.exr", "_LightMapB", false);
            material.SetTexture("_LightMapB", lightTexture);

            if (backgroundReady)
            {
                Texture2D backgroundLightTexture = SaveLinear(folder, _existing.name, width, height, backgroundPhotos.Lit, "_BackgroundLitB.exr", "_BackLightMapB", false);
                material.SetTexture("_BackLightMapB", backgroundLightTexture);
            }

            EditorUtility.SetDirty(material);
            Debug.Log($"Captured the second lighting for '{_existing.name}'. Light Blend 0 is the first capture and 1 is this one.", _existing);
        }

        static Color[] ReadLinearTexture(Texture2D texture)
        {
            string path = AssetDatabase.GetAssetPath(texture);
            TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
            bool restoreReadable = false;
            if (importer != null && !importer.isReadable)
            {
                importer.isReadable = true;
                importer.SaveAndReimport();
                texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                restoreReadable = true;
            }

            Color[] pixels = texture.GetPixels();
            if (restoreReadable && importer != null)
            {
                importer.isReadable = false;
                importer.SaveAndReimport();
            }

            return pixels;
        }

        void RunCapture()
        {
            Shader interiorShader = Shader.Find("RoomWindow/Interior");
            Shader glassShader = Shader.Find("RoomWindow/Glass");
            Shader depthShader = Shader.Find("Hidden/RoomWindow/Depth");
            Shader albedoShader = Shader.Find("Hidden/RoomWindow/Albedo");
            if (interiorShader == null || glassShader == null || depthShader == null || albedoShader == null)
                throw new InvalidOperationException("Room Window shaders are not imported yet. Wait for compilation and capture again.");

            float aspect = NearClipAspect(_camera);
            int width = Mathf.Clamp(_width, 16, 4096);
            int height = FitHeight(width, aspect);
            _width = width;
            _height = height;

            int meshResolution = Mathf.Clamp(_meshResolution, 1, 256);
            FitGrid(meshResolution, aspect, out int resolutionX, out int resolutionY);
            string folder = _assetFolder.Replace('\\', '/').TrimEnd('/');
            if (!folder.StartsWith("Assets"))
                throw new InvalidOperationException("Output folder must be inside the project's Assets directory.");

            string assetName = Sanitize(_assetName);
            float windowDistance = Mathf.Max(0.01f, _windowDistance);

            Renderer existingRenderer = _existing != null ? _existing.GetComponent<Renderer>() : null;
            ShowCapturedSources();
            List<Renderer> hiddenWindows = HideRoomWindows();
            ScenePhotos photos;
            try
            {
                photos = CapturePhotos(_camera, _camera.transform.position, _camera.transform.rotation, width, height, _layerMask.value & _camera.cullingMask, existingRenderer, false, Matrix4x4.identity, 0.3f, 1000f, depthShader, albedoShader);
            }
            finally
            {
                RestoreRenderers(hiddenWindows);
                HideCapturedSources();
            }

            Color[] litPixels = photos.Lit;
            Color[] albedoPixels = photos.Albedo;
            Color[] depthColors = photos.Depth;
            Matrix4x4 projection = photos.Projection;
            float nearClip = photos.Near;
            float farClip = photos.Far;
            if (windowDistance < nearClip || windowDistance >= farClip)
                throw new InvalidOperationException("Window distance must sit between the camera near and far clip planes.");

            if (litPixels.Length == 0 || albedoPixels.Length == 0 || depthColors.Length == 0)
                throw new InvalidOperationException("The capture textures were empty.");

            float[] eyeDepth = new float[depthColors.Length];
            for (int i = 0; i < depthColors.Length; i++)
                eyeDepth[i] = depthColors[i].r;

            EditorUtility.DisplayProgressBar("Room Window", "Filling gaps", 0.62f);
            FillEmptySurfaces(litPixels, albedoPixels, eyeDepth, width, height, farClip);

            BackgroundPhotos backgroundPhotos = photos.Background;
            bool hasBackground = backgroundPhotos.Valid;
            float[] backgroundDepth = null;
            if (hasBackground)
            {
                backgroundDepth = new float[backgroundPhotos.Depth.Length];
                for (int i = 0; i < backgroundDepth.Length; i++)
                    backgroundDepth[i] = backgroundPhotos.Depth[i].r;
                FillEmptySurfaces(backgroundPhotos.Lit, backgroundPhotos.Albedo, backgroundDepth, width, height, farClip);
                backgroundPhotos.HiddenCount = DilateOccluderEdges(eyeDepth, litPixels, albedoPixels, backgroundDepth, backgroundPhotos.Lit, backgroundPhotos.Albedo, width, height, farClip);
            }

            Color[] backColors = photos.BackDepth;
            bool hasBack = backColors != null && backColors.Length == depthColors.Length;
            float nearestHit = farClip;
            float farthestHit = windowDistance;
            int hitCount = 0;
            for (int i = 0; i < eyeDepth.Length; i++)
            {
                float depth = eyeDepth[i];
                if (depth > windowDistance + 0.001f && depth < farClip - 0.05f)
                {
                    hitCount++;
                    nearestHit = Mathf.Min(nearestHit, depth);
                    farthestHit = Mathf.Max(farthestHit, depth);
                }

                if (hasBackground)
                {
                    float behind = backgroundDepth[i];
                    if (behind > windowDistance + 0.001f && behind < farClip - 0.05f)
                        farthestHit = Mathf.Max(farthestHit, behind);
                }

                if (!hasBack)
                    continue;

                float back = backColors[i].r;
                if (back > windowDistance + 0.001f && back < farClip - 0.05f)
                    farthestHit = Mathf.Max(farthestHit, back);
            }

            if (hitCount < 32)
                throw new InvalidOperationException("Depth capture is empty. The camera needs opaque meshes inside its frustum on the selected layers.");

            float contentFar = Mathf.Max(farthestHit, windowDistance + 0.05f);

            EditorUtility.DisplayProgressBar("Room Window", "Packing depth", 0.7f);
            for (int i = 0; i < eyeDepth.Length; i++)
            {
                if (eyeDepth[i] <= 0f || eyeDepth[i] >= farClip - 0.05f)
                    eyeDepth[i] = contentFar;
            }

            float encodingNear = Mathf.Min(nearClip, nearestHit);
            float encodingFar = Mathf.Max(contentFar, encodingNear + 0.05f);
            float span = encodingFar - encodingNear;
            PackDepth(litPixels, eyeDepth, encodingNear, span);

            if (hasBackground)
            {
                for (int i = 0; i < backgroundDepth.Length; i++)
                {
                    // The background can never sit in front of what was seen over it.
                    if (backgroundDepth[i] <= 0f || backgroundDepth[i] >= farClip - 0.05f)
                        backgroundDepth[i] = contentFar;
                    backgroundDepth[i] = Mathf.Max(backgroundDepth[i], eyeDepth[i]);
                }

                PackDepth(backgroundPhotos.Lit, backgroundDepth, encodingNear, span);
            }

            EditorUtility.DisplayProgressBar("Room Window", "Saving assets", 0.85f);
            EnsureFolder(folder);

            bool orthographic = _camera.orthographic;
            Vector4 frustum = new Vector4(projection.m00, projection.m11, projection.m02, projection.m12);
            Vector4 orthoTerms = new Vector4(projection.m03, projection.m13, 0f, 0f);

            Texture2D colorTexture = SaveLinear(folder, assetName, width, height, litPixels, "_LitA.exr", "_BaseMap");
            Texture2D lightBTexture = ExistingOrCopy(folder, assetName, "_LitB.exr", "_LightMapB", colorTexture, false);

            Texture2D backgroundTexture = null;
            Texture2D backgroundLightBTexture = null;
            if (hasBackground)
            {
                backgroundTexture = SaveLinear(folder, assetName, width, height, backgroundPhotos.Lit, "_BackgroundLitA.exr", "_BackMap");
                backgroundLightBTexture = ExistingOrCopy(folder, assetName, "_BackgroundLitB.exr", "_BackLightMapB", backgroundTexture, false);
            }

            Mesh mesh = SaveMesh(folder, assetName, resolutionX, resolutionY, windowDistance, encodingFar, frustum, orthoTerms, orthographic);
            Material interior = SaveInterior(folder, assetName, interiorShader, colorTexture, lightBTexture, backgroundTexture, backgroundLightBTexture, encodingNear, encodingFar, windowDistance, orthographic, frustum, orthoTerms);
            Material glass = SaveGlass(folder, assetName, glassShader);

            RoomWindow window = PlaceWindow(assetName, windowDistance, mesh, interior, glass);
            window.SetCaptureSettings(_camera, windowDistance, width, height, meshResolution, _edgeThreshold, folder, _layerMask, projection, nearClip, farClip);
            EditorUtility.SetDirty(window);

            Selection.activeGameObject = window.gameObject;
            EditorSceneManager.MarkSceneDirty(window.gameObject.scene);

            int triangles = resolutionX * resolutionY * 2;
            string backgroundNote = hasBackground
                ? $" Background layer hid {backgroundPhotos.RendererCount:N0} foreground objects and dilated {backgroundPhotos.HiddenCount:N0} silhouette pixels."
                : " No background layer was found, so hidden spots behind objects fill from their edges.";
            Debug.Log($"Room Window '{assetName}' captured. {width}x{height} textures, {triangles:N0} grid triangles, depth {nearestHit:0.00}m to {farthestHit:0.00}m.{backgroundNote}", window);
        }

        static void CopyUniversalSettings(Camera source, Camera destination, bool postProcessing)
        {
            UniversalAdditionalCameraData sourceData = source.GetUniversalAdditionalCameraData();
            UniversalAdditionalCameraData destinationData = destination.GetUniversalAdditionalCameraData();
            destinationData.renderShadows = sourceData.renderShadows;
            destinationData.renderPostProcessing = postProcessing;
            destinationData.antialiasing = AntialiasingMode.None;
            destinationData.volumeLayerMask = sourceData.volumeLayerMask;
            destinationData.volumeTrigger = sourceData.volumeTrigger;
            destinationData.renderType = CameraRenderType.Base;

            SerializedObject serialized = new SerializedObject(sourceData);
            SerializedProperty rendererIndex = serialized.FindProperty("m_RendererIndex");
            if (rendererIndex != null)
                destinationData.SetRenderer(rendererIndex.intValue);
        }

        struct ScenePhotos
        {
            public Color[] Lit;
            public Color[] Albedo;
            public Color[] Depth;
            public Color[] BackDepth;
            public BackgroundPhotos Background;
            public Matrix4x4 Projection;
            public float Near;
            public float Far;
        }

        /// <summary>
        /// The room with its foreground objects removed: walls, floor, counters and
        /// anything else that was ever seen behind another object. The shader draws
        /// this where the camera looks past an object into a spot the capture never saw.
        /// </summary>
        struct BackgroundPhotos
        {
            public bool Valid;
            public Color[] Lit;
            public Color[] Albedo;
            public Color[] Depth;
            public int RendererCount;
            public int HiddenCount;
        }

        ScenePhotos CapturePhotos(Camera settingsSource, Vector3 position, Quaternion rotation, int width, int height, int cullingMask, Renderer skip, bool useStoredProjection, Matrix4x4 storedProjection, float storedNear, float storedFar, Shader depthShader, Shader albedoShader)
        {
            RenderTexture colorRT = null;
            RenderTexture depthRT = null;
            RenderTexture backRT = null;
            GameObject tempGo = null;
            Material depthMaterial = null;
            Texture2D colorRead = null;
            Texture2D depthRead = null;
            Texture2D backRead = null;
            ScenePhotos photos = new ScenePhotos
            {
                Lit = Array.Empty<Color>(),
                Albedo = Array.Empty<Color>(),
                Depth = Array.Empty<Color>(),
                BackDepth = Array.Empty<Color>()
            };

            try
            {
                EditorUtility.DisplayProgressBar("Room Window", "Rendering lighting", 0.15f);
                colorRT = new RenderTexture(width, height, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
                {
                    antiAliasing = 1,
                    name = "RoomWindowColor",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear
                };
                colorRT.Create();

                depthRT = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
                {
                    antiAliasing = 1,
                    name = "RoomWindowDepth",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Point
                };
                depthRT.Create();

                tempGo = new GameObject("Room Window Capture Camera");
                tempGo.transform.SetPositionAndRotation(position, rotation);
                Camera tempCam = tempGo.AddComponent<Camera>();
                tempCam.CopyFrom(settingsSource);
                tempCam.enabled = false;
                tempCam.rect = new Rect(0f, 0f, 1f, 1f);
                tempCam.targetTexture = colorRT;
                tempCam.allowMSAA = false;
                tempCam.allowHDR = true;
                tempCam.useOcclusionCulling = false;
                tempCam.cullingMask = cullingMask;
                tempCam.clearFlags = CameraClearFlags.SolidColor;
                tempCam.backgroundColor = Color.black;
                CopyUniversalSettings(settingsSource, tempCam, false);

                if (useStoredProjection)
                {
                    tempCam.usePhysicalProperties = false;
                    tempCam.nearClipPlane = storedNear;
                    tempCam.farClipPlane = storedFar;
                    tempCam.projectionMatrix = storedProjection;
                    photos.Projection = storedProjection;
                    photos.Near = storedNear;
                    photos.Far = storedFar;
                }
                else if (tempCam.usePhysicalProperties)
                {
                    photos.Near = tempCam.nearClipPlane;
                    photos.Far = tempCam.farClipPlane;
                    photos.Projection = tempCam.projectionMatrix;
                }
                else
                {
                    tempCam.aspect = (float)width / Mathf.Max(1, height);
                    photos.Near = tempCam.nearClipPlane;
                    photos.Far = tempCam.farClipPlane;
                    photos.Projection = NearClipProjection(tempCam, tempCam.aspect);
                }

                if (Mathf.Abs(photos.Projection.m00) < 1e-6f || Mathf.Abs(photos.Projection.m11) < 1e-6f)
                    throw new InvalidOperationException("The camera projection is invalid.");

                tempCam.Render();
                colorRead = ReadTexture(colorRT, width, height, TextureFormat.RGBAHalf, true);
                photos.Lit = colorRead.GetPixels();

                EditorUtility.DisplayProgressBar("Room Window", "Rendering depth", 0.4f);
                depthMaterial = new Material(depthShader) { hideFlags = HideFlags.HideAndDontSave };
                depthMaterial.SetVector("_BaseMap_ST", new Vector4(1f, 1f, 0f, 0f));
                depthMaterial.SetFloat("_UseCutout", 0f);
                depthMaterial.SetFloat("_Cutoff", 0.5f);
                depthMaterial.SetFloat("_RoomWindowCull", 0f);

                Matrix4x4 worldToView = tempCam.worldToCameraMatrix;
                Matrix4x4 viewProj = GL.GetGPUProjectionMatrix(photos.Projection, true) * worldToView;
                depthMaterial.SetFloat("_RoomWindowPeel", 0f);
                DrawDepth(tempCam, depthRT, depthMaterial, cullingMask, skip, worldToView, viewProj, photos.Far, null);

                depthRead = ReadTexture(depthRT, width, height, TextureFormat.RGBAFloat, true);
                photos.Depth = depthRead.GetPixels();

                EditorUtility.DisplayProgressBar("Room Window", "Rendering back faces", 0.48f);
                backRT = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
                {
                    antiAliasing = 1,
                    name = "RoomWindowBackDepth",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Point
                };
                backRT.Create();
                depthMaterial.SetTexture("_RoomWindowFrontDepth", depthRT);
                depthMaterial.SetFloat("_RoomWindowPeel", 1f);
                photos.BackDepth = CapturePeel(tempCam, backRT, depthMaterial, cullingMask, skip, worldToView, viewProj, photos.Far, width, height, photos.Depth, null);
                depthMaterial.SetFloat("_RoomWindowPeel", 0f);

                EditorUtility.DisplayProgressBar("Room Window", "Rendering room color", 0.55f);
                photos.Albedo = RenderAlbedo(tempCam, depthRT, albedoShader, cullingMask, skip, worldToView, viewProj, width, height, null);

                EditorUtility.DisplayProgressBar("Room Window", "Rendering the background", 0.58f);
                photos.Background = CaptureBackground(tempCam, colorRT, depthRT, backRT, depthMaterial, albedoShader, cullingMask, skip, worldToView, viewProj, photos.Far, width, height, photos.Depth, photos.BackDepth);
                return photos;
            }
            finally
            {
                if (colorRead != null)
                    DestroyImmediate(colorRead);
                if (depthRead != null)
                    DestroyImmediate(depthRead);
                if (backRead != null)
                    DestroyImmediate(backRead);
                if (tempGo != null)
                    DestroyImmediate(tempGo);
                if (depthMaterial != null)
                    DestroyImmediate(depthMaterial);

                RenderTexture.active = null;
                if (colorRT != null)
                {
                    colorRT.Release();
                    DestroyImmediate(colorRT);
                }

                if (depthRT != null)
                {
                    depthRT.Release();
                    DestroyImmediate(depthRT);
                }

                if (backRT != null)
                {
                    backRT.Release();
                    DestroyImmediate(backRT);
                }
            }
        }

        static int BackFaceScore(Color[] front, Color[] back, float farClip)
        {
            if (front == null || back == null)
                return 0;

            int score = 0;
            int count = Mathf.Min(front.Length, back.Length);
            for (int i = 0; i < count; i++)
            {
                float first = front[i].r;
                if (first <= 0f || first >= farClip - 0.05f)
                    continue;

                float second = back[i].r;
                if (second > first + 0.01f && second < farClip - 0.05f)
                    score++;
                else if (second + 0.02f < first)
                    score -= 2;
            }

            return score;
        }

        /// <summary>
        /// Peels the next surface behind <paramref name="reference"/>. The screen orientation of the
        /// reference texture is not known up front, so both are tried and the one that lands behind
        /// the reference wins. The winning render is left in <paramref name="target"/>.
        /// </summary>
        static Color[] CapturePeel(Camera camera, RenderTexture target, Material material, int cullingMask, Renderer skip, Matrix4x4 worldToView, Matrix4x4 viewProj, float farClip, int width, int height, Color[] reference, HashSet<Renderer> exclude)
        {
            Color[] best = null;
            int bestScore = int.MinValue;
            int bestFlip = 0;
            for (int flip = 0; flip < 2; flip++)
            {
                material.SetVector("_RoomWindowScreenSize", new Vector4(width, height, flip, 0f));
                DrawDepth(camera, target, material, cullingMask, skip, worldToView, viewProj, farClip, exclude);
                Texture2D read = ReadTexture(target, width, height, TextureFormat.RGBAFloat, true);
                Color[] pixels = read.GetPixels();
                DestroyImmediate(read);
                int score = BackFaceScore(reference, pixels, farClip);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = pixels;
                    bestFlip = flip;
                }
            }

            if (bestFlip != 1)
            {
                material.SetVector("_RoomWindowScreenSize", new Vector4(width, height, bestFlip, 0f));
                DrawDepth(camera, target, material, cullingMask, skip, worldToView, viewProj, farClip, exclude);
            }

            return best ?? reference;
        }

        static bool IsSurface(float depth, float farClip)
        {
            return depth > 0.001f && depth < farClip - 0.05f;
        }

        /// <summary>
        /// Finds the first camera-facing surface behind every object, works out which renderers make up
        /// that background, then photographs the room with only those renderers visible.
        /// </summary>
        static BackgroundPhotos CaptureBackground(Camera camera, RenderTexture colorRT, RenderTexture frontRT, RenderTexture backRT, Material depthMaterial, Shader albedoShader, int cullingMask, Renderer skip, Matrix4x4 worldToView, Matrix4x4 viewProj, float farClip, int width, int height, Color[] front, Color[] back)
        {
            BackgroundPhotos result = new BackgroundPhotos();
            int count = width * height;
            if (front == null || front.Length != count)
                return result;
            if (back == null || back.Length != count)
                back = front;

            RenderTexture peelRT = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
            {
                antiAliasing = 1,
                name = "RoomWindowBackgroundDepth",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Point
            };
            peelRT.Create();
            Texture2D cursorTex = null;
            List<Renderer> hidden = new List<Renderer>();
            try
            {
                // Front renderer at every pixel, then peel forward-facing surfaces until the renderer
                // changes. A concave mesh (a Suzanne) occludes itself, so the first peel is still
                // that mesh; stopping there put the whole object in the background layer.
                Renderer[] renderers = FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
                Color[] frontIds = RenderIds(camera, frontRT, albedoShader, cullingMask, skip, worldToView, viewProj, width, height, renderers);
                int[] frontIndex = new int[count];
                int[] occludingPixels = new int[renderers.Length];
                for (int i = 0; i < count; i++)
                {
                    int id = Mathf.RoundToInt(frontIds[i].r) - 1;
                    frontIndex[i] = id >= 0 && id < renderers.Length ? id : -1;
                }

                float[] cursor = new float[count];
                bool[] resolved = new bool[count];
                int[] behindIndex = new int[count];
                Color[] cursorColors = new Color[count];
                for (int i = 0; i < count; i++)
                {
                    float start = front[i].r;
                    if (IsSurface(back[i].r, farClip) && back[i].r > start + 0.01f)
                        start = back[i].r;
                    cursor[i] = start;
                    cursorColors[i] = new Color(start, start, start, 1f);
                    behindIndex[i] = -1;
                }

                cursorTex = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true)
                {
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp
                };
                depthMaterial.SetFloat("_RoomWindowCull", 2f);
                depthMaterial.SetFloat("_RoomWindowPeel", 1f);
                for (int peel = 0; peel < 4; peel++)
                {
                    cursorTex.SetPixels(cursorColors);
                    cursorTex.Apply(false, false);
                    depthMaterial.SetTexture("_RoomWindowFrontDepth", cursorTex);
                    Color[] next = CapturePeel(camera, peelRT, depthMaterial, cullingMask, skip, worldToView, viewProj, farClip, width, height, cursorColors, null);
                    Color[] nextIds = RenderIds(camera, peelRT, albedoShader, cullingMask, skip, worldToView, viewProj, width, height, renderers);

                    bool advanced = false;
                    for (int i = 0; i < count; i++)
                    {
                        if (resolved[i] || frontIndex[i] < 0)
                            continue;

                        float depth = next[i].r;
                        if (!IsSurface(depth, farClip) || depth < cursor[i] + 0.01f)
                            continue;

                        int id = Mathf.RoundToInt(nextIds[i].r) - 1;
                        if (id >= 0 && id < renderers.Length && id != frontIndex[i] && depth > front[i].r + 0.05f)
                        {
                            resolved[i] = true;
                            behindIndex[i] = id;
                            occludingPixels[frontIndex[i]]++;
                        }
                        else
                        {
                            cursor[i] = depth;
                            cursorColors[i] = new Color(depth, depth, depth, 1f);
                            advanced = true;
                        }
                    }

                    if (!advanced)
                        break;
                }

                DestroyImmediate(cursorTex);
                cursorTex = null;
                depthMaterial.SetFloat("_RoomWindowCull", 0f);
                depthMaterial.SetFloat("_RoomWindowPeel", 0f);

                // A prop is foreground when its outline in the photo is a depth cliff onto the
                // room behind it. Wall and ceiling panels meet each other at continuous corners
                // rather than depth cliffs, and the back wall is farther than its neighbors, so
                // the room shell stays intact even in a hollow building or a double-walled room.
                int[] frontPixels = new int[renderers.Length];
                for (int i = 0; i < count; i++)
                {
                    if (frontIndex[i] >= 0)
                        frontPixels[frontIndex[i]]++;
                }

                Dictionary<long, int> pairs = new Dictionary<long, int>();
                for (int i = 0; i < count; i++)
                {
                    if (!resolved[i] || behindIndex[i] < 0 || frontPixels[behindIndex[i]] == 0)
                        continue;

                    long key = ((long)frontIndex[i] << 32) | (uint)behindIndex[i];
                    pairs.TryGetValue(key, out int seen);
                    pairs[key] = seen + 1;
                }

                HashSet<long> hides = new HashSet<long>();
                foreach (KeyValuePair<long, int> pair in pairs)
                {
                    if (pair.Value >= 8)
                        hides.Add(pair.Key);
                }

                int[] outline = new int[renderers.Length];
                int[] cliffRinged = new int[renderers.Length];
                int[] borderTouches = new int[renderers.Length];
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int index = y * width + x;
                        int self = frontIndex[index];
                        if (self < 0)
                            continue;

                        float selfDepth = front[index].r;
                        if (!IsSurface(selfDepth, farClip))
                            continue;

                        float jump = Mathf.Max(0.15f, selfDepth * 0.05f);
                        for (int n = 0; n < 4; n++)
                        {
                            int nx = x + (n == 0 ? 1 : (n == 1 ? -1 : 0));
                            int ny = y + (n == 2 ? 1 : (n == 3 ? -1 : 0));
                            if ((uint)nx >= (uint)width || (uint)ny >= (uint)height)
                            {
                                borderTouches[self]++;
                                continue;
                            }

                            int neighborIndex = ny * width + nx;
                            int other = frontIndex[neighborIndex];
                            if (other == self)
                                continue;

                            float otherDepth = front[neighborIndex].r;
                            if (!IsSurface(otherDepth, farClip))
                            {
                                borderTouches[self]++;
                                continue;
                            }

                            outline[self]++;
                            if (other >= 0
                                && otherDepth > selfDepth + jump
                                && hides.Contains(((long)self << 32) | (uint)other))
                                cliffRinged[self]++;
                        }
                    }
                }

                int maxPropPixels = Mathf.Max(64, count / 10);
                HashSet<Renderer> foreground = new HashSet<Renderer>();
                List<string> hiddenNames = new List<string>();
                for (int i = 0; i < renderers.Length; i++)
                {
                    if (renderers[i] == null || occludingPixels[i] < 24 || outline[i] < 8)
                        continue;
                    if (frontPixels[i] > maxPropPixels || borderTouches[i] > outline[i])
                        continue;
                    if (cliffRinged[i] * 2 < outline[i])
                        continue;

                    foreground.Add(renderers[i]);
                    if (hiddenNames.Count < 12)
                        hiddenNames.Add(renderers[i].name);
                }

                if (foreground.Count == 0)
                    return result;

                Debug.Log($"Room Window background removed {foreground.Count} foreground objects: {string.Join(", ", hiddenNames)}{(foreground.Count > hiddenNames.Count ? ", ..." : string.Empty)}.");

                Plane[] planes = GeometryUtility.CalculateFrustumPlanes(camera);
                HashSet<Renderer> exclude = new HashSet<Renderer>();
                foreach (Renderer renderer in renderers)
                {
                    if (!foreground.Contains(renderer))
                        continue;
                    if (!CanDraw(renderer, skip, cullingMask, planes, null))
                        continue;

                    exclude.Add(renderer);
                    if (!renderer.forceRenderingOff)
                    {
                        renderer.forceRenderingOff = true;
                        hidden.Add(renderer);
                    }
                }

                DrawDepth(camera, peelRT, depthMaterial, cullingMask, skip, worldToView, viewProj, farClip, exclude);
                Texture2D depthRead = ReadTexture(peelRT, width, height, TextureFormat.RGBAFloat, true);
                result.Depth = depthRead.GetPixels();
                DestroyImmediate(depthRead);

                camera.targetTexture = colorRT;
                camera.Render();
                Texture2D litRead = ReadTexture(colorRT, width, height, TextureFormat.RGBAHalf, true);
                result.Lit = litRead.GetPixels();
                DestroyImmediate(litRead);

                result.Albedo = RenderAlbedo(camera, peelRT, albedoShader, cullingMask, skip, worldToView, viewProj, width, height, exclude);
                result.RendererCount = foreground.Count;
                result.HiddenCount = exclude.Count;
                result.Valid = result.Lit.Length == count && result.Albedo.Length == count && result.Depth.Length == count;
                return result;
            }
            finally
            {
                for (int i = 0; i < hidden.Count; i++)
                {
                    if (hidden[i] != null)
                        hidden[i].forceRenderingOff = false;
                }

                depthMaterial.SetFloat("_RoomWindowCull", 0f);
                depthMaterial.SetFloat("_RoomWindowPeel", 0f);
                if (cursorTex != null)
                    DestroyImmediate(cursorTex);
                RenderTexture.active = null;
                peelRT.Release();
                DestroyImmediate(peelRT);
            }
        }

        static void DrawDepth(Camera camera, RenderTexture target, Material material, int cullingMask, Renderer skip, Matrix4x4 worldToView, Matrix4x4 viewProj, float farClip, HashSet<Renderer> exclude)
        {
            Plane[] planes = GeometryUtility.CalculateFrustumPlanes(camera);
            Renderer[] renderers = FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            CommandBuffer buffer = CommandBufferPool.Get("Room Window Depth");
            buffer.SetRenderTarget(target);
            buffer.SetViewport(new Rect(0f, 0f, target.width, target.height));
            buffer.ClearRenderTarget(false, true, new Color(farClip, farClip, farClip, farClip));
            buffer.SetGlobalMatrix("_RoomWindowWorldToView", worldToView);
            buffer.SetGlobalMatrix("_RoomWindowViewProj", viewProj);

            MaterialPropertyBlock block = new MaterialPropertyBlock();
            List<Mesh> temporaryMeshes = new List<Mesh>();
            int drawn = 0;
            foreach (Renderer renderer in renderers)
            {
                if (!CanDraw(renderer, skip, cullingMask, planes, exclude))
                    continue;

                Mesh mesh = GetMesh(renderer);
                if (mesh == null)
                    continue;

                Material[] materials = renderer.sharedMaterials;
                int subMeshes = mesh.subMeshCount;
                for (int submesh = 0; submesh < subMeshes; submesh++)
                {
                    Material source = materials != null && submesh < materials.Length ? materials[submesh] : null;
                    if (TrySetupCutout(source, block))
                        DrawMeshWithBlock(buffer, renderer, mesh, material, submesh, 0, block, temporaryMeshes);
                    else
                        buffer.DrawRenderer(renderer, material, submesh, 0);
                }

                drawn++;
            }

            Graphics.ExecuteCommandBuffer(buffer);
            CommandBufferPool.Release(buffer);
            for (int i = 0; i < temporaryMeshes.Count; i++)
                DestroyImmediate(temporaryMeshes[i]);

            if (drawn == 0)
                throw new InvalidOperationException("No mesh renderers were inside the camera frustum.");
        }

        static Color[] RenderAlbedo(Camera camera, Texture depth, Shader shader, int cullingMask, Renderer skip, Matrix4x4 worldToView, Matrix4x4 viewProj, int width, int height, HashSet<Renderer> exclude)
        {
            Color[] upright = DrawAlbedo(camera, depth, shader, cullingMask, skip, worldToView, viewProj, width, height, false, null, false, exclude);
            if (MissingFraction(upright) <= 0.5f)
                return upright;

            Color[] flipped = DrawAlbedo(camera, depth, shader, cullingMask, skip, worldToView, viewProj, width, height, true, null, false, exclude);
            return MissingFraction(flipped) < MissingFraction(upright) ? flipped : upright;
        }

        /// <summary>
        /// Red channel is 1 + the index into <paramref name="renderers"/> of whichever renderer sits at the
        /// given depth, or -1 where nothing does.
        /// </summary>
        static Color[] RenderIds(Camera camera, Texture depth, Shader shader, int cullingMask, Renderer skip, Matrix4x4 worldToView, Matrix4x4 viewProj, int width, int height, Renderer[] renderers)
        {
            Color[] upright = DrawAlbedo(camera, depth, shader, cullingMask, skip, worldToView, viewProj, width, height, false, renderers, true, null);
            Color[] flipped = DrawAlbedo(camera, depth, shader, cullingMask, skip, worldToView, viewProj, width, height, true, renderers, true, null);
            return MissingFraction(flipped) < MissingFraction(upright) ? flipped : upright;
        }

        static Color[] DrawAlbedo(Camera camera, Texture depth, Shader shader, int cullingMask, Renderer skip, Matrix4x4 worldToView, Matrix4x4 viewProj, int width, int height, bool flip, Renderer[] renderers, bool outputIds, HashSet<Renderer> exclude)
        {
            RenderTexture target = new RenderTexture(width, height, 0, outputIds ? RenderTextureFormat.ARGBFloat : RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
            {
                antiAliasing = 1,
                name = outputIds ? "RoomWindowIds" : "RoomWindowAlbedo",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = outputIds ? FilterMode.Point : FilterMode.Bilinear
            };
            target.Create();
            Material material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            Texture2D read = null;
            try
            {
                material.SetVector("_BaseMap_ST", new Vector4(1f, 1f, 0f, 0f));
                material.SetColor("_BaseColor", Color.white);
                material.SetFloat("_HasMap", 0f);
                material.SetFloat("_UseCutout", 0f);
                material.SetFloat("_RoomWindowOutputId", outputIds ? 1f : 0f);

                Plane[] planes = GeometryUtility.CalculateFrustumPlanes(camera);
                if (renderers == null)
                    renderers = FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
                CommandBuffer buffer = CommandBufferPool.Get("Room Window Albedo");
                buffer.SetRenderTarget(target);
                buffer.SetViewport(new Rect(0f, 0f, width, height));
                buffer.ClearRenderTarget(false, true, new Color(-1f, -1f, -1f, 1f));
                buffer.SetGlobalMatrix("_RoomWindowWorldToView", worldToView);
                buffer.SetGlobalMatrix("_RoomWindowViewProj", viewProj);
                buffer.SetGlobalTexture("_RoomWindowEyeDepth", depth);
                buffer.SetGlobalVector("_RoomWindowScreenSize", new Vector4(width, height, flip ? 1f : 0f, 0f));

                MaterialPropertyBlock block = new MaterialPropertyBlock();
                List<Mesh> temporaryMeshes = new List<Mesh>();
                for (int index = 0; index < renderers.Length; index++)
                {
                    Renderer renderer = renderers[index];
                    if (!CanDraw(renderer, skip, cullingMask, planes, exclude))
                        continue;
                    Mesh mesh = GetMesh(renderer);
                    if (mesh == null)
                        continue;

                    Material[] materials = renderer.sharedMaterials;
                    int subMeshes = mesh.subMeshCount;
                    for (int submesh = 0; submesh < subMeshes; submesh++)
                    {
                        Material source = materials != null && submesh < materials.Length ? materials[submesh] : null;
                        SetupAlbedoBlock(source, block);
                        if (outputIds)
                        {
                            block.SetFloat("_RoomWindowId", index + 1);
                            block.SetFloat("_RoomWindowOutputId", 1f);
                        }
                        DrawMeshWithBlock(buffer, renderer, mesh, material, submesh, 0, block, temporaryMeshes);
                    }
                }

                Graphics.ExecuteCommandBuffer(buffer);
                CommandBufferPool.Release(buffer);
                for (int i = 0; i < temporaryMeshes.Count; i++)
                    DestroyImmediate(temporaryMeshes[i]);
                read = ReadTexture(target, width, height, outputIds ? TextureFormat.RGBAFloat : TextureFormat.RGBAHalf, true);
                return read.GetPixels();
            }
            finally
            {
                if (read != null)
                    DestroyImmediate(read);
                DestroyImmediate(material);
                RenderTexture.active = null;
                target.Release();
                DestroyImmediate(target);
            }
        }

        static float MissingFraction(Color[] albedo)
        {
            int missing = 0;
            for (int i = 0; i < albedo.Length; i++)
            {
                if (albedo[i].r < -0.5f)
                    missing++;
            }

            return albedo.Length == 0 ? 1f : (float)missing / albedo.Length;
        }

        static void SetupAlbedoBlock(Material source, MaterialPropertyBlock block)
        {
            block.Clear();
            Color tint = Color.white;
            if (source != null && source.HasProperty("_BaseColor"))
                tint = source.GetColor("_BaseColor");
            else if (source != null && source.HasProperty("_Color"))
                tint = source.GetColor("_Color");

            Texture texture = null;
            Vector4 scaleOffset = new Vector4(1f, 1f, 0f, 0f);
            if (source != null && source.HasProperty("_BaseMap"))
            {
                texture = source.GetTexture("_BaseMap");
                scaleOffset = source.GetVector("_BaseMap_ST");
            }
            else if (source != null && source.HasProperty("_MainTex"))
            {
                texture = source.GetTexture("_MainTex");
                Vector2 scale = source.GetTextureScale("_MainTex");
                Vector2 offset = source.GetTextureOffset("_MainTex");
                scaleOffset = new Vector4(scale.x, scale.y, offset.x, offset.y);
            }

            bool cutout = false;
            float cutoff = 0.5f;
            if (source != null)
            {
                cutout = source.IsKeywordEnabled("_ALPHATEST_ON");
                if (source.HasProperty("_AlphaClip"))
                    cutout |= source.GetFloat("_AlphaClip") > 0.5f;
                if (source.HasProperty("_Cutoff"))
                    cutoff = source.GetFloat("_Cutoff");
                else if (source.HasProperty("_AlphaCutoff"))
                    cutoff = source.GetFloat("_AlphaCutoff");
            }

            block.SetColor("_BaseColor", tint);
            block.SetVector("_BaseMap_ST", scaleOffset);
            block.SetFloat("_HasMap", texture != null ? 1f : 0f);
            block.SetFloat("_UseCutout", cutout && texture != null ? 1f : 0f);
            block.SetFloat("_Cutoff", cutoff);
            if (texture != null)
                block.SetTexture("_BaseMap", texture);
        }

        static void DrawMeshWithBlock(CommandBuffer buffer, Renderer renderer, Mesh mesh, Material material, int submesh, int pass, MaterialPropertyBlock block, List<Mesh> temporaryMeshes)
        {
            Mesh drawMesh = mesh;
            if (renderer is SkinnedMeshRenderer skinned && skinned.sharedMesh != null)
            {
                drawMesh = new Mesh();
                skinned.BakeMesh(drawMesh);
                temporaryMeshes.Add(drawMesh);
            }

            buffer.DrawMesh(drawMesh, renderer.localToWorldMatrix, material, submesh, pass, block);
        }

        static void ShowCapturedSources()
        {
            RoomWindow[] windows = FindObjectsByType<RoomWindow>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < windows.Length; i++)
                windows[i].ShowSources();
        }

        static void HideCapturedSources()
        {
            RoomWindow[] windows = FindObjectsByType<RoomWindow>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < windows.Length; i++)
                windows[i].HideSources();
        }

        static List<Renderer> HideRoomWindows()
        {
            List<Renderer> hidden = new List<Renderer>();
            RoomWindow[] windows = FindObjectsByType<RoomWindow>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            foreach (RoomWindow window in windows)
            {
                Renderer renderer = window.GetComponent<Renderer>();
                if (renderer == null || !renderer.enabled)
                    continue;

                renderer.enabled = false;
                hidden.Add(renderer);
            }

            return hidden;
        }

        static void RestoreRenderers(List<Renderer> renderers)
        {
            for (int i = 0; i < renderers.Count; i++)
            {
                if (renderers[i] != null)
                    renderers[i].enabled = true;
            }
        }

        static bool CanDraw(Renderer renderer, Renderer skip, int cullingMask, Plane[] planes, HashSet<Renderer> exclude)
        {
            if (renderer == null || renderer == skip || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                return false;
            if (exclude != null && exclude.Contains(renderer))
                return false;
            if (renderer.GetComponent<RoomWindow>() != null)
                return false;
            if (skip != null && renderer.transform.IsChildOf(skip.transform))
                return false;
            if (((1 << renderer.gameObject.layer) & cullingMask) == 0)
                return false;
            if (renderer.shadowCastingMode == ShadowCastingMode.ShadowsOnly)
                return false;
            if ((renderer.hideFlags & HideFlags.HideAndDontSave) != 0)
                return false;
            return GeometryUtility.TestPlanesAABB(planes, renderer.bounds);
        }

        static Mesh GetMesh(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer skinned)
                return skinned.sharedMesh;
            if (renderer is MeshRenderer)
            {
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                return filter != null ? filter.sharedMesh : null;
            }

            return null;
        }

        static bool TrySetupCutout(Material source, MaterialPropertyBlock block)
        {
            if (source == null)
                return false;

            bool cutout = source.IsKeywordEnabled("_ALPHATEST_ON");
            if (source.HasProperty("_AlphaClip"))
                cutout |= source.GetFloat("_AlphaClip") > 0.5f;
            if (!cutout)
                return false;

            Texture texture = null;
            Vector4 scaleOffset = new Vector4(1f, 1f, 0f, 0f);
            if (source.HasProperty("_BaseMap"))
            {
                texture = source.GetTexture("_BaseMap");
                scaleOffset = source.GetVector("_BaseMap_ST");
            }
            else if (source.HasProperty("_MainTex"))
            {
                texture = source.GetTexture("_MainTex");
                Vector2 scale = source.GetTextureScale("_MainTex");
                Vector2 offset = source.GetTextureOffset("_MainTex");
                scaleOffset = new Vector4(scale.x, scale.y, offset.x, offset.y);
            }

            if (texture == null)
                return false;

            float cutoff = 0.5f;
            if (source.HasProperty("_Cutoff"))
                cutoff = source.GetFloat("_Cutoff");
            else if (source.HasProperty("_AlphaCutoff"))
                cutoff = source.GetFloat("_AlphaCutoff");

            block.Clear();
            block.SetTexture("_BaseMap", texture);
            block.SetVector("_BaseMap_ST", scaleOffset);
            block.SetFloat("_Cutoff", cutoff);
            block.SetFloat("_UseCutout", 1f);
            return true;
        }

        /// <summary>
        /// Copies the background photo over the outer texels of each foreground object so the
        /// silhouette ring is room, not a contour of the object. Only erodes where the front
        /// depth actually drops onto a farther neighbor, so a wall is never eroded at its seam.
        /// </summary>
        static int DilateOccluderEdges(float[] frontDepth, Color[] frontLit, Color[] frontAlbedo, float[] backDepth, Color[] backLit, Color[] backAlbedo, int width, int height, float farClip)
        {
            int count = frontDepth.Length;
            float[] origDepth = (float[])frontDepth.Clone();
            bool[] occluder = new bool[count];
            for (int i = 0; i < count; i++)
            {
                float jump = Mathf.Max(0.15f, origDepth[i] * 0.05f);
                occluder[i] = IsSurface(origDepth[i], farClip)
                    && IsSurface(backDepth[i], farClip)
                    && backDepth[i] > origDepth[i] + jump;
            }

            const int radius = 2;
            int dilated = 0;
            for (int y = radius; y < height - radius; y++)
            {
                for (int x = radius; x < width - radius; x++)
                {
                    int index = y * width + x;
                    if (!occluder[index])
                        continue;

                    float selfDepth = origDepth[index];
                    float jump = Mathf.Max(0.15f, selfDepth * 0.05f);
                    bool rim = false;
                    for (int oy = -radius; oy <= radius && !rim; oy++)
                    {
                        int sy = y + oy;
                        for (int ox = -radius; ox <= radius; ox++)
                        {
                            int neighbor = sy * width + (x + ox);
                            if (!occluder[neighbor] && origDepth[neighbor] > selfDepth + jump)
                            {
                                rim = true;
                                break;
                            }
                        }
                    }

                    if (!rim)
                        continue;

                    frontDepth[index] = backDepth[index];
                    if (frontLit != null && backLit != null)
                        frontLit[index] = backLit[index];
                    if (frontAlbedo != null && backAlbedo != null && backAlbedo[index].r >= 0f)
                        frontAlbedo[index] = backAlbedo[index];
                    dilated++;
                }
            }

            return dilated;
        }

        static void FillEmptySurfaces(Color[] lit, Color[] albedo, float[] depth, int width, int height, float farClip)
        {
            int count = depth.Length;
            if (count == 0 || lit == null || lit.Length != count || width * height != count)
                return;

            bool Hole(float value)
            {
                return value <= 0.001f || value >= farClip - 0.05f;
            }

            int[] nearest = new int[count];
            int holes = 0;
            for (int i = 0; i < count; i++)
            {
                if (Hole(depth[i]))
                {
                    nearest[i] = -1;
                    holes++;
                }
                else
                {
                    nearest[i] = i;
                }
            }

            if (holes == 0)
                return;

            int[] buffer = new int[count];
            int limit = Mathf.Max(width, height);
            for (int step = Mathf.NextPowerOfTwo(limit) >> 1; step >= 1; step >>= 1)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int best = -1;
                        int bestDist = int.MaxValue;
                        for (int oy = -1; oy <= 1; oy++)
                        {
                            int sy = y + oy * step;
                            if ((uint)sy >= (uint)height)
                                continue;

                            for (int ox = -1; ox <= 1; ox++)
                            {
                                int sx = x + ox * step;
                                if ((uint)sx >= (uint)width)
                                    continue;

                                int id = nearest[sy * width + sx];
                                if (id < 0)
                                    continue;

                                int dx = (id % width) - x;
                                int dy = (id / width) - y;
                                int dist = dx * dx + dy * dy;
                                if (dist < bestDist)
                                {
                                    bestDist = dist;
                                    best = id;
                                }
                            }
                        }

                        buffer[y * width + x] = best;
                    }
                }

                int[] swap = nearest;
                nearest = buffer;
                buffer = swap;
            }

            for (int i = 0; i < count; i++)
            {
                if (!Hole(depth[i]))
                    continue;

                int source = nearest[i];
                if (source < 0 || source == i)
                    continue;

                depth[i] = depth[source];
                lit[i] = lit[source];
                if (albedo != null && i < albedo.Length && source < albedo.Length)
                    albedo[i] = albedo[source];
            }
        }

        static float NearClipAspect(Camera camera)
        {
            if (camera.targetTexture != null)
                return camera.targetTexture.width / (float)Mathf.Max(1, camera.targetTexture.height);
            if (camera.usePhysicalProperties)
                return Mathf.Max(0.01f, camera.aspect);

            Vector2 game = Handles.GetMainGameViewSize();
            if (game.x > 1f && game.y > 1f)
                return game.x / game.y;
            return Mathf.Max(0.01f, camera.aspect);
        }

        static int FitHeight(int width, float aspect)
        {
            return Mathf.Clamp(Mathf.RoundToInt(width / Mathf.Max(0.01f, aspect)), 16, 4096);
        }

        static void FitGrid(int resolution, float aspect, out int resolutionX, out int resolutionY)
        {
            resolution = Mathf.Clamp(resolution, 1, 256);
            aspect = Mathf.Max(0.01f, aspect);
            if (aspect >= 1f)
            {
                resolutionX = resolution;
                resolutionY = Mathf.Clamp(Mathf.RoundToInt(resolution / aspect), 1, 256);
            }
            else
            {
                resolutionY = resolution;
                resolutionX = Mathf.Clamp(Mathf.RoundToInt(resolution * aspect), 1, 256);
            }
        }

        static Matrix4x4 NearClipProjection(Camera camera, float aspect)
        {
            if (camera.usePhysicalProperties || camera.targetTexture != null)
                return camera.projectionMatrix;

            aspect = Mathf.Max(0.01f, aspect);
            if (camera.orthographic)
            {
                float halfHeight = camera.orthographicSize;
                float halfWidth = halfHeight * aspect;
                return Matrix4x4.Ortho(-halfWidth, halfWidth, -halfHeight, halfHeight, camera.nearClipPlane, camera.farClipPlane);
            }

            return Matrix4x4.Perspective(camera.fieldOfView, aspect, camera.nearClipPlane, camera.farClipPlane);
        }

        static RoomWindow FindCapturedWindow(Camera camera, string assetName)
        {
            RoomWindow[] windows = FindObjectsByType<RoomWindow>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            RoomWindow named = null;
            for (int i = 0; i < windows.Length; i++)
            {
                RoomWindow window = windows[i];
                if (window == null)
                    continue;
                if (camera != null && window.CapturedFrom == camera)
                    return window;
                if (window.name == assetName)
                    named = window;
            }

            return named;
        }

        static RoomWindow CreatePlane(Camera camera)
        {
            if (camera == null)
                return null;

            float windowDistance = camera.nearClipPlane;
            if (windowDistance < 0.01f)
            {
                EditorUtility.DisplayDialog("Room Window", "The camera near clip is too close. Set it to at least 0.01.", "OK");
                return null;
            }

            float aspect = NearClipAspect(camera);
            Matrix4x4 projection = NearClipProjection(camera, aspect);
            if (Mathf.Abs(projection.m00) < 1e-6f || Mathf.Abs(projection.m11) < 1e-6f)
            {
                EditorUtility.DisplayDialog("Room Window", "The camera projection is invalid.", "OK");
                return null;
            }

            Shader interiorShader = Shader.Find("RoomWindow/Interior");
            Shader glassShader = Shader.Find("RoomWindow/Glass");
            if (interiorShader == null || glassShader == null)
            {
                EditorUtility.DisplayDialog("Room Window", "The Room Window shaders are missing.", "OK");
                return null;
            }

            string assetName = Sanitize(camera.name) + " Room Window";
            GameObject go = new GameObject(assetName);
            Undo.RegisterCreatedObjectUndo(go, "Add Room Window Plane");
            RoomWindow window = go.AddComponent<RoomWindow>();
            MeshFilter filter = go.AddComponent<MeshFilter>();
            MeshRenderer renderer = go.AddComponent<MeshRenderer>();

            Transform transform = go.transform;
            transform.SetPositionAndRotation(camera.transform.position + camera.transform.forward * windowDistance, camera.transform.rotation);

            PrefabStage stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && stage.IsPartOfPrefabContents(camera.gameObject))
                transform.SetParent(stage.prefabContentsRoot.transform, true);
            else if (camera.gameObject.scene.IsValid())
                SceneManager.MoveGameObjectToScene(go, camera.gameObject.scene);

            transform.localScale = Vector3.one;
            Vector3 lossy = transform.lossyScale;
            if (lossy.x != 0f && lossy.y != 0f && lossy.z != 0f)
                transform.localScale = new Vector3(1f / lossy.x, 1f / lossy.y, 1f / lossy.z);

            bool orthographic = camera.orthographic;
            Vector4 frustum = new Vector4(projection.m00, projection.m11, projection.m02, projection.m12);
            Vector4 orthoTerms = new Vector4(projection.m03, projection.m13, 0f, 0f);
            filter.sharedMesh = BuildPlaneMesh(windowDistance, camera.farClipPlane, frustum, orthoTerms, orthographic);

            Material interior = new Material(interiorShader) { name = assetName + " Interior" };
            interior.SetColor("_BaseColor", Color.white);
            interior.SetVector("_DepthRange", new Vector4(windowDistance, camera.farClipPlane, windowDistance, orthographic ? 1f : 0f));
            interior.SetVector("_Frustum", frustum);
            interior.SetVector("_OrthoTerms", orthoTerms);
            interior.SetFloat("_EdgeThreshold", 0.25f);
            interior.SetFloat("_BackBias", 0.02f);
            interior.SetFloat("_EdgeCutout", 3f);
            interior.SetFloat("_BackgroundCutout", 24f);
            interior.SetFloat("_FlipY", 0f);
            interior.SetFloat("_DebugView", 0f);
            interior.SetFloat("_LightBlend", 0f);

            Material glass = new Material(glassShader) { name = assetName + " Glass" };
            renderer.sharedMaterials = new[] { interior, glass };
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.BlendProbesAndSkybox;
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;

            int textureWidth = 1024;
            int textureHeight = FitHeight(textureWidth, aspect);
            window.SetCaptureSettings(camera, windowDistance, textureWidth, textureHeight, 1, 0.25f, DefaultFolder, ~0, projection, camera.nearClipPlane, camera.farClipPlane);
            Undo.RegisterCreatedObjectUndo(interior, "Add Room Window Plane");
            Undo.RegisterCreatedObjectUndo(glass, "Add Room Window Plane");
            Undo.RegisterCreatedObjectUndo(filter.sharedMesh, "Add Room Window Plane");
            return window;
        }

        static Mesh BuildPlaneMesh(float windowDistance, float farDepth, Vector4 frustum, Vector4 orthoTerms, bool orthographic)
        {
            Mesh mesh = new Mesh { name = "Room Window Plane" };
            Vector3[] vertices = new Vector3[4];
            Vector2[] uvs = new Vector2[4];
            Vector3[] normals = new Vector3[4];
            float[] u = { 0f, 1f, 0f, 1f };
            float[] v = { 0f, 0f, 1f, 1f };
            for (int i = 0; i < 4; i++)
            {
                uvs[i] = new Vector2(u[i], v[i]);
                vertices[i] = RoomWindow.MeshPosition(u[i], v[i], windowDistance, windowDistance, frustum, orthoTerms, orthographic);
                normals[i] = Vector3.back;
            }

            int[] triangles = { 0, 2, 1, 2, 3, 1 };
            Vector3 min = Vector3.one * float.MaxValue;
            Vector3 max = Vector3.one * float.MinValue;
            float[] depths = { windowDistance, Mathf.Max(farDepth, windowDistance + 0.05f) };
            for (int depthIndex = 0; depthIndex < depths.Length; depthIndex++)
            {
                for (int corner = 0; corner < 4; corner++)
                {
                    Vector3 point = RoomWindow.MeshPosition(u[corner], v[corner], depths[depthIndex], windowDistance, frustum, orthoTerms, orthographic);
                    min = Vector3.Min(min, point);
                    max = Vector3.Max(max, point);
                }
            }

            mesh.vertices = vertices;
            mesh.uv = uvs;
            mesh.normals = normals;
            mesh.subMeshCount = 2;
            mesh.SetTriangles(triangles, 0, false);
            mesh.SetTriangles(triangles, 1, false);
            mesh.bounds = new Bounds((min + max) * 0.5f, max - min + Vector3.one * 0.25f);
            return mesh;
        }

        RoomWindow PlaceWindow(string assetName, float windowDistance, Mesh mesh, Material interior, Material glass)
        {
            RoomWindow window = _existing != null ? _existing : FindCapturedWindow(_camera, assetName);
            GameObject go;
            if (window == null)
            {
                go = new GameObject(assetName);
                Undo.RegisterCreatedObjectUndo(go, "Create Room Window");
                window = go.AddComponent<RoomWindow>();
                go.AddComponent<MeshFilter>();
                go.AddComponent<MeshRenderer>();
            }
            else
            {
                go = window.gameObject;
                Undo.RecordObject(go.transform, "Capture Room Window");
                Undo.RecordObject(window, "Capture Room Window");
            }

            Transform transform = go.transform;
            transform.SetPositionAndRotation(_camera.transform.position + _camera.transform.forward * windowDistance, _camera.transform.rotation);

            PrefabStage stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && stage.IsPartOfPrefabContents(_camera.gameObject))
                transform.SetParent(stage.prefabContentsRoot.transform, true);
            else if (_camera.gameObject.scene.IsValid() && go.scene != _camera.gameObject.scene)
                SceneManager.MoveGameObjectToScene(go, _camera.gameObject.scene);

            transform.localScale = Vector3.one;
            Vector3 lossy = transform.lossyScale;
            if (lossy.x != 0f && lossy.y != 0f && lossy.z != 0f)
            {
                transform.localScale = new Vector3(1f / lossy.x, 1f / lossy.y, 1f / lossy.z);
                lossy = transform.lossyScale;
            }

            if (Mathf.Abs(lossy.x - 1f) > 0.02f || Mathf.Abs(lossy.y - 1f) > 0.02f || Mathf.Abs(lossy.z - 1f) > 0.02f)
                Debug.LogWarning("Room Window is under a scaled parent, so the room will stretch. Keep this object at unit scale.", go);

            MeshFilter filter = go.GetComponent<MeshFilter>();
            if (filter == null)
                filter = go.AddComponent<MeshFilter>();
            MeshRenderer renderer = go.GetComponent<MeshRenderer>();
            if (renderer == null)
                renderer = go.AddComponent<MeshRenderer>();

            Undo.RecordObject(filter, "Capture Room Window");
            Undo.RecordObject(renderer, "Capture Room Window");
            filter.sharedMesh = mesh;
            renderer.sharedMaterials = new[] { interior, glass };
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.BlendProbesAndSkybox;
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            renderer.enabled = true;
            return window;
        }

        Mesh SaveMesh(string folder, string assetName, int resolutionX, int resolutionY, float windowDistance, float farDepth, Vector4 frustum, Vector4 orthoTerms, bool orthographic)
        {
            string path = MeshPath(folder, assetName);
            Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            bool created = mesh == null;
            if (created)
            {
                mesh = new Mesh { name = assetName + "_Mesh" };
                AssetDatabase.CreateAsset(mesh, path);
            }

            int sideX = resolutionX + 1;
            int sideY = resolutionY + 1;
            Vector3[] vertices = new Vector3[sideX * sideY];
            Vector3[] normals = new Vector3[sideX * sideY];
            Vector2[] uvs = new Vector2[sideX * sideY];
            for (int y = 0; y <= resolutionY; y++)
            {
                for (int x = 0; x <= resolutionX; x++)
                {
                    int index = y * sideX + x;
                    float u = (float)x / resolutionX;
                    float v = (float)y / resolutionY;
                    uvs[index] = new Vector2(u, v);
                    vertices[index] = RoomWindow.MeshPosition(u, v, windowDistance, windowDistance, frustum, orthoTerms, orthographic);
                    normals[index] = Vector3.back;
                }
            }

            int[] triangles = new int[resolutionX * resolutionY * 6];
            int cursor = 0;
            for (int y = 0; y < resolutionY; y++)
            {
                for (int x = 0; x < resolutionX; x++)
                {
                    int i00 = y * sideX + x;
                    int i10 = i00 + 1;
                    int i01 = (y + 1) * sideX + x;
                    int i11 = i01 + 1;
                    triangles[cursor++] = i00;
                    triangles[cursor++] = i01;
                    triangles[cursor++] = i10;
                    triangles[cursor++] = i01;
                    triangles[cursor++] = i11;
                    triangles[cursor++] = i10;
                }
            }

            int bottomLeft = 0;
            int bottomRight = resolutionX;
            int topLeft = resolutionY * sideX;
            int topRight = sideX * sideY - 1;
            int[] glass = { bottomLeft, topLeft, bottomRight, topLeft, topRight, bottomRight };

            Vector3 min = Vector3.one * float.MaxValue;
            Vector3 max = Vector3.one * float.MinValue;
            float[] depths = { windowDistance, farDepth };
            for (int i = 0; i < depths.Length; i++)
            {
                for (int corner = 0; corner < 4; corner++)
                {
                    float u = (corner & 1) == 0 ? 0f : 1f;
                    float v = (corner & 2) == 0 ? 0f : 1f;
                    Vector3 point = RoomWindow.MeshPosition(u, v, depths[i], windowDistance, frustum, orthoTerms, orthographic);
                    min = Vector3.Min(min, point);
                    max = Vector3.Max(max, point);
                }
            }

            mesh.Clear(false);
            mesh.name = assetName + "_Mesh";
            mesh.indexFormat = vertices.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.vertices = vertices;
            mesh.uv = uvs;
            mesh.normals = normals;
            mesh.subMeshCount = 2;
            mesh.SetTriangles(triangles, 0, false);
            mesh.SetTriangles(glass, 1, false);
            Vector3 size = max - min + Vector3.one * 0.25f;
            mesh.bounds = new Bounds((min + max) * 0.5f, size);
            EditorUtility.SetDirty(mesh);
            return mesh;
        }

        Material SaveInterior(string folder, string assetName, Shader shader, Texture2D color, Texture2D lightB, Texture2D background, Texture2D backgroundLightB, float near, float far, float windowDistance, bool orthographic, Vector4 frustum, Vector4 orthoTerms)
        {
            string path = $"{folder}/{assetName}_Interior.mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            bool created = material == null;
            if (created)
            {
                material = new Material(shader) { name = assetName + "_Interior" };
                AssetDatabase.CreateAsset(material, path);
            }

            material.shader = shader;
            material.SetTexture("_BaseMap", color);
            material.SetTexture("_LightMapB", lightB);
            material.SetTexture("_BackMap", background);
            material.SetTexture("_BackLightMapB", backgroundLightB);
            material.SetFloat("_HasBackground", background != null ? 1f : 0f);
            material.SetVector("_DepthRange", new Vector4(near, far, windowDistance, orthographic ? 1f : 0f));
            material.SetVector("_Frustum", frustum);
            material.SetVector("_OrthoTerms", orthoTerms);
            material.SetFloat("_EdgeThreshold", _edgeThreshold);
            if (created)
            {
                material.SetColor("_BaseColor", Color.white);
                material.SetFloat("_FlipY", 0f);
                material.SetFloat("_BackBias", 0.02f);
                material.SetFloat("_EdgeCutout", 3f);
                material.SetFloat("_BackgroundCutout", 24f);
                material.SetFloat("_DebugView", 0f);
                material.SetFloat("_LightBlend", 0f);
            }

            EditorUtility.SetDirty(material);
            return material;
        }

        static Material SaveGlass(string folder, string assetName, Shader shader)
        {
            string path = $"{folder}/{assetName}_Glass.mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(shader) { name = assetName + "_Glass" };
                AssetDatabase.CreateAsset(material, path);
            }

            material.shader = shader;
            EditorUtility.SetDirty(material);
            return material;
        }

        string MeshPath(string folder, string assetName)
        {
            if (_existing != null)
            {
                MeshFilter filter = _existing.GetComponent<MeshFilter>();
                if (filter != null && filter.sharedMesh != null)
                {
                    string existingPath = AssetDatabase.GetAssetPath(filter.sharedMesh);
                    if (!string.IsNullOrEmpty(existingPath))
                        return existingPath;
                }
            }

            return $"{folder}/{assetName}_Mesh.asset";
        }

        Texture2D SaveLinear(string folder, string assetName, int width, int height, Color[] pixels, string suffix, string property, bool withAlpha = true)
        {
            string path = TexturePath(folder, assetName, suffix, property);
            if (withAlpha)
                WriteLinearExr(path, width, height, pixels);
            else
                WriteRgbExr(path, width, height, pixels);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        Texture2D ExistingOrCopy(string folder, string assetName, string suffix, string property, Texture2D fallback, bool withAlpha = true)
        {
            if (_existing != null)
            {
                Renderer renderer = _existing.GetComponent<Renderer>();
                Material material = renderer != null ? renderer.sharedMaterial : null;
                Texture current = material != null && material.HasProperty(property) ? material.GetTexture(property) : null;
                if (current is Texture2D existing && existing != fallback)
                    return existing;
            }

            string path = $"{folder}/{assetName}{suffix}";
            Texture2D onDisk = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (onDisk != null && onDisk != fallback)
                return onDisk;

            File.Copy(ToAbsolute(AssetDatabase.GetAssetPath(fallback)), ToAbsolute(path), true);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            ConfigureTexture(path, false, true, FilterMode.Bilinear, withAlpha ? TextureImporterFormat.RGBAHalf : TextureImporterFormat.BC6H, withAlpha);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        static void WriteLinearExr(string path, int width, int height, Color[] pixels)
        {
            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true);
            texture.SetPixels(pixels);
            texture.Apply(false, false);
            File.WriteAllBytes(ToAbsolute(path), texture.EncodeToEXR(Texture2D.EXRFlags.OutputAsFloat));
            DestroyImmediate(texture);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            ConfigureTexture(path, false, true, FilterMode.Bilinear, TextureImporterFormat.RGBAHalf, true);
        }

        // Lighting B has no depth, so the file stores RGB only and imports as BC6H.
        static void WriteRgbExr(string path, int width, int height, Color[] pixels)
        {
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                writer.Write(20000630);
                writer.Write(2);
                WriteExrChannels(writer, "B", "G", "R");
                WriteExrAttribute(writer, "compression", "compression", new byte[] { 0 });
                WriteExrBox(writer, "dataWindow", width, height);
                WriteExrBox(writer, "displayWindow", width, height);
                WriteExrAttribute(writer, "lineOrder", "lineOrder", new byte[] { 0 });
                WriteExrAttribute(writer, "pixelAspectRatio", "float", BitConverter.GetBytes(1f));
                WriteExrAttribute(writer, "screenWindowCenter", "v2f", new byte[8]);
                WriteExrAttribute(writer, "screenWindowWidth", "float", BitConverter.GetBytes(1f));
                writer.Write((byte)0);

                long offsetTable = stream.Position;
                writer.Write(new byte[height * 8]);
                long[] offsets = new long[height];
                byte[] blue = new byte[width * 4];
                byte[] green = new byte[width * 4];
                byte[] red = new byte[width * 4];
                for (int y = 0; y < height; y++)
                {
                    offsets[y] = stream.Position;
                    writer.Write(y);
                    writer.Write(width * 12);
                    int row = (height - 1 - y) * width;
                    for (int x = 0; x < width; x++)
                    {
                        Color color = pixels[row + x];
                        int offset = x * 4;
                        Buffer.BlockCopy(BitConverter.GetBytes(color.b), 0, blue, offset, 4);
                        Buffer.BlockCopy(BitConverter.GetBytes(color.g), 0, green, offset, 4);
                        Buffer.BlockCopy(BitConverter.GetBytes(color.r), 0, red, offset, 4);
                    }

                    writer.Write(blue);
                    writer.Write(green);
                    writer.Write(red);
                }

                stream.Position = offsetTable;
                for (int y = 0; y < height; y++)
                    writer.Write(offsets[y]);
                writer.Flush();
                File.WriteAllBytes(ToAbsolute(path), stream.ToArray());
            }

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            ConfigureTexture(path, false, true, FilterMode.Bilinear, TextureImporterFormat.BC6H, false);
        }

        static void WriteExrChannels(BinaryWriter writer, params string[] names)
        {
            using (MemoryStream value = new MemoryStream())
            using (BinaryWriter channel = new BinaryWriter(value))
            {
                foreach (string name in names)
                {
                    channel.Write(System.Text.Encoding.ASCII.GetBytes(name));
                    channel.Write((byte)0);
                    channel.Write(2);
                    channel.Write((byte)0);
                    channel.Write((byte)0);
                    channel.Write((byte)0);
                    channel.Write((byte)0);
                    channel.Write(1);
                    channel.Write(1);
                }

                channel.Write((byte)0);
                channel.Flush();
                WriteExrAttribute(writer, "channels", "chlist", value.ToArray());
            }
        }

        static void WriteExrBox(BinaryWriter writer, string name, int width, int height)
        {
            using (MemoryStream value = new MemoryStream())
            using (BinaryWriter box = new BinaryWriter(value))
            {
                box.Write(0);
                box.Write(0);
                box.Write(width - 1);
                box.Write(height - 1);
                box.Flush();
                WriteExrAttribute(writer, name, "box2i", value.ToArray());
            }
        }

        static void WriteExrAttribute(BinaryWriter writer, string name, string type, byte[] value)
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes(name));
            writer.Write((byte)0);
            writer.Write(System.Text.Encoding.ASCII.GetBytes(type));
            writer.Write((byte)0);
            writer.Write(value.Length);
            writer.Write(value);
        }

        static void PackDepth(Color[] albedo, float[] eyeDepth, float near, float span)
        {
            float inverse = 1f / Mathf.Max(span, 1e-4f);
            int count = Mathf.Min(albedo.Length, eyeDepth.Length);
            for (int i = 0; i < count; i++)
                albedo[i].a = Mathf.Clamp01((eyeDepth[i] - near) * inverse);
        }

        string TexturePath(string folder, string assetName, string suffix, string propertyLookup)
        {
            if (_existing != null)
            {
                Renderer renderer = _existing.GetComponent<Renderer>();
                Material material = renderer != null ? renderer.sharedMaterial : null;
                if (material != null && material.HasProperty(propertyLookup))
                {
                    Texture texture = material.GetTexture(propertyLookup);
                    string existingPath = texture != null ? AssetDatabase.GetAssetPath(texture) : null;
                    if (!string.IsNullOrEmpty(existingPath) && existingPath.EndsWith(suffix.EndsWith(".exr") ? ".exr" : ".png"))
                        return existingPath;
                }
            }

            return $"{folder}/{assetName}{suffix}";
        }

        static void ConfigureTexture(string path, bool srgb, bool mipmaps, FilterMode filter, TextureImporterFormat format, bool keepAlpha)
        {
            TextureImporter importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.sRGBTexture = srgb;
            importer.mipmapEnabled = mipmaps;
            importer.textureCompression = keepAlpha ? TextureImporterCompression.Uncompressed : TextureImporterCompression.Compressed;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = filter;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.textureType = TextureImporterType.Default;
            importer.alphaSource = keepAlpha ? TextureImporterAlphaSource.FromInput : TextureImporterAlphaSource.None;
            importer.alphaIsTransparency = false;
            importer.isReadable = false;

            TextureImporterPlatformSettings platform = importer.GetDefaultPlatformTextureSettings();
            platform.overridden = true;
            platform.format = format;
            platform.textureCompression = importer.textureCompression;
            platform.maxTextureSize = 4096;
            importer.SetPlatformTextureSettings(platform);
            importer.SaveAndReimport();
        }

        static Texture2D ReadTexture(RenderTexture source, int width, int height, TextureFormat format, bool linear)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = source;
            Texture2D texture = new Texture2D(width, height, format, false, linear);
            texture.ReadPixels(new Rect(0f, 0f, width, height), 0, 0);
            texture.Apply(false, false);
            RenderTexture.active = previous;
            return texture;
        }

        static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder))
                return;

            string parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
            string name = Path.GetFileName(folder);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name))
                throw new InvalidOperationException("Output folder is not a valid Assets path.");

            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, name);
        }

        static string ToAbsolute(string assetPath)
        {
            string project = Directory.GetParent(Application.dataPath)?.FullName ?? "";
            return Path.Combine(project, assetPath);
        }

        static string Sanitize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "RoomWindow";

            foreach (char invalid in Path.GetInvalidFileNameChars())
                value = value.Replace(invalid, '_');
            return value.Trim();
        }

        static LayerMask LayerMaskField(string label, LayerMask mask)
        {
            string[] layers = InternalEditorUtility.layers;
            int[] values = new int[layers.Length];
            for (int i = 0; i < layers.Length; i++)
                values[i] = LayerMask.NameToLayer(layers[i]);

            int field = 0;
            for (int i = 0; i < values.Length; i++)
            {
                if ((mask.value & (1 << values[i])) != 0)
                    field |= 1 << i;
            }

            field = EditorGUILayout.MaskField(label, field, layers);
            int result = 0;
            for (int i = 0; i < values.Length; i++)
            {
                if ((field & (1 << i)) != 0)
                    result |= 1 << values[i];
            }

            return result;
        }
    }

    [CustomEditor(typeof(RoomWindow))]
    public class RoomWindowEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            EditorGUILayout.Space(6f);
            if (GUILayout.Button("Open Capture Tool"))
                RoomWindowCaptureWindow.Open((RoomWindow)target);
        }
    }
}
