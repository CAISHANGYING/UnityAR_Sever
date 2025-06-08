using TensorFlowLite;
using UnityEngine;
using UnityEngine.UI;
using TextureSource;
using TMPro;
using NativeWebSocket;
using System.Collections.Generic;
using System;
using Newtonsoft.Json;
using System.Text;
using Newtonsoft.Json.Linq;


////github https://github.com/PimDeWitte/UnityMainThreadDispatcher
//using UnityMainThreadDispatcher;  // 或是對應的 namespace



[RequireComponent(typeof(VirtualTextureSource))]
public class EfficientDetSample : MonoBehaviour
{

    private WebSocket websocket;

    [Serializable]
    public class Coordinate
    {
        public float[] left_top;
        public float[] right_bottom;
        public float[] middle;
    }

    [System.Serializable]
    public class HoleInfo
    {
        public string tag;
        public Coordinate coordinate;
        public float width;
        public float height;
        public float[] xywh;
        public string status;
    }

    [System.Serializable]
    public class HoleWrapper
    {
        // 動態 key（a、b、c…），對應每個 HoleInfo
        public Dictionary<string, HoleInfo> hole;

        // wrench 和 boundary
        public Dictionary<string, object> wrench = new Dictionary<string, object>();
        public float[] boundary = new float[0];
    }


    [SerializeField]
    private EfficientDet.Options options = default;

    [SerializeField]
    private AspectRatioFitter frameContainer = null;

    [SerializeField]
    private Text framePrefab = null;


    [SerializeField]
    private TextMeshProUGUI holeInfoTMP;

    [SerializeField, Range(0f, 1f)]
    private float scoreThreshold = 0.5f;

    [SerializeField]
    private TextAsset labelMap = null;

    private EfficientDet efficientDet;
    private Text[] frames;
    private string[] labels;

    private Dictionary<string, HoleInfo> pendingPushbacks;
    private string lastDetectionInfo = "";

    [Serializable]
    public class PushbackWrapper
    {
        public Dictionary<string, HoleInfo> pushback_positions;
    }

private void Update()
{
    websocket?.DispatchMessageQueue();

        if (pendingPushbacks != null)
        {
            // 1. 組出回推的 tag 列表
            var sb = new StringBuilder();
            sb.AppendLine("Returned Tags:");
            foreach (var kv in pendingPushbacks)
            {
                // kv.Key 是 a, b, c...；kv.Value.tag 才是後端回傳的數字
                sb.AppendLine($"{kv.Key}: tag = {kv.Value.tag}");
            }
            sb.AppendLine();

            // 2. 接著把最後一次的偵測結果貼在後面
            sb.Append(lastDetectionInfo);

            // 3. 顯示到同一個 TMP
            holeInfoTMP.text = sb.ToString();

            // 4. 照常畫框
            DrawPushbackFrames(pendingPushbacks);
            pendingPushbacks = null;
        }
    }




    private async void Start()
    {
        efficientDet = new EfficientDet(options);

        // 初始化畫框
        frames = new Text[10];
        Transform parent = frameContainer.transform;
        for (int i = 0; i < frames.Length; i++)
        {
            frames[i] = Instantiate(framePrefab, Vector3.zero, Quaternion.identity, parent);
            frames[i].transform.localPosition = Vector3.zero;
        }

        // Label
        labels = labelMap.text
       .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        // 啟動 TFLite 輸入
        if (TryGetComponent(out VirtualTextureSource source))
        {
            source.OnTexture.AddListener(Invoke);
        }

        // ✅ 初始化 WebSocket（請填入你的電腦區網 IP）
        websocket = new WebSocket("ws://192.168.137.1:8765");

        websocket.OnOpen += () => Debug.Log("WebSocket Connected!");
        websocket.OnError += (e) => Debug.Log(" WebSocket Error: " + e);
        websocket.OnClose += (e) => Debug.Log(" WebSocket Closed!");

        websocket.OnMessage += (bytes) =>
        {
            var json = Encoding.UTF8.GetString(bytes);
            Debug.Log($"[WebSocket] Received JSON: {json}");

            try
            {
                // 先 parse 成 JObject
                var jObj = JObject.Parse(json);
                // 準備要塞給 Update() 畫框的字典
                Dictionary<string, HoleInfo> dict;
                if (jObj["hole"] != null)
                {
                    // 有外層 hole 屬性，就讀裡面的那個
                    dict = jObj["hole"]
                        .ToObject<Dictionary<string, HoleInfo>>();
                }
                else
                {
                    // 沒有外層 hole，就直接把根物件當作字典
                    dict = jObj
                        .ToObject<Dictionary<string, HoleInfo>>();
                }

                pendingPushbacks = dict;
                Debug.Log($"[WebSocket] Parsed holes: count={pendingPushbacks.Count}, keys={string.Join(",", pendingPushbacks.Keys)}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[WebSocket] JSON parse error: {ex}");
            }
        };



        await websocket.Connect();
    }


