using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;

public class EngramRecallZones : MonoBehaviour
{
    [Serializable]
    public class ZoneObjectResult
    {
        public string cocoLabel;
        public Vector3 referenceLocalPosition;
        public Vector3 recalledLocalPosition;

        public float lengthCm;
        public float widthCm;
        public float zoneRadiusCm;
        public float baseZoneRadiusCm;
        public float additionalToleranceCm;

        public float absoluteErrorCm;
        public float zoneErrorCm;
    }

    [Serializable]
    public class ZoneResult
    {
        public int schemaVersion = 2;
        public string timestampUtc;
        public string anchorUuid;

        public float meanAbsoluteErrorCm;
        public float meanZoneErrorCm;

        public EngramMemoryRecall.RecallResult originalResult;
        public List<ZoneObjectResult> objects = new();
    }

    [Header("Engram")]
    [SerializeField] private EngramMemoryRecall memoryRecall;
    [SerializeField] private EngramTableReference tableReference;

    [Header("Result UI")]
    [SerializeField] private TMP_Text meanErrorText;
    [SerializeField] private TMP_Text meanErrorLabel;
    [SerializeField] private TMP_Text[] resultObjectTexts = new TMP_Text[4];
    [SerializeField] private TMP_Text saveStatusText;

    [Header("Reference Zones")]
    [SerializeField, Min(0.005f)]
    private float minimumRadius = 0.015f;

    [SerializeField, Min(0.01f)]
    private float maximumRadius = 0.12f;

    [Header("Optional Tolerance Calibration")]
    [SerializeField, Range(0f, 5f)]
    private float appleAdditionalToleranceCm = 3.2f;

    [SerializeField, Range(0f, 5f)]
    private float bananaAdditionalToleranceCm = 4f;

    [SerializeField, Range(0f, 5f)]
    private float clockAdditionalToleranceCm = 3.2f;

    [SerializeField, Range(0f, 5f)]
    private float scissorsAdditionalToleranceCm = 1.6f;

    [SerializeField, Range(0f, 5f)]
    private float wineGlassAdditionalToleranceCm = 1f;

    [Header("Debug Visualization")]
    [SerializeField] private bool showZonesAfterCompletion = true;

    [SerializeField] private Color zoneColor =
        new Color(0.2f, 0.9f, 0.85f, 0.85f);

    [SerializeField, Min(0.001f)]
    private float lineWidth = 0.003f;

    [SerializeField] private float surfaceOffset = 0.012f;

    private readonly List<GameObject> zoneVisuals = new();

    private ZoneResult currentResult;
    private Material zoneMaterial;
    private bool resultSaved;

    private void Awake()
    {
        if (memoryRecall == null)
            memoryRecall = FindFirstObjectByType<EngramMemoryRecall>();

        if (tableReference == null)
            tableReference = FindFirstObjectByType<EngramTableReference>();
    }

    private void OnEnable()
    {
        if (memoryRecall != null)
        {
            memoryRecall.RecallCompleted += OnRecallCompleted;
            memoryRecall.RecallReset += ClearResults;
            memoryRecall.RecallStarted += ClearResults;
        }

        if (tableReference != null)
            tableReference.ReferenceCleared += ClearResults;
    }

