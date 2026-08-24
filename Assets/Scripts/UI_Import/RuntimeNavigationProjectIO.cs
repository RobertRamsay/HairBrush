using System;
using System.Collections;
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
        // Deliberately NOT telling the undo history here. LoadModel opens a file picker and
        // returns if it is cancelled, by which point the cards below are already gone - and a
        // history dropped in advance is a cancel the user cannot walk back. The model object
        // changing is enough for this path; the project path needs the explicit call because a
        // project with no modelPath replaces everything while leaving the model alone.
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
        string path;
#if UNITY_EDITOR
        path = EditorUtility.SaveFilePanel("Save Hair Project", "", "HairProject", "json");
#else
        path = RuntimeFileDialog.SaveFile("Save Hair Project", "HairBrush Projects\0*.json\0All Files\0*.*\0\0", "HairProject", "json");
#endif
        if (string.IsNullOrEmpty(path)) return;
        HairProjectSaveData data = BuildSaveData();
        File.WriteAllText(path, JsonUtility.ToJson(data,true));
        Debug.Log("Project saved successfully to: "+path);
    }

    // The whole session as a save payload, with no file involved. Split out of
    // SaveProjectEnhanced so UndoHistoryAuthority can take the same picture the file gets:
    // one gatherer means an undo step can never quietly cover less than a save does, and a
    // feature added to the save format is undoable the moment it is saveable.
    public HairProjectSaveData BuildSaveData()
    {
        HairProjectSaveData data = new HairProjectSaveData();
        data.modelPath = GetField<string>("currentModelPath");
        data.sliderLength=viewer.currentLength; data.sliderWidth=viewer.currentWidth; data.sliderSegments=viewer.currentSegments;
        data.sliderBend=viewer.currentBend; data.sliderTwist=viewer.currentTwist; data.sliderEmbedDepth=viewer.currentEmbedDepth;
        data.sliderOffsetX=viewer.currentOffsetX; data.sliderOffsetY=viewer.currentOffsetY; data.sliderOffsetZ=viewer.currentOffsetZ;
        data.sliderUScale=viewer.currentUScale; data.sliderVScale=viewer.currentVScale; data.sliderUOffset=viewer.currentUOffset; data.sliderVOffset=viewer.currentVOffset;
        data.sliderCurlFrequency=viewer.currentCurlFrequency; data.sliderCurlDiameter=viewer.currentCurlDiameter;
        data.sliderWaveAmplitude=viewer.currentWaveAmplitude; data.sliderWaveFrequency=viewer.currentWaveFrequency; data.sliderWaveDirection=viewer.currentWaveDirection; data.sliderArch=viewer.currentArch;

        HashSet<int> ids = GetField<HashSet<int>>("allGroupIds") ?? new HashSet<int>();
        var names=GetField<Dictionary<int,string>>("groupNames"); var us=GetField<Dictionary<int,float>>("groupUScales"); var vs=GetField<Dictionary<int,float>>("groupVScales"); var uo=GetField<Dictionary<int,float>>("groupUOffsets"); var vo=GetField<Dictionary<int,float>>("groupVOffsets");
        foreach(int id in ids)
        {
            GroupSaveData g=new GroupSaveData{groupId=id,groupName=names!=null&&names.ContainsKey(id)?names[id]:"Group "+id,uScale=us!=null&&us.ContainsKey(id)?us[id]:1f,vScale=vs!=null&&vs.ContainsKey(id)?vs[id]:1f,uOffset=uo!=null&&uo.ContainsKey(id)?uo[id]:0f,vOffset=vo!=null&&vo.ContainsKey(id)?vo[id]:0f};
            modifiers?.PopulateGroupSave(g); data.groups.Add(g);
        }
        // Recorded alongside the payload so CanonicalizeForSave can pair each entry back to the
        // card it was built from rather than searching for it. Not serialized; see the field.
        data.captureSourceCards = new List<HairCard>();
        foreach(HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
        {
            Vector3 hit = card.GetSpawnHitPoint();
            Vector3 normal = card.GetSurfaceNormal();
            data.captureSourceCards.Add(card);
            data.hairCards.Add(new HairCardSaveData
            {
                posX=card.transform.position.x,posY=card.transform.position.y,posZ=card.transform.position.z,
                rotX=card.transform.rotation.x,rotY=card.transform.rotation.y,rotZ=card.transform.rotation.z,rotW=card.transform.rotation.w,
                hitX=hit.x,hitY=hit.y,hitZ=hit.z,normalX=normal.x,normalY=normal.y,normalZ=normal.z,
                length=card.length,width=card.width,segments=card.segments,bendAngle=card.bendAngle,twistAngle=card.twistAngle,flattenFactor=card.flattenFactor,
                embedDepth=card.GetEmbedDepth(),offsetX=card.GetOffsetX(),offsetY=card.GetOffsetY(),offsetZ=card.GetOffsetZ(),
                uScale=card.uScale,vScale=card.vScale,uOffset=card.uOffset,vOffset=card.vOffset,groupId=card.groupId,
                curlFrequency=card.curlFrequency,curlDiameter=card.curlDiameter,waveAmplitude=card.waveAmplitude,waveFrequency=card.waveFrequency,waveDirection=card.waveDirection,arch=card.arch,mirrored=card.mirrored
            });
        }
        return data;
    }

    public void LoadProjectEnhanced()
    {
        string path;
#if UNITY_EDITOR
        path = EditorUtility.OpenFilePanel("Open Hair Project", "", "json");
#else
        path = RuntimeFileDialog.OpenFile("Open Hair Project", "HairBrush Projects\0*.json\0All Files\0*.*\0\0", "json");
#endif
        if(string.IsNullOrEmpty(path))return;
        HairProjectSaveData data=JsonUtility.FromJson<HairProjectSaveData>(File.ReadAllText(path)); if(data==null)return;
        UndoHistoryAuthority.NotifySessionReplaced();
        CleanupEditorUIAndCards();

        if(!string.IsNullOrEmpty(data.modelPath))
        {
            SetField("currentModelPath",data.modelPath); GameObject old=GetField<GameObject>("loadedModel"); if(old!=null)Destroy(old);
            GameObject model=CustomOBJImporter.Load(data.modelPath); SetField("loadedModel",model);
            if(model!=null){model.transform.position=Vector3.zero;model.transform.eulerAngles=new Vector3(0,180,0);MeshRenderer[] rs=model.GetComponentsInChildren<MeshRenderer>();if(rs.Length>0){Bounds b=rs[0].bounds;for(int i=1;i<rs.Length;i++)b.Encapsulate(rs[i].bounds);if(viewer.cameraPivot!=null)viewer.cameraPivot.position=b.center;}}
            else Debug.LogError("HairBrush: could not load model referenced by project - file not found at: " + data.modelPath);
        }

        ApplyGlobalSliders(data);
        ApplyGroupRegistry(data);
        viewer.currentGroupId=data.groups.Count>0?data.groups[0].groupId:0;
        SpawnSavedCards(data);

        // SOLO is session-only and is never written to the project file. A load must
        // therefore come up with every group visible and every SOLO button unlit, whatever
        // was soloed in the session being replaced. Cleared AFTER the cards are created so
        // ApplyVisibility can see them and switch their renderers back on.
        viewer.ResetSoloState();

        if(viewer.uiContainer!=null)viewer.uiContainer.SetActive(false);
        viewer.OnModelLoaded();
        viewer.BuildRuntimeGroomingUI();
        MethodInfo buildGroups=typeof(ModelViewer).GetMethod("BuildGroupManagementUI",BindingFlags.Instance|BindingFlags.NonPublic);buildGroups?.Invoke(viewer,null);
        SetField("isGroomingMode",true);
        // Any root states still cached belong to the session being replaced. Forget them
        // BEFORE RestoreGroup, because restoring a group's variance immediately re-applies
        // it to every card, and the base it varies around is read from those roots first.
        // Left in place, the incoming project's cards get varied around the outgoing
        // project's numbers.
        GroomRootStateAuthority rootState=FindFirstObjectByType<GroomRootStateAuthority>();
        if(rootState!=null)rootState.ForgetStoredRoots();

        foreach(GroupSaveData g in data.groups)modifiers?.RestoreGroup(g);
        StartCoroutine(SelectLoadedGroupWhenSettled(data));
        RepairAngleControls(true);
        Debug.Log("Project loaded successfully from: "+path);
    }

    // The three steps below are the part of a load that rebuilds the SESSION rather than the
    // scene: no file, no model, no panel teardown. Split out so UndoHistoryAuthority can replay
    // a snapshot without reloading the OBJ or flashing the panels, and so it is replaying the
    // same code a project load runs rather than a second copy of it that can drift.

    public void ApplyGlobalSliders(HairProjectSaveData data)
    {
        if(data==null||viewer==null)return;
        viewer.currentLength=data.sliderLength;viewer.currentWidth=data.sliderWidth;viewer.currentSegments=data.sliderSegments;viewer.currentBend=data.sliderBend;viewer.currentTwist=data.sliderTwist;viewer.currentEmbedDepth=data.sliderEmbedDepth;viewer.currentOffsetX=data.sliderOffsetX;viewer.currentOffsetY=data.sliderOffsetY;viewer.currentOffsetZ=data.sliderOffsetZ;viewer.currentUScale=data.sliderUScale;viewer.currentVScale=data.sliderVScale;viewer.currentUOffset=data.sliderUOffset;viewer.currentVOffset=data.sliderVOffset;
        viewer.currentCurlFrequency=data.sliderCurlFrequency;viewer.currentCurlDiameter=data.sliderCurlDiameter;
        viewer.currentWaveAmplitude=data.sliderWaveAmplitude;viewer.currentWaveFrequency=data.sliderWaveFrequency;viewer.currentWaveDirection=data.sliderWaveDirection;viewer.currentArch=data.sliderArch;
    }

    public void ApplyGroupRegistry(HairProjectSaveData data)
    {
        if(data==null||data.groups==null||viewer==null)return;
        HashSet<int> ids=GetField<HashSet<int>>("allGroupIds");var names=GetField<Dictionary<int,string>>("groupNames");var us=GetField<Dictionary<int,float>>("groupUScales");var vs=GetField<Dictionary<int,float>>("groupVScales");var uo=GetField<Dictionary<int,float>>("groupUOffsets");var vo=GetField<Dictionary<int,float>>("groupVOffsets");ids?.Clear();names?.Clear();us?.Clear();vs?.Clear();uo?.Clear();vo?.Clear();
        foreach(GroupSaveData g in data.groups){ids?.Add(g.groupId);if(names!=null)names[g.groupId]=g.groupName;if(us!=null)us[g.groupId]=g.uScale;if(vs!=null)vs[g.groupId]=g.vScale;if(uo!=null)uo[g.groupId]=g.uOffset;if(vo!=null)vo[g.groupId]=g.vOffset;}
    }

    public void SpawnSavedCards(HairProjectSaveData data)
    {
        if(data==null||data.hairCards==null||viewer==null)return;
        foreach(HairCardSaveData c in data.hairCards)
        {
            GameObject go=new GameObject("HairCard_Strip",typeof(MeshFilter),typeof(MeshRenderer),typeof(HairCard));
            HairCard card=go.GetComponent<HairCard>();
            // Restored BEFORE SetPlacementData/SetParameters, both of which orient and build
            // from it. Set afterwards, the card would come up shaped like its partner.
            card.mirrored=c.mirrored;
            Vector3 hit=new Vector3(c.hitX,c.hitY,c.hitZ);
            Vector3 normal=new Vector3(c.normalX,c.normalY,c.normalZ).normalized;
            card.SetPlacementData(hit,normal,c.embedDepth,c.offsetX,c.offsetY,c.offsetZ,c.groupId);
            card.SetParameters(c.length,c.width,c.segments,c.bendAngle,c.twistAngle,c.offsetX,c.offsetY,c.offsetZ,c.embedDepth,1f,c.uScale,c.vScale,c.uOffset,c.vOffset,c.curlFrequency,c.curlDiameter,c.waveAmplitude,c.waveFrequency,c.waveDirection,c.arch);
            if(viewer.hairCardMaterial!=null)go.GetComponent<MeshRenderer>().sharedMaterial=viewer.hairCardMaterial;
        }
    }

    // A loaded project must come up exactly as if the user had just clicked its first
    // group: that group highlighted, and every shape slider showing THAT GROUP'S own
    // authored settings, ready to tweak and ready for the next hair placed to inherit.
    //
    // Load used to assign viewer.currentGroupId directly, which skips SelectGroup and
    // therefore skips SyncShapeSlidersToGroupRoot entirely. The sliders were left on
    // the file's single global slider block, which is whatever happened to be on screen
    // when Save was pressed - a POST's values, a half-finished experiment, anything.
    // That is how a curly project could load looking perfect and then place dead
    // straight cards: the curl lived on the cards, but the sliders never learned it.
    // The group's settings are recovered from its own cards, so they can only be read
    // once those cards genuinely hold their saved values - and at the end of load they
    // do not.
    //
    // RestoreGroup re-applies the group's variance the moment it is imported, varying
    // every card around whatever base is on hand at that instant, which is the file's
    // single global slider block. That overwrites each card's canonical state.
    // CanonicalProjectStateBridge is the safety net that puts the real per-card values
    // back, but it deliberately waits for the modifier restore and a settle frame, so it
    // lands two or more frames after load returns.
    //
    // Sampling in between reads the clobbered values. That is why a curly project came
    // up with near-zero curl sliders while the cards on screen looked perfectly correct,
    // and why nudging the curl sliders afterwards "unified" everything - that nudge was
    // the first time the group's real base reached the cards.
    IEnumerator SelectLoadedGroupWhenSettled(HairProjectSaveData data)
    {
        // Highlight the group immediately so the panel is never left without a selection.
        SelectLoadedGroup(data);

        // Wait out any queued canonical restore. An older project that never queues one
        // falls straight through; the frame guard covers a restore that cannot finish.
        CanonicalProjectStateBridge bridge=FindFirstObjectByType<CanonicalProjectStateBridge>();
        int frames=0;
        while(frames<600&&(CanonicalProjectStateBridge.PendingCanonicalRestore!=null||(bridge!=null&&bridge.HasPendingRestore)))
        {
            frames++;
            yield return null;
        }

        // One more frame so the restore's own SetParameters calls have settled.
        yield return null;

        // Now the cards hold their saved values, so the group's real settings can be read
        // off them. This also re-forgets the roots captured from the clobbered state.
        SelectLoadedGroup(data);
    }

    void SelectLoadedGroup(HairProjectSaveData data)
    {
        if(viewer==null)return;

        // Drop any root captured from the intermediate state so the group is recovered
        // from its own cards rather than from whatever the sliders were showing.
        GroomRootStateAuthority rootState=FindFirstObjectByType<GroomRootStateAuthority>();
        if(rootState!=null)rootState.ForgetStoredRoots();

        int groupId=int.MaxValue;
        if(data!=null&&data.groups!=null)
            foreach(GroupSaveData g in data.groups)
                if(g!=null&&g.groupId<groupId)groupId=g.groupId;

        // Older saves may carry no group block at all - take it from the cards instead.
        if(groupId==int.MaxValue)
            foreach(HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
                if(card!=null&&card.groupId<groupId)groupId=card.groupId;

        if(groupId==int.MaxValue)groupId=0;

        MethodInfo selectGroup=typeof(ModelViewer).GetMethod("SelectGroup",BindingFlags.Instance|BindingFlags.NonPublic);
        if(selectGroup!=null)selectGroup.Invoke(viewer,new object[]{groupId});
        else{viewer.currentGroupId=groupId;viewer.SyncShapeSlidersToGroupRoot(groupId);}
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
