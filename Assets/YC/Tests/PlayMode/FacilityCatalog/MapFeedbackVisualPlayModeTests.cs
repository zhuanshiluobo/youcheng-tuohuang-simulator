using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace YC.Tests.PlayMode
{
    public sealed class MapFeedbackVisualPlayModeTests
    {
        [UnityTest]
        public IEnumerator PlacementFeedback_TimeScaleZero_CompletesAndDisablesAnimator()
        {
            var startLoad = SceneManager.LoadSceneAsync("StartScene", LoadSceneMode.Single);
            Assert.That(startLoad, Is.Not.Null);
            yield return startLoad;
            yield return null;
            yield return null;

            var sampleLoad = SceneManager.LoadSceneAsync("SampleScene", LoadSceneMode.Single);
            Assert.That(sampleLoad, Is.Not.Null);
            yield return sampleLoad;
            yield return null;
            yield return null;

            var feedbackType = Type.GetType(
                "YC.Presentation.MapPlacementFeedback, Assembly-CSharp",
                true);
            var feedbacks = UnityEngine.Object.FindObjectsOfType(feedbackType)
                .Cast<Component>()
                .ToArray();
            Assert.That(feedbacks, Has.Length.EqualTo(99));

            var animators = feedbacks.Select(item => item.GetComponent<Animator>()).ToArray();
            Assert.That(animators, Has.All.Not.Null);
            Assert.That(animators.Select(item => item.runtimeAnimatorController).Distinct().Count(),
                Is.EqualTo(1));
            Assert.That(animators, Has.All.Matches<Animator>(item =>
                !item.enabled && item.updateMode == AnimatorUpdateMode.UnscaledTime &&
                item.cullingMode == AnimatorCullingMode.AlwaysAnimate && !item.applyRootMotion));

            var feedback = feedbacks.First(item => item.gameObject.activeInHierarchy);
            var animator = feedback.GetComponent<Animator>();
            var flash = ReadField<SpriteRenderer>(feedback, "flashRenderer");
            var ring = ReadField<SpriteRenderer>(feedback, "expandingRingRenderer");
            var play = feedbackType.GetMethod("Play", BindingFlags.Instance | BindingFlags.Public);
            var reset = feedbackType.GetMethod("ResetFeedback", BindingFlags.Instance | BindingFlags.Public);
            Assert.That(play, Is.Not.Null);
            Assert.That(reset, Is.Not.Null);

            var originalScale = feedback.transform.localScale;
            var originalTimeScale = Time.timeScale;
            try
            {
                Time.timeScale = 0f;
                var startedAt = Time.realtimeSinceStartup;
                play.Invoke(feedback, null);

                Assert.That(animator.enabled, Is.True);
                Assert.That(flash.enabled, Is.True);
                Assert.That(ring.enabled, Is.True);
                Assert.That(feedback.transform.localScale, Is.EqualTo(originalScale));
                Assert.That(flash.transform.localScale.x, Is.EqualTo(1.2f).Within(0.001f));
                Assert.That(ring.transform.localScale.x, Is.EqualTo(1f).Within(0.001f));

                while (animator.enabled && Time.realtimeSinceStartup - startedAt < 1f)
                {
                    yield return null;
                }

                var elapsed = Time.realtimeSinceStartup - startedAt;
                Assert.That(elapsed, Is.GreaterThanOrEqualTo(0.10f));
                Assert.That(elapsed, Is.LessThan(1f));
                Assert.That(animator.enabled, Is.False);
                Assert.That(flash.enabled, Is.False);
                Assert.That(ring.enabled, Is.False);
                Assert.That(feedback.transform.localScale, Is.EqualTo(originalScale));

                var restartedAt = Time.realtimeSinceStartup;
                play.Invoke(feedback, null);
                Assert.That(animator.enabled, Is.True);
                Assert.That(flash.enabled, Is.True);
                Assert.That(ring.enabled, Is.True);
                while (animator.enabled && Time.realtimeSinceStartup - restartedAt < 1f)
                {
                    yield return null;
                }
                var restartedElapsed = Time.realtimeSinceStartup - restartedAt;
                Assert.That(restartedElapsed, Is.GreaterThanOrEqualTo(0.10f));
                Assert.That(restartedElapsed, Is.LessThan(1f));
                Assert.That(animator.enabled, Is.False);
                Assert.That(flash.enabled, Is.False);
                Assert.That(ring.enabled, Is.False);
                Assert.That(feedback.transform.localScale, Is.EqualTo(originalScale));
            }
            finally
            {
                Time.timeScale = originalTimeScale;
                reset.Invoke(feedback, null);
            }
        }

        private static T ReadField<T>(Component target, string fieldName) where T : UnityEngine.Object
        {
            var field = target.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, fieldName);
            var value = field.GetValue(target) as T;
            Assert.That(value, Is.Not.Null, fieldName);
            return value;
        }
    }
}
