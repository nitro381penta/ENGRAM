using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

// Separates the three-page introduction from capture and recall; supports skip buttons.
public class EngramSessionFlow : MonoBehaviour
{
    private enum Phase
    {
        CalibrateTable,
        DetectObjects,
        LearnPositions,
        Recall,
        Results
    }

    [Serializable]
    private class SavedObject
    {
        public string label;
    }

    [Serializable]
    private class SavedObjects
    {
        public string anchorUuid;
        public List<SavedObject> objects;
    }

    [Header("Engram Components")]
    [SerializeField] private EngramTableReference tableReference;
    [SerializeField] private EngramSpatialObjectMarkers spatialMarkers;
    [SerializeField] private EngramSpatialInventory spatialInventory;
    [SerializeField] private EngramObjectVisualizer objectVisualizer;
    [SerializeField] private EngramMemoryRecall memoryRecall;
    [SerializeField] private EngramObjectDetector objectDetector;

    [Header("Setup Pages (editable in the scene)")]
    [SerializeField] private Canvas setupCanvas;
    [SerializeField] private GameObject setupPage1;
    [SerializeField] private GameObject setupPage2;
    [SerializeField] private GameObject setupPage3;
    [SerializeField] private Button nextButton;
    [SerializeField] private Button nextButtonPage2;
    [SerializeField, InspectorName("Skip Button Page 1")] private Button skipButtonPage1;
    [SerializeField, InspectorName("Skip Button Page 2")] private Button skipButtonPage2;
    [SerializeField, InspectorName("Start Button")] private Button understoodButton;

    [Header("Appearance for the editor page generator")]
    [SerializeField] private Vector2 setupPanelOffset = Vector2.zero;
    [SerializeField] private Color panelColor = new Color(0.55f, 0.87f, 0.95f, 0.90f);
    [SerializeField] private Color badgeColor = new Color(0.05f, 0.78f, 0.77f, 0.95f);
    [SerializeField] private Color textColor = new Color(0.04f, 0.09f, 0.13f, 1f);

    [Header("Results")]
    [Tooltip("Assign the existing RestartButton on your ResultPanel.")]
    [SerializeField] private Button restartButton;

    [Header("Capture-Only Visuals")]
    [Tooltip("Optional extra capture/debug objects; never assign the camera rig or passthrough camera.")]
    [SerializeField] private GameObject[] captureOnlyVisuals;

    private readonly Dictionary<GameObject, bool> captureVisualInitialStates = new();
    private Phase? currentPhase;
    private bool instructionsDismissed;
    private int currentPage;
    private float nextRefreshTime;

    public event Action SetupOpened;

    public Button RestartButton => restartButton;
    public Button NextButton => nextButton;
    public Button NextButtonPage2 => nextButtonPage2;
    public Button SkipButtonPage1 => skipButtonPage1;
    public Button SkipButtonPage2 => skipButtonPage2;
    public Button UnderstoodButton => understoodButton;
    public Button StartButton => understoodButton;
    public int CurrentSetupPageIndex => currentPage;
    public bool InstructionsVisible => !instructionsDismissed;

    private string SavedPath => Path.Combine(
        Application.persistentDataPath, "EngramSpatialObjects.json");

    private void Awake()
    {
        if (tableReference == null)
            tableReference = FindFirstObjectByType<EngramTableReference>();
        if (spatialMarkers == null)
            spatialMarkers = FindFirstObjectByType<EngramSpatialObjectMarkers>();
        if (spatialInventory == null)
            spatialInventory = FindFirstObjectByType<EngramSpatialInventory>();
        if (objectVisualizer == null)
            objectVisualizer = FindFirstObjectByType<EngramObjectVisualizer>();
        if (memoryRecall == null)
            memoryRecall = FindFirstObjectByType<EngramMemoryRecall>();
        if (objectDetector == null)
            objectDetector = FindFirstObjectByType<EngramObjectDetector>();

        if (nextButton != null)
            nextButton.onClick.AddListener(ShowNextSetupPage);
        if (nextButtonPage2 != null)
            nextButtonPage2.onClick.AddListener(ShowNextSetupPage);
        if (skipButtonPage1 != null)
            skipButtonPage1.onClick.AddListener(SkipSetupInstructions);
        if (skipButtonPage2 != null)
            skipButtonPage2.onClick.AddListener(SkipSetupInstructions);
        if (understoodButton != null)
            understoodButton.onClick.AddListener(StartSetup);
        if (restartButton != null)
            restartButton.onClick.AddListener(RestartWithNewObjects);

        if (setupPage1 == null || setupPage2 == null || setupPage3 == null ||
            nextButton == null || nextButtonPage2 == null ||
            skipButtonPage1 == null || skipButtonPage2 == null || understoodButton == null)
        {
            Debug.LogWarning(
                "Engram Setup: Assign all three setup pages and five buttons, " +
                "or use the component menu 'Create Missing Editable Setup Pages'.");
        }

        if (captureOnlyVisuals != null)
        {
            foreach (GameObject visual in captureOnlyVisuals)
            {
                if (visual != null && !captureVisualInitialStates.ContainsKey(visual))
                    captureVisualInitialStates.Add(visual, visual.activeSelf);
            }
        }
    }