    private void OnDestroy()
    {
        if (TryGetComponent(out VirtualTextureSource source))
        {
            source.OnTexture.RemoveListener(Invoke);
        }
        efficientDet?.Dispose();
    }

    private async void SendHoleData(List<HoleInfo> holes)
    {
        if (websocket != null
            && websocket.State == WebSocketState.Open
            && holes != null
            && holes.Count > 0)
        {
            // 1. 建立 wrapper 並初始化 Dictionary
            HoleWrapper wrapper = new HoleWrapper();
            wrapper.hole = new Dictionary<string, HoleInfo>();

            // 2. 將 List 轉成 key 為 a, b, c… 的 Dictionary
            for (int i = 0; i < holes.Count; i++)
            {
                // a → 'a'+0, b → 'a'+1, …
                string key = ((char)('a' + i)).ToString();
                wrapper.hole[key] = holes[i];
            }

            // 3. （wrench、boundary 已在 HoleWrapper ctor 預設好）

            // 4. 用 JsonConvert 來序列化 Dictionary
            string json = JsonConvert.SerializeObject(wrapper);
            Debug.Log("JSON: " + json);

            // 5. 傳送
            await websocket.SendText(json);
        }
    }

    private int lastTextureWidth;
    private int lastTextureHeight;

    private void Invoke(Texture texture)
    {

        lastTextureWidth = texture.width;
        lastTextureHeight = texture.height;
        // 執行推論
        efficientDet.Run(texture);

        // 取得偵測結果
        var results = efficientDet.GetResults();

        // 用來計算在畫面上顯示時，如何對應 UI 空間的大小
        Vector2 size = (frameContainer.transform as RectTransform).rect.size;

        float aspect = (float)texture.width / texture.height;
        Vector2 ratio = aspect > 1
            ? new Vector2(1.0f, 1 / aspect)
            : new Vector2(aspect, 1.0f);

        for (int i = 0; i < frames.Length; i++)
        {
            SetFrame(frames[i], results[i], size * ratio);
        }


        // 統整孔洞資訊
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        sb.AppendLine("find the hole data:");

        for (int i = 0; i < results.Length; i++)
        {
            if (results[i].score >= scoreThreshold)
            {
                var r = results[i].rect;
                float centerX = r.x + r.width / 2f;
                float centerY = r.y + r.height / 2f;
                string label = GetLabelName(results[i].classID);

                sb.AppendLine($"{i + 1}({centerX:F2}, {centerY:F2}) | trust {(results[i].score * 100):F1}%");
            }
        }

        // 顯示到 TMP UI 上
        lastDetectionInfo = sb.ToString();
        holeInfoTMP.text = lastDetectionInfo;
        // 傳送孔洞資訊給後端
        List<HoleInfo> holes = new List<HoleInfo>();

        for (int i = 0; i < results.Length; i++)
        {
            if (results[i].score >= scoreThreshold)
            {
                var r = results[i].rect;
                float centerX = r.x + r.width / 2f;
                float centerY = r.y + r.height / 2f;

                holes.Add(new HoleInfo
                {
                    tag = "0",  // 如果要動態改 tag，可以再調整
                    coordinate = new Coordinate
                    {
                        left_top = new float[] { Mathf.Round(r.xMin * texture.width * 10f) / 10f, Mathf.Round(r.yMin * texture.height * 10f) / 10f },
                        right_bottom = new float[] { Mathf.Round(r.xMax * texture.width * 10f) / 10f, Mathf.Round(r.yMax * texture.height * 10f) / 10f },
                        middle = new float[] { Mathf.Round(centerX * texture.width * 10f) / 10f, Mathf.Round(centerY * texture.height * 10f) / 10f }
                    },
                    width = Mathf.Round(r.width * texture.width * 10f) / 10f,
                    height = Mathf.Round(r.height * texture.height * 10f) / 10f,
                    xywh = new float[] { Mathf.Round(centerX * texture.width * 10f) / 10f, Mathf.Round(centerY * texture.height * 10f) / 10f, Mathf.Round(r.width * texture.width * 10f) / 10f, Mathf.Round(r.height * texture.height * 10f) / 10f },
                    status = $"{GetLabelName(results[i].classID)}"
                });
            }
        }

        if (websocket != null && websocket.State == WebSocketState.Open && holes.Count > 0)
        {
            SendHoleData(holes);
        }
    }

