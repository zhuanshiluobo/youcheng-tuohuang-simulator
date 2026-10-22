using System;
using System.Collections;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace YC.Tests.PlayMode
{
    public sealed class MapNavigationPlayModeTests
    {
        private bool completed;
        [TearDown] public void CheckCompletion() => Assert.That(completed, Is.True);

        [UnityTest]
        public IEnumerator ActualScenes_FullMapOpeningAndNavigation()
        {
            completed = false;
            foreach (var scene in new[] { "SampleScene", "ThreePlayerScene" })
            {
                var launchType = T("GameLaunchContext");
                var launch = launchType.GetProperty("Instance").GetValue(null) as Component;
                if (launch != null) { UnityEngine.Object.Destroy(launch.gameObject); yield return null; }
                yield return SceneManager.LoadSceneAsync(scene, LoadSceneMode.Single);
                yield return null;
                var display = UnityEngine.Object.FindObjectOfType(T("MapDisplayController"));
                var frame = UnityEngine.Object.FindObjectOfType(T("GameplayHudFrame"));
                Assert.That(display, Is.Not.Null);
                var camera = (Camera)F(display, "targetCamera");
                var renderer = (SpriteRenderer)F(display, "mapRenderer");
                var region = (RectTransform)F(frame, "mapRegion");
                var opening = (RectTransform)F(frame, "mapVisibleBounds");
                var fixedViewport = (RectTransform)F(frame, "mapViewport");
                foreach (var size in new[] { new Vector2Int(1920,1080), new Vector2Int(2560,1080), new Vector2Int(1080,1920) })
                {
#if UNITY_EDITOR
                    var capture = Type.GetType("YC.Presentation.Editor.GameplaySupplementalPageCapture, Assembly-CSharp-Editor", true);
                    capture.GetMethod("PrepareGameView", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null,null);
                    capture.GetMethod("SelectSize", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null,new object[] { size });
#endif
                    for (var i=0; i<6; i++) yield return null;
                    var fixedRects = fixedViewport.GetComponentsInChildren<RectTransform>(true);
                    var fixedPositions = Array.ConvertAll(fixedRects, rect => rect.position);
                    var fixedSizes = Array.ConvertAll(fixedRects, rect => rect.rect.size);
                    var minimum = (float)F(display,"minimumNavigationZoom");
                    SetZoom(display, minimum);
                    for (var i=0; i<3; i++) yield return null;
                    for (var i=0; i<fixedRects.Length; i++)
                    {
                        Assert.That(Vector3.Distance(fixedRects[i].position,fixedPositions[i]),Is.LessThan(.01f),fixedRects[i].name);
                        Assert.That(Vector2.Distance(fixedRects[i].rect.size,fixedSizes[i]),Is.LessThan(.01f),fixedRects[i].name);
                    }
                    var relative = (Rect)display.GetType().GetProperty("VisibleViewportInRegion").GetValue(display);
                    Assert.That(Mathf.Max(relative.width,relative.height), Is.EqualTo(1f).Within(.0001f));
                    Assert.That(relative.width, Is.InRange(.001f,1f));
                    Assert.That(relative.height, Is.InRange(.001f,1f));
                    var bounds=renderer.sprite.bounds;
                    for(var i=0;i<4;i++)
                    {
                        var world=renderer.transform.TransformPoint(new Vector3((i&1)==0?bounds.min.x:bounds.max.x,
                            (i&2)==0?bounds.min.y:bounds.max.y,bounds.center.z));
                        var point=camera.WorldToViewportPoint(world);
                        Assert.That(point.x, Is.InRange(-.001f,1.001f));
                        Assert.That(point.y, Is.InRange(-.001f,1.001f));
                    }
                    var corners=new Vector3[4];opening.GetWorldCorners(corners);
                    Assert.That(Vector2.Distance(corners[0],camera.pixelRect.min),Is.LessThan(1f));
                    Assert.That(Vector2.Distance(corners[2],camera.pixelRect.max),Is.LessThan(1f));
                    Assert.That(opening.rect.width, Is.LessThanOrEqualTo(region.rect.width+.01f));
                    Assert.That(opening.rect.height, Is.LessThanOrEqualTo(region.rect.height+.01f));
                    var configured=Environment.GetEnvironmentVariable("YC_MAP_NAVIGATION_CAPTURE_OUTPUT");
                    var folder=Path.GetFullPath(string.IsNullOrWhiteSpace(configured)?"Logs/MapNavigation-20260930":configured);
                    Directory.CreateDirectory(folder);
                    var path=Path.Combine(folder,scene+"-"+size.x+"x"+size.y+"-full.png");
                    ScreenCapture.CaptureScreenshot(path);
                    for(var i=0;i<12;i++) yield return null;
                    Assert.That(File.Exists(path),Is.True);
                    Debug.Log("MAP_FULL "+scene+" "+size+" opening="+relative+" zoom="+minimum);
                    SetZoom(display, (float)F(display,"maxZoom"));
                    yield return null;
                    relative=(Rect)display.GetType().GetProperty("VisibleViewportInRegion").GetValue(display);
                    Assert.That(relative,Is.EqualTo(new Rect(0,0,1,1)));
                    var center=camera.pixelRect.center;
                    var oldFocus=(Vector3)F(display,"focusPoint");
                    Call(display,"BeginPointer",center);
                    Call(display,"MovePointer",center+new Vector2(35f,25f));
                    Assert.That(Vector3.Distance(oldFocus,(Vector3)F(display,"focusPoint")),Is.GreaterThan(.001f));
                    Assert.That((bool)T("MapDisplayController").GetProperty("SuppressMapClick").GetValue(null),Is.True);
                    Call(display,"EndDrag");
                    Assert.That((bool)T("MapDisplayController").GetProperty("SuppressMapClick").GetValue(null),Is.True);
                    SetZoom(display, 1f);
                    S(display,"previousPinchCenter",center);
                    S(display,"previousPinchDistance",100f);
                    Call(display,"ApplyPinch",center-new Vector2(75,0),center+new Vector2(75,0));
                    Assert.That((float)F(display,"targetZoom"),Is.EqualTo(1.5f).Within(.001f));
                    Call(display,"SetZoomTarget",center,0f);
                    Assert.That((float)F(display,"targetZoom"),Is.EqualTo(minimum).Within(.001f));
                    var backdrops=(RectTransform[])F(frame,"mapBackdrops");
                    foreach(var backdrop in backdrops) Assert.That(backdrop.GetComponent<UnityEngine.UI.Image>().sprite,Is.Not.Null);
                }
                S(display,"isPinching",true);
                display.GetType().GetMethod("SetScreenViewport").Invoke(display,new object[] { Rect.zero });
                Assert.That((bool)F(display,"isPinching"),Is.False);
                Assert.That(camera.enabled,Is.False);
            }
            completed=true;
            Debug.Log("MAP_NAVIGATION_ACCEPTED");
        }
        private static void SetZoom(object display,float zoom)
        { S(display,"currentZoom",zoom);S(display,"targetZoom",zoom);Call(display,"ApplyCameraTransform"); }
        private static Type T(string name)=>Type.GetType("YC.Presentation."+name+", Assembly-CSharp",true);
        private static object F(object target,string name)=>target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(target);
        private static void S(object target,string name,object value)=>target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(target,value);
        private static object Call(object target,string name,params object[] args)=>target.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(target,args);
    }
}
