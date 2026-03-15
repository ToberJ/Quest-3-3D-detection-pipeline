using System;
using System.Collections;
using System.IO;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Android;
using UnityEngine.Rendering;
using UnityEngine.UI;
using Meta.XR;
using Meta.XR.EnvironmentDepth;

public class SnapshotCapture : MonoBehaviour
{
    [Header("Camera")]
    [SerializeField] private PassthroughCameraAccess cameraAccess;

    [Header("Depth")]
    [SerializeField] private EnvironmentDepthManager depthManager;

    [Header("Detection Pipeline")]
    [SerializeField] private DetectionClient detectionClient;
    [SerializeField] private BBoxVisualizer bboxVisualizer;

    [Header("Capture Settings")]
    public OVRInput.Button captureButton = OVRInput.Button.PrimaryIndexTrigger;
    public OVRInput.Controller controller = OVRInput.Controller.RTouch;
    public float captureCooldown = 0.5f;

    [Header("Feedback")]
    public AudioClip shutterSound;

    const string CameraPermission = "horizonos.permission.HEADSET_CAMERA";
    const string ScenePermission = "com.oculus.permission.USE_SCENE";
    const int DepthOutputSize = 320;

    static readonly int DepthTextureID = Shader.PropertyToID("_EnvironmentDepthTexture");
    static readonly int ZBufferParamsID = Shader.PropertyToID("_EnvironmentDepthZBufferParams");
    static readonly int ReprojMatricesID = Shader.PropertyToID("_EnvironmentDepthReprojectionMatrices");
    static readonly int DepthTextureSizeID = Shader.PropertyToID("_EnvironmentDepthTextureSize");
    static readonly int CopiedDepthTextureID = Shader.PropertyToID("_CopiedDepthTexture");

    int _captureCount;
    float _lastCaptureTime;
    string _saveDir;
    AudioSource _audioSource;
    ComputeShader _depthCopyShader;
    ComputeBuffer _depthCopyBuffer;

    // HUD
    Canvas _hudCanvas;
    Text _hudText;
    Image _flashImage;
    float _flashAlpha;
    string _lastStatus = "Requesting permissions...";

    void Start()
    {
        _saveDir = Application.persistentDataPath;
        Debug.Log($"[SnapshotCapture] Save directory: {_saveDir}");

        if (cameraAccess == null)
        {
            cameraAccess = FindAnyObjectByType<PassthroughCameraAccess>();
            if (cameraAccess == null)
                Debug.LogError("[SnapshotCapture] PassthroughCameraAccess not found!");
        }

        if (depthManager == null)
        {
            depthManager = FindAnyObjectByType<EnvironmentDepthManager>();
            if (depthManager == null)
                Debug.LogWarning("[SnapshotCapture] EnvironmentDepthManager not found - depth will not be captured");
            else
                Debug.Log("[SnapshotCapture] Found EnvironmentDepthManager");
        }

        if (shutterSound != null)
        {
            _audioSource = gameObject.AddComponent<AudioSource>();
            _audioSource.playOnAwake = false;
        }

        _depthCopyShader = Resources.Load<ComputeShader>("CopyDepthTextureIntoNativeArray");
        if (_depthCopyShader == null)
            Debug.LogWarning("[SnapshotCapture] Compute shader 'CopyDepthTextureIntoNativeArray' not found - depth readback will fail");
        else
            Debug.Log("[SnapshotCapture] Depth compute shader loaded OK");

        if (detectionClient == null)
            detectionClient = FindAnyObjectByType<DetectionClient>();
        if (detectionClient == null)
        {
            detectionClient = gameObject.AddComponent<DetectionClient>();
            Debug.Log("[SnapshotCapture] Auto-created DetectionClient on this GameObject");
        }

        if (bboxVisualizer == null)
            bboxVisualizer = FindAnyObjectByType<BBoxVisualizer>();
        if (bboxVisualizer == null)
        {
            bboxVisualizer = new GameObject("BBoxVisualizer").AddComponent<BBoxVisualizer>();
            Debug.Log("[SnapshotCapture] Auto-created BBoxVisualizer");
        }

        int existing = Directory.GetFiles(_saveDir, "capture_*.png").Length;
        _captureCount = existing;

        CreateHUD();
        RequestPermissions();
        UpdateHUD();
    }

