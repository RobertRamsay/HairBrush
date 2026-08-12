using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Adds deterministic +/- per-card variation underneath selected grooming controls.
public class GroomVarianceController : MonoBehaviour
{
    private enum Channel { Length, Bend, Twist, AngleX, AngleY, AngleZ }

    [Serializable] private class VarianceSetting { public float amount; public int seed; }
    private class VarianceRow { public Channel channel; public Slider slider; public TextMeshProUGUI valueText; public TMP_InputField seedInput; }

    private readonly Dictionary<int, Dictionary<Channel, VarianceSetting>> groupSettings = new();
    private readonly Dictionary<Channel, VarianceRow> rows = new();
    private readonly Dictionary<Channel, Slider> mainSliders = new();
    private readonly Dictionary<Channel, TextMeshProUGUI> mainLabels = new();
    private ModelViewer viewer; private bool installed; private int lastGroupId = int.MinValue; private int lastCardCount = -1; private float nextInstallAttempt;

    public void Init(ModelViewer owner) { viewer = owner; }

    public List<VarianceChannelSaveData> ExportGroupSettings(int groupId)
    {
        List<VarianceChannelSaveData> result = new();
        foreach (Channel channel in Enum.GetValues(typeof(Channel)))
        {
            VarianceSetting s = GetSetting(groupId, channel);
            result.Add(new VarianceChannelSaveData { channel = channel.ToString(), amount = s.amount, seed = s.seed });
        }
        return result;
    }

    public void ImportGroupSettings(int groupId, List<VarianceChannelSaveData> data)
    {
        if (data == null) return;
        foreach (VarianceChannelSaveData item in data)
        {
            if (item == null || !Enum.TryParse(item.channel, out Channel channel)) continue;
            VarianceSetting s = GetSetting(groupId, channel); s.amount = item.amount; s.seed = item.seed;
        }
        if (viewer != null && viewer.currentGroupId == groupId) SyncRowsForGroup(groupId);
        ApplyAllVarianceForGroup(groupId);
    }

    public void ClearSavedSettings() { groupSettings.Clear(); }

    void Update()
    {
        if (viewer == null) viewer = FindFirstObjectByType<ModelViewer>(); if (viewer == null) return;
        if (!installed && Time.unscaledTime >= nextInstallAttempt) { nextInstallAttempt = Time.unscaledTime + 0.25f; TryInstall(); }
        if (!installed) return;
        MaintainAngleLabels();
        if (viewer.currentGroupId != lastGroupId) { lastGroupId = viewer.currentGroupId; SyncRowsForGroup(lastGroupId); lastCardCount = CountCards(lastGroupId); }
        int count = CountCards(viewer.currentGroupId); if (count != lastCardCount) { lastCardCount = count; ApplyAllActiveVariance(); }
    }

    void TryInstall()
    {
        if (viewer.groomingSliderPanelGO == null) return; Transform panel = viewer.groomingSliderPanelGO.transform;
        var definitions = new[] { (Channel.Length,"Length_Row","Length",0.5f),(Channel.Bend,"Bend Angle_Row","Bend Angle",360f),(Channel.Twist,"Twist Angle_Row","Twist Angle",360f),(Channel.AngleX,"Offset X_Row","Angle X",360f),(Channel.AngleY,"Offset Y_Row","Angle Y",360f),(Channel.AngleZ,"Offset Z_Row","Angle Z",360f) };
        foreach (var d in definitions) { Transform r=panel.Find(d.Item2); if(r==null||r.GetComponentInChildren<Slider>(true)==null)return; }
        foreach (var d in definitions)
        {
            Transform mainRow=panel.Find(d.Item2); Slider main=mainRow.GetComponentInChildren<Slider>(true); TextMeshProUGUI label=mainRow.GetComponentInChildren<TextMeshProUGUI>(true); mainSliders[d.Item1]=main; if(label!=null)mainLabels[d.Item1]=label;
            if(d.Item1>=Channel.AngleX) RenameMainControl(mainRow,d.Item3); VarianceRow row=BuildVarianceRow(panel,mainRow,d.Item1,d.Item4); rows[d.Item1]=row; Channel captured=d.Item1;
            main.onValueChanged.AddListener(_=>{ if(GetSetting(viewer.currentGroupId,captured).amount>0f)ApplyChannel(captured); MaintainMainLabel(captured); });
        }
        installed=true; lastGroupId=viewer.currentGroupId; lastCardCount=CountCards(lastGroupId); SyncRowsForGroup(lastGroupId); MaintainAngleLabels();
    }

