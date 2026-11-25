using System;
using System.Collections;
using System.Collections.Generic;
using Meta.XR;
using TMPro;
using UnityEngine;

public class EngramTableReference : MonoBehaviour
{
    [SerializeField] private PassthroughCameraAccess cameraAccess;
    [SerializeField] private EnvironmentRaycastManager raycastManager;
    [SerializeField] private Material markerMaterial;
    [SerializeField] private TMP_Text spatialStatus;

    [SerializeField] private float maxDistance = 4f;
    [SerializeField] private float previewMarkerSize = 0.06f;
    [SerializeField] private float anchorMarkerSize = 0.04f;
    [SerializeField] private float previewInterval = 0.12f;

    [SerializeField, Range(0f, 1f)]
    private float minimumHorizontalAlignment = 0.85f;

    [SerializeField] private int requiredSamples = 5;
    [SerializeField] private float maximumSampleDistance = 0.15f;

    private const string AnchorUuidKey = "Engram.TableAnchorUuid";

    private readonly List<Vector3> previewSamples = new();

    private OVRSpatialAnchor tableAnchor;

    private GameObject previewMarker;
    private GameObject anchorMarker;

    private Vector3 previewPosition;

    private bool previewReady;
    private bool previewSelected;

    private bool isCreatingOrSaving;
    private bool isDeleting;
    private bool isLoading;
    private bool deleteAfterSave;

    private float nextPreviewUpdate;
    private float lastPreviewTime;

    private int loadGeneration;

    private string currentStatus = "";

    public event Action ReferenceReady;
    public event Action ReferenceCleared;

    public bool HasReference =>
        tableAnchor != null && tableAnchor.Localized;

    public Transform AnchorTransform =>
        HasReference ? tableAnchor.transform : null;

    public Guid AnchorUuid =>
        HasReference ? tableAnchor.Uuid : Guid.Empty;

    public float TableHeight =>
        HasReference ? tableAnchor.transform.position.y : 0f;

    public Plane TablePlane =>
        HasReference
            ? new Plane(Vector3.up, tableAnchor.transform.position)
            : default;

    private void Start()
    {
        if (cameraAccess == null ||
            raycastManager == null ||
            markerMaterial == null)
        {
            SetStatus("Missing references");
            enabled = false;
            return;
        }

        previewMarker = CreateMarker(
            "TablePreviewMarker",
            previewMarkerSize
        );

        anchorMarker = CreateMarker(
            "TableAnchorMarker",
            anchorMarkerSize
        );

        if (PlayerPrefs.HasKey(AnchorUuidKey))
        {
            SetStatus("Initializing saved anchor");
            StartCoroutine(LoadAfterInitialization());
        }
        else
        {
            SetStatus("Aim at table");
        }
    }

    private IEnumerator LoadAfterInitialization()
    {
        yield return new WaitForSeconds(0.5f);

        if (!isDeleting)
            LoadSavedReference();
    }

    private void Update()
    {
        if (OVRInput.GetDown(
            OVRInput.Button.Two,
            OVRInput.Controller.RTouch))
        {
            ResetReference();
            return;
        }

        if (OVRInput.GetDown(
            OVRInput.Button.One,
            OVRInput.Controller.RTouch))
        {
            CalibrateTable();
        }

        if (HasReference)
        {
            UpdateAnchorMarker();
            return;
        }

        if (previewSelected ||
            isCreatingOrSaving ||
            isDeleting ||
            isLoading ||
            PlayerPrefs.HasKey(AnchorUuidKey))
        {
            return;
        }

        UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (Time.unscaledTime < nextPreviewUpdate)
            return;

        nextPreviewUpdate =
            Time.unscaledTime + previewInterval;

        if (!cameraAccess.IsPlaying)
        {
            ClearPreview();
            SetStatus("Waiting for camera");
            return;
        }

        if (!EnvironmentRaycastManager.IsSupported)
        {
            ClearPreview();
            SetStatus("Raycasting unavailable");
            return;
        }

        Ray ray = cameraAccess.ViewportPointToRay(
            new Vector2(0.5f, 0.5f)
        );

        if (!raycastManager.Raycast(
            ray,
            out EnvironmentRaycastHit hit,
            maxDistance))
        {
            ClearPreview();
            SetStatus($"No surface: {hit.status}");
            return;
        }

        float alignment = Vector3.Dot(
            hit.normal.normalized,
            Vector3.up
        );

        if (alignment < minimumHorizontalAlignment)
        {
            ClearPreview();
            SetStatus("Aim at horizontal surface");
            return;
        }

        AddPreviewSample(hit.point);
    }