    private async void OnApplicationQuit()
    {
        await websocket.Close();
    }


    private void DrawPushbackFrames(Dictionary<string, HoleInfo> dict)
    {
        // 先把所有 frame 隱藏
        foreach (var f in frames) f.gameObject.SetActive(false);

        // 跑回推字典
        int idx = 0;
        // 取出 UI 尺寸：要跟 Invoke 裡一樣
        Vector2 texSize = new Vector2(lastTextureWidth, lastTextureHeight);
        Vector2 containerSize = (frameContainer.transform as RectTransform).rect.size;
        float aspect = texSize.x / texSize.y;
        Vector2 ratio = aspect > 1
            ? new Vector2(1.0f, 1 / aspect)
            : new Vector2(aspect, 1.0f);
        Vector2 uiSize = containerSize * ratio;

        foreach (var kv in dict)
        {
            if (idx >= frames.Length) break;
            SetFrame(frames[idx], kv.Value, texSize, uiSize, idx);
            idx++;
        }
    }


    private void SetFrame(Text frame,
                      HoleInfo info,
                      Vector2 textureSize,   // texture.width, texture.height
                      Vector2 uiSize,        // size * ratio
                      int displayIndex)      // 0,1,2... 對應 a,b,c...
    {
        frame.gameObject.SetActive(true);

        // tag 是字串序號，我們先轉成 int
        string returnedTag = info.tag;

        // 2. 为了让每个框前面也有 a、b、c…… 的标识
        char letter = (char)('a' + displayIndex);

        // 3. 把 tag 转成 int，以便拿分类名称（如果你的标签映射是按数字来的）
        int tagId = int.Parse(returnedTag);
        string name = GetLabelName(tagId);


        frame.text = $"{displayIndex + 1}. [{letter}] {name} (tag={returnedTag})";
        // xywh = [centerX, centerY, w, h]（都是像素）
        float cx = info.xywh[0],
              cy = info.xywh[1],
              w = info.xywh[2],
              h = info.xywh[3];

        // 先把像素換成 normalized 再到 UI 空間
        float nx = cx / textureSize.x;
        float ny = cy / textureSize.y;
        float nw = w / textureSize.x;
        float nh = h / textureSize.y;

        var rt = frame.transform as RectTransform;
        // normalized 0~1 轉 anchoredPosition, sizeDelta
        rt.anchoredPosition = new Vector2(
            nx * uiSize.x - uiSize.x * 0.5f,
            ny * uiSize.y - uiSize.y * 0.5f
        );
        rt.sizeDelta = new Vector2(nw * uiSize.x, nh * uiSize.y);
    }
    private void SetFrame(Text frame, EfficientDet.Result result, Vector2 size)
    {
        // 如果分數低於閾值，就隱藏不顯示
        if (result.score < scoreThreshold)
        {
            frame.gameObject.SetActive(false);
            return;
        }
        else
        {
            frame.gameObject.SetActive(true);
        }

        // 取得對應的類別名稱
        frame.text = $"{GetLabelName(result.classID)} : {(int)(result.score * 100)}%";

        // 依照偵測出的框座標，設定在 Unity UI 裡的位置與大小
        var rt = frame.transform as RectTransform;
        rt.anchoredPosition = result.rect.position * size - size * 0.5f;
        rt.sizeDelta = result.rect.size * size;
    }

    private string GetLabelName(int id)
    {
        // 不做 +1
        if (id < 0 || id >= labels.Length)
        {
            return "?";
        }
        return labels[id].Trim();
    }
}