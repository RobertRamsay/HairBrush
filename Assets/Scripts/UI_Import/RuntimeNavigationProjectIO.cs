using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
#if UNITY_EDITOR
using UnityEditor;
#endif

[DefaultExecutionOrder(1800)]
public class RuntimeNavigationProjectIO : MonoBehaviour
{
    private ModelViewer viewer;
    private ModifierPersistenceBridge modifiers;
    private float nextScan;
    private bool hookedInitialButtons;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        var go = new GameObject("RuntimeNavigationProjectIO");
        DontDestroyOnLoad(go);
        go.AddComponent<RuntimeNavigationProjectIO>();
    }

    void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + 0.2f;
        if (viewer == null) viewer = FindFirstObjectByType<ModelViewer>();
        if (viewer == null) return;
        if (modifiers == null) modifiers = FindFirstObjectByType<ModifierPersistenceBridge>();
        HookInitialMenuButtons();
        InstallEditorMenuButton();
        HookRuntimeSaveButton();
        RepairAngleControls();
    }

    void HookInitialMenuButtons()
    {
        if (!hookedInitialButtons)
        {
            if (viewer.loadProjectButton != null)
            {
                viewer.loadProjectButton.onClick.RemoveAllListeners();
                viewer.loadProjectButton.onClick.AddListener(LoadProjectEnhanced);
            }
            if (viewer.loadButton != null)
            {
                viewer.loadButton.onClick.RemoveAllListeners();
                viewer.loadButton.onClick.AddListener(LoadFreshModel);
            }
            hookedInitialButtons = true;
        }
    }

    void InstallEditorMenuButton()
    {
        GameObject panel = GameObject.Find("GroupManagerPanel");
        if (panel == null || panel.transform.Find("MenuButton_Runtime") != null) return;
        GameObject menu = MakeButton(panel.transform, "MenuButton_Runtime", "MENU", new Color(.24f,.30f,.38f), 36f);
        menu.transform.SetSiblingIndex(0);
        menu.GetComponent<Button>().onClick.AddListener(ReturnToMenu);
    }

    void HookRuntimeSaveButton()
    {
        GameObject save = GameObject.Find("SaveProjectButton");
        if (save == null) return;
        Button button = save.GetComponent<Button>();
        if (button == null || save.GetComponent<EnhancedSaveMarker>() != null) return;
        button.onClick.RemoveAllListeners();
        button.onClick.AddListener(SaveProjectEnhanced);
        save.AddComponent<EnhancedSaveMarker>();
    }

    void ReturnToMenu()
    {
        if (viewer.uiContainer != null) viewer.uiContainer.SetActive(true);
        GameObject groom = GameObject.Find("GroomingPanel"); if (groom != null) groom.SetActive(false);
        GameObject groups = GameObject.Find("GroupManagerPanel"); if (groups != null) groups.SetActive(false);
        viewer.ToggleGroomingMode(false);
    }

    void LoadFreshModel()
    {
        CleanupEditorUIAndCards();
        MethodInfo load = typeof(ModelViewer).GetMethod("LoadModel", BindingFlags.Instance | BindingFlags.NonPublic);
        load?.Invoke(viewer, null);
    }

    void CleanupEditorUIAndCards()
    {
        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None)) Destroy(card.gameObject);
        foreach (string name in new[] { "GroomingPanel", "GroupManagerPanel" })
        {
            GameObject go = GameObject.Find(name); if (go != null) Destroy(go);
        }
        SetField("activeSliderPanel", null);
        viewer.groomingSliderPanelGO = null;
    }

    public void SaveProjectEnhanced()
    {
#if UNITY_EDITOR
        string path = EditorUtility.SaveFilePanel("Save Hair Project", "", "HairProject", "json");
        if (string.IsNullOrEmpty(path)) return;
        HairProjectSaveData data = new HairProjectSaveData();
        data.modelPath = GetField<string>("currentModelPath");
        data.sliderLength=viewer.currentLength; data.sliderWidth=viewer.currentWidth; data.sliderSegments=viewer.currentSegments;
        data.sliderBend=viewer.currentBend; data.sliderTwist=viewer.currentTwist; data.sliderEmbedDepth=viewer.currentEmbedDepth;
        data.sliderOffsetX=viewer.currentOffsetX; data.sliderOffsetY=viewer.currentOffsetY; data.sliderOffsetZ=viewer.currentOffsetZ;
        data.sliderUScale=viewer.currentUScale; data.sliderVScale=viewer.currentVScale; data.sliderUOffset=viewer.currentUOffset; data.sliderVOffset=viewer.currentVOffset;

        HashSet<int> ids = GetField<HashSet<int>>("allGroupIds") ?? new HashSet<int>();
        var names=GetField<Dictionary<int,string>>("groupNames"); var us=GetField<Dictionary<int,float>>("groupUScales"); var vs=GetField<Dictionary<int,float>>("groupVScales"); var uo=GetField<Dictionary<int,float>>("groupUOffsets"); var vo=GetField<Dictionary<int,float>>("groupVOffsets");
        foreach(int id in ids)
        {
            GroupSaveData g=new GroupSaveData{groupId=id,groupName=names!=null&&names.ContainsKey(id)?names[id]:"Group "+id,uScale=us!=null&&us.ContainsKey(id)?us[id]:1f,vScale=vs!=null&&vs.ContainsKey(id)?vs[id]:1f,uOffset=uo!=null&&uo.ContainsKey(id)?uo[id]:0f,vOffset=vo!=null&&vo.ContainsKey(id)?vo[id]:0f};
            modifiers?.PopulateGroupSave(g); data.groups.Add(g);
        }
        foreach(HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
        {
            Vector3 hit = card.GetSpawnHitPoint();
            Vector3 normal = card.GetSurfaceNormal();
            data.hairCards.Add(new HairCardSaveData
            {
                posX=card.transform.position.x,posY=card.transform.position.y,posZ=card.transform.position.z,
                rotX=card.transform.rotation.x,rotY=card.transform.rotation.y,rotZ=card.transform.rotation.z,rotW=card.transform.rotation.w,
                hitX=hit.x,hitY=hit.y,hitZ=hit.z,normalX=normal.x,normalY=normal.y,normalZ=normal.z,
                length=card.length,width=card.width,segments=card.segments,bendAngle=card.bendAngle,twistAngle=card.twistAngle,flattenFactor=card.flattenFactor,
                embedDepth=card.GetEmbedDepth(),offsetX=card.GetOffsetX(),offsetY=card.GetOffsetY(),offsetZ=card.GetOffsetZ(),
                uScale=card.uScale,vScale=card.vScale,uOffset=card.uOffset,vOffset=card.vOffset,groupId=card.groupId
            });
        }
        File.WriteAllText(path, JsonUtility.ToJson(data,true));
        Debug.Log("Project saved successfully to: "+path);
#endif
    }

    public void LoadProjectEnhanced()
    {
#if UNITY_EDITOR
        string path=EditorUtility.OpenFilePanel("Open Hair Project","","json"); if(string.IsNullOrEmpty(path))return;
        HairProjectSaveData data=JsonUtility.FromJson<HairProjectSaveData>(File.ReadAllText(path)); if(data==null)return;
        CleanupEditorUIAndCards();

        if(!string.IsNullOrEmpty(data.modelPath))
        {
            SetField("currentModelPath",data.modelPath); GameObject old=GetField<GameObject>("loadedModel"); if(old!=null)Destroy(old);
            GameObject model=CustomOBJImporter.Load(data.modelPath); SetField("loadedModel",model);
            if(model!=null){model.transform.position=Vector3.zero;model.transform.eulerAngles=new Vector3(0,180,0);MeshRenderer[] rs=model.GetComponentsInChildren<MeshRenderer>();if(rs.Length>0){Bounds b=rs[0].bounds;for(int i=1;i<rs.Length;i++)b.Encapsulate(rs[i].bounds);if(viewer.cameraPivot!=null)viewer.cameraPivot.position=b.center;}}
        }

        viewer.currentLength=data.sliderLength;viewer.currentWidth=data.sliderWidth;viewer.currentSegments=data.sliderSegments;viewer.currentBend=data.sliderBend;viewer.currentTwist=data.sliderTwist;viewer.currentEmbedDepth=data.sliderEmbedDepth;viewer.currentOffsetX=data.sliderOffsetX;viewer.currentOffsetY=data.sliderOffsetY;viewer.currentOffsetZ=data.sliderOffsetZ;viewer.currentUScale=data.sliderUScale;viewer.currentVScale=data.sliderVScale;viewer.currentUOffset=data.sliderUOffset;viewer.currentVOffset=data.sliderVOffset;

        HashSet<int> ids=GetField<HashSet<int>>("allGroupIds");var names=GetField<Dictionary<int,string>>("groupNames");var us=GetField<Dictionary<int,float>>("groupUScales");var vs=GetField<Dictionary<int,float>>("groupVScales");var uo=GetField<Dictionary<int,float>>("groupUOffsets");var vo=GetField<Dictionary<int,float>>("groupVOffsets");ids?.Clear();names?.Clear();us?.Clear();vs?.Clear();uo?.Clear();vo?.Clear();
        foreach(GroupSaveData g in data.groups){ids?.Add(g.groupId);if(names!=null)names[g.groupId]=g.groupName;if(us!=null)us[g.groupId]=g.uScale;if(vs!=null)vs[g.groupId]=g.vScale;if(uo!=null)uo[g.groupId]=g.uOffset;if(vo!=null)vo[g.groupId]=g.vOffset;}
        viewer.currentGroupId=data.groups.Count>0?data.groups[0].groupId:0;

        foreach(HairCardSaveData c in data.hairCards)
        {
            GameObject go=new GameObject("HairCard_Strip",typeof(MeshFilter),typeof(MeshRenderer),typeof(HairCard));
            HairCard card=go.GetComponent<HairCard>();
            Vector3 hit=new Vector3(c.hitX,c.hitY,c.hitZ);
            Vector3 normal=new Vector3(c.normalX,c.normalY,c.normalZ).normalized;
            card.SetPlacementData(hit,normal,c.embedDepth,c.offsetX,c.offsetY,c.offsetZ,c.groupId);
            card.SetParameters(c.length,c.width,c.segments,c.bendAngle,c.twistAngle,c.offsetX,c.offsetY,c.offsetZ,c.embedDepth,1f,c.uScale,c.vScale,c.uOffset,c.vOffset);
            if(viewer.hairCardMaterial!=null)go.GetComponent<MeshRenderer>().sharedMaterial=viewer.hairCardMaterial;
        }

        if(viewer.uiContainer!=null)viewer.uiContainer.SetActive(false);
        viewer.OnModelLoaded();
        viewer.BuildRuntimeGroomingUI();
        MethodInfo buildGroups=typeof(ModelViewer).GetMethod("BuildGroupManagementUI",BindingFlags.Instance|BindingFlags.NonPublic);buildGroups?.Invoke(viewer,null);
        SetField("isGroomingMode",true);
        foreach(GroupSaveData g in data.groups)modifiers?.RestoreGroup(g);
        RepairAngleControls(true);
        Debug.Log("Project loaded successfully from: "+path);
#endif
    }

    void RepairAngleControls(bool forceValues=false)
    {
        if(viewer==null||viewer.groomingSliderPanelGO==null)return;
        RepairAngle("Angle X_Row","Offset X_Row",viewer.currentOffsetX,viewer.OnSliderOffsetXChanged,forceValues);
        RepairAngle("Angle Y_Row","Offset Y_Row",viewer.currentOffsetY,viewer.OnSliderOffsetYChanged,forceValues);
        RepairAngle("Angle Z_Row","Offset Z_Row",viewer.currentOffsetZ,viewer.OnSliderOffsetZChanged,forceValues);
    }

    void RepairAngle(string renamed,string legacy,float value,UnityEngine.Events.UnityAction<float> callback,bool force)
    {
        Transform panel=viewer.groomingSliderPanelGO.transform;Transform row=panel.Find(renamed)??panel.Find(legacy);if(row==null)return;Slider slider=row.GetComponentInChildren<Slider>(true);if(slider==null)return;
        AngleBindingMarker marker=slider.GetComponent<AngleBindingMarker>();if(marker==null){marker=slider.gameObject.AddComponent<AngleBindingMarker>();slider.onValueChanged.AddListener(callback);}if(force)slider.SetValueWithoutNotify(value);
    }

    GameObject MakeButton(Transform parent,string name,string label,Color color,float height)
    {
        GameObject go=new GameObject(name,typeof(RectTransform),typeof(Image),typeof(Button),typeof(LayoutElement));go.transform.SetParent(parent,false);go.GetComponent<RectTransform>().sizeDelta=new Vector2(0,height);go.GetComponent<LayoutElement>().preferredHeight=height;go.GetComponent<Image>().color=color;
        GameObject tg=new GameObject("Text",typeof(RectTransform),typeof(TextMeshProUGUI));tg.transform.SetParent(go.transform,false);RectTransform tr=tg.GetComponent<RectTransform>();tr.anchorMin=Vector2.zero;tr.anchorMax=Vector2.one;tr.offsetMin=Vector2.zero;tr.offsetMax=Vector2.zero;TextMeshProUGUI t=tg.GetComponent<TextMeshProUGUI>();t.text=label;t.fontSize=16;t.fontStyle=FontStyles.Bold;t.alignment=TextAlignmentOptions.Center;t.color=Color.white;t.raycastTarget=false;return go;
    }

    void QuitApplication()
    {
#if UNITY_EDITOR
        EditorApplication.isPlaying=false;
#else
        Application.Quit();
#endif
    }

    T GetField<T>(string name){FieldInfo f=typeof(ModelViewer).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic);return f!=null?(T)f.GetValue(viewer):default;}
    void SetField(string name,object value){FieldInfo f=typeof(ModelViewer).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic);f?.SetValue(viewer,value);}
}

public class EnhancedSaveMarker:MonoBehaviour{}
public class AngleBindingMarker:MonoBehaviour{}