    private void AddPreviewSample(Vector3 point)
    {
        if (previewSamples.Count > 0)
        {
            Vector3 first = previewSamples[0];

            if (Vector3.Distance(
                first,
                point
            ) > maximumSampleDistance)
            {
                previewSamples.Clear();
                previewReady = false;
            }
        }

        previewSamples.Add(point);

        int sampleCount = Mathf.Max(1, requiredSamples);

        if (previewSamples.Count > sampleCount)
            previewSamples.RemoveAt(0);

        if (previewSamples.Count < sampleCount)
        {
            previewReady = false;
            previewMarker.SetActive(false);

            SetStatus(
                $"Sampling table: {previewSamples.Count}/{sampleCount}"
            );

            return;
        }

        previewPosition = CalculatePreviewPosition();

        previewReady = true;
        lastPreviewTime = Time.unscaledTime;

        previewMarker.transform.position =
            previewPosition +
            Vector3.up * (previewMarkerSize * 0.5f);

        previewMarker.SetActive(true);

        SetStatus(
            $"Preview: {previewPosition.y:F2} m | Press A"
        );
    }

    private Vector3 CalculatePreviewPosition()
    {
        var heights = new List<float>();

        Vector3 sum = Vector3.zero;

        foreach (Vector3 sample in previewSamples)
        {
            heights.Add(sample.y);
            sum += sample;
        }

        heights.Sort();

        int count = heights.Count;

        float medianHeight;

        if (count % 2 == 0)
        {
            medianHeight =
                (heights[count / 2 - 1] +
                 heights[count / 2]) * 0.5f;
        }
        else
        {
            medianHeight = heights[count / 2];
        }

        Vector3 average = sum / count;

        return new Vector3(
            average.x,
            medianHeight,
            average.z
        );
    }

    public void CalibrateTable()
    {
        if (isCreatingOrSaving || isDeleting)
        {
            SetStatus("Anchor operation in progress");
            return;
        }

        if (isLoading)
        {
            SetStatus("Loading anchor | Press B to delete");
            return;
        }

        if (PlayerPrefs.HasKey(AnchorUuidKey))
        {
            SetStatus("Saved anchor exists | Press B to delete");
            return;
        }

        if (HasReference)
        {
            SetStatus("Table already anchored");
            return;
        }

        if (!previewSelected)
        {
            if (!previewReady ||
                Time.unscaledTime - lastPreviewTime > 0.5f)
            {
                SetStatus("No stable table preview");
                return;
            }

            previewSelected = true;

            SetStatus(
                "Position selected | A: Save | B: Cancel"
            );

            return;
        }

        SaveSelectedReference();
    }