    private void Start()
    {
        currentPage = 0;
        instructionsDismissed = false;
        currentPhase = null;
        RefreshPhase();
        SetupOpened?.Invoke();

        if (setupCanvas != null && !setupCanvas.gameObject.activeInHierarchy)
            Debug.LogWarning("Engram Setup: Setup Canvas is inactive in the hierarchy. Intro pages cannot be displayed.");
    }

    private void Update()
    {
        if (Time.unscaledTime < nextRefreshTime)
            return;

        nextRefreshTime = Time.unscaledTime + 0.2f;
        RefreshPhase();
    }

    private void RefreshPhase()
    {
        Phase phase = GetPhase();
        if (currentPhase == phase)
            return;

        currentPhase = phase;
        bool capture = phase == Phase.CalibrateTable ||
                       phase == Phase.DetectObjects ||
                       phase == Phase.LearnPositions;

        // Keep the introduction visible regardless of restored table/recall state.
        UpdateSetupPages();

        // Passthrough remains active, but technical detection views stay hidden
        // until Start/Skip and are hidden again during recall/results.
        bool captureEnabled = capture && instructionsDismissed;
        if (objectDetector != null)
            objectDetector.SetCaptureMode(captureEnabled);

        if (captureOnlyVisuals != null)
        {
            foreach (GameObject visual in captureOnlyVisuals)
            {
                if (visual == null || visual == setupPage1 ||
                    visual == setupPage2 || visual == setupPage3)
                    continue;
                if (captureVisualInitialStates.TryGetValue(visual, out bool initiallyVisible))
                    visual.SetActive(captureEnabled && initiallyVisible);
            }
        }
    }

    private void UpdateSetupPages()
    {
        bool visible = !instructionsDismissed;

        if (setupPage1 != null)
            setupPage1.SetActive(visible && currentPage == 0);
        if (setupPage2 != null)
            setupPage2.SetActive(visible && currentPage == 1);
        if (setupPage3 != null)
            setupPage3.SetActive(visible && currentPage == 2);
    }

    public void ShowNextSetupPage()
    {
        if (instructionsDismissed || currentPage >= 2)
            return;

        currentPage++;
        UpdateSetupPages();
    }

    // The Start button closes the introduction and opens the setup workflow.
    public void StartSetup()
    {
        if (instructionsDismissed)
            return;

        instructionsDismissed = true;
        currentPhase = null;
        RefreshPhase();
    }

    // Skip from page 1 or 2 has the same effect as Start on page 3.
    public void SkipSetupInstructions() => StartSetup();

    public void DismissSetupInstructions() => StartSetup();

    // Can be connected to a future Info/Help button without rebuilding the panels.
    public void ShowSetupInstructions()
    {
        currentPage = 0;
        instructionsDismissed = false;
        currentPhase = null;
        RefreshPhase();
        SetupOpened?.Invoke();
    }

    private Phase GetPhase()
    {
        if (memoryRecall != null && memoryRecall.Completed)
            return Phase.Results;
        if (memoryRecall != null && memoryRecall.SessionStarted)
            return Phase.Recall;
        if (tableReference == null || !tableReference.HasReference)
            return Phase.CalibrateTable;

        return CountSavedObjects() >= 4 ? Phase.LearnPositions : Phase.DetectObjects;
    }

    private int CountSavedObjects()
    {
        if (tableReference == null || !tableReference.HasReference ||
            !File.Exists(SavedPath))
            return 0;

        try
        {
            SavedObjects saved = JsonUtility.FromJson<SavedObjects>(
                File.ReadAllText(SavedPath));
            if (saved == null ||
                saved.anchorUuid != tableReference.AnchorUuid.ToString() ||
                saved.objects == null)
                return 0;

            return saved.objects.Count;
        }
        catch (Exception error)
        {
            Debug.LogWarning("Engram Setup: Could not read positions: " + error.Message);
            return 0;
        }
    }

