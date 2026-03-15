using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

public class DetectionClient : MonoBehaviour
{
    [Header("API Configuration")]
    public string apiUrl = "https://diploidic-describably-anabelle.ngrok-free.dev/detect3d_json";
    public string textPrompt = "monitor.keyboard.table.chair.computer";
    public float scoreThreshold = 0.5f;
    public float timeoutSeconds = 30f;

    public BBoxVisualizer.BBox3D[] LastResult { get; private set; }
    public string LastError { get; private set; }
    public bool IsDetecting { get; private set; }

    public Coroutine Detect(byte[] pngBytes, float fx, float fy, float cx, float cy, Matrix4x4 camToWorld)
    {
        return StartCoroutine(DetectCoroutine(pngBytes, fx, fy, cx, cy, camToWorld));
    }

    IEnumerator DetectCoroutine(byte[] pngBytes, float fx, float fy, float cx, float cy, Matrix4x4 camToWorld)
    {
        IsDetecting = true;
        LastResult = null;
        LastError = null;

        string base64 = Convert.ToBase64String(pngBytes);
        string json = BuildRequestJson(base64, fx, fy, cx, cy, camToWorld);

        Debug.Log($"[DetectionClient] Sending request to {apiUrl} ({pngBytes.Length / 1024}KB image, prompt=\"{textPrompt}\")");

        byte[] bodyBytes = Encoding.UTF8.GetBytes(json);
        var request = new UnityWebRequest(apiUrl, "POST");
        request.uploadHandler = new UploadHandlerRaw(bodyBytes);
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");
        request.SetRequestHeader("ngrok-skip-browser-warning", "true");
        request.timeout = (int)timeoutSeconds;

        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            LastError = $"{request.result}: {request.error}";
            Debug.LogError($"[DetectionClient] Request failed: {LastError}");
        }
        else
        {
            string responseText = request.downloadHandler.text;
            Debug.Log($"[DetectionClient] Response: {responseText}");

            try
            {
                var response = JsonUtility.FromJson<BBoxVisualizer.BBoxList>(responseText);
                LastResult = response.boxes ?? Array.Empty<BBoxVisualizer.BBox3D>();
                Debug.Log($"[DetectionClient] Detected {LastResult.Length} objects");
            }
            catch (Exception e)
            {
                LastError = $"Parse error: {e.Message}";
                Debug.LogError($"[DetectionClient] {LastError}\nResponse: {responseText}");
            }
        }

        request.Dispose();
        IsDetecting = false;
    }

    string BuildRequestJson(string imageBase64, float fx, float fy, float cx, float cy, Matrix4x4 m)
    {
        var sb = new StringBuilder(imageBase64.Length + 1024);
        sb.Append("{");
        sb.Append("\"image_base64\":\"").Append(imageBase64).Append("\",");
        sb.Append("\"intrinsic\":{\"K\":[");
        sb.AppendFormat("[{0},{1},{2}],", F(fx), F(0), F(cx));
        sb.AppendFormat("[{0},{1},{2}],", F(0), F(fy), F(cy));
        sb.Append("[0,0,1]]},");
        sb.Append("\"camera_to_world\":{\"matrix_4x4\":[");
        sb.AppendFormat("[{0},{1},{2},{3}],", F(m.m00), F(m.m01), F(m.m02), F(m.m03));
        sb.AppendFormat("[{0},{1},{2},{3}],", F(m.m10), F(m.m11), F(m.m12), F(m.m13));
        sb.AppendFormat("[{0},{1},{2},{3}],", F(m.m20), F(m.m21), F(m.m22), F(m.m23));
        sb.AppendFormat("[{0},{1},{2},{3}]", F(m.m30), F(m.m31), F(m.m32), F(m.m33));
        sb.Append("]},");
        sb.AppendFormat("\"text_prompt\":\"{0}\",", textPrompt);
        sb.AppendFormat("\"score_threshold\":{0}", F(scoreThreshold));
        sb.Append("}");
        return sb.ToString();
    }

    static string F(float v) => v.ToString("G");
}
