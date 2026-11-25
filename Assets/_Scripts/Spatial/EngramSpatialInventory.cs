using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using Oculus.Interaction;
using Oculus.Interaction.HandGrab;

public class EngramSpatialInventory : MonoBehaviour
{
    [Serializable]
    private class ObjectRecord
    {
        public string label;
        public Vector3 localPosition;
    }

    [Serializable]
    private class ObjectData
    {
        public string anchorUuid;
        public List<ObjectRecord> objects;
    }

    [Serializable]
    private class ObjectScaleOverride
    {
        public string cocoLabel;
        [Range(0.2f, 1f)] public float multiplier = 1f;
    }

    private class InventoryItem
    {
        public string Label;
        public Vector3 ReferenceLocalPosition;
        public Transform Model;
        public Transform OriginalParent;
        public Vector3 OriginalLocalPosition;
        public Quaternion OriginalLocalRotation;
        public Vector3 OriginalLocalScale;
        public Vector3 StartPosition;
        public Quaternion StartRotation;
        public Vector3 StartScale;
        public Vector3 TargetPosition;
        public Quaternion TargetRotation;
        public Vector3 TargetScale;
        public readonly List<HighlightRendererState> Highlights = new();
    }

    private class HighlightState
    {
        public int MaterialIndex;
        public MaterialPropertyBlock OriginalBlock;
        public MaterialPropertyBlock CurrentBlock;
        public Color OriginalColor;
        public Color OriginalEmission;
        public bool HasBaseColor;
        public bool HasLegacyColor;
        public bool HasEmission;
    }

    private class HighlightRendererState
    {
        public Renderer Renderer;
        public Material[] OriginalMaterials;
        public Material[] HighlightMaterials;
        public readonly List<HighlightState> MaterialStates = new();
    }

    [Header("Existing Engram Components")]
    [SerializeField] private EngramTableReference tableReference;
    [SerializeField] private EngramObjectVisualizer objectVisualizer;

    [Header("Inventory Prefab")]
    [SerializeField] private GameObject cylinderVisual;
    [SerializeField] private Transform[] snapPoints = new Transform[4];
    [SerializeField] private TMP_Text[] objectTexts = new TMP_Text[4];

    [Header("Object Labels")]
    [SerializeField] private bool faceLabelsTowardsViewer = false;
    [SerializeField] private Transform viewerTransform;

    [Header("Position Relative to Table Anchor")]
    [SerializeField] private Vector3 anchorLocalPosition = new(0.75f, 0.275f, 0f);
    [SerializeField] private Vector3 anchorLocalRotation = Vector3.zero;

    [Header("Object Display")]
    [SerializeField] private Vector3 maximumObjectSize = new(0.14f, 0.085f, 0.14f);
    [SerializeField, Min(0.1f)] private float maximumScaleMultiplier = 3f;
    [SerializeField] private List<ObjectScaleOverride> individualObjectScales = new()
    {
        new ObjectScaleOverride { cocoLabel = "apple", multiplier = 0.72f }
    };
    [SerializeField] private bool rotateToSnapPoint = false;
    [SerializeField] private bool requireFourObjects = true;

    [Header("Transfer Animation")]
    [SerializeField, Min(0.1f)] private float flightDuration = 0.9f;
    [SerializeField, Min(0f)] private float staggerDelay = 0.12f;
    [SerializeField, Min(0f)] private float flightArcHeight = 0.09f;
    [SerializeField, Min(0.1f)] private float highlightDuration = 0.45f;
    [SerializeField] private Color highlightColor = new(0.45f, 1f, 0.95f, 1f);
    [SerializeField, Range(0f, 0.2f)] private float highlightScalePulse = 0.12f;
    [SerializeField, Range(0f, 5f)] private float highlightEmissionIntensity = 2.5f;

    [Header("Docking Sound")]
    [SerializeField] private bool dockingAudioEnabled = true;
    [SerializeField] private AudioClip dockingSound;
    [SerializeField, Range(0f, 1f)] private float dockingVolume = 0.65f;
    [SerializeField] private AudioSource dockingAudioSource;

