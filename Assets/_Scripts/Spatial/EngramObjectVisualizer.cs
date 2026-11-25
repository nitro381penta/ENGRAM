using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

public class EngramObjectVisualizer : MonoBehaviour
{
    [Serializable]
    private class ModelMapping
    {
        public string cocoLabel;
        public GameObject prefab;
        public Vector3 localPositionOffset;
        public Vector3 localRotationOffset;
    }

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

    [Header("Table Reference")]
    [SerializeField] private EngramTableReference tableReference;

    [Header("COCO Class to Prefab Mapping")]
    [SerializeField] private List<ModelMapping> models = new()
    {
        new ModelMapping { cocoLabel = "apple" },
        new ModelMapping { cocoLabel = "banana" },
        new ModelMapping { cocoLabel = "book" },
        new ModelMapping { cocoLabel = "bottle" },
        new ModelMapping { cocoLabel = "bowl" },
        new ModelMapping { cocoLabel = "clock" },
        new ModelMapping { cocoLabel = "cup" },
        new ModelMapping { cocoLabel = "scissors" },
        new ModelMapping { cocoLabel = "cell phone" },
        new ModelMapping { cocoLabel = "wine glass" }
    };

    [Header("Visualization")]
    [SerializeField] private bool modelsVisible = true;
    [SerializeField, Min(0.2f)] private float refreshInterval = 0.5f;
    [SerializeField] private float globalHeightOffset = -0.01f;

    private readonly List<GameObject> instances = new();
    private string lastLoadedJson;
    private float nextRefreshTime;

    private string SavePath => Path.Combine(
        Application.persistentDataPath,
        "EngramSpatialObjects.json"
    );

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
        if (tableReference == null)
        {
            Debug.LogError(
                "Engram: Assign a Table Reference to the visualizer."
            );

            enabled = false;
            return;
        }

        RefreshNow();
    }

    private void Update()
    {
        if (Time.unscaledTime < nextRefreshTime)
            return;

        nextRefreshTime = Time.unscaledTime + refreshInterval;
        RefreshNow();
    }

    private void OnReferenceReady()
    {
        ClearInstances();
        lastLoadedJson = null;
        RefreshNow();
    }

    private void OnReferenceCleared()
    {
        ClearInstances();
        lastLoadedJson = null;
    }

    public void RefreshNow()
    {
        if (tableReference == null ||
            !tableReference.HasReference ||
            tableReference.AnchorTransform == null)
        {
            ClearInstances();
            lastLoadedJson = null;
            return;
        }

        if (!File.Exists(SavePath))
        {
            if (lastLoadedJson != null || instances.Count > 0)
            {
                ClearInstances();
                lastLoadedJson = null;
            }

            return;
        }

        try
        {
            string json = File.ReadAllText(SavePath);

            if (json == lastLoadedJson)
            {
                // Markers may have been restored after the models on startup.
                HideMarkersCoveredByModels();
                return;
            }

            ObjectData data = JsonUtility.FromJson<ObjectData>(json);

            if (data == null ||
                data.objects == null ||
                data.anchorUuid != tableReference.AnchorUuid.ToString())
            {
                ClearInstances();
                lastLoadedJson = json;
                return;
            }

            ClearInstances();

            Transform anchor = tableReference.AnchorTransform;

            // Convert the global vertical offset to anchor-local space.
            Vector3 heightOffsetLocal = anchor.InverseTransformVector(
                Vector3.up * globalHeightOffset
            );

            foreach (ObjectRecord record in data.objects)
            {
                if (record == null ||
                    string.IsNullOrWhiteSpace(record.label))
                    continue;

                ModelMapping mapping = FindMapping(record.label);

                if (mapping == null || mapping.prefab == null)
                {
                    Debug.LogWarning(
                        $"Engram: No prefab assigned for '{record.label}'."
                    );

                    continue;
                }

                // Instantiate the model relative to the table anchor.
                GameObject instance = Instantiate(
                    mapping.prefab,
                    anchor,
                    false
                );

                instance.name =
                    $"EngramModel_{record.label.Replace(' ', '_')}";

                // Apply individual and global position corrections.
                instance.transform.localPosition =
                    record.localPosition +
                    mapping.localPositionOffset +
                    heightOffsetLocal;

                // Preserve the prefab rotation with optional adjustment.
                instance.transform.localRotation =
                    mapping.prefab.transform.localRotation *
                    Quaternion.Euler(mapping.localRotationOffset);

                // Preserve the prefab's authored scale and materials.
                instance.SetActive(modelsVisible);

                instances.Add(instance);
            }

            lastLoadedJson = json;
            HideMarkersCoveredByModels();

            Debug.Log(
                $"Engram: Visualized {instances.Count} saved objects."
            );
        }
        catch (Exception exception)
        {
            Debug.LogWarning(
                $"Engram: Could not load saved object models: {exception.Message}"
            );
        }
    }

    public void SetModelsVisible(bool visible)
    {
        modelsVisible = visible;

        foreach (GameObject instance in instances)
        {
            if (instance != null)
                instance.SetActive(visible);
        }

        if (visible)
            HideMarkersCoveredByModels();
    }

    private void HideMarkersCoveredByModels()
    {
        if (!modelsVisible || tableReference == null ||
            !tableReference.HasReference || tableReference.AnchorTransform == null)
            return;

        Transform anchor = tableReference.AnchorTransform;
        const string modelPrefix = "EngramModel_";

        foreach (GameObject model in instances)
        {
            if (model == null || !model.activeInHierarchy ||
                !model.name.StartsWith(modelPrefix, StringComparison.Ordinal))
                continue;

            string label = model.name.Substring(modelPrefix.Length)
                .Replace('_', ' ');
            Transform savedMarker = anchor.Find($"Engram_{label}");

            if (savedMarker != null && savedMarker.gameObject.activeSelf)
                savedMarker.gameObject.SetActive(false);
        }
    }

    private ModelMapping FindMapping(string label)
    {
        foreach (ModelMapping mapping in models)
        {
            if (mapping != null &&
                string.Equals(
                    mapping.cocoLabel,
                    label.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                return mapping;
            }
        }

        return null;
    }

    private void ClearInstances()
    {
        foreach (GameObject instance in instances)
        {
            if (instance != null)
                Destroy(instance);
        }

        instances.Clear();
    }

    private void OnDisable()
    {
        if (tableReference != null)
        {
            tableReference.ReferenceReady -= OnReferenceReady;
            tableReference.ReferenceCleared -= OnReferenceCleared;
        }

        ClearInstances();
        lastLoadedJson = null;
    }
}