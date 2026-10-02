using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using YC.Infrastructure.Lua;

namespace YC.Tests.EditMode
{
    public sealed class CardSpriteSliceTests
    {
        private string root;
        private string originalRegistry, originalManifest;
        private static Type Runtime => Type.GetType("YC.Presentation.ExternalContentRuntime, Assembly-CSharp", true);

        [OneTimeSetUp]
        public void CopyExternalPack()
        {
            var source = Path.Combine(UnityEngine.Application.streamingAssetsPath, "Content/core");
            root = Path.GetFullPath(Path.Combine("Logs/CardSpriteMigration/test-pack", Guid.NewGuid().ToString("N")));
            foreach (var path in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = path.Substring(source.Length + 1);
                if (relative.EndsWith(".meta") || relative.Replace('\\', '/').StartsWith("artwork/") &&
                    !relative.Replace('\\', '/').StartsWith("artwork/sheets/")) continue;
                var target = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(path, target);
            }
            // 共享牌背及城市板保持原有资源，不属于正面迁移范围。
            var manifestPath = Path.Combine(root, "pack.json");
            var manifest = JObject.Parse(File.ReadAllText(manifestPath));
            manifest.Remove("sharedArtwork");
            originalManifest = manifest.ToString();
            File.WriteAllText(manifestPath, originalManifest);
            originalRegistry = File.ReadAllText(Path.Combine(root, "artwork_slices.json"));
        }

        [TearDown]
        public void RestoreFiles()
        {
            File.WriteAllText(Path.Combine(root, "artwork_slices.json"), originalRegistry);
            File.WriteAllText(Path.Combine(root, "pack.json"), originalManifest);
        }

