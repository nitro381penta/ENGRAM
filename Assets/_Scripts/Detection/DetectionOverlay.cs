using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class DetectionOverlay : MonoBehaviour
{
    private const float InputSize = 640f;

    private readonly List<BoxView> boxPool = new();
    private RectTransform previewRect;
    private int activeBoxes;

    private class BoxView
    {
        public RectTransform root;
        public TextMeshProUGUI label;
    }

    private void Awake()
    {
        previewRect = GetComponent<RectTransform>();
    }

    public void Clear()
    {
        foreach (var box in boxPool)
            box.root.gameObject.SetActive(false);

        activeBoxes = 0;
    }

    public void AddBox(
        float centerX,
        float centerY,
        float width,
        float height,
        string className)
    {
        BoxView box;

        if (activeBoxes < boxPool.Count)
        {
            box = boxPool[activeBoxes];
        }
        else
        {
            box = CreateBox();
            boxPool.Add(box);
        }

        box.root.gameObject.SetActive(true);

        float displayWidth = previewRect.rect.width;
        float displayHeight = previewRect.rect.height;

        float x = (centerX / InputSize - 0.5f) * displayWidth;
        float y = (0.5f - centerY / InputSize) * displayHeight;

        box.root.anchoredPosition = new Vector2(x, y);

        box.root.sizeDelta = new Vector2(
            width / InputSize * displayWidth,
            height / InputSize * displayHeight
        );

        box.label.text = className;
        activeBoxes++;
    }

    private BoxView CreateBox()
    {
        var rootObject = new GameObject(
            "DetectionBox",
            typeof(RectTransform)
        );

        var root = rootObject.GetComponent<RectTransform>();
        root.SetParent(previewRect, false);

        root.anchorMin = new Vector2(0.5f, 0.5f);
        root.anchorMax = new Vector2(0.5f, 0.5f);
        root.pivot = new Vector2(0.5f, 0.5f);

        Color color = new Color(0.1f, 0.95f, 0.5f);

        CreateLine(root, "Top",
            new Vector2(0, 1), new Vector2(1, 1),
            new Vector2(0, 3), color);

        CreateLine(root, "Bottom",
            new Vector2(0, 0), new Vector2(1, 0),
            new Vector2(0, 3), color);

        CreateLine(root, "Left",
            new Vector2(0, 0), new Vector2(0, 1),
            new Vector2(3, 0), color);

        CreateLine(root, "Right",
            new Vector2(1, 0), new Vector2(1, 1),
            new Vector2(3, 0), color);

        var labelObject = new GameObject(
            "ClassLabel",
            typeof(RectTransform),
            typeof(TextMeshProUGUI)
        );

        labelObject.transform.SetParent(root, false);

        var labelRect = labelObject.GetComponent<RectTransform>();
        labelRect.anchorMin = new Vector2(0, 1);
        labelRect.anchorMax = new Vector2(0, 1);
        labelRect.pivot = new Vector2(0, 1);
        labelRect.anchoredPosition = new Vector2(4, -4);
        labelRect.sizeDelta = new Vector2(200, 30);

        var label = labelObject.GetComponent<TextMeshProUGUI>();
        label.fontSize = 20;
        label.color = color;
        label.raycastTarget = false;

        return new BoxView
        {
            root = root,
            label = label
        };
    }

    private void CreateLine(
        RectTransform parent,
        string name,
        Vector2 anchorMin,
        Vector2 anchorMax,
        Vector2 size,
        Color color)
    {
        var lineObject = new GameObject(
            name,
            typeof(RectTransform),
            typeof(Image)
        );

        var line = lineObject.GetComponent<RectTransform>();

        line.SetParent(parent, false);
        line.anchorMin = anchorMin;
        line.anchorMax = anchorMax;
        line.sizeDelta = size;
        line.anchoredPosition = Vector2.zero;

        var image = lineObject.GetComponent<Image>();
        image.color = color;
        image.raycastTarget = false;
    }
}