    [Header("Testing")]
    [SerializeField] private bool autoTransferAfterNewCapture = false;
    [SerializeField] private bool enableRightThumbstickTest = true;

    private readonly List<InventoryItem> items = new();
    private readonly List<GameObject> hiddenMarkers = new();
    private Transform originalInventoryParent;
    private Coroutine transferRoutine;
    private float nextSaveCheck;
    private string lastObservedJson;
    private bool inventoryAttached;
    private bool objectsDocked;
    private bool recallStarted;
    private float recallAvailableAt;

    public bool ObjectsDocked => objectsDocked;
    public bool RecallStarted => recallStarted;

    public event Action ObjectDocked;
    // Raised once after every saved object has arrived in its inventory slot.
    public event Action AllObjectsDocked;

    public struct RecallObject
    {
        public string CocoLabel;
        public Transform Model;
        public int SlotIndex;
        public Vector3 ReferenceLocalPosition;
        public Vector3 VisualOffsetLocal;
        public Quaternion OriginalLocalRotation;
        public Vector3 OriginalLocalScale;
        public Vector3 DockWorldPosition;
        public Quaternion DockWorldRotation;
        public Vector3 DockLocalScale;
    }

    // Hand over the already instantiated models, not duplicate prefabs.
    public bool TryBeginRecall(List<RecallObject> result)
    {
        if (result == null || recallStarted || !objectsDocked ||
            Time.time < recallAvailableAt || transferRoutine != null ||
            tableReference == null ||
            !tableReference.HasReference || items.Count != 4)
            return false;

        foreach (InventoryItem item in items)
        {
            if (item.Model == null)
                return false;
        }

        // Stop lingering highlight coroutines before users can grab models.
        StopAllCoroutines();
        foreach (InventoryItem item in items)
        {
            ResetHighlight(item);
            item.Model.localScale = item.TargetScale;
        }

        result.Clear();
        for (int i = 0; i < items.Count; i++)
        {
            InventoryItem item = items[i];
            result.Add(new RecallObject
            {
                CocoLabel = item.Label,
                Model = item.Model,
                SlotIndex = i,
                ReferenceLocalPosition = item.ReferenceLocalPosition,
                VisualOffsetLocal = item.OriginalLocalPosition -
                    item.ReferenceLocalPosition,
                OriginalLocalRotation = item.OriginalLocalRotation,
                OriginalLocalScale = item.OriginalLocalScale,
                DockWorldPosition = item.Model.position,
                DockWorldRotation = item.Model.rotation,
                DockLocalScale = item.TargetScale
            });
        }

        recallStarted = true;
        Debug.Log("Engram Inventory: Recall interaction enabled.");
        return true;
    }

    public void SetRecallSlotOccupied(int slotIndex, bool occupied)
    {
        if (objectTexts == null || slotIndex < 0 ||
            slotIndex >= objectTexts.Length || objectTexts[slotIndex] == null)
            return;

        objectTexts[slotIndex].gameObject.SetActive(occupied);
    }

    public void EndRecall()
    {
        recallStarted = false;
    }
    public bool IsTransferring => transferRoutine != null;
    public string SavedMessage { get; private set; } = "";

    private string SavePath => Path.Combine(
        Application.persistentDataPath, "EngramSpatialObjects.json");

    private void Reset()
    {
        // Match the names already used by the inventory prefab.
        Transform[] children = GetComponentsInChildren<Transform>(true);
        TMP_Text[] texts = GetComponentsInChildren<TMP_Text>(true);

        snapPoints = new Transform[4];
        objectTexts = new TMP_Text[4];

        foreach (Transform child in children)
        {
            if (child.name == "SnapCylinder" && cylinderVisual == null)
                cylinderVisual = child.gameObject;

            for (int i = 0; i < 4; i++)
            {
                if (child.name == $"SnapCylinder_{i}")
                    snapPoints[i] = child;
            }
        }

        foreach (TMP_Text label in texts)
        {
            for (int i = 0; i < 4; i++)
            {
                if (label.name == $"SnapObjectText_{i}")
                    objectTexts[i] = label;
            }
        }
    }

