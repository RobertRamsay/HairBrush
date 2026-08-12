using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

[DefaultExecutionOrder(1500)]
public class RuntimeToolInteractionFixes : MonoBehaviour
{
    private float nextScan;
    private ClumpLayerManager clumpManager;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        GameObject go = new GameObject("RuntimeToolInteractionFixes");
        DontDestroyOnLoad(go);
        go.AddComponent<RuntimeToolInteractionFixes>();
    }

    void Update()
    {
        if (Time.unscaledTime >= nextScan)
        {
            nextScan = Time.unscaledTime + 0.2f;
            InstallClumpFillButtons();
        }
        HandleSeedControls();
    }

    void HandleSeedControls()
    {
        if (Mouse.current == null) return;
        Vector2 mouse = Mouse.current.position.ReadValue();
        bool pressed = Mouse.current.leftButton.wasPressedThisFrame;

        TMP_InputField[] fields = FindObjectsByType<TMP_InputField>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
            .Where(f => f.gameObject.name == "SeedInput").ToArray();
        foreach (TMP_InputField field in fields)
        {
            RectTransform rect = field.transform as RectTransform; if (rect == null) continue;
            bool hover = ScreenRectContains(rect, mouse); Image image = field.GetComponent<Image>();
            if (image != null) image.color = field.isFocused ? new Color(.20f,.38f,.24f,1f) : hover ? new Color(.36f,.46f,.30f,1f) : new Color(.12f,.12f,.12f,1f);
            if (pressed && hover)
            {
                field.interactable = true; field.enabled = true;
                if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(field.gameObject);
                field.Select(); field.ActivateInputField(); field.MoveTextEnd(false);
            }
        }

        Button[] randomButtons = FindObjectsByType<Button>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
            .Where(b => b.gameObject.name == "RButton" && b.transform.parent != null && b.transform.parent.name.EndsWith("_VarianceRow")).ToArray();
        foreach (Button button in randomButtons)
        {
            RectTransform rect = button.transform as RectTransform; if (rect == null) continue;
            bool hover = ScreenRectContains(rect, mouse); Image image = button.GetComponent<Image>();
            if (image != null) image.color = hover ? new Color(.52f,.72f,.34f,1f) : new Color(.27f,.34f,.20f,1f);
            if (pressed && hover)
            {
                button.onClick.Invoke();
                if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(button.gameObject);
            }
        }
    }

    bool ScreenRectContains(RectTransform rect, Vector2 screenPoint)
    {
        Canvas canvas = rect.GetComponentInParent<Canvas>(); Camera cam = null;
        if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay) cam = canvas.worldCamera != null ? canvas.worldCamera : Camera.main;
        Vector3[] corners = new Vector3[4]; rect.GetWorldCorners(corners);
        Vector2 p0 = RectTransformUtility.WorldToScreenPoint(cam, corners[0]); Vector2 p1 = RectTransformUtility.WorldToScreenPoint(cam, corners[1]); Vector2 p2 = RectTransformUtility.WorldToScreenPoint(cam, corners[2]); Vector2 p3 = RectTransformUtility.WorldToScreenPoint(cam, corners[3]);
        float minX=Mathf.Min(p0.x,p1.x,p2.x,p3.x),maxX=Mathf.Max(p0.x,p1.x,p2.x,p3.x),minY=Mathf.Min(p0.y,p1.y,p2.y,p3.y),maxY=Mathf.Max(p0.y,p1.y,p2.y,p3.y); const float pad=2f;
        return screenPoint.x>=minX-pad&&screenPoint.x<=maxX+pad&&screenPoint.y>=minY-pad&&screenPoint.y<=maxY+pad;
    }

    void InstallClumpFillButtons()
    {
        if (clumpManager == null) clumpManager = FindFirstObjectByType<ClumpLayerManager>(); if (clumpManager == null) return;
        RectTransform[] modifiers = FindObjectsByType<RectTransform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).Where(r => r.name.StartsWith("ClumpModifier_") && r.childCount > 1).ToArray();
        foreach (RectTransform modifier in modifiers)
        {
            Transform old = modifier.Find("FILL 1.0"); if (old != null) Destroy(old.gameObject);
            if (modifier.Find("FILL PAINT VALUE") != null) continue;
            if (!int.TryParse(modifier.name.Substring("ClumpModifier_".Length), out int groupId)) continue;
            GameObject buttonGO = new GameObject("FILL PAINT VALUE", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
            buttonGO.transform.SetParent(modifier, false); buttonGO.transform.SetSiblingIndex(Mathf.Min(4, modifier.childCount - 1));
            RectTransform br=buttonGO.GetComponent<RectTransform>(); br.sizeDelta=new Vector2(0,28); LayoutElement le=buttonGO.GetComponent<LayoutElement>(); le.preferredHeight=28; le.minHeight=28;
            Image image=buttonGO.GetComponent<Image>(); image.color=new Color(.20f,.38f,.20f,1f); Button button=buttonGO.GetComponent<Button>(); ColorBlock colors=button.colors; colors.normalColor=new Color(.20f,.38f,.20f,1f); colors.highlightedColor=new Color(.28f,.58f,.28f,1f); colors.pressedColor=new Color(.16f,.46f,.18f,1f); colors.selectedColor=colors.highlightedColor; button.colors=colors; button.targetGraphic=image;
            GameObject textGO=new GameObject("Text",typeof(RectTransform),typeof(TextMeshProUGUI)); textGO.transform.SetParent(buttonGO.transform,false); RectTransform tr=textGO.GetComponent<RectTransform>(); tr.anchorMin=Vector2.zero;tr.anchorMax=Vector2.one;tr.offsetMin=Vector2.zero;tr.offsetMax=Vector2.zero; TextMeshProUGUI text=textGO.GetComponent<TextMeshProUGUI>(); text.text="FILL PAINT VALUE";text.fontSize=12;text.fontStyle=FontStyles.Bold;text.alignment=TextAlignmentOptions.Center;text.color=Color.white;text.raycastTarget=false;
            int capturedId=groupId; button.onClick.AddListener(()=>FillClump(capturedId));
        }
    }

    void FillClump(int groupId)
    {
        if (clumpManager == null) return; Type managerType=typeof(ClumpLayerManager); BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        MethodInfo getLayer=managerType.GetMethod("GetOrCreateLayer",flags), regenerate=managerType.GetMethod("Regenerate",flags), apply=managerType.GetMethod("ApplyLayer",flags), refresh=managerType.GetMethod("RefreshGuideVisuals",flags); if(getLayer==null||apply==null)return;
        object layer=getLayer.Invoke(clumpManager,new object[]{groupId}); if(layer==null)return; Type layerType=layer.GetType(); FieldInfo pointsField=layerType.GetField("points",BindingFlags.Instance|BindingFlags.Public),enabledField=layerType.GetField("enabled",BindingFlags.Instance|BindingFlags.Public),pointCountField=layerType.GetField("pointCount",BindingFlags.Instance|BindingFlags.Public),brushValueField=layerType.GetField("brushValue",BindingFlags.Instance|BindingFlags.Public);
        float target=brushValueField!=null?Mathf.Clamp01((float)brushValueField.GetValue(layer)):1f; IList points=pointsField?.GetValue(layer) as IList;
        if((points==null||points.Count==0)&&regenerate!=null){int count=pointCountField!=null?(int)pointCountField.GetValue(layer):20;if(count>0)regenerate.Invoke(clumpManager,new[]{layer});points=pointsField?.GetValue(layer) as IList;}
        if(points!=null)foreach(object point in points){if(point==null)continue;FieldInfo strength=point.GetType().GetField("strength",BindingFlags.Instance|BindingFlags.Public);strength?.SetValue(point,target);}
        enabledField?.SetValue(layer,true); apply.Invoke(clumpManager,new[]{layer}); refresh?.Invoke(clumpManager,new[]{layer});
    }
}
