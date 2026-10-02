using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using YC.Domain.Rules;

namespace YC.Tests.EditMode
{
    public sealed class PlayerSelectionPageTests
    {
        private static Type T(string n) => Type.GetType("YC.Presentation."+n+", Assembly-CSharp",true);
        private static object P(object o,string n) => o.GetType().GetProperty(n).GetValue(o);
        private static void S(object o,string n,object v) => o.GetType().GetField(n).SetValue(o,v);
        private static object Call(object o,string n,params object[] a) => o.GetType().GetMethod(n).Invoke(o,a);
        private static int[] Selected(object d) => ((IEnumerable)P(d,"SelectedIds")).Cast<int>().ToArray();
        private static object Config(int count, bool multi, int min, int max, string request = "test")
        {
            var c=Activator.CreateInstance(T("PlayerSelectionConfig"));
            S(c,"RequestId",request);S(c,"Min",min);S(c,"Max",max);
            S(c,"Mode",Enum.Parse(T("PlayerSelectionMode"),multi?"Multiple":"Single"));
            var players=Array.CreateInstance(T("PlayerSelectionOption"),count);
            for(var i=0;i<count;i++){var p=Activator.CreateInstance(T("PlayerSelectionOption"));S(p,"Id",i+1);S(p,"Color",PlayerColor.Green);players.SetValue(p,i);}
            S(c,"Players",players);return c;
        }
        [Test]
        public void Draft_SingleReplacesWithoutDeselectAndMultipleHonorsBounds()
        {
            var d=Activator.CreateInstance(T("PlayerSelectionDraft"));Call(d,"Refresh",Config(4,false,1,1));
            Assert.That(P(d,"CanConfirm"),Is.False);Call(d,"Toggle",1);Call(d,"Toggle",2);Call(d,"Toggle",2);
            CollectionAssert.AreEqual(new[]{2},Selected(d));Assert.That(P(d,"CanConfirm"),Is.True);
            Call(d,"Refresh",Config(4,true,2,2,"multiple"));Call(d,"Toggle",1);Assert.That(P(d,"CanConfirm"),Is.False);
            Call(d,"Toggle",2);Assert.That(Call(d,"Toggle",3),Is.False);CollectionAssert.AreEqual(new[]{1,2},Selected(d));
            Call(d,"Toggle",1);Call(d,"Toggle",3);CollectionAssert.AreEqual(new[]{2,3},Selected(d));
            Call(d,"Refresh",Config(0,true,0,2,"optional"));Assert.That(P(d,"CanConfirm"),Is.True);Assert.That((int[])Call(d,"Result"),Is.Empty);
        }
        [Test]
        public void Draft_RefreshUsesStableIdsRemovesDisabledAndNeverFillsSelections()
        {
            var d=Activator.CreateInstance(T("PlayerSelectionDraft"));var c=Config(3,true,2,2);Call(d,"Refresh",c);Call(d,"Toggle",1);Call(d,"Toggle",2);
            var next=Config(3,true,2,2);var players=(Array)next.GetType().GetField("Players").GetValue(next);
            S(players.GetValue(0),"Name","改名");S(players.GetValue(0),"Color",PlayerColor.Blue);
            S(players.GetValue(1),"Eligible",false);Call(d,"Refresh",next);
            CollectionAssert.AreEqual(new[]{1},Selected(d));Assert.That(P(d,"CanConfirm"),Is.False);
            Assert.That(Call(d,"Toggle",2),Is.False);
            S(next,"Players",Array.CreateInstance(T("PlayerSelectionOption"),0));Call(d,"Refresh",next);
            Assert.That(Selected(d),Is.Empty);Assert.That(P(d,"HasEnoughPlayers"),Is.False);
            var initial=Config(2,false,1,1,"initial");S(initial,"SelectedIds",new[]{99,2});Call(d,"Refresh",initial);
            CollectionAssert.AreEqual(new[]{2},Selected(d));
        }
        [Test]
        public void ActualPrefabs_AllThreePagesUseEnglishObjectsAndCompleteBindings()
        {
            var hud=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/YC/Presentation/Prefabs/Gameplay/GameplayInteractionHud.prefab");
            var registry=hud.GetComponentInChildren(T("GameplayDialogRegistry"),true);Assert.That(registry,Is.Not.Null);var args=new object[]{null};
            Assert.That((bool)registry.GetType().GetMethod("TryValidateConfiguration").Invoke(registry,args),Is.True,args[0]?.ToString());
            var page=(Component)P(registry,"PlayerSelectionPrefab");
            foreach(var path in new[]{"Assets/YC/Presentation/Prefabs/Gameplay/Dialogs/Information/RegionInformationPage.prefab",
                "Assets/YC/Presentation/Prefabs/Gameplay/Dialogs/Information/GameBoxPage.prefab",
                "Assets/YC/Presentation/Prefabs/Gameplay/Dialogs/PlayerSelectionPage.prefab"})
            {
                var asset=AssetDatabase.LoadAssetAtPath<GameObject>(path);Assert.That(asset,Is.Not.Null);
                foreach(var node in asset.GetComponentsInChildren<Transform>(true))
                    Assert.That(node.name.Any(ch=>ch>='\u4e00'&&ch<='\u9fff'),Is.False,node.name);
            }
            Assert.That(page.gameObject.activeSelf,Is.False);Assert.That(page.GetComponent<CanvasScaler>().enabled,Is.False);
            Assert.That(page.GetComponent<Canvas>().overrideSorting,Is.True);
            Assert.That(page.GetComponent<Canvas>().sortingOrder,Is.LessThan(30000));
            Assert.That(page.GetComponent<VerticalLayoutGroup>(),Is.Not.Null);
            Assert.That(((RectTransform)P(page,"Window")).GetComponent<VerticalLayoutGroup>(),Is.Not.Null);
            Assert.That(((RectTransform)P(page,"OptionsRow")).GetComponent<HorizontalLayoutGroup>(),Is.Not.Null);
        }
    }
}
