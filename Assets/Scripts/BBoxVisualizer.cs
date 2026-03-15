using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Visualizes 3D bounding boxes in Quest 3 passthrough AR.
/// Boxes are rendered as wireframes using LineRenderer.
/// Can load boxes from a JSON file or use built-in demo boxes.
/// </summary>
public class BBoxVisualizer : MonoBehaviour
{
    [Header("Bounding Box Source")]
    [Tooltip("JSON file in StreamingAssets with bbox definitions. Leave empty to skip auto-load.")]
    public string bboxJsonFile = "";

    [Tooltip("Show demo boxes on start if no JSON file is set")]
    public bool showDemoOnStart = false;

    [Header("Rendering")]
    public float lineWidth = 0.005f;
    public Material lineMaterial;

    [Header("Label")]
    public bool showLabels = true;
    public float labelScale = 0.002f;
    public Font labelFont;

    Transform _cameraRig;
    readonly List<GameObject> _boxObjects = new();

    [Serializable]
    public class BBox3D
    {
        public string label;
        public float[] center;    // [x, y, z]
        public float[] size;      // [w, h, d]
        public float[] rotation;  // [x, y, z, w] quaternion, optional
        public float[] color;     // [r, g, b] 0-1, optional
        public float score;       // detection confidence 0-1
    }

    [Serializable]
    public class BBoxList
    {
        public BBox3D[] boxes;
    }

    void Start()
    {
        _cameraRig = FindAnyObjectByType<OVRCameraRig>()?.transform;

        var boxes = LoadBoxes();
        if (boxes == null || boxes.Length == 0)
        {
            if (showDemoOnStart)
            {
                Debug.Log("[BBoxVis] No JSON found, using demo boxes");
                boxes = CreateDemoBoxes();
            }
            else
            {
                Debug.Log("[BBoxVis] Waiting for detection results (no auto-load)");
                return;
            }
        }

        ShowBoxes(boxes);
    }

    /// <summary>
    /// Clear all existing boxes and display new ones. Called at runtime by the detection pipeline.
    /// </summary>
    public void ShowBoxes(BBox3D[] boxes)
    {
        ClearBoxes();
        if (boxes == null) return;

        foreach (var box in boxes)
            CreateBoxVisual(box);

        Debug.Log($"[BBoxVis] Showing {boxes.Length} bounding boxes");
    }

    /// <summary>
    /// Remove all currently displayed bounding boxes.
    /// </summary>
    public void ClearBoxes()
    {
        foreach (var go in _boxObjects)
            if (go != null) Destroy(go);
        _boxObjects.Clear();
    }