    private void Awake()
    {
        originalInventoryParent = transform.parent;
        ClearLabels();
    }

    private void OnEnable()
    {
        if (tableReference != null)
        {
            tableReference.ReferenceReady += OnReferenceReady;
            tableReference.ReferenceCleared += OnReferenceCleared;
        }
    }

    private void Start()
    {
        if (tableReference == null || objectVisualizer == null)
        {
            Debug.LogError("Engram Inventory: Assign Table Reference and Object Visualizer.");
            enabled = false;
            return;
        }

        if (tableReference.HasReference)
            OnReferenceReady();
        else if (cylinderVisual != null)
            cylinderVisual.SetActive(false);
    }

    private void Update()
    {
        // Do not restart the inventory during recall, but detect deleted models.
        if (recallStarted)
        {
            foreach (InventoryItem item in items)
            {
                if (item.Model == null)
                {
                    ResetInventory(false);
                    break;
                }
            }
            return;
        }

        // Test an already saved capture directly on Quest without recording again.
        if (enableRightThumbstickTest &&
            OVRInput.GetDown(OVRInput.Button.PrimaryThumbstick,
                OVRInput.Controller.RTouch))
        {
            MoveSavedObjectsToInventory();
        }

        if (items.Count > 0)
        {
            foreach (InventoryItem item in items)
            {
                if (item.Model == null)
                {
                    ResetInventory(false);
                    break;
                }
            }
        }

        if (!autoTransferAfterNewCapture || !inventoryAttached ||
            Time.unscaledTime < nextSaveCheck)
            return;

        nextSaveCheck = Time.unscaledTime + 0.3f;
        string json = ReadSavedJson();

        if (json == lastObservedJson)
            return;

        lastObservedJson = json;
        if (!string.IsNullOrEmpty(json))
            MoveSavedObjectsToInventory();
    }

    private void LateUpdate()
    {
        if (!faceLabelsTowardsViewer || objectTexts == null)
            return;

        if (viewerTransform == null)
        {
            Camera mainCamera = Camera.main;
            if (mainCamera == null)
                return;
            viewerTransform = mainCamera.transform;
        }

        foreach (TMP_Text label in objectTexts)
        {
            if (label == null || !label.gameObject.activeInHierarchy)
                continue;

            // Keep labels upright while turning their front towards the viewer.
            Vector3 direction = label.transform.position - viewerTransform.position;
            direction.y = 0f;

            if (direction.sqrMagnitude > 0.0001f)
                label.transform.rotation = Quaternion.LookRotation(direction, Vector3.up);
        }
    }

    private void OnReferenceReady()
    {
        if (!tableReference.HasReference || tableReference.AnchorTransform == null)
            return;

        ResetInventory(false);

        // The table anchor persists; the inventory needs no separate anchor.
        transform.SetParent(tableReference.AnchorTransform, false);
        transform.localPosition = anchorLocalPosition;
        transform.localRotation = Quaternion.Euler(anchorLocalRotation);
        inventoryAttached = true;
        lastObservedJson = ReadSavedJson();

        if (cylinderVisual != null)
            cylinderVisual.SetActive(true);
    }

    private void OnReferenceCleared()
    {
        ResetInventory(false);
        inventoryAttached = false;
        lastObservedJson = null;

        // Detach before the table anchor GameObject is destroyed.
        transform.SetParent(originalInventoryParent, true);
        if (cylinderVisual != null)
            cylinderVisual.SetActive(false);
    }