    // Removes the current capture and zones; the table anchor and old result files remain.
    public void RestartWithNewObjects()
    {
        if (memoryRecall == null || !memoryRecall.Completed)
            return;

        memoryRecall.ResetRecallToInventory();
        if (spatialInventory != null)
            spatialInventory.RestoreModelsToTable();
        if (spatialMarkers != null)
            spatialMarkers.ClearSavedPositions();
        if (objectVisualizer != null)
            objectVisualizer.RefreshNow();

        // Always reopen the three-page introduction for each new run.
        currentPage = 0;
        instructionsDismissed = false;
        currentPhase = null;
        RefreshPhase();
        SetupOpened?.Invoke();
        Debug.Log("Engram: New capture started. Table anchor retained.");
    }

#if UNITY_EDITOR
    [ContextMenu("Create Missing Editable Setup Pages")]
    private void CreateEditableSetupPages()
    {
        if (Application.isPlaying)
        {
            Debug.LogWarning("Engram Setup: Create pages in Edit Mode, not Play Mode.");
            return;
        }
        if (setupCanvas == null)
        {
            Debug.LogError("Engram Setup: Assign the World Space Setup Canvas first.");
            return;
        }
        if (setupCanvas.renderMode != RenderMode.WorldSpace)
        {
            Debug.LogError("Engram Setup: The Setup Canvas must use World Space rendering.");
            return;
        }
        if ((setupPage1 != null && setupPage1 == setupPage2) ||
            (setupPage1 != null && setupPage1 == setupPage3) ||
            (setupPage2 != null && setupPage2 == setupPage3))
        {
            Debug.LogError("Engram Setup: The three setup pages must be different objects.");
            return;
        }

        if (setupPage1 == null)
        {
            setupPage1 = CreateEditablePage("EngramSetupPage1",
                "ENGRAM  ·  EINRICHTUNG (1/3)",
                "1. Tisch kalibrieren\n\n" +
                "Richten Sie Ihren Blick auf die freie Tischoberfläche. " +
                "Drücken Sie A, um den Markierungspunkt auszuwählen, " +
                "und erneut A, um die Tischposition zu speichern.\n\n" +
                "Mit B können Sie die Tischreferenz zurücksetzen. " +
                "Die gespeicherte Tischposition lässt sich für weitere Durchgänge wiederverwenden.");
        }

        if (setupPage2 == null)
        {
            setupPage2 = CreateEditablePage("EngramSetupPage2",
                "ENGRAM  ·  EINRICHTUNG (2/3)",
                "2. Gegenstände erfassen\n\n" +
                "Platzieren Sie vier unterschiedliche Gegenstände auf dem Tisch. " +
                "Betrachten Sie diese aus verschiedenen Blickwinkeln, " +
                "bis ihre Positionen erkannt und markiert wurden.\n\n" +
                "X: Objektpositionen speichern\n" +
                "Y: Objektpositionen verwerfen");
        }

        if (setupPage3 == null)
        {
            setupPage3 = CreateEditablePage("EngramSetupPage3",
                "ENGRAM  ·  EINRICHTUNG (3/3)",
                "3. Gedächtnisaufgabe vorbereiten\n\n" +
                "Prägen Sie sich die Positionen der vier Gegenstände ein. " +
                "Entfernen Sie anschließend alle Gegenstände vom Tisch.\n\n" +
                "Wählen Sie „Starten“, um die Anleitung zu schließen " +
                "und mit der Einrichtung zu beginnen. Die Gedächtnisaufgabe startet später.");
        }

        if (nextButton == null)
            nextButton = CreateEditableButton(setupPage1, "WeiterButton", "Weiter");
        if (nextButtonPage2 == null)
            nextButtonPage2 = CreateEditableButton(setupPage2, "WeiterButtonPage2", "Weiter");
        if (skipButtonPage1 == null)
            skipButtonPage1 = CreateEditableButton(setupPage1, "UeberspringenButtonPage1", "Überspringen", true);
        if (skipButtonPage2 == null)
            skipButtonPage2 = CreateEditableButton(setupPage2, "UeberspringenButtonPage2", "Überspringen", true);

        // When upgrading a two-page setup, retain and relocate the existing button.
        if (understoodButton != null && understoodButton.transform.IsChildOf(setupPage2.transform))
        {
            Undo.SetTransformParent(understoodButton.transform, setupPage3.transform,
                "Move start button to third setup page");
            RectTransform buttonRect = understoodButton.GetComponent<RectTransform>();
            if (buttonRect != null)
            {
                buttonRect.anchorMin = buttonRect.anchorMax = new Vector2(1f, 0f);
                buttonRect.pivot = new Vector2(1f, 0f);
                buttonRect.anchoredPosition = new Vector2(-25f, 22f);
            }
        }
        if (understoodButton == null)
            understoodButton = CreateEditableButton(setupPage3, "StartButton", "Starten");

        if (understoodButton != null &&
            !understoodButton.transform.IsChildOf(setupPage3.transform))
        {
            Debug.LogWarning("Engram Setup: Move the Start button under Page 3 in the hierarchy.");
        }

        setupPage1.SetActive(true);
        setupPage2.SetActive(false);
        setupPage3.SetActive(false);

        EditorUtility.SetDirty(this);
        EditorSceneManager.MarkSceneDirty(gameObject.scene);
        Debug.Log("Engram Setup: Three editable pages and their buttons are ready in the scene.");
    }