    void RequestPermissions()
    {
        bool needCamera = !Permission.HasUserAuthorizedPermission(CameraPermission);
        bool needScene = !Permission.HasUserAuthorizedPermission(ScenePermission);

        if (!needCamera && !needScene)
        {
            Debug.Log("[SnapshotCapture] All permissions already granted");
            _lastStatus = "Ready - Press right trigger to capture";
            return;
        }

        var callbacks = new PermissionCallbacks();
        callbacks.PermissionGranted += (perm) =>
        {
            Debug.Log($"[SnapshotCapture] Permission granted: {perm}");
            bool camOk = Permission.HasUserAuthorizedPermission(CameraPermission);
            bool sceneOk = Permission.HasUserAuthorizedPermission(ScenePermission);
            if (camOk && sceneOk)
                _lastStatus = "Ready - Press right trigger to capture";
            else if (camOk)
                _lastStatus = "Camera OK, waiting for Scene permission...";
            else
                _lastStatus = "Scene OK, waiting for Camera permission...";
            UpdateHUD();
        };
        callbacks.PermissionDenied += (perm) =>
        {
            Debug.LogError($"[SnapshotCapture] Permission denied: {perm}");
            _lastStatus = $"ERROR: {perm} denied!\nGo to Settings > Apps to grant it.";
            UpdateHUD();
        };
        callbacks.PermissionDeniedAndDontAskAgain += (perm) =>
        {
            Debug.LogError($"[SnapshotCapture] Permission permanently denied: {perm}");
            _lastStatus = $"ERROR: {perm} permanently denied!\nGo to Settings > Apps to grant it.";
            UpdateHUD();
        };

        if (needCamera)
        {
            Debug.Log("[SnapshotCapture] Requesting HEADSET_CAMERA permission...");
            Permission.RequestUserPermission(CameraPermission, callbacks);
        }
        if (needScene)
        {
            Debug.Log("[SnapshotCapture] Requesting USE_SCENE permission...");
            Permission.RequestUserPermission(ScenePermission, callbacks);
        }
    }

    bool IsDepthReady()
    {
        return depthManager != null && depthManager.enabled && depthManager.IsDepthAvailable;
    }

    void Update()
    {
        if (_flashAlpha > 0)
        {
            _flashAlpha -= Time.deltaTime * 4f;
            if (_flashAlpha < 0) _flashAlpha = 0;
            _flashImage.color = new Color(1, 1, 1, _flashAlpha);
        }

        if (OVRInput.GetDown(captureButton, controller))
        {
            if (Time.time - _lastCaptureTime < captureCooldown)
                return;
            if (detectionClient != null && detectionClient.IsDetecting)
                return;
            _lastCaptureTime = Time.time;
            Capture();
        }
    }

