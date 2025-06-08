using TensorFlowLite;
using UnityEngine;
using UnityEngine.UI;
using TextureSource;
using TMPro;
using NativeWebSocket;
using System.Collections.Generic;
using System;
using Newtonsoft.Json;


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
        labels = labelMap.text.Split('\n');

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


    private void Invoke(Texture texture)
    {
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
        holeInfoTMP.text = sb.ToString();
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
                        left_top = new float[] { r.xMin * texture.width, r.yMin * texture.height },
                        right_bottom = new float[] { r.xMax * texture.width, r.yMax * texture.height },
                        middle = new float[] { centerX * texture.width, centerY * texture.height }
                    },
                    width = r.width * texture.width,
                    height = r.height * texture.height,
                    xywh = new float[] { centerX * texture.width, centerY * texture.height, r.width * texture.width, r.height * texture.height },
                    status = "hole"  // 或 "lock_hole"，依你的邏輯
                });
            }
        }

        if (websocket != null && websocket.State == WebSocketState.Open && holes.Count > 0)
        {
            SendHoleData(holes);
        }
    }
    private void Update()
    {
        websocket?.DispatchMessageQueue();
    }

    private async void OnApplicationQuit()
    {
        await websocket.Close();
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
        return labels[id];
    }
}