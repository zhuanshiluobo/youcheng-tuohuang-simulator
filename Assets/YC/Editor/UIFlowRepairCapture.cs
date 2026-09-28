using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace YC.Presentation.Editor
{
    // 只实例化场景 Registry 现有预制体，不生成、不保存界面资产，不提交游戏命令。
    public static class UIFlowRepairCapture
    {
        private static int step, frames;
        private static string pending;
        private static double started;
        private static DateTime requested;
        private static object controller, choice;
        private static readonly Vector2Int[] Sizes = { new Vector2Int(1920,1080), new Vector2Int(900,600) };
        private static readonly string Output = Path.GetFullPath("Logs/UIFlowRepair-20260928/visual");
        private static object Field(object target,string name) => target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(target);
        private static object Call(object target,string name,params object[] args) => target.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public).Invoke(target,args);
        private static void View(string name, params object[] args) => typeof(GameplaySupplementalPageCapture).GetMethod(name,BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,args);
        public static void Run()
        {
            Directory.CreateDirectory(Output); step=frames=0; pending=null; controller=choice=null;
            EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity",OpenSceneMode.Single);
            View("PrepareGameView"); View("SelectSize",Sizes[0]); started=EditorApplication.timeSinceStartup;
            EditorApplication.update += Tick; EditorApplication.EnterPlaymode();
        }
        private static void Tick()
        {
            try
            {
                if(EditorApplication.timeSinceStartup-started>180) throw new Exception("页面截图超时");
                if(!EditorApplication.isPlaying) return;
                if(pending!=null)
                {
                    if(!File.Exists(pending)||new FileInfo(pending).Length<256||File.GetLastWriteTimeUtc(pending)<requested) return;
                    pending=null; frames=0;
                    Call(controller,"CancelBuildFacilityDialog"); if(choice!=null) Call(choice,"Hide");
                    if(++step==6){Stop(0);return;}
                    View("SelectSize",Sizes[step/3]); return;
                }
                var size=Sizes[step/3]; if(Screen.width!=size.x||Screen.height!=size.y) return;
                if(controller==null) controller=UnityEngine.Object.FindObjectOfType(Type.GetType("YC.Presentation.MobileCityInteractionController, Assembly-CSharp",true));
                if(controller==null||GameplayHudFrame.Active==null) return;
                if(frames==0)
                {
                    if(step%3==0) Call(controller,"OnBuildActionClicked");
                    else
                    {
                        choice=Field(controller,"characterCardEffectChoiceDialog");
                        if(step%3==1)
                        {
                            Call(choice,"ShowResourceSale",new[]{"源岩","源石碎片","异铁","至纯源石"},new[]{10,8,6,2},new[]{3,3,4,15},new Action<IReadOnlyList<int>>(_=>{}),new Action(()=>{}));
                            var page=(Component)Field(Field(choice,"shell"),"view");
                            page.GetComponentsInChildren<InputField>()[0].text="2";
                        }
                        else
                        {
                            var t=Type.GetType("YC.Presentation.EffectDialogOption, Assembly-CSharp",true);
                            var options=Array.CreateInstance(t,3);
                            for(int i=0;i<3;i++)
                            {
                                var o=Activator.CreateInstance(t,new object[]{"诊断选项 "+(i+1)+" · 规则返回的费用、回报与条件",new Action(()=>{}),i!=2});
                                t.GetField("StableId").SetValue(o,"diagnostic-"+i); options.SetValue(o,i);
                            }
                            Call(choice,"ShowExecutionChoices","选择执行","组件诊断：只预选，不提交正式命令。",options,new Func<bool>(()=>true));
                            var page=(Component)Field(Field(choice,"shell"),"view");
                            page.GetComponentsInChildren<Button>().First(b=>b.name=="Special Action Option 0").onClick.Invoke();
                        }
                    }
                }
                Canvas.ForceUpdateCanvases(); if(++frames<30) return;
                pending=Path.Combine(Output,new[]{"设施建设","次数选择","选择执行"}[step%3]+"-"+size.x+"x"+size.y+".png");
                requested=DateTime.UtcNow; ScreenCapture.CaptureScreenshot(pending);
            }
            catch(Exception e){Debug.LogException(e);Stop(1);}
        }
        private static void Stop(int code){EditorApplication.update-=Tick;EditorApplication.ExitPlaymode();EditorApplication.delayCall+=()=>EditorApplication.Exit(code);}
    }
}