        [Test]
        public void Registry_HasExactPositionsAndPreservesIdsAndFourTowerCopies()
        {
            var document = JObject.Parse(originalRegistry);
            var entries = document["slices"].ToList();
            Assert.That(document["sheets"].Sum(s => (int)s["rows"] * (int)s["columns"]), Is.EqualTo(101));
            Assert.That(document["sheets"].Sum(s => s["skippedCells"].Count()), Is.EqualTo(7));
            Assert.That(entries.Count, Is.EqualTo(94));
            foreach (var group in new[] { ("city_style", 12), ("character", 7), ("facility", 51), ("event", 24) })
                Assert.That(entries.Count(e => (string)e["contentType"] == group.Item1), Is.EqualTo(group.Item2));
            var pack = ExternalContentPack.Load(root);
            Assert.That(pack.ActiveDefinitions.Count, Is.EqualTo(78));
            for (var number = 1; number <= 41; number++)
            {
                var cell = number + (number > 21 ? 1 : 0) + (number > 31 ? 1 : 0);
                Assert.That(pack.FindArtworkSlice("facility", "building_" + number.ToString("000")).Id,
                    Is.EqualTo("facility_" + cell.ToString("000")));
            }
            Assert.That(pack.FindArtworkSlice("facility", "reserve_002").Id, Is.EqualTo("facility_022"));
            Assert.That(pack.FindArtworkSlice("facility", "reserve_003").Id, Is.EqualTo("facility_033"));
            Assert.That(pack.FindArtworkSlice("facility", "reserve_004").Id, Is.EqualTo("facility_044"));
            Assert.That(entries.Count(e => e["definitionIds"].Values<string>().Contains("reserve_001")), Is.EqualTo(4));
            Assert.That(pack.CreateFacilities().Count(c => c.FacilityId == "reserve_001"), Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MissingEnterpriseRules_NeverActivateImagesOrDuplicateBaseEvents(bool enterprise)
        {
            var manifest = JObject.Parse(originalManifest);
            manifest["enabledExpansionIds"] = enterprise ? new JArray("core", "enterprise") : new JArray("core");
            File.WriteAllText(Path.Combine(root, "pack.json"), manifest.ToString());
            var pack = ExternalContentPack.Load(root);
            Assert.That(pack.ActiveDefinitions.All(d => d.ExpansionId == "core"), Is.True);
            Assert.That(pack.CreateEvents().Count, Is.EqualTo(22));
            Assert.That(pack.FindArtworkSlice("event", "event_yellow_02").Id, Is.EqualTo("event_yellow_002"));
            Assert.That(pack.FindArtworkSlice("event", "event_red_03").Id, Is.EqualTo("event_red_003"));
            var entries = JObject.Parse(originalRegistry)["slices"].ToList();
            var missing = entries.Where(e => (string)e["expansionId"] == "enterprise").ToList();
            Assert.That(missing.Count, Is.EqualTo(13));
            Assert.That(missing.All(e => (string)e["ruleStatus"] == "missing"), Is.True);
            Assert.That(entries.Single(e => (string)e["id"] == "event_yellow_012")["replaces"]["definitionId"].Value<string>(), Is.EqualTo("event_yellow_02"));
            Assert.That(entries.Single(e => (string)e["id"] == "event_red_007")["replaces"]["definitionId"].Value<string>(), Is.EqualTo("event_red_03"));
        }

        [TestCase("outside")]
        [TestCase("wrong_cell")]
        [TestCase("skipped")]
        [TestCase("duplicate")]
        [TestCase("wrong_grid")]
        public void InvalidSliceRegistry_IsRejected(string fault)
        {
            var document = JObject.Parse(originalRegistry);
            var first = document["slices"][0];
            if (fault == "outside") first["rect"]["x"] = 99999;
            if (fault == "wrong_cell") first["cell"] = 2;
            if (fault == "skipped") ((JArray)document["sheets"][0]["skippedCells"]).Add(1);
            if (fault == "duplicate") ((JArray)document["slices"]).Add(first.DeepClone());
            if (fault == "wrong_grid") document["sheets"][0]["width"] = 2048;
            File.WriteAllText(Path.Combine(root, "artwork_slices.json"), document.ToString());
            Assert.Throws<InvalidDataException>(() => ExternalContentPack.Load(root));
        }

        [Test]
        public void SliceRegistry_IsIncludedInContentHashAndReturnedAsCopies()
        {
            var first = ExternalContentPack.Load(root);
            var document = JObject.Parse(originalRegistry);
            document["slices"][0]["note"] = "哈希验证";
            File.WriteAllText(Path.Combine(root, "artwork_slices.json"), document.ToString());
            Assert.That(ExternalContentPack.Load(root).ContentHash, Is.Not.EqualTo(first.ContentHash));
            var slice = first.FindRegisteredArtworkSlice("facility_022");
            slice.X = 0;
            Assert.That(first.FindRegisteredArtworkSlice("facility_022").X, Is.EqualTo(1800));
        }

        [Test]
        public void SpriteCache_UsesSevenFullResolutionTexturesAndReleasesOwnedObjects()
        {
            var method = Runtime.GetMethod("GetRegisteredArtworkSprite");
            var sprites = JObject.Parse(originalRegistry)["slices"].Select(e =>
                (Sprite)method.Invoke(null, new object[] { (string)e["id"] })).ToList();
            Assert.That(sprites.Count, Is.EqualTo(94));
            Assert.That(sprites.Select(s => s.texture).Distinct().Count(), Is.EqualTo(7));
            Assert.That(method.Invoke(null, new object[] { "facility_022" }), Is.SameAs(sprites.Single(s => s.name == "facility_022")));
            Assert.That(sprites.Single(s => s.name == "facility_051").texture.width, Is.EqualTo(5400));
            Assert.That(sprites.All(s => !s.texture.isReadable && s.texture.mipmapCount == 1), Is.True);
            var textures = sprites.Select(s => s.texture).Distinct().ToList();
            Runtime.GetMethod("Reset", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            Assert.That(sprites.All(s => s == null), Is.True);
            Assert.That(textures.All(t => t == null), Is.True);
        }

        [Test]
        public void RawImageAdapter_DisplaysSingleSpriteAndClearsStaleUv()
        {
            var sprite = (Sprite)Runtime.GetMethod("GetRegisteredArtworkSprite").Invoke(null, new object[] { "facility_050" });
            var canvas = new GameObject("切片验证画布", typeof(RectTransform), typeof(Canvas));
            var owner = new GameObject("切片显示验证", typeof(RectTransform), typeof(RawImage), typeof(AspectRatioFitter));
            owner.transform.SetParent(canvas.transform, false);
            owner.GetComponent<AspectRatioFitter>().aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            try
            {
                var image = owner.GetComponent<RawImage>();
                var set = Type.GetType("YC.Presentation.CardArtworkView, Assembly-CSharp", true).GetMethod("Set");
                set.Invoke(null, new object[] { image, sprite });
                Assert.That(image.texture, Is.SameAs(sprite.texture));
                Assert.That(image.uvRect.x, Is.EqualTo((sprite.rect.x + .5f) / 5400).Within(.00001f));
                Assert.That(image.uvRect.width, Is.EqualTo(599f / 5400).Within(.00001f));
                Assert.That(owner.GetComponent<AspectRatioFitter>().aspectRatio, Is.EqualTo(600f / 850).Within(.00001f));
                set.Invoke(null, new object[] { image, null });
                Assert.That(image.texture, Is.Null);
                Assert.That(image.uvRect, Is.EqualTo(new Rect(0, 0, 1, 1)));
            }
            finally { UnityEngine.Object.DestroyImmediate(canvas); }
        }
    }
}
