using Meta.XR;
using TMPro;
using UnityEngine;

public class EngramHandTrackingIsolationTest : MonoBehaviour
{
    [Header("Existing Engram Objects")]
    [SerializeField] private EngramSpatialInventory spatialInventory;
    [SerializeField] private GameObject objectDetection;
    [SerializeField] private GameObject cameraPreviewCanvas;
    [SerializeField] private CameraFeedPreview cameraFeedPreview;
    [SerializeField] private PassthroughCameraAccess cameraAccess;

    [Header("Optional Test Status")]
    [SerializeField] private TMP_Text testStatusText;

    private int testStage;

    private void Start()
    {
        SetStatus("TEST 0: Alles aktiv. Nach dem Einrasten L3 druecken.");
    }

    private void Update()
    {
        if (!OVRInput.GetDown(OVRInput.Button.PrimaryThumbstick,
                OVRInput.Controller.LTouch))
            return;

        if (spatialInventory == null || !spatialInventory.ObjectsDocked)
        {
            SetStatus("Zuerst vier Modelle ins Inventar einrasten lassen.");
            return;
        }

        if (testStage == 0)
        {
            // Hide only the additional camera image.
            if (cameraFeedPreview != null)
                cameraFeedPreview.enabled = false;
            if (cameraPreviewCanvas != null)
                cameraPreviewCanvas.SetActive(false);

            testStage = 1;
            SetStatus("TEST 1: Nur Kameravorschau AUS, YOLO und Kamera-API AN.");
            return;
        }

        if (testStage == 1)
        {
            // Disable the entire detector GameObject to stop its coroutine.
            if (objectDetection != null)
                objectDetection.SetActive(false);

            testStage = 2;
            SetStatus("TEST 2: Vorschau und YOLO AUS, Kamera-API AN.");
            return;
        }

        if (testStage == 2)
        {
            // Stop raw camera access, not the Quest's normal passthrough.
            if (cameraAccess != null)
                cameraAccess.enabled = false;

            testStage = 3;
            SetStatus("TEST 3: Vorschau, YOLO und Kamera-API AUS.");
        }
    }

    private void SetStatus(string message)
    {
        Debug.Log("Engram Isolation: " + message);
        if (testStatusText != null)
            testStatusText.text = message;
    }
}