    [ContextMenu("Engram/Move Saved Objects To Inventory")]
    public void MoveSavedObjectsToInventory()
    {
        if (!inventoryAttached || tableReference == null ||
            !tableReference.HasReference || objectVisualizer == null)
        {
            Debug.LogWarning("Engram Inventory: Table anchor is not ready.");
            return;
        }

        if (!HasAllSlotReferences())
        {
            Debug.LogError("Engram Inventory: Assign four SnapCylinder_0..3 and SnapObjectText_0..3 references.");
            return;
        }

        string json = ReadSavedJson();
        if (string.IsNullOrEmpty(json))
        {
            Debug.LogWarning("Engram Inventory: No saved object positions found.");
            return;
        }

        ObjectData data;
        try
        {
            data = JsonUtility.FromJson<ObjectData>(json);
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"Engram Inventory: Invalid JSON: {exception.Message}");
            return;
        }

        if (data == null || data.objects == null ||
            data.anchorUuid != tableReference.AnchorUuid.ToString())
        {
            Debug.LogWarning("Engram Inventory: Saved objects do not match the table anchor.");
            return;
        }

        if (data.objects.Count == 0 || data.objects.Count > 4 ||
            (requireFourObjects && data.objects.Count != 4))
        {
            Debug.LogWarning($"Engram Inventory: Expected {(requireFourObjects ? "exactly four" : "one to four")} objects; found {data.objects.Count}.");
            return;
        }

        ResetInventory(true);
        objectVisualizer.RefreshNow();
        objectVisualizer.SetModelsVisible(true);