    private void OnRecallCompleted(
        EngramMemoryRecall.RecallResult original)
    {
        ClearVisuals();

        if (original == null ||
            original.objects == null ||
            tableReference == null ||
            !tableReference.HasReference)
        {
            return;
        }

        ZoneResult result = new ZoneResult
        {
            timestampUtc = original.timestampUtc,
            anchorUuid = original.anchorUuid,
            meanAbsoluteErrorCm = original.meanHorizontalErrorCm,
            originalResult = original
        };

        float zoneSum = 0f;

        foreach (EngramMemoryRecall.ObjectResult item
                 in original.objects)
        {
            Transform model = FindModel(item.cocoLabel);

            if (model == null ||
                !TryGetFootprint(model, out Vector2 size))
            {
                Debug.LogWarning(
                    "Engram Zones: Cannot measure " +
                    item.cocoLabel
                );

                currentResult = null;
                return;
            }

            float radius = Mathf.Sqrt(
                size.x * size.y / Mathf.PI
            );

            radius = Mathf.Clamp(
                radius,
                minimumRadius,
                Mathf.Max(minimumRadius, maximumRadius)
            );

            float baseRadiusCm = radius * 100f;
            float additionalToleranceCm = 0f;

            if (string.Equals(item.cocoLabel, "apple",
                    StringComparison.OrdinalIgnoreCase))
            {
                additionalToleranceCm = Mathf.Max(0f, appleAdditionalToleranceCm);
            }
            else if (string.Equals(item.cocoLabel, "banana",
                         StringComparison.OrdinalIgnoreCase))
            {
                additionalToleranceCm = Mathf.Max(0f, bananaAdditionalToleranceCm);
            }
            else if (string.Equals(item.cocoLabel, "clock",
                         StringComparison.OrdinalIgnoreCase))
            {
                additionalToleranceCm = Mathf.Max(0f, clockAdditionalToleranceCm);
            }
            else if (string.Equals(item.cocoLabel, "scissors",
                         StringComparison.OrdinalIgnoreCase))
            {
                additionalToleranceCm = Mathf.Max(0f, scissorsAdditionalToleranceCm);
            }
            else if (string.Equals(item.cocoLabel, "wine glass",
                         StringComparison.OrdinalIgnoreCase))
            {
                additionalToleranceCm = Mathf.Max(0f, wineGlassAdditionalToleranceCm);
            }

            float radiusCm = baseRadiusCm + additionalToleranceCm;

            float zoneError = Mathf.Max(
                0f,
                item.horizontalErrorCm - radiusCm
            );

            ZoneObjectResult zone = new ZoneObjectResult
            {
                cocoLabel = item.cocoLabel,

                referenceLocalPosition =
                    item.referenceLocalPosition,

                recalledLocalPosition =
                    item.recalledLocalPosition,

                lengthCm = size.x * 100f,
                widthCm = size.y * 100f,
                zoneRadiusCm = radiusCm,
                baseZoneRadiusCm = baseRadiusCm,
                additionalToleranceCm = additionalToleranceCm,

                absoluteErrorCm = item.horizontalErrorCm,
                zoneErrorCm = zoneError
            };

            result.objects.Add(zone);
            zoneSum += zoneError;

            Debug.Log(
                $"Engram Zones: {item.cocoLabel} | " +
                $"Size: {zone.lengthCm:F1} x " +
                $"{zone.widthCm:F1} cm | " +
                $"Radius: {radiusCm:F1} cm " +
                $"(base {baseRadiusCm:F1}, extra {additionalToleranceCm:F1}) | " +
                $"Absolute: {item.horizontalErrorCm:F1} cm | " +
                $"Zone: {zoneError:F1} cm"
            );
        }

        if (result.objects.Count != original.objects.Count ||
            result.objects.Count == 0)
        {
            return;
        }

        result.meanZoneErrorCm =
            zoneSum / result.objects.Count;

        currentResult = result;

        SaveResults(result);

        if (showZonesAfterCompletion)
            CreateZoneVisuals(result);
    }

    private Transform FindModel(string label)
    {
        if (tableReference == null ||
            !tableReference.HasReference)
        {
            return null;
        }

        Transform anchor = tableReference.AnchorTransform;

        string modelName =
            "EngramModel_" + label.Replace(' ', '_');

        Transform direct = anchor.Find(modelName);

        if (direct != null)
            return direct;

        foreach (Transform child in
                 anchor.GetComponentsInChildren<Transform>(true))
        {
            if (child.name == modelName)
                return child;
        }

        return null;
    }