    BBox3D[] LoadBoxes()
    {
        if (string.IsNullOrEmpty(bboxJsonFile)) return null;

        string path = System.IO.Path.Combine(Application.streamingAssetsPath, bboxJsonFile);

        try
        {
            string json;
            if (path.Contains("://") || path.Contains("jar:"))
            {
                var www = UnityEngine.Networking.UnityWebRequest.Get(path);
                www.SendWebRequest();
                while (!www.isDone) { }
                if (www.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
                    return null;
                json = www.downloadHandler.text;
            }
            else
            {
                if (!System.IO.File.Exists(path)) return null;
                json = System.IO.File.ReadAllText(path);
            }

            return JsonUtility.FromJson<BBoxList>(json).boxes;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[BBoxVis] Failed to load {path}: {e.Message}");
            return null;
        }
    }

    BBox3D[] CreateDemoBoxes()
    {
        // Place boxes relative to headset starting position
        return new[]
        {
            new BBox3D
            {
                label = "Chair",
                center = new[] { 0.5f, 0.0f, 1.5f },
                size = new[] { 0.6f, 0.9f, 0.6f },
                color = new[] { 0f, 1f, 0f }
            },
            new BBox3D
            {
                label = "Table",
                center = new[] { 0f, 0.0f, 2.0f },
                size = new[] { 1.2f, 0.75f, 0.8f },
                color = new[] { 1f, 0.5f, 0f }
            },
            new BBox3D
            {
                label = "Monitor",
                center = new[] { 0f, 0.7f, 2.0f },
                size = new[] { 0.6f, 0.4f, 0.05f },
                color = new[] { 0f, 0.5f, 1f }
            },
            new BBox3D
            {
                label = "Lamp",
                center = new[] { -0.8f, 0.3f, 1.5f },
                size = new[] { 0.25f, 0.6f, 0.25f },
                color = new[] { 1f, 1f, 0f }
            },
            new BBox3D
            {
                label = "Backpack",
                center = new[] { 1.0f, -0.2f, 1.0f },
                size = new[] { 0.35f, 0.5f, 0.2f },
                color = new[] { 1f, 0f, 0.5f }
            },
        };
    }

    void CreateBoxVisual(BBox3D box)
    {
        var go = new GameObject($"BBox_{box.label}");
        go.transform.SetParent(transform, true);
        _boxObjects.Add(go);

        Vector3 center = new(box.center[0], box.center[1], box.center[2]);
        Vector3 size = new(box.size[0], box.size[1], box.size[2]);
        Color color = (box.color != null && box.color.Length >= 3)
            ? new Color(box.color[0], box.color[1], box.color[2])
            : Color.green;
        Quaternion rot = (box.rotation != null && box.rotation.Length >= 4)
            ? new Quaternion(box.rotation[0], box.rotation[1], box.rotation[2], box.rotation[3])
            : Quaternion.identity;

        go.transform.position = center;
        go.transform.rotation = rot * Quaternion.Euler(0, 90, 0);

        CreateWireframeBox(go, size, color);

        if (showLabels)
        {
            string labelText = box.score > 0 ? $"{box.label} ({box.score:F2})" : box.label;
            CreateLabel(go, labelText, size, color);
        }
    }

    void CreateWireframeBox(GameObject parent, Vector3 size, Color color)
    {
        Vector3 half = size * 0.5f;

        // 8 corners of the box
        Vector3[] corners = new Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            corners[i] = new Vector3(
                (i & 1) == 0 ? -half.x : half.x,
                (i & 2) == 0 ? -half.y : half.y,
                (i & 4) == 0 ? -half.z : half.z
            );
        }

        // 12 edges: pairs of corner indices
        int[,] edges =
        {
            {0,1}, {2,3}, {4,5}, {6,7},  // x-axis edges
            {0,2}, {1,3}, {4,6}, {5,7},  // y-axis edges
            {0,4}, {1,5}, {2,6}, {3,7},  // z-axis edges
        };

        Material mat = lineMaterial;
        if (mat == null)
        {
            mat = new Material(Shader.Find("Sprites/Default"));
        }

        for (int i = 0; i < 12; i++)
        {
            var edgeGo = new GameObject($"Edge_{i}");
            edgeGo.transform.SetParent(parent.transform, false);

            var lr = edgeGo.AddComponent<LineRenderer>();
            lr.useWorldSpace = false;
            lr.positionCount = 2;
            lr.SetPosition(0, corners[edges[i, 0]]);
            lr.SetPosition(1, corners[edges[i, 1]]);
            lr.startWidth = lineWidth;
            lr.endWidth = lineWidth;
            lr.material = mat;
            lr.startColor = color;
            lr.endColor = color;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
        }
    }

    void CreateLabel(GameObject parent, string text, Vector3 boxSize, Color color)
    {
        var labelGo = new GameObject("Label");
        labelGo.transform.SetParent(parent.transform, false);
        labelGo.transform.localPosition = new Vector3(0, boxSize.y * 0.5f + 0.05f, 0);

        var textMesh = labelGo.AddComponent<TextMesh>();
        textMesh.text = text;
        textMesh.characterSize = labelScale;
        textMesh.fontSize = 100;
        textMesh.anchor = TextAnchor.LowerCenter;
        textMesh.alignment = TextAlignment.Center;
        textMesh.color = color;
        if (labelFont != null) textMesh.font = labelFont;

        var billboard = labelGo.AddComponent<BBoxLabelBillboard>();
        billboard.cam = _cameraRig;
    }

    void OnDestroy()
    {
        ClearBoxes();
    }
}

/// <summary>
/// Makes the label always face the camera.
/// </summary>
public class BBoxLabelBillboard : MonoBehaviour
{
    public Transform cam;

    void LateUpdate()
    {
        if (cam == null) return;
        transform.rotation = Quaternion.LookRotation(
            transform.position - cam.position, Vector3.up
        );
    }
}