    private async void SaveSelectedReference()
    {
        if (isCreatingOrSaving || !previewSelected)
            return;

        isCreatingOrSaving = true;

        GameObject anchorObject = null;

        try
        {
            SetStatus("Creating table anchor");

            anchorObject =
                new GameObject("EngramTableAnchor");

            anchorObject.transform.SetPositionAndRotation(
                previewPosition,
                Quaternion.identity
            );

            anchorObject.transform.SetParent(
                transform,
                true
            );

            tableAnchor =
                anchorObject.AddComponent<OVRSpatialAnchor>();

            bool created =
                await tableAnchor.WhenCreatedAsync();

            if (!created || tableAnchor == null)
                throw new Exception("Anchor creation failed");

            SetStatus("Localizing table anchor");

            bool localized =
                await tableAnchor.WhenLocalizedAsync();

            if (!localized)
                throw new Exception("Anchor localization failed");

            SetStatus("Saving table anchor");

            var result =
                await tableAnchor.SaveAnchorAsync();

            if (!result.Success)
            {
                throw new Exception(
                    $"Anchor save failed: {result.Status}"
                );
            }

            PlayerPrefs.SetString(
                AnchorUuidKey,
                tableAnchor.Uuid.ToString()
            );

            PlayerPrefs.Save();

            previewSelected = false;
            previewReady = false;

            previewMarker.SetActive(false);

            UpdateAnchorMarker();

            SetStatus(
                $"Table saved | Height: {TableHeight:F2} m"
            );

            Debug.Log(
                $"Engram: Saved anchor {tableAnchor.Uuid}"
            );

            ReferenceReady?.Invoke();
        }
        catch (Exception exception)
        {
            Debug.LogError(
                $"Engram: Anchor save error: {exception}"
            );

            if (anchorObject != null)
                Destroy(anchorObject);

            tableAnchor = null;

            SetStatus(
                "Save failed | A: Retry | B: Cancel"
            );
        }
        finally
        {
            isCreatingOrSaving = false;

            if (deleteAfterSave)
            {
                deleteAfterSave = false;
                ResetReference();
            }
        }
    }

    private async void LoadSavedReference()
    {
        if (isDeleting || isLoading || isCreatingOrSaving)
            return;

        string savedUuid = PlayerPrefs.GetString(
            AnchorUuidKey,
            ""
        );

        if (!Guid.TryParse(savedUuid, out Guid uuid))
        {
            SetStatus("No valid saved anchor | Press B to reset");
            return;
        }

        isLoading = true;

        int generation = ++loadGeneration;

        try
        {
            SetStatus("Loading saved table anchor");

            var unboundAnchors =
                new List<OVRSpatialAnchor.UnboundAnchor>();

            var result =
                await OVRSpatialAnchor.LoadUnboundAnchorsAsync(
                    new[] { uuid },
                    unboundAnchors
                );

            if (generation != loadGeneration || isDeleting)
                return;

            if (!result.Success)
            {
                SetStatus(
                    $"Load failed: {result.Status} | Press B"
                );

                return;
            }

            if (unboundAnchors.Count == 0)
            {
                SetStatus("Saved anchor not found | Press B");
                return;
            }

            var unboundAnchor = unboundAnchors[0];

            if (!unboundAnchor.Localized)
            {
                SetStatus("Localizing saved table");

                bool localized =
                    await unboundAnchor.LocalizeAsync();

                if (generation != loadGeneration || isDeleting)
                    return;

                if (!localized)
                {
                    SetStatus(
                        "Localization failed | Press B to delete"
                    );

                    return;
                }
            }

            if (!unboundAnchor.TryGetPose(out Pose pose))
            {
                SetStatus("Anchor pose unavailable | Press B");
                return;
            }

            if (generation != loadGeneration || isDeleting)
                return;

            GameObject anchorObject =
                new GameObject("EngramTableAnchor");

            anchorObject.transform.SetPositionAndRotation(
                pose.position,
                pose.rotation
            );

            anchorObject.transform.SetParent(
                transform,
                true
            );

            tableAnchor =
                anchorObject.AddComponent<OVRSpatialAnchor>();

            unboundAnchor.BindTo(tableAnchor);

            bool boundLocalized =
                await tableAnchor.WhenLocalizedAsync();

            if (generation != loadGeneration || isDeleting)
                return;

            if (!boundLocalized)
                throw new Exception("Bound anchor localization failed");

            UpdateAnchorMarker();

            SetStatus(
                $"Table restored | Height: {TableHeight:F2} m"
            );

            Debug.Log(
                $"Engram: Restored anchor {uuid}"
            );

            ReferenceReady?.Invoke();
        }
        catch (Exception exception)
        {
            if (generation == loadGeneration)
            {
                Debug.LogError(
                    $"Engram: Anchor load error: {exception}"
                );

                SetStatus("Load error | Press B to delete");
            }
        }
        finally
        {
            if (generation == loadGeneration)
                isLoading = false;
        }
    }