    private bool TryGetFootprint(
        Transform model,
        out Vector2 size)
    {
        size = Vector2.zero;

        Vector3 axisX = Vector3.ProjectOnPlane(
            model.right,
            Vector3.up
        );

        if (axisX.sqrMagnitude < 0.001f)
            axisX = Vector3.right;

        axisX.Normalize();

        Vector3 axisZ = Vector3.Cross(
            Vector3.up,
            axisX
        ).normalized;

        float minX = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        float minZ = float.PositiveInfinity;
        float maxZ = float.NegativeInfinity;

        bool found = false;

        foreach (MeshFilter filter in
                 model.GetComponentsInChildren<MeshFilter>(true))
        {
            if (filter.sharedMesh == null ||
                !filter.gameObject.activeInHierarchy)
            {
                continue;
            }

            IncludeBounds(
                filter.sharedMesh.bounds,
                filter.transform,
                model.position,
                axisX,
                axisZ,
                ref minX,
                ref maxX,
                ref minZ,
                ref maxZ
            );

            found = true;
        }

        foreach (SkinnedMeshRenderer renderer in
                 model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (!renderer.enabled ||
                !renderer.gameObject.activeInHierarchy)
            {
                continue;
            }

            IncludeBounds(
                renderer.localBounds,
                renderer.transform,
                model.position,
                axisX,
                axisZ,
                ref minX,
                ref maxX,
                ref minZ,
                ref maxZ
            );

            found = true;
        }

        // Fallback for models without accessible meshes.
        if (!found)
        {
            foreach (Collider collider in
                     model.GetComponentsInChildren<Collider>(true))
            {
                if (!collider.enabled ||
                    !collider.gameObject.activeInHierarchy)
                {
                    continue;
                }

                Bounds bounds = collider.bounds;

                IncludeWorldBounds(
                    bounds,
                    model.position,
                    axisX,
                    axisZ,
                    ref minX,
                    ref maxX,
                    ref minZ,
                    ref maxZ
                );

                found = true;
            }
        }

        if (!found)
            return false;

        float length = maxX - minX;
        float width = maxZ - minZ;

        if (float.IsNaN(length) ||
            float.IsNaN(width) ||
            float.IsInfinity(length) ||
            float.IsInfinity(width) ||
            length <= 0f ||
            width <= 0f)
        {
            return false;
        }

        size = new Vector2(length, width);
        return true;
    }

    private static void IncludeBounds(
        Bounds bounds,
        Transform source,
        Vector3 origin,
        Vector3 axisX,
        Vector3 axisZ,
        ref float minX,
        ref float maxX,
        ref float minZ,
        ref float maxZ)
    {
        Vector3 min = bounds.min;
        Vector3 max = bounds.max;

        for (int x = 0; x < 2; x++)
        {
            for (int y = 0; y < 2; y++)
            {
                for (int z = 0; z < 2; z++)
                {
                    Vector3 local = new Vector3(
                        x == 0 ? min.x : max.x,
                        y == 0 ? min.y : max.y,
                        z == 0 ? min.z : max.z
                    );

                    Vector3 world = source.TransformPoint(local);
                    Vector3 offset = world - origin;

                    float px = Vector3.Dot(offset, axisX);
                    float pz = Vector3.Dot(offset, axisZ);

                    minX = Mathf.Min(minX, px);
                    maxX = Mathf.Max(maxX, px);
                    minZ = Mathf.Min(minZ, pz);
                    maxZ = Mathf.Max(maxZ, pz);
                }
            }
        }
    }

    private static void IncludeWorldBounds(
        Bounds bounds,
        Vector3 origin,
        Vector3 axisX,
        Vector3 axisZ,
        ref float minX,
        ref float maxX,
        ref float minZ,
        ref float maxZ)
    {
        Vector3 min = bounds.min;
        Vector3 max = bounds.max;

        for (int x = 0; x < 2; x++)
        {
            for (int y = 0; y < 2; y++)
            {
                for (int z = 0; z < 2; z++)
                {
                    Vector3 world = new Vector3(
                        x == 0 ? min.x : max.x,
                        y == 0 ? min.y : max.y,
                        z == 0 ? min.z : max.z
                    );

                    Vector3 offset = world - origin;

                    float px = Vector3.Dot(offset, axisX);
                    float pz = Vector3.Dot(offset, axisZ);

                    minX = Mathf.Min(minX, px);
                    maxX = Mathf.Max(maxX, px);
                    minZ = Mathf.Min(minZ, pz);
                    maxZ = Mathf.Max(maxZ, pz);
                }
            }
        }
    }

