using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using YC.Domain.Cards;
using YC.Domain.CityStyles;
using YC.Domain.Facilities;
using YC.Domain.SpecialActions;

namespace YC.Tests.EditMode
{
    [SetUpFixture]
    public sealed class FacilityCardDatabaseSetUpFixture
    {
        public const string CatalogAssetPath =
            "Assets/YC/Presentation/Content/FacilityCardCatalog.asset";
        public const string CityStyleSpecialActionCatalogAssetPath =
            "Assets/YC/Presentation/Content/CityStyleSpecialActionCatalog.asset";
        public const string UiThemeCatalogAssetPath =
            "Assets/YC/Presentation/Content/UiThemeCatalog.asset";
        public const string EventCharacterCatalogAssetPath =
            "Assets/YC/Presentation/Content/EventCharacterCardCatalog.asset";

        [OneTimeSetUp]
        public void InitializeFacilityCardDatabase()
        {
            var catalogType = Type.GetType("YC.Presentation.FacilityCardCatalog, Assembly-CSharp", true);
            var catalog = AssetDatabase.LoadAssetAtPath(CatalogAssetPath, catalogType);
            Assert.That(catalog, Is.Not.Null, CatalogAssetPath);

            var createDefinitions = catalogType.GetMethod("CreateDefinitions");
            Assert.That(createDefinitions, Is.Not.Null);
            var definitions = createDefinitions.Invoke(catalog, null) as
                IEnumerable<FacilityCardDefinition>;
            Assert.That(definitions, Is.Not.Null);

            FacilityCardDatabase.Initialize(definitions);
            Assert.That(FacilityCardDatabase.IsInitialized, Is.True);

            InitializeCityStyleAndSpecialActionDatabases();
            InitializeEventAndCharacterCardDatabases();
            InitializeUiTheme();
        }

        internal static void InitializeEventAndCharacterCardDatabases()
        {
            var catalogType = Type.GetType(
                "YC.Presentation.EventCharacterCardCatalog, Assembly-CSharp",
                true);
            var catalog = AssetDatabase.LoadAssetAtPath(
                EventCharacterCatalogAssetPath,
                catalogType);
            Assert.That(catalog, Is.Not.Null, EventCharacterCatalogAssetPath);

            var createEvents = catalogType.GetMethod("CreateEventDefinitions");
            var createCharacters = catalogType.GetMethod("CreateCharacterDefinitions");
            Assert.That(createEvents, Is.Not.Null);
            Assert.That(createCharacters, Is.Not.Null);

            var events = createEvents.Invoke(catalog, null) as
                IEnumerable<EventCardDefinition>;
            var characters = createCharacters.Invoke(catalog, null) as
                IEnumerable<CharacterCardDefinition>;
            Assert.That(events, Is.Not.Null);
            Assert.That(characters, Is.Not.Null);

            EventCardDatabase.Initialize(events);
            CharacterCardDatabase.Initialize(characters);
            Assert.That(EventCardDatabase.IsInitialized, Is.True);
            Assert.That(CharacterCardDatabase.IsInitialized, Is.True);
        }

        private static void InitializeUiTheme()
        {
            var catalogType = Type.GetType("YC.Presentation.UiThemeCatalog, Assembly-CSharp", true);
            var catalog = AssetDatabase.LoadAssetAtPath(UiThemeCatalogAssetPath, catalogType);
            Assert.That(catalog, Is.Not.Null, UiThemeCatalogAssetPath);
            var themeType = Type.GetType("YC.Presentation.UiTheme, Assembly-CSharp", true);
            var initialize = themeType.GetMethod("Initialize");
            Assert.That(initialize, Is.Not.Null);
            initialize.Invoke(null, new[] { catalog });
        }

        private static void InitializeCityStyleAndSpecialActionDatabases()
        {
            var catalogType = Type.GetType(
                "YC.Presentation.CityStyleSpecialActionCatalog, Assembly-CSharp",
                true);
            var catalog = AssetDatabase.LoadAssetAtPath(
                CityStyleSpecialActionCatalogAssetPath,
                catalogType);
            Assert.That(catalog, Is.Not.Null, CityStyleSpecialActionCatalogAssetPath);

            var createDefinitions = catalogType.GetMethod("CreateDefinitions");
            Assert.That(createDefinitions, Is.Not.Null);
            var definitionSet = createDefinitions.Invoke(catalog, null);
            Assert.That(definitionSet, Is.Not.Null);

            var definitionSetType = definitionSet.GetType();
            var cityStyles = definitionSetType.GetProperty("CityStyles").GetValue(
                definitionSet,
                null) as IEnumerable<CityStyleDefinition>;
            var specialActions = definitionSetType.GetProperty("SpecialActions").GetValue(
                definitionSet,
                null) as IEnumerable<SpecialActionDefinition>;
            Assert.That(cityStyles, Is.Not.Null);
            Assert.That(specialActions, Is.Not.Null);

            CityStyleDatabase.Initialize(cityStyles);
            SpecialActionDatabase.Initialize(specialActions);
            Assert.That(CityStyleDatabase.IsInitialized, Is.True);
            Assert.That(SpecialActionDatabase.IsInitialized, Is.True);
        }
    }
}
