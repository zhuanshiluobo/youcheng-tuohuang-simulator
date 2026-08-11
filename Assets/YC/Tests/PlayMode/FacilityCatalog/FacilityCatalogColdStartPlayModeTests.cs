using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YC.Domain.Facilities;

namespace YC.Tests.PlayMode
{
    public sealed class FacilityCatalogColdStartPlayModeTests
    {
        [UnityTest]
        public IEnumerator SampleSceneColdStart_NaturallyInitializesExactCatalog()
        {
            ResetDatabaseForTest();
            Assert.That(FacilityCardDatabase.IsInitialized, Is.False);

            var load = SceneManager.LoadSceneAsync("SampleScene", LoadSceneMode.Single);
            Assert.That(load, Is.Not.Null);
            yield return load;
            yield return null;

            var scene = SceneManager.GetActiveScene();
            Assert.That(scene.name, Is.EqualTo("SampleScene"));
            Assert.That(scene.GetRootGameObjects(), Has.Length.EqualTo(6));
            Assert.That(FacilityCardDatabase.IsInitialized, Is.True);

            var allIds = new List<string>(FacilityCardDatabase.DefaultSupplyIds);
            allIds.AddRange(FacilityCardDatabase.ReserveIds);
            allIds.Add(FacilityCardDatabase.EnterpriseOffice);
            Assert.That(allIds, Has.Count.EqualTo(46));
            Assert.That(new HashSet<string>(allIds, StringComparer.Ordinal), Has.Count.EqualTo(46));
            for (var index = 0; index < allIds.Count; index++)
            {
                var definition = FacilityCardDatabase.Get(allIds[index]);
                Assert.That(definition, Is.Not.Null, allIds[index]);
                Assert.That(definition.FacilityId, Is.EqualTo(allIds[index]));
            }

            Assert.That(FacilityCardDatabase.DefaultSupplyIds, Has.Count.EqualTo(41));
            Assert.That(FacilityCardDatabase.ReserveIds, Has.Count.EqualTo(4));
            Assert.That(FacilityCardDatabase.DefaultSupplyIds,
                Does.Not.Contain(FacilityCardDatabase.EnterpriseOffice));
            Assert.That(FacilityCardDatabase.ReserveIds,
                Does.Not.Contain(FacilityCardDatabase.EnterpriseOffice));

            var bootstrapType = Type.GetType(
                "YC.Presentation.FacilityCatalogBootstrap, Assembly-CSharp",
                true);
            var bootstraps = UnityEngine.Object.FindObjectsOfType(bootstrapType);
            Assert.That(bootstraps, Has.Length.EqualTo(1));
            var bootstrap = bootstraps[0];
            var catalog = bootstrapType.GetProperty("Catalog").GetValue(bootstrap, null);
            Assert.That(catalog, Is.Not.Null);
            var arguments = new object[] { null };
            Assert.That(
                (bool)catalog.GetType().GetMethod("TryValidateConfiguration")
                    .Invoke(catalog, arguments),
                Is.True,
                arguments[0] as string);
            Assert.That(bootstrap as Component, Is.Not.Null);

            LogAssert.NoUnexpectedReceived();
        }

        private static void ResetDatabaseForTest()
        {
            var type = typeof(FacilityCardDatabase);
            var definitions = type.GetField(
                "Definitions",
                BindingFlags.Static | BindingFlags.NonPublic).GetValue(null) as IDictionary;
            Assert.That(definitions, Is.Not.Null);
            definitions.Clear();
            type.GetField("injectedDefinitions", BindingFlags.Static | BindingFlags.NonPublic)
                .SetValue(null, false);
            type.GetField("defaultSupplyIds", BindingFlags.Static | BindingFlags.NonPublic)
                .SetValue(null, Array.Empty<string>());
            type.GetField("reserveIds", BindingFlags.Static | BindingFlags.NonPublic)
                .SetValue(null, Array.Empty<string>());
        }
    }
}