    void RenameMainControl(Transform row,string newLabel){row.name=newLabel+"_Row";var text=row.GetComponentInChildren<TextMeshProUGUI>(true);var slider=row.GetComponentInChildren<Slider>(true);if(text!=null&&slider!=null)text.text=newLabel+": "+slider.value.ToString("F3");if(text!=null)text.gameObject.name=newLabel+"_Text";if(slider!=null)slider.gameObject.name=newLabel+"_Slider";}
    void MaintainAngleLabels(){MaintainMainLabel(Channel.AngleX);MaintainMainLabel(Channel.AngleY);MaintainMainLabel(Channel.AngleZ);}
    void MaintainMainLabel(Channel c){if(!mainLabels.TryGetValue(c,out var l)||l==null||!mainSliders.TryGetValue(c,out var s)||s==null)return;string n=c==Channel.AngleX?"Angle X":c==Channel.AngleY?"Angle Y":c==Channel.AngleZ?"Angle Z":null;if(n!=null)l.text=n+": "+s.value.ToString("F3");}

    VarianceRow BuildVarianceRow(Transform panel,Transform mainRow,Channel channel,float maxVariance)
    {
        string key=ChannelLabel(channel);GameObject rowGO=new GameObject(key+"_VarianceRow",typeof(RectTransform),typeof(HorizontalLayoutGroup));rowGO.transform.SetParent(panel,false);rowGO.transform.SetSiblingIndex(mainRow.GetSiblingIndex()+1);rowGO.GetComponent<RectTransform>().sizeDelta=new Vector2(0,28);
        var layout=rowGO.GetComponent<HorizontalLayoutGroup>();layout.spacing=5;layout.padding=new RectOffset(4,2,2,2);layout.childControlHeight=true;layout.childControlWidth=false;layout.childForceExpandHeight=false;layout.childForceExpandWidth=false;
        var valueText=AddText(rowGO.transform,"VAR ± 0.000",11,82);valueText.alignment=TextAlignmentOptions.MidlineLeft;var varianceSlider=AddCompactSlider(rowGO.transform,0,maxVariance,0,245);var seedLabel=AddText(rowGO.transform,"SEED",10,38);seedLabel.alignment=TextAlignmentOptions.Center;var seedInput=AddSeedField(rowGO.transform,78);var randomButton=AddButton(rowGO.transform,"R",30);
        VarianceRow result=new(){channel=channel,slider=varianceSlider,valueText=valueText,seedInput=seedInput};
        varianceSlider.onValueChanged.AddListener(v=>{var s=GetSetting(viewer.currentGroupId,channel);s.amount=v;valueText.text="VAR ± "+FormatVariance(channel,v);ApplyChannel(channel);});
        seedInput.onEndEdit.AddListener(value=>{var s=GetSetting(viewer.currentGroupId,channel);if(!int.TryParse(value,out int parsed))parsed=0;s.seed=parsed;seedInput.SetTextWithoutNotify(parsed.ToString());if(s.amount>0)ApplyChannel(channel);});
        randomButton.GetComponent<Button>().onClick.AddListener(()=>{var s=GetSetting(viewer.currentGroupId,channel);s.seed=UnityEngine.Random.Range(0,1000000);seedInput.SetTextWithoutNotify(s.seed.ToString());if(s.amount>0)ApplyChannel(channel);});return result;
    }

