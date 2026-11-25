using UnityEngine;

public class EngramMarkerStabilityTest : MonoBehaviour
{
    [SerializeField] private EngramTableReference tableReference;
    [SerializeField] private Material markerMaterial;

    [SerializeField] private float lowMarkerHeight = 0.03f;
    [SerializeField] private float highMarkerHeight = 0.20f;

    private Transform currentAnchor;

    private GameObject lowMarker;
    private GameObject highMarker;

    private void Update()
    {
        if (tableReference == null || markerMaterial == null)
            return;

        Transform anchor = tableReference.AnchorTransform;

        if (anchor == currentAnchor)
            return;

        ClearMarkers();

        currentAnchor = anchor;

        if (currentAnchor == null)
            return;

        lowMarker = CreateMarker(
            "StaticMarker_Low",
            lowMarkerHeight,
            0.04f
        );

        highMarker = CreateMarker(
            "StaticMarker_High",
            highMarkerHeight,
            0.06f
        );

        Debug.Log("Engram: Static marker test initialized.");
    }

    private GameObject CreateMarker(
        string markerName,
        float height,
        float size)
    {
        GameObject marker = GameObject.CreatePrimitive(
            PrimitiveType.Sphere
        );

        marker.name = markerName;

        marker.transform.SetParent(currentAnchor, false);

        // Keep both markers at the same horizontal position.
        marker.transform.localPosition = new Vector3(
            0.10f,
            height,
            0f
        );

        marker.transform.localScale =
            Vector3.one * size;

        Renderer renderer = marker.GetComponent<Renderer>();

        renderer.sharedMaterial = markerMaterial;

        Collider collider = marker.GetComponent<Collider>();

        if (collider != null)
            Destroy(collider);

        return marker;
    }

    private void ClearMarkers()
    {
        if (lowMarker != null)
            Destroy(lowMarker);

        if (highMarker != null)
            Destroy(highMarker);

        lowMarker = null;
        highMarker = null;
    }

    private void OnDestroy()
    {
        ClearMarkers();
    }
}