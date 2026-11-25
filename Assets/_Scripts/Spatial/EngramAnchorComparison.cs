using UnityEngine;

public class EngramAnchorComparison : MonoBehaviour
{
    [SerializeField] private EngramTableReference tableReference;
    [SerializeField] private Material markerMaterial;

    [SerializeField] private float markerSize = 0.06f;
    [SerializeField] private float markerHeight = 0.15f;
    [SerializeField] private float horizontalOffset = 0.12f;

    private Transform currentAnchor;

    private GameObject anchoredSphere;
    private GameObject worldCube;

    private Vector3 initialAnchorPosition;
    private Vector3 previousAnchorPosition;

    private float maximumFrameMovement;
    private float nextLogTime;

    private void Update()
    {
        if (tableReference == null || markerMaterial == null)
            return;

        Transform anchor = tableReference.AnchorTransform;

        if (anchor != currentAnchor)
        {
            ClearMarkers();

            currentAnchor = anchor;

            if (currentAnchor != null)
                CreateMarkers();
        }

        if (currentAnchor == null)
            return;

        float frameMovement = Vector3.Distance(
            currentAnchor.position,
            previousAnchorPosition
        );

        maximumFrameMovement = Mathf.Max(
            maximumFrameMovement,
            frameMovement
        );

        previousAnchorPosition = currentAnchor.position;

        if (Time.unscaledTime >= nextLogTime)
        {
            float totalMovement = Vector3.Distance(
                currentAnchor.position,
                initialAnchorPosition
            );

            Debug.Log(
                $"Engram Anchor | " +
                $"Position change: {totalMovement * 100f:F2} cm | " +
                $"Max frame movement: {maximumFrameMovement * 100f:F2} cm"
            );

            maximumFrameMovement = 0f;
            nextLogTime = Time.unscaledTime + 2f;
        }
    }

    private void CreateMarkers()
    {
        initialAnchorPosition = currentAnchor.position;
        previousAnchorPosition = currentAnchor.position;

        anchoredSphere = CreatePrimitive(
            PrimitiveType.Sphere,
            "Anchor_Sphere"
        );

        anchoredSphere.transform.SetParent(
            currentAnchor,
            false
        );

        anchoredSphere.transform.localPosition =
            new Vector3(
                -horizontalOffset,
                markerHeight,
                0f
            );

        worldCube = CreatePrimitive(
            PrimitiveType.Cube,
            "WorldFixed_Cube"
        );

        // The cube is not parented to the spatial anchor.
        worldCube.transform.position =
            currentAnchor.TransformPoint(
                new Vector3(
                    horizontalOffset,
                    markerHeight,
                    0f
                )
            );

        worldCube.transform.rotation =
            currentAnchor.rotation;

        Debug.Log(
            "Engram: Anchor comparison markers created."
        );
    }

    private GameObject CreatePrimitive(
        PrimitiveType type,
        string objectName)
    {
        GameObject marker =
            GameObject.CreatePrimitive(type);

        marker.name = objectName;

        marker.transform.localScale =
            Vector3.one * markerSize;

        marker.GetComponent<Renderer>()
            .sharedMaterial = markerMaterial;

        Collider collider = marker.GetComponent<Collider>();

        if (collider != null)
            Destroy(collider);

        return marker;
    }

    private void ClearMarkers()
    {
        if (anchoredSphere != null)
            Destroy(anchoredSphere);

        if (worldCube != null)
            Destroy(worldCube);

        anchoredSphere = null;
        worldCube = null;
    }

    private void OnDisable()
    {
        ClearMarkers();
        currentAnchor = null;
    }

    private void OnDestroy()
    {
        ClearMarkers();
    }
}