    void Capture()
    {
        if (cameraAccess == null)
        {
            _lastStatus = "ERROR: No camera access";
            UpdateHUD();
            return;
        }

        bool hasPerm = Permission.HasUserAuthorizedPermission(CameraPermission);
        Texture tex = cameraAccess.GetTexture();
        if (tex == null)
        {
            string reason = !hasPerm ? "Permission not granted" :
                            !cameraAccess.enabled ? "Component disabled" :
                            !cameraAccess.IsPlaying ? "Camera starting up..." :
                            "Texture is null";
            _lastStatus = $"WAITING: Camera not ready\n  Reason: {reason}\n  Permission: {(hasPerm ? "YES" : "NO")}\n  IsPlaying: {cameraAccess.IsPlaying}";
            Debug.LogWarning($"[SnapshotCapture] {_lastStatus}");
            UpdateHUD();
            return;
        }

        long timestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string baseName = $"capture_{_captureCount:D3}";

        // --- Save RGB ---
        RenderTexture rt = RenderTexture.GetTemporary(tex.width, tex.height, 0);
        Graphics.Blit(tex, rt);
        Texture2D tex2d = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false);
        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = rt;
        tex2d.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
        tex2d.Apply();
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);

        byte[] png = tex2d.EncodeToPNG();
        Destroy(tex2d);

        string pngPath = Path.Combine(_saveDir, $"{baseName}.png");
        File.WriteAllBytes(pngPath, png);

        // --- Save Depth ---
        bool depthSaved = false;
        int depthW = 0, depthH = 0;
        Matrix4x4 depthReprojLeft = Matrix4x4.identity;
        Vector4 depthZParams = Vector4.zero;

        if (IsDepthReady())
        {
            var depthTex = Shader.GetGlobalTexture(DepthTextureID) as RenderTexture;
            if (depthTex != null && depthTex.IsCreated())
            {
                depthZParams = Shader.GetGlobalVector(ZBufferParamsID);
                var reprojMatrices = Shader.GetGlobalMatrixArray(ReprojMatricesID);
                if (reprojMatrices != null && reprojMatrices.Length > 0)
                    depthReprojLeft = reprojMatrices[0];

                depthSaved = SaveDepthFromRT(depthTex, depthZParams,
                    Path.Combine(_saveDir, $"{baseName}_depth.png"));

                depthW = DepthOutputSize;
                depthH = DepthOutputSize;

                Debug.Log($"[SnapshotCapture] Depth: nativeTex={depthTex.width}x{depthTex.height} " +
                          $"dim={depthTex.dimension} vol={depthTex.volumeDepth} " +
                          $"fmt={depthTex.format} zParams={depthZParams} saved={depthSaved}");
            }
            else
            {
                Debug.LogWarning("[SnapshotCapture] Depth available but texture is null/not created");
            }
        }
        else
        {
            Debug.Log($"[SnapshotCapture] Depth not ready: manager={depthManager != null} enabled={depthManager?.enabled} available={depthManager?.IsDepthAvailable}");
        }

        // --- Gather camera data ---
        var intrinsics = cameraAccess.Intrinsics;
        Pose cameraPose = cameraAccess.GetCameraPose();

        Vector3 hmdPos = Vector3.zero;
        Quaternion hmdRot = Quaternion.identity;
        Matrix4x4 leftEyeView = Matrix4x4.identity;
        Matrix4x4 leftEyeProj = Matrix4x4.identity;
        Vector3 leftEyePos = Vector3.zero;
        Quaternion leftEyeRot = Quaternion.identity;

        var cameraRig = FindAnyObjectByType<OVRCameraRig>();
        if (cameraRig != null)
        {
            hmdPos = cameraRig.centerEyeAnchor.position;
            hmdRot = cameraRig.centerEyeAnchor.rotation;
            leftEyePos = cameraRig.leftEyeAnchor.position;
            leftEyeRot = cameraRig.leftEyeAnchor.rotation;

            Camera mainCam = cameraRig.centerEyeAnchor.GetComponent<Camera>();
            if (mainCam != null)
            {
                leftEyeView = mainCam.GetStereoViewMatrix(Camera.StereoscopicEye.Left);
                leftEyeProj = mainCam.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left);
            }
        }

        Matrix4x4 camToWorld = Matrix4x4.TRS(cameraPose.position, cameraPose.rotation, Vector3.one);

        string json = BuildJson(
            _captureCount, timestampMs, baseName,
            tex.width, tex.height,
            intrinsics, cameraPose, camToWorld,
            hmdPos, hmdRot,
            leftEyePos, leftEyeRot, leftEyeView, leftEyeProj,
            depthSaved, depthW, depthH, depthReprojLeft, depthZParams
        );

        string jsonPath = Path.Combine(_saveDir, $"{baseName}.json");
        File.WriteAllText(jsonPath, json);

        _captureCount++;

        _flashAlpha = 0.6f;

        if (_audioSource != null && shutterSound != null)
            _audioSource.PlayOneShot(shutterSound);

        Debug.Log($"[SnapshotCapture] Captured #{_captureCount - 1}: {pngPath} depth={depthSaved}");

        if (detectionClient != null && bboxVisualizer != null)
        {
            StartCoroutine(RunDetectionPipeline(
                png, intrinsics.FocalLength.x, intrinsics.FocalLength.y,
                intrinsics.PrincipalPoint.x, intrinsics.PrincipalPoint.y,
                camToWorld, _captureCount - 1));
        }
        else
        {
            string depthInfo = depthSaved ? $"\n  Depth: {depthW}x{depthH}" : "\n  Depth: not available";
            _lastStatus = $"CAPTURED #{_captureCount - 1}\n" +
                          $"  File: {baseName}.png\n" +
                          $"  Size: {tex.width}x{tex.height}{depthInfo}\n" +
                          $"  Cam pos: ({cameraPose.position.x:F2}, {cameraPose.position.y:F2}, {cameraPose.position.z:F2})\n" +
                          $"  (No detection client - save only mode)";
            UpdateHUD();
        }
    }

    IEnumerator RunDetectionPipeline(byte[] pngBytes, float fx, float fy, float cx, float cy,
                                     Matrix4x4 camToWorld, int captureId)
    {
        _lastStatus = $"Captured #{captureId}\nDetecting objects...";
        UpdateHUD();

        bboxVisualizer.ClearBoxes();

        yield return detectionClient.Detect(pngBytes, fx, fy, cx, cy, camToWorld);

        if (detectionClient.LastError != null)
        {
            _lastStatus = $"Captured #{captureId}\nDetection FAILED:\n  {detectionClient.LastError}";
        }
        else
        {
            var boxes = detectionClient.LastResult;
            if (boxes.Length == 0)
            {
                _lastStatus = $"Captured #{captureId}\nNo objects detected\n  (try different angle or prompt)";
            }
            else
            {
                bboxVisualizer.ShowBoxes(boxes);
                string labels = "";
                foreach (var b in boxes)
                    labels += $"\n  {b.label} ({b.score:F2})";
                _lastStatus = $"Captured #{captureId}\nFound {boxes.Length} objects:{labels}";
            }
        }
        UpdateHUD();
    }

    bool SaveDepthFromRT(RenderTexture depthRT, Vector4 zParams, string path)
    {
        try
        {
            if (_depthCopyShader == null)
            {
                Debug.LogError("[SnapshotCapture] Depth compute shader not loaded");
                return false;
            }

            int numPixels = DepthOutputSize * DepthOutputSize;
            int bufferSize = numPixels * 2; // left + right eyes

            if (_depthCopyBuffer == null || _depthCopyBuffer.count != bufferSize)
            {
                _depthCopyBuffer?.Dispose();
                _depthCopyBuffer = new ComputeBuffer(bufferSize, sizeof(float));
            }

            int kernel = _depthCopyShader.FindKernel("CopyDepth");
            _depthCopyShader.SetTexture(kernel, DepthTextureID, depthRT);
            _depthCopyShader.SetFloat(DepthTextureSizeID, depthRT.width);
            _depthCopyShader.SetVector(ZBufferParamsID, zParams);
            _depthCopyShader.SetBuffer(kernel, CopiedDepthTextureID, _depthCopyBuffer);
            _depthCopyShader.Dispatch(kernel, 1, 1, 1);

            var request = AsyncGPUReadback.Request(_depthCopyBuffer);
            request.WaitForCompletion();

            if (request.hasError)
            {
                Debug.LogError("[SnapshotCapture] Depth GPU readback failed");
                return false;
            }

            var data = request.GetData<float>();

            var outData = new byte[numPixels * 2];
            int nonZero = 0;
            float minD = float.MaxValue, maxD = 0;

            for (int i = 0; i < numPixels; i++)
            {
                float linearMeters = data[i];
                if (linearMeters > 0 && linearMeters < 65.535f)
                {
                    nonZero++;
                    if (linearMeters < minD) minD = linearMeters;
                    if (linearMeters > maxD) maxD = linearMeters;
                }
                if (linearMeters < 0) linearMeters = 0;
                ushort mm = (ushort)Mathf.Clamp(linearMeters * 1000f, 0, 65535);
                outData[i * 2] = (byte)(mm & 0xFF);
                outData[i * 2 + 1] = (byte)(mm >> 8);
            }

            Debug.Log($"[SnapshotCapture] Depth readback: {DepthOutputSize}x{DepthOutputSize}, " +
                      $"valid={nonZero}/{numPixels}, range={minD:F3}-{maxD:F3}m");

            var depthOut = new Texture2D(DepthOutputSize, DepthOutputSize, TextureFormat.R16, false);
            depthOut.LoadRawTextureData(outData);
            depthOut.Apply();
            byte[] pngBytes = depthOut.EncodeToPNG();
            Destroy(depthOut);

            File.WriteAllBytes(path, pngBytes);
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError($"[SnapshotCapture] Failed to save depth: {e.Message}\n{e.StackTrace}");
            return false;
        }
    }

    void OnDestroy()
    {
        _depthCopyBuffer?.Dispose();
    }

    void CreateHUD()
    {
        var canvasGo = new GameObject("SnapshotHUD");
        canvasGo.transform.SetParent(transform);
        _hudCanvas = canvasGo.AddComponent<Canvas>();
        _hudCanvas.renderMode = RenderMode.WorldSpace;

        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.dynamicPixelsPerUnit = 10;

        canvasGo.AddComponent<GraphicRaycaster>();

        RectTransform canvasRect = _hudCanvas.GetComponent<RectTransform>();
        canvasRect.sizeDelta = new Vector2(500, 250);
        canvasRect.localScale = Vector3.one * 0.001f;

        var panelGo = new GameObject("Panel");
        panelGo.transform.SetParent(canvasGo.transform, false);
        var panelImg = panelGo.AddComponent<Image>();
        panelImg.color = new Color(0, 0, 0, 0.7f);
        var panelRect = panelGo.GetComponent<RectTransform>();
        panelRect.anchorMin = Vector2.zero;
        panelRect.anchorMax = Vector2.one;
        panelRect.offsetMin = Vector2.zero;
        panelRect.offsetMax = Vector2.zero;

        var textGo = new GameObject("Text");
        textGo.transform.SetParent(panelGo.transform, false);
        _hudText = textGo.AddComponent<Text>();
        _hudText.font = Font.CreateDynamicFontFromOSFont("Arial", 14);
        _hudText.fontSize = 22;
        _hudText.color = Color.white;
        _hudText.alignment = TextAnchor.UpperLeft;
        _hudText.horizontalOverflow = HorizontalWrapMode.Wrap;
        _hudText.verticalOverflow = VerticalWrapMode.Truncate;

        var textRect = textGo.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(10, 10);
        textRect.offsetMax = new Vector2(-10, -10);

        var flashCanvasGo = new GameObject("FlashCanvas");
        flashCanvasGo.transform.SetParent(transform);
        var flashCanvas = flashCanvasGo.AddComponent<Canvas>();
        flashCanvas.renderMode = RenderMode.WorldSpace;
        var flashRect = flashCanvas.GetComponent<RectTransform>();
        flashRect.sizeDelta = new Vector2(2000, 2000);
        flashRect.localScale = Vector3.one * 0.001f;

        var flashGo = new GameObject("Flash");
        flashGo.transform.SetParent(flashCanvasGo.transform, false);
        _flashImage = flashGo.AddComponent<Image>();
        _flashImage.color = new Color(1, 1, 1, 0);
        var fRect = flashGo.GetComponent<RectTransform>();
        fRect.anchorMin = Vector2.zero;
        fRect.anchorMax = Vector2.one;
        fRect.offsetMin = Vector2.zero;
        fRect.offsetMax = Vector2.zero;
    }

    void UpdateHUD()
    {
        if (_hudText == null) return;
        string depthStatus = IsDepthReady() ? "OK" : (depthManager != null ? "waiting..." : "N/A");
        _hudText.text = $"[Snapshot Capture]\n" +
                        $"Total: {_captureCount} captures\n" +
                        $"Depth: {depthStatus}\n" +
                        $"Trigger: Right Index\n" +
                        $"---\n" +
                        $"{_lastStatus}";
    }

    void LateUpdate()
    {
        if (_hudCanvas == null) return;

        var cameraRig = FindAnyObjectByType<OVRCameraRig>();
        if (cameraRig != null)
        {
            Transform head = cameraRig.centerEyeAnchor;
            Vector3 forward = head.forward;
            forward.y = 0;
            forward.Normalize();

            _hudCanvas.transform.position = head.position + forward * 1.5f + Vector3.down * 0.3f;
            _hudCanvas.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);

            if (_flashImage != null)
            {
                _flashImage.transform.root.position = head.position + head.forward * 0.5f;
                _flashImage.transform.root.rotation = head.rotation;
            }
        }
    }

    string BuildJson(
        int captureId, long timestampMs, string baseName,
        int width, int height,
        PassthroughCameraAccess.CameraIntrinsics intrinsics,
        Pose cameraPose, Matrix4x4 camToWorld,
        Vector3 hmdPos, Quaternion hmdRot,
        Vector3 leftEyePos, Quaternion leftEyeRot,
        Matrix4x4 leftEyeView, Matrix4x4 leftEyeProj,
        bool hasDepth, int depthW, int depthH,
        Matrix4x4 depthReprojLeft, Vector4 depthZParams)
    {
        float fx = intrinsics.FocalLength.x;
        float fy = intrinsics.FocalLength.y;
        float cx = intrinsics.PrincipalPoint.x;
        float cy = intrinsics.PrincipalPoint.y;

        string m = FormatMatrix4x4(camToWorld);

        string depthBlock = "";
        if (hasDepth)
        {
            string reprojMat = FormatMatrix4x4(depthReprojLeft);
            string viewMat = FormatMatrix4x4(leftEyeView);
            string projMat = FormatMatrix4x4(leftEyeProj);
            depthBlock = $@",
  ""depth"": {{
    ""file"": ""{baseName}_depth.png"",
    ""size"": [{depthW}, {depthH}],
    ""encoding"": ""uint16_millimeters"",
    ""max_range_mm"": 65535,
    ""z_buffer_params"": [{depthZParams.x:G}, {depthZParams.y:G}, {depthZParams.z:G}, {depthZParams.w:G}],
    ""reprojection_matrix_left_eye"": {reprojMat}
  }},
  ""left_eye"": {{
    ""position"": [{leftEyePos.x:G}, {leftEyePos.y:G}, {leftEyePos.z:G}],
    ""rotation_quat_xyzw"": [{leftEyeRot.x:G}, {leftEyeRot.y:G}, {leftEyeRot.z:G}, {leftEyeRot.w:G}],
    ""view_matrix"": {viewMat},
    ""projection_matrix"": {projMat}
  }}";
        }

        return $@"{{
  ""capture_id"": {captureId},
  ""timestamp_utc"": ""{DateTime.UtcNow:O}"",
  ""timestamp_ms"": {timestampMs},
  ""image_file"": ""{baseName}.png"",
  ""image_size"": [{width}, {height}],
  ""camera_side"": ""left"",
  ""intrinsic"": {{
    ""fx"": {fx:G},
    ""fy"": {fy:G},
    ""cx"": {cx:G},
    ""cy"": {cy:G},
    ""K"": [
      [{fx:G}, 0, {cx:G}],
      [0, {fy:G}, {cy:G}],
      [0, 0, 1]
    ]
  }},
  ""camera_to_world"": {{
    ""position"": [{cameraPose.position.x:G}, {cameraPose.position.y:G}, {cameraPose.position.z:G}],
    ""rotation_quat_xyzw"": [{cameraPose.rotation.x:G}, {cameraPose.rotation.y:G}, {cameraPose.rotation.z:G}, {cameraPose.rotation.w:G}],
    ""matrix_4x4"": {m}
  }},
  ""hmd_to_world"": {{
    ""position"": [{hmdPos.x:G}, {hmdPos.y:G}, {hmdPos.z:G}],
    ""rotation_quat_xyzw"": [{hmdRot.x:G}, {hmdRot.y:G}, {hmdRot.z:G}, {hmdRot.w:G}]
  }},
  ""coordinate_system"": {{
    ""origin"": ""guardian_floor_center"",
    ""up"": ""+Y"",
    ""right"": ""+X"",
    ""forward"": ""+Z"",
    ""handedness"": ""right"",
    ""units"": ""meters""
  }}{depthBlock}
}}";
    }

    string FormatMatrix4x4(Matrix4x4 m)
    {
        return $@"[
      [{m.m00:G}, {m.m01:G}, {m.m02:G}, {m.m03:G}],
      [{m.m10:G}, {m.m11:G}, {m.m12:G}, {m.m13:G}],
      [{m.m20:G}, {m.m21:G}, {m.m22:G}, {m.m23:G}],
      [{m.m30:G}, {m.m31:G}, {m.m32:G}, {m.m33:G}]
    ]";
    }
}
