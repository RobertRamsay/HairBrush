using System.Collections;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

// Keeps the Texture Editor material picker compact and deterministic without owning
// material creation/persistence itself. The existing MaterialEditorManager remains the
// authority for material data; this component only controls presentation and the 18-item cap.
[DefaultExecutionOrder(9350)]
public class MaterialGridLayoutAuthority : MonoBehaviour
{
    private const int Columns = 3;
    private const int MaxMaterials = 18;
    private const float CellHeight = 24f;
    private const float RowSpacing = 4f;

    private MaterialEditorManager editor;
    private Transform materialButtons;
    private IList materials;
    private FieldInfo materialsField;
    private float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<MaterialGridLayoutAuthority>() != null) return;
        GameObject go = new GameObject("MaterialGridLayoutAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<MaterialGridLayoutAuthority>();
    }

    void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + .1f;

        Resolve();
        if (editor == null || materialButtons == null) return;

        if (!EnsureGridLayout()) return;
        UpdateGridHeightAndAddButton();
    }

    void Resolve()
    {
        if (editor == null)
        {
            editor = FindFirstObjectByType<MaterialEditorManager>();
            if (editor != null)
            {
                materialsField = typeof(MaterialEditorManager).GetField("materials", BindingFlags.Instance | BindingFlags.NonPublic);
                materials = materialsField?.GetValue(editor) as IList;
            }
        }

        if (materialButtons == null)
        {
            foreach (Transform t in FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (t != null && t.name == "MaterialButtons")
                {
                    materialButtons = t;
                    break;
                }
            }
        }

        if (materials == null && editor != null && materialsField != null)
            materials = materialsField.GetValue(editor) as IList;
    }

    // Returns true only once the object is safely using GridLayoutGroup. Unity defers
    // Destroy(Component) until end-of-frame, and a GameObject cannot hold two LayoutGroup
    // components at once. Therefore conversion from the legacy HorizontalLayoutGroup must
    // happen over two scans rather than destroy+add in the same frame.
    bool EnsureGridLayout()
    {
        if (materialButtons == null) return false;

        HorizontalLayoutGroup horizontal = materialButtons.GetComponent<HorizontalLayoutGroup>();
        if (horizontal != null)
        {
            horizontal.enabled = false;
            Destroy(horizontal);
            return false;
        }

        GridLayoutGroup grid = materialButtons.GetComponent<GridLayoutGroup>();
        if (grid == null)
        {
            grid = materialButtons.gameObject.AddComponent<GridLayoutGroup>();
            if (grid == null) return false;
        }

        RectTransform rect = materialButtons as RectTransform;
        float availableWidth = rect != null && rect.rect.width > 1f ? rect.rect.width : 230f;
        float cellWidth = Mathf.Floor((availableWidth - RowSpacing * (Columns - 1)) / Columns);

        grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        grid.constraintCount = Columns;
        grid.cellSize = new Vector2(Mathf.Max(48f, cellWidth), CellHeight);
        grid.spacing = new Vector2(RowSpacing, RowSpacing);
        grid.startCorner = GridLayoutGroup.Corner.UpperLeft;
        grid.startAxis = GridLayoutGroup.Axis.Horizontal;
        grid.childAlignment = TextAnchor.UpperLeft;
        return true;
    }

    void UpdateGridHeightAndAddButton()
    {
        if (materialButtons == null) return;

        int count = materials != null ? materials.Count : Mathf.Max(0, materialButtons.childCount - 1);
        count = Mathf.Clamp(count, 0, MaxMaterials);

        Transform addButton = null;
        for (int i = 0; i < materialButtons.childCount; i++)
        {
            Transform child = materialButtons.GetChild(i);
            if (child == null) continue;
            if (child.name == "+Button" || child.name.StartsWith("+"))
            {
                addButton = child;
                break;
            }
        }

        bool canAdd = count < MaxMaterials;
        if (addButton != null && addButton.gameObject.activeSelf != canAdd)
            addButton.gameObject.SetActive(canAdd);

        int visibleCells = count + (canAdd ? 1 : 0);
        int rows = Mathf.Clamp(Mathf.CeilToInt(visibleCells / (float)Columns), 1, 6);
        float height = rows * CellHeight + (rows - 1) * RowSpacing;

        LayoutElement layout = materialButtons.GetComponent<LayoutElement>();
        if (layout == null) layout = materialButtons.gameObject.AddComponent<LayoutElement>();
        layout.preferredHeight = height;
        layout.minHeight = height;

        RectTransform rect = materialButtons as RectTransform;
        if (rect != null) rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
    }
}
