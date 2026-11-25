using Meta.XR;
using TMPro;
using UnityEngine;

public class EngramSpatialProbe : MonoBehaviour
{
    [SerializeField] private PassthroughCameraAccess cameraAccess;
    [SerializeField] private EnvironmentRaycastManager raycastManager;
    [SerializeField] private Material markerMaterial;
    [SerializeField] private TMP_Text spatialStatus;

    [SerializeField] private float maxDistance = 4f;
    [SerializeField] private float updateInterval = 0.15f;
    [SerializeField] private float markerSize = 0.06f;

    private GameObject marker;
    private float nextUpdate;
    private string lastStatus;

    private void Start()
    {
        if (cameraAccess == null || raycastManager == null)
        {
            SetStatus("Missing references");
            enabled = false;
            return;
        }

        if (markerMaterial == null)
        {
            SetStatus("Missing marker material");
            enabled = false;
            return;
        }

        marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        marker.name = "SpatialProbeMarker";
        marker.transform.localScale = Vector3.one * markerSize;

        var renderer = marker.GetComponent<Renderer>();
        renderer.sharedMaterial = markerMaterial;

        var collider = marker.GetComponent<Collider>();

        if (collider != null)
            Destroy(collider);

        marker.SetActive(false);

        SetStatus("Initializing");
    }

    private void Update()
    {
        if (Time.unscaledTime < nextUpdate)
            return;

        nextUpdate = Time.unscaledTime + updateInterval;

        if (!cameraAccess.IsPlaying)
        {
            HideMarker();
            SetStatus("Waiting for camera");
            return;
        }

        if (!EnvironmentRaycastManager.IsSupported)
        {
            HideMarker();
            SetStatus("Not supported");
            return;
        }

        Ray cameraRay = cameraAccess.ViewportPointToRay(
            new Vector2(0.5f, 0.5f)
        );

        bool hasHit = raycastManager.Raycast(
            cameraRay,
            out EnvironmentRaycastHit hit,
            maxDistance
        );

        if (hasHit)
        {
            marker.transform.position =
                hit.point + hit.normal * 0.02f;

            marker.SetActive(true);
            SetStatus("Hit");
        }
        else
        {
            HideMarker();
            SetStatus(hit.status.ToString());
        }
    }

    private void HideMarker()
    {
        if (marker != null)
            marker.SetActive(false);
    }

    private void SetStatus(string status)
    {
        if (spatialStatus != null)
            spatialStatus.text = $"Spatial: {status}";

        if (lastStatus == status)
            return;

        lastStatus = status;
        Debug.Log($"Engram Spatial: {status}");
    }

    private void OnDestroy()
    {
        if (marker != null)
            Destroy(marker);
    }
}