        Transform anchor = tableReference.AnchorTransform;
        var prepared = new List<InventoryItem>();
        var labels = new List<string>();
        var uniqueLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ObjectRecord record in data.objects)
        {
            if (record == null || string.IsNullOrWhiteSpace(record.label) ||
                !uniqueLabels.Add(record.label.Trim()))
            {
                Debug.LogWarning("Engram Inventory: Missing or duplicate object class.");
                return;
            }

            string label = record.label.Trim();
            string modelName = $"EngramModel_{label.Replace(' ', '_')}";
            Transform model = anchor.Find(modelName);

            if (model == null)
            {
                Debug.LogWarning($"Engram Inventory: Model '{modelName}' not found. Check its prefab mapping in EngramObjectVisualizer.");
                return;
            }

            // Do not allow interaction while the model flies into the inventory.
            SetModelGrabEnabled(model, false);

            InventoryItem item = new InventoryItem
            {
                Label = label,
                ReferenceLocalPosition = record.localPosition,
                Model = model,
                OriginalParent = model.parent,
                OriginalLocalPosition = model.localPosition,
                OriginalLocalRotation = model.localRotation,
                OriginalLocalScale = model.localScale
            };

            prepared.Add(item);
            labels.Add(ToGerman(label));
        }

        items.AddRange(prepared);
        lastObservedJson = json;
        SavedMessage = "Gespeichert: " + JoinGerman(labels) + ".";
        HideSavedMarkers(data.objects);
        transferRoutine = StartCoroutine(AnimateTransfer());
        Debug.Log($"Engram Inventory: {SavedMessage}");
    }

    private IEnumerator AnimateTransfer()
    {
        for (int i = 0; i < items.Count; i++)
        {
            InventoryItem item = items[i];
            Transform model = item.Model;
            Renderer[] renderers = model.GetComponentsInChildren<Renderer>(true);
            Bounds bounds = CalculateBounds(renderers, model.position);
            Vector3 size = bounds.size;

            float factor = Mathf.Min(
                SafeRatio(maximumObjectSize.x, size.x),
                SafeRatio(maximumObjectSize.y, size.y),
                SafeRatio(maximumObjectSize.z, size.z),
                maximumScaleMultiplier);

            // Apply presentation-only size adjustments after fitting the chamber.
            factor *= GetIndividualScale(item.Label);
            factor = Mathf.Max(0.01f, factor);
            item.StartPosition = model.position;
            item.StartRotation = model.rotation;

            // Keep the same GameObject created by EngramObjectVisualizer.
            model.SetParent(transform, true);
            item.StartScale = model.localScale;
            item.TargetScale = item.StartScale * factor;
            item.TargetRotation = rotateToSnapPoint
                ? snapPoints[i].rotation : item.StartRotation;

            Vector3 centerOffset = bounds.center - item.StartPosition;
            Vector3 rotatedOffset = item.TargetRotation *
                (Quaternion.Inverse(item.StartRotation) * centerOffset) * factor;
            item.TargetPosition = snapPoints[i].position - rotatedOffset;
        }

        float totalTime = flightDuration + staggerDelay * (items.Count - 1);
        float elapsed = 0f;
        bool[] arrived = new bool[items.Count];

        while (elapsed < totalTime)
        {
            elapsed += Time.deltaTime;
            for (int i = 0; i < items.Count; i++)
            {
                InventoryItem item = items[i];
                if (item.Model == null || arrived[i])
                    continue;

                float t = Mathf.Clamp01((elapsed - i * staggerDelay) / flightDuration);
                float eased = t * t * (3f - 2f * t);
                Vector3 arc = Vector3.up * (Mathf.Sin(t * Mathf.PI) * flightArcHeight);

                item.Model.position = Vector3.Lerp(item.StartPosition,
                    item.TargetPosition, eased) + arc;
                item.Model.rotation = Quaternion.Slerp(item.StartRotation,
                    item.TargetRotation, eased);
                item.Model.localScale = Vector3.Lerp(item.StartScale,
                    item.TargetScale, eased);

                if (t >= 1f && !arrived[i])
                {
                    arrived[i] = true;
                    objectTexts[i].text = ToGerman(item.Label);
                    objectTexts[i].gameObject.SetActive(true);
                    // Use the central audio manager when subscribed; otherwise keep the old sound.
                    if (ObjectDocked != null)
                        ObjectDocked.Invoke();
                    else
                        PlayDockingSound();

                    StartCoroutine(HighlightModel(item));
                }
            }
            yield return null;
        }

        for (int i = 0; i < items.Count; i++)
        {
            InventoryItem item = items[i];
            if (item.Model == null || arrived[i])
                continue;
            item.Model.position = item.TargetPosition;
            item.Model.rotation = item.TargetRotation;
            item.Model.localScale = item.TargetScale;
        }

        objectsDocked = true;
        recallAvailableAt = Time.time + highlightDuration;
        AllObjectsDocked?.Invoke();
        transferRoutine = null;
        Debug.Log("Engram Inventory: All models docked.");
    }

    private IEnumerator HighlightModel(InventoryItem item)
    {
        PrepareHighlights(item);
        float elapsed = 0f;

        while (elapsed < highlightDuration && item.Model != null)
        {
            elapsed += Time.deltaTime;
            float phase = Mathf.Clamp01(elapsed / highlightDuration);
            float intensity = Mathf.Sin(phase * Mathf.PI);
            SetHighlight(item, intensity);
            item.Model.localScale = item.TargetScale *
                (1f + highlightScalePulse * intensity);
            yield return null;
        }

        ResetHighlight(item);
        if (item.Model != null)
            item.Model.localScale = item.TargetScale;
    }

    [ContextMenu("Engram/Restore Models To Table")]
    public void RestoreModelsToTable()
    {
        ResetInventory(true);
    }

    private void ResetInventory(bool showMarkers)
    {
        StopAllCoroutines();
        transferRoutine = null;
        objectsDocked = false;
        recallStarted = false;
        recallAvailableAt = 0f;

        foreach (InventoryItem item in items)
        {
            ResetHighlight(item);
            SetModelGrabEnabled(item.Model, false);
            if (item.Model == null || item.OriginalParent == null)
                continue;

            item.Model.SetParent(item.OriginalParent, false);
            item.Model.localPosition = item.OriginalLocalPosition;
            item.Model.localRotation = item.OriginalLocalRotation;
            item.Model.localScale = item.OriginalLocalScale;
        }

        items.Clear();
        ClearLabels();
        SavedMessage = "";

        foreach (GameObject marker in hiddenMarkers)
        {
            if (marker != null && showMarkers)
                marker.SetActive(true);
        }
        hiddenMarkers.Clear();
    }

    private void HideSavedMarkers(List<ObjectRecord> records)
    {
        Transform anchor = tableReference.AnchorTransform;
        foreach (ObjectRecord record in records)
        {
            Transform marker = anchor.Find($"Engram_{record.label}");
            if (marker != null && marker.gameObject.activeSelf)
            {
                hiddenMarkers.Add(marker.gameObject);
                marker.gameObject.SetActive(false);
            }
        }
    }

    private static Bounds CalculateBounds(Renderer[] renderers, Vector3 fallback)
    {
        bool found = false;
        Bounds bounds = new Bounds(fallback, Vector3.zero);
        foreach (Renderer renderer in renderers)
        {
            if (renderer == null || !renderer.enabled)
                continue;

            if (!found)
            {
                bounds = renderer.bounds;
                found = true;
            }
            else
                bounds.Encapsulate(renderer.bounds);
        }
        return bounds;
    }

    private void PrepareHighlights(InventoryItem item)
    {
        foreach (Renderer renderer in item.Model.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer == null)
                continue;

            Material[] originals = renderer.sharedMaterials;
            Material[] temporary = new Material[originals.Length];
            var rendererState = new HighlightRendererState
            {
                Renderer = renderer,
                OriginalMaterials = originals,
                HighlightMaterials = temporary
            };

            for (int index = 0; index < originals.Length; index++)
            {
                Material original = originals[index];
                if (original == null)
                    continue;

                // Clone per instance so the imported prefab materials stay unchanged.
                Material highlightMaterial = new Material(original);
                highlightMaterial.name = original.name + " (Engram Highlight)";
                bool hasEmission = highlightMaterial.HasProperty("_EmissionColor");
                if (hasEmission)
                    highlightMaterial.EnableKeyword("_EMISSION");
                temporary[index] = highlightMaterial;

                var state = new HighlightState
                {
                    MaterialIndex = index,
                    OriginalBlock = new MaterialPropertyBlock(),
                    CurrentBlock = new MaterialPropertyBlock(),
                    HasBaseColor = original.HasProperty("_BaseColor"),
                    HasLegacyColor = original.HasProperty("_Color"),
                    HasEmission = hasEmission
                };

                state.OriginalColor = state.HasBaseColor
                    ? original.GetColor("_BaseColor")
                    : state.HasLegacyColor
                        ? original.GetColor("_Color") : Color.white;
                state.OriginalEmission = state.HasEmission
                    ? original.GetColor("_EmissionColor") : Color.black;

                renderer.GetPropertyBlock(state.OriginalBlock, index);
                rendererState.MaterialStates.Add(state);
            }

            renderer.sharedMaterials = temporary;
            item.Highlights.Add(rendererState);
        }
    }

    private void SetHighlight(InventoryItem item, float intensity)
    {
        foreach (HighlightRendererState rendererState in item.Highlights)
        {
            Renderer renderer = rendererState.Renderer;
            if (renderer == null)
                continue;

            foreach (HighlightState state in rendererState.MaterialStates)
            {
                // The brighter tint is also visible on shaders without emission.
                renderer.GetPropertyBlock(state.CurrentBlock, state.MaterialIndex);
                Color color = new Color(highlightColor.r, highlightColor.g,
                    highlightColor.b, state.OriginalColor.a);
                Color tinted = Color.Lerp(state.OriginalColor, color,
                    0.9f * intensity);

                if (state.HasBaseColor)
                    state.CurrentBlock.SetColor("_BaseColor", tinted);
                else if (state.HasLegacyColor)
                    state.CurrentBlock.SetColor("_Color", tinted);

                if (state.HasEmission)
                {
                    Color emission = state.OriginalEmission +
                        new Color(highlightColor.r, highlightColor.g,
                            highlightColor.b, 1f) *
                        (highlightEmissionIntensity * intensity);
                    state.CurrentBlock.SetColor("_EmissionColor", emission);
                }

                renderer.SetPropertyBlock(state.CurrentBlock, state.MaterialIndex);
            }
        }
    }

    private void ResetHighlight(InventoryItem item)
    {
        foreach (HighlightRendererState rendererState in item.Highlights)
        {
            if (rendererState.Renderer != null)
            {
                foreach (HighlightState state in rendererState.MaterialStates)
                {
                    rendererState.Renderer.SetPropertyBlock(
                        state.OriginalBlock, state.MaterialIndex);
                }

                rendererState.Renderer.sharedMaterials =
                    rendererState.OriginalMaterials;
            }

            foreach (Material material in rendererState.HighlightMaterials)
            {
                if (material != null)
                    Destroy(material);
            }
        }

        item.Highlights.Clear();
    }

    private static void SetModelGrabEnabled(Transform model, bool enabled)
    {
        if (model == null)
            return;

        foreach (HandGrabInteractable interactable in
            model.GetComponentsInChildren<HandGrabInteractable>(true))
        {
            if (interactable != null)
                interactable.enabled = enabled;
        }

        // Controller ray placement is handled by EngramMemoryRecall.
        foreach (GrabInteractable interactable in
            model.GetComponentsInChildren<GrabInteractable>(true))
        {
            if (interactable != null)
                interactable.enabled = false;
        }
    }

    public void SetDockingAudioEnabled(bool enabled)
    {
        dockingAudioEnabled = enabled;
        if (dockingAudioSource != null)
        {
            if (!enabled)
                dockingAudioSource.Stop();
            dockingAudioSource.mute = !enabled;
        }
    }

    private void PlayDockingSound()
    {
        if (!dockingAudioEnabled || dockingSound == null || dockingVolume <= 0f)
            return;

        if (dockingAudioSource == null)
        {
            dockingAudioSource = GetComponent<AudioSource>();
            if (dockingAudioSource == null)
                dockingAudioSource = gameObject.AddComponent<AudioSource>();

            dockingAudioSource.playOnAwake = false;
            dockingAudioSource.spatialBlend = 0f;
        }

        // PlayOneShot supports overlapping arrivals without cutting off a clip.
        dockingAudioSource.PlayOneShot(dockingSound, dockingVolume);
    }

    private bool HasAllSlotReferences()
    {
        if (snapPoints == null || objectTexts == null ||
            snapPoints.Length != 4 || objectTexts.Length != 4)
            return false;

        for (int i = 0; i < 4; i++)
        {
            if (snapPoints[i] == null || objectTexts[i] == null)
                return false;
        }
        return true;
    }

    private void ClearLabels()
    {
        if (objectTexts == null)
            return;

        foreach (TMP_Text label in objectTexts)
        {
            if (label == null)
                continue;
            label.text = "";
            label.gameObject.SetActive(false);
        }
    }

    private string ReadSavedJson()
    {
        try
        {
            return File.Exists(SavePath) ? File.ReadAllText(SavePath) : null;
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"Engram Inventory: Could not read saved objects: {exception.Message}");
            return null;
        }
    }

    private float GetIndividualScale(string cocoLabel)
    {
        if (individualObjectScales == null)
            return 1f;

        foreach (ObjectScaleOverride entry in individualObjectScales)
        {
            if (entry != null &&
                !string.IsNullOrWhiteSpace(entry.cocoLabel) &&
                string.Equals(entry.cocoLabel.Trim(), cocoLabel,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Mathf.Clamp(entry.multiplier, 0.2f, 1f);
            }
        }

        return 1f;
    }

    private static float SafeRatio(float maximum, float actual)
    {
        return actual > 0.00001f ? maximum / actual : float.MaxValue;
    }

    private static string ToGerman(string label)
    {
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

    private static string JoinGerman(List<string> names)
    {
        if (names.Count == 0) return "";
        if (names.Count == 1) return names[0];
        if (names.Count == 2) return names[0] + " und " + names[1];
        return string.Join(", ", names.GetRange(0, names.Count - 1)) +
               " und " + names[names.Count - 1];
    }

    private void OnDisable()
    {
        if (tableReference != null)
        {
            tableReference.ReferenceReady -= OnReferenceReady;
            tableReference.ReferenceCleared -= OnReferenceCleared;
        }
        ResetInventory(false);
    }
}