    public void ResetReference()
    {
        if (isDeleting)
        {
            SetStatus("Deleting anchor");
            return;
        }

        if (isCreatingOrSaving)
        {
            deleteAfterSave = true;

            SetStatus("Reset queued until save finishes");
            return;
        }

        if (PlayerPrefs.HasKey(AnchorUuidKey))
        {
            DeleteSavedReference();
            return;
        }

        ClearRuntimeReference();

        SetStatus("Selection cleared | Aim at table");
    }

    private async void DeleteSavedReference()
    {
        if (isDeleting)
            return;

        isDeleting = true;

        // Invalidate any unfinished load operation.
        ++loadGeneration;
        isLoading = false;

        try
        {
            SetStatus("Deleting saved anchor");

            string savedUuid = PlayerPrefs.GetString(
                AnchorUuidKey,
                ""
            );

            if (Guid.TryParse(savedUuid, out Guid uuid))
            {
                var result =
                    await OVRSpatialAnchor.EraseAnchorsAsync(
                        null,
                        new[] { uuid }
                    );

                if (!result.Success)
                {
                    SetStatus(
                        $"Delete failed: {result.Status}"
                    );

                    Debug.LogError(
                        $"Engram: Anchor deletion failed: {result.Status}"
                    );

                    return;
                }

                Debug.Log(
                    $"Engram: Deleted anchor {uuid}"
                );
            }
            else
            {
                Debug.LogWarning(
                    "Engram: Invalid stored UUID. " +
                    "Only the local reference can be cleared."
                );
            }

            PlayerPrefs.DeleteKey(AnchorUuidKey);
            PlayerPrefs.Save();

            ClearRuntimeReference();

            SetStatus("Anchor deleted | Aim at table");
        }
        catch (Exception exception)
        {
            Debug.LogError(
                $"Engram: Anchor deletion error: {exception}"
            );

            SetStatus("Delete error | Press B to retry");
        }
        finally
        {
            isDeleting = false;
        }
    }

    private void ClearRuntimeReference()
    {
        previewSelected = false;
        previewReady = false;

        previewSamples.Clear();

        if (previewMarker != null)
            previewMarker.SetActive(false);

        if (anchorMarker != null)
            anchorMarker.SetActive(false);

        try
        {
            ReferenceCleared?.Invoke();
        }
        catch (Exception exception)
        {
            Debug.LogError(
                $"Engram: Reference cleanup error: {exception}"
            );
        }

        if (tableAnchor != null)
            Destroy(tableAnchor.gameObject);

        tableAnchor = null;
    }

    public bool TryProjectRay(
        Ray ray,
        out Vector3 worldPosition)
    {
        worldPosition = Vector3.zero;

        if (!HasReference)
            return false;

        if (!TablePlane.Raycast(ray, out float distance))
            return false;

        if (distance <= 0f || distance > maxDistance)
            return false;

        worldPosition = ray.GetPoint(distance);

        return true;
    }

    private void UpdateAnchorMarker()
    {
        if (!HasReference || anchorMarker == null)
            return;

        anchorMarker.transform.position =
            tableAnchor.transform.position +
            Vector3.up * (anchorMarkerSize * 0.5f);

        anchorMarker.SetActive(true);
    }

    private GameObject CreateMarker(
        string markerName,
        float size)
    {
        GameObject marker =
            GameObject.CreatePrimitive(PrimitiveType.Sphere);

        marker.name = markerName;

        marker.transform.SetParent(
            transform,
            false
        );

        marker.transform.localScale =
            Vector3.one * size;

        marker.GetComponent<Renderer>()
            .sharedMaterial = markerMaterial;

        Collider collider =
            marker.GetComponent<Collider>();

        if (collider != null)
            Destroy(collider);

        marker.SetActive(false);

        return marker;
    }

    private void ClearPreview()
    {
        previewSamples.Clear();
        previewReady = false;

        if (previewMarker != null)
            previewMarker.SetActive(false);
    }

    private void SetStatus(string status)
    {
        if (currentStatus == status)
            return;

        currentStatus = status;

        if (spatialStatus != null)
            spatialStatus.text = $"Spatial: {status}";

        Debug.Log($"Engram Spatial: {status}");
    }

    private void OnDestroy()
    {
        if (previewMarker != null)
            Destroy(previewMarker);

        if (anchorMarker != null)
            Destroy(anchorMarker);
    }
}