    private void SaveResults(ZoneResult result)
    {
        resultSaved = false;

        try
        {
            string originalPath =
                memoryRecall.LastSavedResultPath;

            string filename =
                !string.IsNullOrEmpty(originalPath)
                    ? Path.GetFileNameWithoutExtension(originalPath)
                      + "_zones.json"
                    : "EngramZones_" +
                      DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff") +
                      ".json";

            string path = Path.Combine(
                Application.persistentDataPath,
                filename
            );

            File.WriteAllText(
                path,
                JsonUtility.ToJson(result, true)
            );

            ZoneResult verification =
                JsonUtility.FromJson<ZoneResult>(
                    File.ReadAllText(path)
                );

            if (verification == null ||
                verification.objects == null ||
                verification.objects.Count != result.objects.Count)
            {
                throw new InvalidDataException(
                    "Zone result verification failed."
                );
            }

            resultSaved = true;

            Debug.Log(
                "Engram Zones: Saved results to " + path
            );
        }
        catch (Exception exception)
        {
            Debug.LogError(
                "Engram Zones: Saving failed: " +
                exception.Message
            );
        }
    }

    private void CreateZoneVisuals(ZoneResult result)
    {
        Transform anchor = tableReference.AnchorTransform;

        Shader shader = Shader.Find("Sprites/Default");

        if (shader == null)
            return;

        zoneMaterial = new Material(shader);

        foreach (ZoneObjectResult item in result.objects)
        {
            GameObject circle = new GameObject(
                "EngramZone_" +
                item.cocoLabel.Replace(' ', '_')
            );

            circle.transform.SetParent(anchor, false);

            LineRenderer line =
                circle.AddComponent<LineRenderer>();

            line.useWorldSpace = false;
            line.loop = true;
            line.positionCount = 64;
            line.widthMultiplier = lineWidth;

            line.sharedMaterial = zoneMaterial;

            line.startColor = zoneColor;
            line.endColor = zoneColor;

            line.shadowCastingMode =
                UnityEngine.Rendering.ShadowCastingMode.Off;

            line.receiveShadows = false;

            float radius = item.zoneRadiusCm / 100f;

            Vector3 center =
                item.referenceLocalPosition;

            for (int i = 0; i < 64; i++)
            {
                float angle =
                    2f * Mathf.PI * i / 64f;

                line.SetPosition(
                    i,
                    new Vector3(
                        center.x + Mathf.Cos(angle) * radius,
                        center.y + surfaceOffset,
                        center.z + Mathf.Sin(angle) * radius
                    )
                );
            }

            zoneVisuals.Add(circle);
        }
    }

    private void LateUpdate()
    {
        if (currentResult == null ||
            memoryRecall == null ||
            !memoryRecall.Completed ||
            memoryRecall.LastResult != currentResult.originalResult)
        {
            return;
        }

        if (meanErrorLabel != null)
            meanErrorLabel.text = "Mittlere Zonenabweichung";

        if (meanErrorText != null)
        {
            meanErrorText.text =
                $"{currentResult.meanZoneErrorCm:F1} cm";
        }

        if (resultObjectTexts != null)
        {
            for (int i = 0;
                 i < resultObjectTexts.Length &&
                 i < currentResult.objects.Count;
                 i++)
            {
                TMP_Text text = resultObjectTexts[i];

                if (text == null)
                    continue;

                ZoneObjectResult item =
                    currentResult.objects[i];

                text.text =
                    $"{ToGerman(item.cocoLabel)}: " +
                    $"{item.zoneErrorCm:F1} cm";
            }
        }

        if (saveStatusText != null)
        {
            saveStatusText.text = resultSaved
                ? "Ergebnis gespeichert"
                : "Zonenauswertung nicht gespeichert";
        }
    }

    private static string ToGerman(string label)
    {
        switch (label)
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

    private void ClearResults()
    {
        currentResult = null;
        resultSaved = false;

        ClearVisuals();
    }

    private void ClearVisuals()
    {
        foreach (GameObject visual in zoneVisuals)
        {
            if (visual != null)
                Destroy(visual);
        }

        zoneVisuals.Clear();

        if (zoneMaterial != null)
        {
            Destroy(zoneMaterial);
            zoneMaterial = null;
        }
    }

    private void OnDisable()
    {
        if (memoryRecall != null)
        {
            memoryRecall.RecallCompleted -= OnRecallCompleted;
            memoryRecall.RecallReset -= ClearResults;
            memoryRecall.RecallStarted -= ClearResults;
        }

        if (tableReference != null)
            tableReference.ReferenceCleared -= ClearResults;

        ClearResults();
    }
}