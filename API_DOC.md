# SAM3_3D - 3D Object Detection API

Monocular 3D object detection API powered by SAM3_3D. Given an RGB image with camera parameters, returns 3D bounding boxes in world coordinates.

Designed for Meta Quest passthrough AR but works with any monocular RGB input.

## Base URL

```
https://diploidic-describably-anabelle.ngrok-free.dev
```

> All requests must include the header `ngrok-skip-browser-warning: true`

## Endpoints

### Health Check

```
GET /health
```

**Response:**
```json
{"status": "ok", "model_loaded": true}
```

---

### 3D Detection (multipart form)

```
POST /detect3d
```

Detects objects in an RGB image and returns 3D bounding boxes in world coordinates.

#### Request

Content-Type: `multipart/form-data`

| Field      | Type           | Required | Description                              |
|------------|----------------|----------|------------------------------------------|
| `image`    | File (PNG/JPG) | Yes      | RGB image                                |
| `metadata` | String (JSON)  | Yes      | Camera parameters + detection settings   |

**`metadata` JSON fields:**

| Field                           | Type        | Required | Description                                      |
|---------------------------------|-------------|----------|--------------------------------------------------|
| `intrinsic.K`                   | float[3][3] | Yes      | 3x3 camera intrinsic matrix                      |
| `camera_to_world.matrix_4x4`   | float[4][4] | Yes      | 4x4 camera-to-world extrinsic matrix             |
| `text_prompt`                   | string      | No       | Object categories, dot-separated (default: `"chair"`) |
| `score_threshold`               | float       | No       | Confidence threshold 0-1 (default: `0.3`)        |

#### Example (curl)

```bash
curl -X POST https://diploidic-describably-anabelle.ngrok-free.dev/detect3d \
  -H "ngrok-skip-browser-warning: true" \
  -F "image=@capture.png" \
  -F 'metadata={
    "intrinsic": {
      "K": [[867.56, 0, 642.54], [0, 867.56, 636.56], [0, 0, 1]]
    },
    "camera_to_world": {
      "matrix_4x4": [
        [0.9998, -0.0013, -0.0203, -0.0033],
        [0.0022,  0.9990,  0.0439,  1.1600],
        [0.0202, -0.0440,  0.9988, -0.1338],
        [0, 0, 0, 1]
      ]
    },
    "text_prompt": "chair",
    "score_threshold": 0.3
  }'
```

#### Example (Python)

```python
import requests

url = "https://diploidic-describably-anabelle.ngrok-free.dev/detect3d"
headers = {"ngrok-skip-browser-warning": "true"}

metadata = {
    "intrinsic": {
        "K": [[867.56, 0, 642.54], [0, 867.56, 636.56], [0, 0, 1]]
    },
    "camera_to_world": {
        "matrix_4x4": [
            [0.9998, -0.0013, -0.0203, -0.0033],
            [0.0022,  0.9990,  0.0439,  1.1600],
            [0.0202, -0.0440,  0.9988, -0.1338],
            [0, 0, 0, 1]
        ]
    },
    "text_prompt": "chair.table",
    "score_threshold": 0.3
}

import json
with open("capture.png", "rb") as f:
    resp = requests.post(url, headers=headers, files={
        "image": ("capture.png", f, "image/png"),
        "metadata": (None, json.dumps(metadata)),
    })

boxes = resp.json()["boxes"]
for box in boxes:
    print(f"{box['label']}: center={box['center']}, score={box['score']:.2f}")
```

---

### 3D Detection (JSON body)

```
POST /detect3d_json
```

Same as `/detect3d` but accepts a single JSON body with base64-encoded image. Convenient for Unity/C# clients.

#### Request

Content-Type: `application/json`

```json
{
  "image_base64": "<base64-encoded PNG or JPG>",
  "intrinsic": {
    "K": [[867.56, 0, 642.54], [0, 867.56, 636.56], [0, 0, 1]]
  },
  "camera_to_world": {
    "matrix_4x4": [
      [1, 0, 0, 0],
      [0, 1, 0, 1.16],
      [0, 0, 1, 0],
      [0, 0, 0, 1]
    ]
  },
  "text_prompt": "chair",
  "score_threshold": 0.3
}
```

