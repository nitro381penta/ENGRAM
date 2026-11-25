using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class EngramRecallUI : MonoBehaviour
{
    [Header("Engram")]
    [SerializeField] private EngramMemoryRecall memoryRecall;

    [Header("Panels")]
    [SerializeField] private GameObject instructionPanel;
    [SerializeField] private GameObject progressPanel;
    [SerializeField] private GameObject resultPanel;

    [Header("Task Text")]
    [SerializeField] private TMP_Text instructionText;
    [SerializeField] private TMP_Text progressText;
    [SerializeField] private TMP_Text durationText;
    [SerializeField] private TMP_Text statusText;

    [Header("Object Queue")]
    [SerializeField] private TMP_Text[] objectQueueTexts = new TMP_Text[4];

    [Header("Object Queue Icons")]
    [SerializeField, Range(0.25f, 0.8f)]
    private float queueSymbolScale = 0.46f;

    [SerializeField, Min(0f)]
    private float queueSymbolGap = 3f;

    [SerializeField, Range(0.001f, 0.012f)]
    private float queueSymbolDepth = 0.004f;

    [SerializeField] private Color queuePendingColor = Color.white;
    [SerializeField] private Color queuePlacedColor = new Color(0.3f, 1f, 0.8f);

    [Header("Results")]
    [SerializeField] private TMP_Text meanErrorText;
    [SerializeField] private TMP_Text resultDurationText;
    [SerializeField] private TMP_Text correctionCountText;
    [SerializeField] private TMP_Text saveStatusText;
    [SerializeField] private TMP_Text[] resultObjectTexts = new TMP_Text[4];

    [Header("Buttons")]
    [SerializeField] private Button finishButton;
    [SerializeField] private Button resetButton;

    private sealed class QueueIcon
    {
        public GameObject Root;
        public SpriteRenderer PendingDot;
        public LineRenderer Check;
    }

    private readonly Dictionary<TMP_Text, QueueIcon> queueIcons = new();
    private Material iconMaterial;
    private Texture2D pendingDotTexture;
    private Sprite pendingDotSprite;

    private float nextRefreshTime;

    private const string MainInstruction =
        "Erinnern Sie sich an die ursprünglichen Positionen.\n" +
        "Greifen Sie die Gegenstände und platzieren Sie sie auf dem Tisch.";

    private const string CorrectionInstruction =
        "Alle vier Gegenstände sind platziert.\n" +
        "Sie können die Positionen korrigieren oder die Aufgabe abschließen.";

    private void OnEnable()
    {
        if (memoryRecall == null)
        {
            memoryRecall =
                FindFirstObjectByType<EngramMemoryRecall>();
        }

        if (memoryRecall != null)
        {
            memoryRecall.RecallStarted += OnRecallStarted;
            memoryRecall.RecallReset += OnRecallReset;

            memoryRecall.RecallProgressChanged +=
                OnRecallProgressChanged;

            memoryRecall.ObjectPlaced += OnObjectPlaced;
            memoryRecall.RecallCompleted += OnRecallCompleted;
        }

        if (finishButton != null)
            finishButton.onClick.AddListener(FinishTask);

        if (resetButton != null)
            resetButton.onClick.AddListener(ResetTask);

        RefreshUI();
    }

    private void OnDisable()
    {
        foreach (QueueIcon icon in queueIcons.Values)
        {
            if (icon.Root != null)
                icon.Root.SetActive(false);
        }

        if (memoryRecall != null)
        {
            memoryRecall.RecallStarted -= OnRecallStarted;
            memoryRecall.RecallReset -= OnRecallReset;

            memoryRecall.RecallProgressChanged -=
                OnRecallProgressChanged;

            memoryRecall.ObjectPlaced -= OnObjectPlaced;
            memoryRecall.RecallCompleted -= OnRecallCompleted;
        }

        if (finishButton != null)
            finishButton.onClick.RemoveListener(FinishTask);

        if (resetButton != null)
            resetButton.onClick.RemoveListener(ResetTask);

    }

    private void Update()
    {
        if (Time.unscaledTime < nextRefreshTime)
            return;

        nextRefreshTime = Time.unscaledTime + 0.2f;

        RefreshUI();
    }

    private void OnRecallStarted()
    {
        if (instructionText != null)
            instructionText.text = MainInstruction;

        RefreshUI();
    }

    private void OnRecallReset()
    {
        if (instructionText != null)
            instructionText.text = MainInstruction;

        RefreshUI();
    }

    private void OnRecallProgressChanged(int placed, int total)
    {
        RefreshUI();
    }

    private void OnObjectPlaced(string cocoLabel, bool isCorrection)
    {
        RefreshUI();
    }

    private void OnRecallCompleted(
        EngramMemoryRecall.RecallResult result)
    {
        RefreshUI();
    }

    private void RefreshUI()
    {
        if (memoryRecall == null)
            return;

        bool active =
            memoryRecall.SessionStarted &&
            !memoryRecall.Completed;

        bool completed = memoryRecall.Completed;

        if (instructionPanel != null)
            instructionPanel.SetActive(active);

        if (progressPanel != null)
            progressPanel.SetActive(active);

        if (resultPanel != null)
            resultPanel.SetActive(completed);

        if (finishButton != null)
        {
            finishButton.interactable =
                memoryRecall.CanFinishRecall;
        }

        if (resetButton != null)
        {
            resetButton.interactable =
                memoryRecall.SessionStarted;
        }

        UpdateTaskProgress();
        UpdateObjectQueue();

        if (completed)
            UpdateResults();
    }

    private void UpdateTaskProgress()
    {
        if (memoryRecall == null)
            return;

        int placed = memoryRecall.PlacedCount;
        int total = memoryRecall.ObjectCount;

        if (progressText != null)
        {
            progressText.text =
                $"{placed} von {total} Gegenständen platziert";
        }

        if (durationText != null)
        {
            durationText.text =
                FormatDuration(memoryRecall.ElapsedSeconds);
        }

        if (instructionText != null)
        {
            instructionText.text =
                memoryRecall.CanFinishRecall
                    ? CorrectionInstruction
                    : MainInstruction;
        }

        if (statusText != null)
        {
            statusText.text =
                memoryRecall.CanFinishRecall
                    ? "Bereit zum Abschließen"
                    : "Gegenstände platzieren";
        }
    }

    private void UpdateObjectQueue()
    {
        if (memoryRecall == null || objectQueueTexts == null)
            return;

        bool showIcons = memoryRecall.SessionStarted &&
                         !memoryRecall.Completed &&
                         progressPanel != null &&
                         progressPanel.activeInHierarchy;

        for (int i = 0; i < objectQueueTexts.Length; i++)
        {
            TMP_Text text = objectQueueTexts[i];
            if (text == null)
                continue;

            bool inQueue = i < memoryRecall.ObjectCount;
            string label = inQueue
                ? ToGerman(memoryRecall.GetObjectLabel(i))
                : string.Empty;

            if (text.text != label)
                text.text = label;

            if (!inQueue || !showIcons || !text.gameObject.activeInHierarchy)
            {
                SetQueueIconVisible(text, false);
                continue;
            }

            UpdateQueueIcon(text, memoryRecall.IsObjectPlaced(i));
        }
    }

    private void UpdateQueueIcon(TMP_Text text, bool placed)
    {
        QueueIcon icon = GetOrCreateQueueIcon(text);
        if (icon == null)
            return;

        text.ForceMeshUpdate();
        TMP_TextInfo info = text.textInfo;

        int firstVisible = 0;
        while (firstVisible < info.characterCount &&
               !info.characterInfo[firstVisible].isVisible)
        {
            firstVisible++;
        }

        if (firstVisible >= info.characterCount)
        {
            SetQueueIconVisible(text, false);
            return;
        }

        TMP_CharacterInfo first = info.characterInfo[firstVisible];
        float glyphHeight = Mathf.Max(1f, first.ascender - first.descender);
        float diameter = glyphHeight * queueSymbolScale;
        float iconX = first.origin - queueSymbolGap - diameter * 0.5f;
        float iconY = (first.ascender + first.descender) * 0.5f;

        Vector3 center = text.transform.TransformPoint(
            new Vector3(iconX, iconY, 0f)
        );

        Vector3 towardViewer = Camera.main != null
            ? Camera.main.transform.position - center
            : text.transform.forward;

        if (towardViewer.sqrMagnitude > 0.000001f)
            center += towardViewer.normalized * queueSymbolDepth;

        Vector3 halfX = text.transform.TransformVector(
            Vector3.right * diameter * 0.5f
        );
        Vector3 halfY = text.transform.TransformVector(
            Vector3.up * diameter * 0.5f
        );

        icon.Root.SetActive(true);
        icon.PendingDot.enabled = !placed;
        icon.Check.enabled = placed;

        if (placed)
        {
            icon.Check.widthMultiplier = Mathf.Clamp(
                halfY.magnitude * 0.20f, 0.0004f, 0.0018f
            );
            icon.Check.startColor = queuePlacedColor;
            icon.Check.endColor = queuePlacedColor;
            icon.Check.SetPosition(0, center - halfX * 0.85f);
            icon.Check.SetPosition(1, center - halfX * 0.10f - halfY * 0.67f);
            icon.Check.SetPosition(2, center + halfX * 0.88f + halfY * 0.79f);
        }
        else
        {
            // High-resolution filled circle with a soft anti-aliased edge.
            float diameterWorld = halfX.magnitude + halfY.magnitude;
            Transform sprite = icon.PendingDot.transform;
            sprite.position = center;
            sprite.rotation = text.transform.rotation;
            sprite.localScale = Vector3.one * diameterWorld;
            icon.PendingDot.color = queuePendingColor;
        }
    }

    private QueueIcon GetOrCreateQueueIcon(TMP_Text text)
    {
        if (queueIcons.TryGetValue(text, out QueueIcon existing) &&
            existing.Root != null)
        {
            return existing;
        }

        if (!EnsureIconResources())
            return null;

        // Keep an identity-scale world root so Canvas scaling cannot shrink the sprite.
        GameObject root = new GameObject("EngramQueueIcon_" + text.name);

        Canvas targetCanvas = text.GetComponentInParent<Canvas>();
        GameObject dotObject = new GameObject("PendingDot");
        dotObject.transform.SetParent(root.transform, false);
        SpriteRenderer dot = dotObject.AddComponent<SpriteRenderer>();
        dot.sprite = pendingDotSprite;
        dot.color = queuePendingColor;
        dot.enabled = false;

        if (targetCanvas != null)
        {
            dot.sortingLayerID = targetCanvas.sortingLayerID;
            dot.sortingOrder = Mathf.Min(32767, targetCanvas.sortingOrder + 10);
        }

        LineRenderer check = CreateIconLine(
            root.transform, "PlacedCheck", 3, targetCanvas
        );

        QueueIcon icon = new QueueIcon
        {
            Root = root,
            PendingDot = dot,
            Check = check
        };

        queueIcons[text] = icon;
        return icon;
    }

    private bool EnsureIconResources()
    {
        if (iconMaterial == null)
        {
            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null)
                shader = Shader.Find("Unlit/Transparent");
            if (shader == null)
                shader = Shader.Find("Universal Render Pipeline/Unlit");

            if (shader == null)
            {
                Debug.LogError("Engram Recall UI: Icon shader not available.");
                return false;
            }

            iconMaterial = new Material(shader);
            iconMaterial.color = Color.white;
        }

        if (pendingDotSprite != null)
            return true;

        const int size = 128;
        pendingDotTexture = new Texture2D(
            size, size, TextureFormat.RGBA32, false
        );
        pendingDotTexture.filterMode = FilterMode.Bilinear;
        pendingDotTexture.wrapMode = TextureWrapMode.Clamp;

        float center = size * 0.5f;
        float radius = size * 0.45f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x + 0.5f - center;
                float dy = y + 0.5f - center;
                float distance = Mathf.Sqrt(dx * dx + dy * dy);
                float alpha = Mathf.Clamp01((radius + 0.75f - distance) / 1.5f);
                pendingDotTexture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
            }
        }
        pendingDotTexture.Apply();

        pendingDotSprite = Sprite.Create(
            pendingDotTexture,
            new Rect(0f, 0f, size, size),
            new Vector2(0.5f, 0.5f),
            size
        );
        return pendingDotSprite != null;
    }

    private LineRenderer CreateIconLine(
        Transform parent, string lineName, int points, Canvas parentCanvas)
    {
        GameObject lineObject = new GameObject(lineName);
        lineObject.transform.SetParent(parent, false);
        LineRenderer line = lineObject.AddComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.positionCount = points;
        line.sharedMaterial = iconMaterial;
        if (parentCanvas != null)
        {
            line.sortingLayerID = parentCanvas.sortingLayerID;
            line.sortingOrder = Mathf.Min(32767, parentCanvas.sortingOrder + 10);
        }
        line.alignment = LineAlignment.View;
        line.numCapVertices = 8;
        line.numCornerVertices = 8;
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;
        line.enabled = false;
        return line;
    }

    private void SetQueueIconVisible(TMP_Text text, bool visible)
    {
        if (queueIcons.TryGetValue(text, out QueueIcon icon) &&
            icon.Root != null)
        {
            icon.Root.SetActive(visible);
        }
    }

    private void UpdateResults()
    {
        EngramMemoryRecall.RecallResult result =
            memoryRecall.LastResult;

        if (result == null)
            return;

        if (meanErrorText != null)
        {
            meanErrorText.text =
                $"{result.meanHorizontalErrorCm:F1} cm";
        }

        if (resultDurationText != null)
        {
            resultDurationText.text =
                FormatDuration(result.durationSeconds);
        }

        if (correctionCountText != null)
        {
            correctionCountText.text =
                result.totalCorrections.ToString();
        }

        if (saveStatusText != null)
        {
            saveStatusText.text =
                memoryRecall.ResultSavedSuccessfully
                    ? "Ergebnis gespeichert"
                    : memoryRecall.LastSaveError != null
                        ? "Speicherung fehlgeschlagen"
                        : "Ergebnis nicht gespeichert";
        }

        if (resultObjectTexts == null)
            return;

        for (int i = 0; i < resultObjectTexts.Length; i++)
        {
            TMP_Text text = resultObjectTexts[i];

            if (text == null)
                continue;

            if (i >= result.objects.Count)
            {
                text.text = "";
                continue;
            }

            EngramMemoryRecall.ObjectResult item =
                result.objects[i];

            string label = ToGerman(item.cocoLabel);

            text.text =
                $"{label}: {item.horizontalErrorCm:F1} cm";
        }
    }

    public void FinishTask()
    {
        if (memoryRecall == null)
            return;

        memoryRecall.TryFinishRecall();
    }

    public void ResetTask()
    {
        if (memoryRecall == null)
            return;

        memoryRecall.ResetRecallToInventory();
    }

    private static string FormatDuration(float seconds)
    {
        int totalSeconds = Mathf.FloorToInt(
            Mathf.Max(0f, seconds)
        );

        int minutes = totalSeconds / 60;
        int remainder = totalSeconds % 60;

        return $"{minutes:00}:{remainder:00}";
    }

    private void OnDestroy()
    {
        foreach (QueueIcon icon in queueIcons.Values)
        {
            if (icon.Root != null)
                Destroy(icon.Root);
        }
        queueIcons.Clear();

        if (iconMaterial != null)
            Destroy(iconMaterial);
        if (pendingDotSprite != null)
            Destroy(pendingDotSprite);
        if (pendingDotTexture != null)
            Destroy(pendingDotTexture);
    }

    private static string ToGerman(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return "";

        switch (label.Trim().ToLowerInvariant())
        {
            case "apple": return "Apfel";
            case "banana": return "Banane";
            case "book": return "Buch";
            case "bottle": return "Flasche";
            case "bowl": return "Schüssel";
            case "cell phone": return "Smartphone";
            case "clock": return "Uhr";
            case "cup": return "Tasse";
            case "scissors": return "Schere";
            case "wine glass": return "Weinglas";
            default: return label;
        }
    }
}