    void SyncRowsForGroup(int id){foreach(var p in rows){var s=GetSetting(id,p.Key);p.Value.slider.SetValueWithoutNotify(s.amount);p.Value.seedInput.SetTextWithoutNotify(s.seed.ToString());p.Value.valueText.text="VAR ± "+FormatVariance(p.Key,s.amount);}}
    VarianceSetting GetSetting(int id,Channel c){if(!groupSettings.TryGetValue(id,out var d)){d=new();groupSettings[id]=d;}if(!d.TryGetValue(c,out var s)){s=new(){amount=0,seed=0};d[c]=s;}return s;}
    void ApplyAllActiveVariance(){ApplyAllVarianceForGroup(viewer.currentGroupId);}
    void ApplyAllVarianceForGroup(int groupId){int old=viewer.currentGroupId;viewer.currentGroupId=groupId;foreach(Channel c in Enum.GetValues(typeof(Channel)))if(GetSetting(groupId,c).amount>0)ApplyChannel(c);viewer.currentGroupId=old;}
    void ApplyChannel(Channel c){if(viewer==null)return;var s=GetSetting(viewer.currentGroupId,c);float b=MainValue(c);foreach(var card in FindObjectsByType<HairCard>(FindObjectsSortMode.None).Where(x=>x.groupId==viewer.currentGroupId)){float varied=b+SignedRandom(card,c,s.seed)*s.amount;float l=card.length,be=card.bendAngle,t=card.twistAngle,x=card.GetOffsetX(),y=card.GetOffsetY(),z=card.GetOffsetZ();switch(c){case Channel.Length:l=Mathf.Max(.0005f,varied);break;case Channel.Bend:be=varied;break;case Channel.Twist:t=varied;break;case Channel.AngleX:x=varied;break;case Channel.AngleY:y=varied;break;case Channel.AngleZ:z=varied;break;}card.SetParameters(l,card.width,card.segments,be,t,x,y,z,card.GetEmbedDepth(),1,card.uScale,card.vScale,card.uOffset,card.vOffset);}}
    float MainValue(Channel c)=>c switch{Channel.Length=>viewer.currentLength,Channel.Bend=>viewer.currentBend,Channel.Twist=>viewer.currentTwist,Channel.AngleX=>viewer.currentOffsetX,Channel.AngleY=>viewer.currentOffsetY,Channel.AngleZ=>viewer.currentOffsetZ,_=>0};
    float SignedRandom(HairCard card,Channel c,int seed){Vector3 p=card.GetSpawnHitPoint();unchecked{uint h=2166136261u;Mix(ref h,Mathf.RoundToInt(p.x*10000));Mix(ref h,Mathf.RoundToInt(p.y*10000));Mix(ref h,Mathf.RoundToInt(p.z*10000));Mix(ref h,viewer.currentGroupId);Mix(ref h,(int)c*7919);Mix(ref h,seed);h^=h>>16;h*=0x7feb352du;h^=h>>15;h*=0x846ca68bu;h^=h>>16;return(h&0x00FFFFFFu)/16777215f*2-1;}}
    static void Mix(ref uint h,int v){unchecked{h^=(uint)v;h*=16777619u;}}
    int CountCards(int id)=>FindObjectsByType<HairCard>(FindObjectsSortMode.None).Count(c=>c.groupId==id);
    string ChannelLabel(Channel c)=>c switch{Channel.Length=>"Length",Channel.Bend=>"Bend",Channel.Twist=>"Twist",Channel.AngleX=>"Angle X",Channel.AngleY=>"Angle Y",Channel.AngleZ=>"Angle Z",_=>c.ToString()};
    string FormatVariance(Channel c,float v)=>c==Channel.Length?v.ToString("F3"):v.ToString("F1")+"°";
    TextMeshProUGUI AddText(Transform p,string text,int size,float width){var go=new GameObject("Text",typeof(RectTransform),typeof(TextMeshProUGUI));go.transform.SetParent(p,false);go.GetComponent<RectTransform>().sizeDelta=new Vector2(width,24);var t=go.GetComponent<TextMeshProUGUI>();t.text=text;t.fontSize=size;t.color=new Color(.86f,.86f,.86f);t.alignment=TextAlignmentOptions.Center;return t;}
    Slider AddCompactSlider(Transform p,float min,float max,float value,float width){var go=new GameObject("VarianceSlider",typeof(RectTransform),typeof(Slider));go.transform.SetParent(p,false);go.GetComponent<RectTransform>().sizeDelta=new Vector2(width,22);var s=go.GetComponent<Slider>();s.minValue=min;s.maxValue=max;s.value=value;var bg=new GameObject("Background",typeof(RectTransform),typeof(Image));bg.transform.SetParent(go.transform,false);var br=bg.GetComponent<RectTransform>();br.anchorMin=new Vector2(0,.4f);br.anchorMax=new Vector2(1,.6f);br.sizeDelta=Vector2.zero;bg.GetComponent<Image>().color=new Color(.28f,.28f,.28f);var fa=new GameObject("Fill Area",typeof(RectTransform));fa.transform.SetParent(go.transform,false);var far=fa.GetComponent<RectTransform>();far.anchorMin=new Vector2(0,.35f);far.anchorMax=new Vector2(1,.65f);far.offsetMin=new Vector2(4,0);far.offsetMax=new Vector2(-4,0);var fill=new GameObject("Fill",typeof(RectTransform),typeof(Image));fill.transform.SetParent(fa.transform,false);var fr=fill.GetComponent<RectTransform>();fr.anchorMin=Vector2.zero;fr.anchorMax=Vector2.one;fr.offsetMin=Vector2.zero;fr.offsetMax=Vector2.zero;fill.GetComponent<Image>().color=new Color(.55f,.45f,.15f);s.fillRect=fr;var ha=new GameObject("Handle Slide Area",typeof(RectTransform));ha.transform.SetParent(go.transform,false);var har=ha.GetComponent<RectTransform>();har.anchorMin=Vector2.zero;har.anchorMax=Vector2.one;har.offsetMin=new Vector2(6,0);har.offsetMax=new Vector2(-6,0);var h=new GameObject("Handle",typeof(RectTransform),typeof(Image));h.transform.SetParent(ha.transform,false);var hr=h.GetComponent<RectTransform>();hr.sizeDelta=new Vector2(10,16);h.GetComponent<Image>().color=Color.white;s.handleRect=hr;return s;}
    TMP_InputField AddSeedField(Transform p,float width){var go=new GameObject("SeedInput",typeof(RectTransform),typeof(Image),typeof(TMP_InputField));go.transform.SetParent(p,false);go.GetComponent<RectTransform>().sizeDelta=new Vector2(width,24);go.GetComponent<Image>().color=new Color(.12f,.12f,.12f);var tg=new GameObject("Text",typeof(RectTransform),typeof(TextMeshProUGUI));tg.transform.SetParent(go.transform,false);var tr=tg.GetComponent<RectTransform>();tr.anchorMin=Vector2.zero;tr.anchorMax=Vector2.one;tr.offsetMin=new Vector2(5,1);tr.offsetMax=new Vector2(-5,-1);var text=tg.GetComponent<TextMeshProUGUI>();text.fontSize=11;text.color=Color.white;text.alignment=TextAlignmentOptions.Center;var input=go.GetComponent<TMP_InputField>();input.textComponent=text;input.contentType=TMP_InputField.ContentType.IntegerNumber;input.text="0";return input;}
    GameObject AddButton(Transform p,string label,float width){var go=new GameObject(label+"Button",typeof(RectTransform),typeof(Image),typeof(Button));go.transform.SetParent(p,false);go.GetComponent<RectTransform>().sizeDelta=new Vector2(width,24);go.GetComponent<Image>().color=new Color(.27f,.34f,.20f);var text=AddText(go.transform,label,12,width);var tr=text.rectTransform;tr.anchorMin=Vector2.zero;tr.anchorMax=Vector2.one;tr.offsetMin=Vector2.zero;tr.offsetMax=Vector2.zero;return go;}
}

[DefaultExecutionOrder(-950)] public class GroomVarianceBootstrap:MonoBehaviour{private bool installed;[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]static void Spawn(){var go=new GameObject("GroomVarianceBootstrap");DontDestroyOnLoad(go);go.AddComponent<GroomVarianceBootstrap>();}void Update(){if(installed)return;var viewer=FindFirstObjectByType<ModelViewer>();if(viewer==null||viewer.groomingSliderPanelGO==null)return;var c=viewer.GetComponent<GroomVarianceController>();if(c==null)c=viewer.gameObject.AddComponent<GroomVarianceController>();c.Init(viewer);installed=true;}}
