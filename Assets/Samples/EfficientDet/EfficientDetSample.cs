using TensorFlowLite;
using UnityEngine;
using UnityEngine.UI;
using TextureSource;
using TMPro;
using NativeWebSocket;
using System.Collections.Generic;


[RequireComponent(typeof(VirtualTextureSource))]
public class EfficientDetSample : MonoBehaviour
{
    private WebSocket websocket;

    [System.Serializable]
    public class HoleInfo
    {
        public float x;
        public float y;
        public float score;
    }

    [System.Serializable]
    public class HoleWrapper
    {
        public List<HoleInfo> holes;
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
        if (websocket != null && websocket.State == WebSocketState.Open && holes.Count > 0)
        {
            HoleWrapper wrapper = new HoleWrapper { holes = holes };
            string json = JsonUtility.ToJson(wrapper);
            Debug.Log("傳送 JSON: " + json);
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
        List<HoleInfo> holes = new();

        for (int i = 0; i < results.Length; i++)
        {
            if (results[i].score >= scoreThreshold)
            {
                var r = results[i].rect;
                float centerX = r.x + r.width / 2f;
                float centerY = r.y + r.height / 2f;

                holes.Add(new HoleInfo
                {
                    x = centerX,
                    y = centerY,
                    score = results[i].score
                });
            }
        }

        if (websocket != null && websocket.State == WebSocketState.Open && holes.Count > 0)
        {
            HoleWrapper wrapper = new HoleWrapper { holes = holes };
            string json = JsonUtility.ToJson(wrapper);
            Debug.Log(" 傳送 JSON: " + json);
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