---

### Interactive Docs

```
GET /docs
```

Opens Swagger UI with interactive API playground.

---

## Response Format

Both detection endpoints return the same response:

```json
{
  "boxes": [
    {
      "label": "chair",
      "center": [0.66, 1.44, 1.70],
      "size": [0.46, 1.06, 0.56],
      "rotation": [-0.054, 0.723, -0.057, 0.686],
      "color": [0.0, 1.0, 0.0],
      "score": 0.41
    }
  ]
}
```

| Field      | Type      | Description                                  |
|------------|-----------|----------------------------------------------|
| `label`    | string    | Detected object category                     |
| `center`   | float[3]  | 3D center `[x, y, z]` in world coords (meters) |
| `size`     | float[3]  | Bounding box dimensions `[width, height, depth]` (meters) |
| `rotation` | float[4]  | Orientation quaternion `[qx, qy, qz, qw]`   |
| `color`    | float[3]  | Suggested display color `[r, g, b]` (0-1)    |
| `score`    | float     | Detection confidence (0-1)                   |

## World Coordinate System

Output uses the **Meta Quest guardian coordinate system**:

| Axis | Direction |
|------|-----------|
| X    | Right (+) / Left (-) |
| Y    | Up (+) / Down (-)    |
| Z    | Forward (+) / Back (-) |

- **Origin**: Guardian floor center
- **Units**: Meters
- **Handedness**: Right-handed

The `camera_to_world` matrix you provide defines the camera pose in this coordinate system. The API transforms all 3D detections from camera space into this world space automatically.

## Multi-Class Detection

Use dot-separated names in `text_prompt` to detect multiple categories:

```
"text_prompt": "chair.table.monitor.lamp"
```

Each category gets a distinct color in the response. The `label` field identifies which category each box belongs to.

## Unity / C# Integration

The response JSON is directly compatible with `BBoxVisualizer.cs`. Example usage:

```csharp
using UnityEngine;
using UnityEngine.Networking;
using System;
using System.Collections;
using System.Text;

public class DetectionClient : MonoBehaviour
{
    string apiUrl = "https://diploidic-describably-anabelle.ngrok-free.dev/detect3d_json";

    public IEnumerator Detect(byte[] pngBytes, float[][] K, float[][] cam2world,
                              string prompt = "chair", float threshold = 0.3f)
    {
        // Build request
        var body = JsonUtility.ToJson(new DetectRequest {
            image_base64 = Convert.ToBase64String(pngBytes),
            text_prompt = prompt,
            score_threshold = threshold
        });
        // Note: intrinsic.K and camera_to_world.matrix_4x4 need manual JSON construction
        // since Unity's JsonUtility doesn't support nested arrays well.
        // Consider using Newtonsoft.Json for proper serialization.

        var request = new UnityWebRequest(apiUrl, "POST");
        request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");
        request.SetRequestHeader("ngrok-skip-browser-warning", "true");

        yield return request.SendWebRequest();

        if (request.result == UnityWebRequest.Result.Success)
        {
            var result = JsonUtility.FromJson<BBoxList>(request.downloadHandler.text);
            Debug.Log($"Detected {result.boxes.Length} objects");
            // Pass result.boxes to BBoxVisualizer
        }
    }

    [Serializable] class DetectRequest {
        public string image_base64;
        public string text_prompt;
        public float score_threshold;
    }
    [Serializable] class BBox3D {
        public string label;
        public float[] center;
        public float[] size;
        public float[] rotation;
        public float[] color;
        public float score;
    }
    [Serializable] class BBoxList { public BBox3D[] boxes; }
}
```

## Notes

- **Inference time**: ~2-3 seconds per image (GPU)
- **Input resolution**: Any size accepted (internally resized to 1008x1008)
- **Score**: Higher = more confident. Recommended threshold: 0.3-0.5
- **Empty response**: `{"boxes": []}` means no objects detected above threshold
