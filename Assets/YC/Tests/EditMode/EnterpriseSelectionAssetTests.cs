using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    public sealed class EnterpriseSelectionAssetTests
    {
        [UnityTearDown] public IEnumerator RestoreEditMode()
        { if (EditorApplication.isPlaying) yield return new ExitPlayMode(); }
        private static Type T(string name) => Type.GetType("YC.Presentation."+name+", Assembly-CSharp",true);
        private static object Call(object o,string name,params object[] args) => o.GetType().GetMethod(name).Invoke(o,args);
        private static V Get<V>(object o,string name) => (V)o.GetType().GetProperty(name).GetValue(o);
        private static V Field<V>(object o,string name) => (V)o.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance).GetValue(o);
        private static Component Load(bool department,Transform parent)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/YC/Presentation/Prefabs/Gameplay/Dialogs/Enterprise/"+
                (department ? "Department" : "Enterprise")+"SelectionDialog.prefab");
            return UnityEngine.Object.Instantiate(prefab,parent,false).GetComponent(T("EnterpriseSelectionDialogView"));
        }
        private static EnterpriseSelectionProjection Data(EnterpriseSelectionMode mode,int count)
        {
            var p = new EnterpriseSelectionProjection { RequestId="component-test",Revision=1,Mode=mode,CanCancel=true,SubmissionAvailable=true };
            p.Boards=Enumerable.Range(0,count).Select(i=>new EnterpriseBoardProjection { Id="b"+i,VisualKey=mode>=EnterpriseSelectionMode.InitialDepartment ? "department-defense":"enterprise-0",Label="诊断板" }).ToArray();
            p.Targets=p.Boards.Select((b,i)=>new EnterpriseSelectionTarget { Id="t"+i,BoardId=b.Id,CompanyId="c"+i,DepartmentId="d"+i,EffectId="e"+i,Tier=2,Available=true,
                Description=string.Concat(Enumerable.Repeat("获得 2 策略，费用 3；仅组件诊断。",40)) }).ToArray();
            if(mode>=EnterpriseSelectionMode.InitialDepartment)p.FixedCompany=new EnterpriseBoardProjection{Id="rhine",VisualKey="enterprise-2"};
            return p;
        }

        [Test] public void View_RefreshesBeforeSubmittingAndRejectsLateReplies_ComponentAdapterOnly()
        {
            var root=new GameObject("提交组件测试",typeof(RectTransform),typeof(Canvas));
            try
            {
                var page=Load(false,root.transform);
                page.GetType().GetMethod("Awake",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(page,null);
                var current=Data(EnterpriseSelectionMode.Effect,2);int sends=0;EnterpriseSelectionIntent sent=null;
                Call(page,"BindSubmission",new Func<EnterpriseSelectionProjection>(()=>current),
                    new Action<EnterpriseSelectionIntent>(i=>{sends++;sent=i;}),new Action(()=>{}));
                Call(page,"Present",current,false);Call(page,"Choose","t0");
                current.Revision=2;Field<Button>(page,"confirm").onClick.Invoke();
                Assert.That(sends,Is.Zero,"版本变化需先刷新，不提交旧报价。");
                Field<Button>(page,"confirm").onClick.Invoke();Field<Button>(page,"confirm").onClick.Invoke();
                Assert.That(sends,Is.EqualTo(1));Assert.That(sent.EffectId,Is.EqualTo("e0"));Assert.That(sent.DepartmentId,Is.Null);
                var rejected=Data(EnterpriseSelectionMode.Effect,2);rejected.Revision=3;rejected.Targets[0].Available=false;current=rejected;
                Call(page,"Complete",sent,false,rejected);
                Assert.That(page.gameObject.activeSelf,Is.True);Assert.That(Get<EnterpriseSelectionDraft>(page,"Draft").SelectedId,Is.Null);
                Call(page,"Choose","t1");Field<Button>(page,"confirm").onClick.Invoke();var late=sent;
                current=Data(EnterpriseSelectionMode.Effect,2);current.RequestId="next";Call(page,"Present",current,false);
                Call(page,"Choose","t0");Call(page,"Complete",late,true,null);
                Assert.That(page.gameObject.activeSelf,Is.True);Assert.That(Get<EnterpriseSelectionDraft>(page,"Draft").SelectedId,Is.EqualTo("t0"));
                Field<Button>(page,"confirm").onClick.Invoke();
                var nextStep=Data(EnterpriseSelectionMode.Effect,2);nextStep.RequestId="next";nextStep.Revision=2;current=nextStep;
                Call(page,"Complete",sent,true,nextStep);
                Assert.That(page.gameObject.activeSelf,Is.True,"成功回复携带同请求新版本时保留新步骤。");
                Assert.That(Get<EnterpriseSelectionDraft>(page,"Draft").Projection.Revision,Is.EqualTo(2));
            }
            finally {UnityEngine.Object.DestroyImmediate(root);}
        }
        [TestCase(false,0)] [TestCase(false,2)] [TestCase(false,12)]
        [TestCase(true,0)] [TestCase(true,3)] [TestCase(true,12)]
        public void Assets_NativeLayoutKeepsBodyFooterAndLastCandidateReachable(bool department,int count)
        {
            var root=new GameObject("组件测试",typeof(RectTransform),typeof(Canvas));
            root.GetComponent<Canvas>().renderMode=RenderMode.WorldSpace;
            try
            {
                var page=Load(department,root.transform);
                var mode=department ? EnterpriseSelectionMode.SwitchDepartment:EnterpriseSelectionMode.Company;
                var p=Data(mode,count); Call(page,"Present",p,false);
                if(count>0)Call(page,"Choose","t0");
                foreach(var size in new[]{new Vector2(1920,900),new Vector2(1200,600),new Vector2(2400,730),new Vector2(1920,900)})
                {
                    ((RectTransform)root.transform).sizeDelta=size;
                    // EditMode 没有逐帧的 LayoutGroup.Update，分别驱动 Viewport 以下的原生布局根。
                    for(int i=0;i<4;i++)
                    {
                        foreach(var group in page.GetComponentsInChildren<LayoutGroup>())
                            LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)group.transform);
                        Canvas.ForceUpdateCanvases();
                    }
                    var panel=Get<RectTransform>(page,"Panel");var body=Get<RectTransform>(page,"Body");
                    Within(panel,(RectTransform)root.transform); Within(body,panel);
                    Assert.That(body.rect.height,Is.GreaterThan(100));
                    var footer=(RectTransform)panel.Find("Footer");Within(footer,panel);
                    Assert.That(Bounds(body).yMin,Is.GreaterThanOrEqualTo(Bounds(footer).yMax));
                    var scroll=Get<ScrollRect>(page,"CandidateScroll");
                    scroll.horizontalNormalizedPosition=1;Canvas.ForceUpdateCanvases();
                    if(count>0)Within((RectTransform)scroll.content.GetChild(count-1),scroll.viewport);
                    Assert.That(body.gameObject.activeSelf,Is.True);
                    if(count>0)Assert.That(Get<EnterpriseSelectionDraft>(page,"Draft").SelectedId,Is.EqualTo("t0"));
                    foreach(var item in page.GetComponentsInChildren(T("EnterpriseBoardItemView")))
                    {
                        var image=Get<RawImage>(item,"BoardImage");Assert.That(image.texture,Is.Not.Null);
                        Assert.That(image.rectTransform.rect.width/image.rectTransform.rect.height,
                            Is.EqualTo(image.GetComponent<AspectRatioFitter>().aspectRatio).Within(.001));
                    }
                }
                Assert.That(Field<Button>(page,"confirm").interactable,Is.False,"没有正式宿主回调时不可提交。");
                if(count>0)
                {
                    Call(page,"OpenDetails","b0");Assert.That(Get<RectTransform>(page,"Body").gameObject.activeSelf,Is.False);
                    Call(page,"ReturnFromDetails");Assert.That(Get<EnterpriseSelectionDraft>(page,"Draft").SelectedId,Is.EqualTo("t0"));
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }
        [UnityTest]
        public IEnumerator ActualScenes_RegistrySelectionFoldAndInputAreComponentOnly()
        {
            yield return new EnterPlayMode();
            foreach(var scene in new[]{"SampleScene","ThreePlayerScene"})
            {
                var launch=UnityEngine.Object.FindObjectOfType(T("GameLaunchContext")) as Component;
                if(launch!=null)UnityEngine.Object.Destroy(launch.gameObject);
                yield return null;
                yield return SceneManager.LoadSceneAsync(scene,LoadSceneMode.Single);yield return null;
                var frame=UnityEngine.Object.FindObjectOfType(T("GameplayHudFrame")) as Component;
                var registry=UnityEngine.Object.FindObjectOfType(T("GameplayDialogRegistry"));
                var content=Get<RectTransform>(frame,"ContentRect");
                foreach(bool department in new[]{false,true})
                {
                    var page=(Component)Call(registry,"InstantiateEnterpriseSelection",content,department);
                    var data=Data(department?EnterpriseSelectionMode.SwitchDepartment:EnterpriseSelectionMode.Effect,7);
                    Call(frame,"SetRequest",data.RequestId,data.Revision);Call(page,"Present",data,true);
                    Call(page,"Choose","t0");yield return null;Canvas.ForceUpdateCanvases();
                    foreach(var key in new[]{"SettingsButton","EndActionButton"})
                    {
                        var button=Get<Button>(frame,key);
                        Assert.That(FirstHit((RectTransform)button.transform),Is.SameAs(button.gameObject));
                    }
                    var panel=Get<RectTransform>(page,"Panel");var position=panel.anchoredPosition;
                    ExecuteEvents.ExecuteHierarchy(panel.gameObject,new PointerEventData(EventSystem.current){delta=new Vector2(100,50)},ExecuteEvents.dragHandler);
                    Assert.That(panel.anchoredPosition,Is.EqualTo(position));
                    var scroll=Get<ScrollRect>(page,"CandidateScroll");
                    var hit=FirstHit(scroll.viewport);var ev=new PointerEventData(EventSystem.current){scrollDelta=new Vector2(-10000,0)};
                    Assert.That(ExecuteEvents.ExecuteHierarchy(hit,ev,ExecuteEvents.scrollHandler),Is.EqualTo(scroll.gameObject));
                    yield return null;Canvas.ForceUpdateCanvases();
                    Within((RectTransform)scroll.content.GetChild(scroll.content.childCount-1),scroll.viewport);
                    Call(frame,"SuspendEffectForInformation");Assert.That(page.gameObject.activeSelf,Is.False);
                    Call(page,"Present",data,true);Assert.That(page.gameObject.activeSelf,Is.False,"同请求刷新不能弹回。");
                    var info=new GameObject("组件资料页",typeof(RectTransform));info.transform.SetParent(content,false);
                    Call(frame,"ShowPage",info,false);Call(frame,"ResumeEffectPage");
                    Assert.That(info.activeSelf,Is.False);Assert.That(page.gameObject.activeSelf,Is.True);
                    Assert.That(Get<EnterpriseSelectionDraft>(page,"Draft").SelectedId,Is.EqualTo("t0"));
                    Call(page,"OpenDetails","b1");Call(page,"ReturnFromDetails");
                    Field<Button>(page,"cancel").onClick.Invoke();Assert.That(page.gameObject.activeSelf,Is.False);
                    Call(frame,"ResumeEffectPage");Assert.That(page.gameObject.activeSelf,Is.False,"关闭后的草稿不能恢复。");
                    UnityEngine.Object.Destroy(page.gameObject);UnityEngine.Object.Destroy(info);Call(frame,"ClearRequest");
                }
            }
            yield return new ExitPlayMode();
        }
        private static GameObject FirstHit(RectTransform rect)
        {
            var hits=new List<RaycastResult>();EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current)
            {position=RectTransformUtility.WorldToScreenPoint(null,rect.TransformPoint(rect.rect.center))},hits);
            Assert.That(hits,Is.Not.Empty);return hits[0].gameObject;
        }
        private static Rect Bounds(RectTransform r)
        {var c=new Vector3[4];r.GetWorldCorners(c);return Rect.MinMaxRect(c[0].x,c[0].y,c[2].x,c[2].y);}
        private static void Within(RectTransform a,RectTransform b)
        {
            var x=Bounds(a);var y=Bounds(b);
            Assert.That(x.xMin,Is.GreaterThanOrEqualTo(y.xMin-.1f),a.name);
            Assert.That(x.xMax,Is.LessThanOrEqualTo(y.xMax+.1f),a.name);
            Assert.That(x.yMin,Is.GreaterThanOrEqualTo(y.yMin-.1f),a.name+" child="+a.rect+" parent="+((RectTransform)a.parent).rect+
                " parentAnchors="+((RectTransform)a.parent).anchorMin+"/"+((RectTransform)a.parent).anchorMax+" parentDelta="+((RectTransform)a.parent).sizeDelta+
                " childPreferred="+LayoutUtility.GetPreferredHeight(a)+" viewport="+b.rect);
            Assert.That(x.yMax,Is.LessThanOrEqualTo(y.yMax+.1f),a.name);
        }
    }
}