    private GameObject CreateEditablePage(string pageName, string heading, string body)
    {
        GameObject page = new GameObject(pageName,
            typeof(RectTransform), typeof(Image));
        Undo.RegisterCreatedObjectUndo(page, "Create Engram setup page");
        page.transform.SetParent(setupCanvas.transform, false);

        RectTransform canvasRect = setupCanvas.GetComponent<RectTransform>();
        float canvasWidth = Mathf.Abs(canvasRect.rect.width);
        float canvasHeight = Mathf.Abs(canvasRect.rect.height);
        if (canvasWidth <= 0f) canvasWidth = 900f;
        if (canvasHeight <= 0f) canvasHeight = 600f;

        RectTransform rect = (RectTransform)page.transform;
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = new Vector2(
            Mathf.Min(760f, canvasWidth * 0.92f),
            Mathf.Min(520f, canvasHeight * 0.92f));
        rect.anchoredPosition = setupPanelOffset;

        Image image = page.GetComponent<Image>();
        image.color = panelColor;
        image.raycastTarget = false;

        AddEditableText(page.transform, "Title", heading, 30f,
            new Vector2(0.05f, 0.82f), new Vector2(0.95f, 0.95f),
            FontStyles.Bold);
        AddEditableText(page.transform, "Instructions", body, 22f,
            new Vector2(0.05f, 0.18f), new Vector2(0.95f, 0.80f),
            FontStyles.Normal);

        return page;
    }

    private void AddEditableText(Transform parent, string name, string content,
        float fontSize, Vector2 minAnchor, Vector2 maxAnchor, FontStyles fontStyle)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);
        RectTransform rect = (RectTransform)go.transform;
        rect.anchorMin = minAnchor;
        rect.anchorMax = maxAnchor;
        rect.offsetMin = rect.offsetMax = Vector2.zero;

        TextMeshProUGUI label = go.GetComponent<TextMeshProUGUI>();
        label.text = content;
        label.fontSize = fontSize;
        label.fontStyle = fontStyle;
        label.color = textColor;
        label.enableWordWrapping = true;
        label.raycastTarget = false;
    }

    private Button CreateEditableButton(GameObject parent, string buttonName, string caption, bool isSkip = false)
    {
        if (parent == null || parent.GetComponent<RectTransform>() == null)
        {
            Debug.LogError("Engram Setup: Setup page needs a RectTransform.");
            return null;
        }

        GameObject go = new GameObject(buttonName,
            typeof(RectTransform), typeof(Image), typeof(Button));
        Undo.RegisterCreatedObjectUndo(go, "Create Engram setup button");
        go.transform.SetParent(parent.transform, false);

        RectTransform rect = (RectTransform)go.transform;
        rect.anchorMin = rect.anchorMax = new Vector2(isSkip ? 0f : 1f, 0f);
        rect.pivot = new Vector2(isSkip ? 0f : 1f, 0f);
        rect.anchoredPosition = new Vector2(isSkip ? 25f : -25f, 22f);
        rect.sizeDelta = new Vector2(isSkip ? 205f : caption == "Starten" ? 205f : 175f, 56f);

        Image image = go.GetComponent<Image>();
        image.color = badgeColor;
        image.raycastTarget = true;

        Button button = go.GetComponent<Button>();
        button.targetGraphic = image;

        AddEditableText(go.transform, "Label", caption, 23f,
            Vector2.zero, Vector2.one, FontStyles.Bold);
        TextMeshProUGUI label = go.GetComponentInChildren<TextMeshProUGUI>();
        label.alignment = TextAlignmentOptions.Center;
        return button;
    }
#endif

    private void OnDestroy()
    {
        if (nextButton != null)
            nextButton.onClick.RemoveListener(ShowNextSetupPage);
        if (nextButtonPage2 != null)
            nextButtonPage2.onClick.RemoveListener(ShowNextSetupPage);
        if (skipButtonPage1 != null)
            skipButtonPage1.onClick.RemoveListener(SkipSetupInstructions);
        if (skipButtonPage2 != null)
            skipButtonPage2.onClick.RemoveListener(SkipSetupInstructions);
        if (understoodButton != null)
            understoodButton.onClick.RemoveListener(StartSetup);
        if (restartButton != null)
            restartButton.onClick.RemoveListener(RestartWithNewObjects);
    }
}
