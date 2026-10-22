using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using YC.Application.Interactions;
using YC.Domain.Commands;
using YC.Domain.Interactions;
using YC.Domain.Items;
using YC.Domain.State;
using YC.Presentation.Workflows;
using Object = UnityEngine.Object;

namespace YC.Tests.EditMode
{
    public sealed class EnterpriseDepartmentOverlayContractTests
    {
        private const string Folder = "Assets/YC/Presentation/Prefabs/Gameplay/Dialogs/Enterprise/";
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        [TestCase("enterprise.upgrade", "arbitrary.source", true)]
        [TestCase("round.enterprise.upgrade", "arbitrary.source", true)]
        [TestCase("round.enterprise.upgrade", "", true)]
        [TestCase("lua.choice", "round.enterprise.upgrade", false)]
        [TestCase("unrelated.choice", "enterprise.upgrade", false)]
        public void UpgradeConsumerUsesDomainTypeInsteadOfSourcePrompt(string type, string prompt, bool expected)
        {
            var handles = Runtime("EnterpriseInteractionUiCoordinator").GetMethod("Handles", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That((bool)handles.Invoke(null, new object[] { type, prompt }), Is.EqualTo(expected));
        }

        [TestCase("enterprise.upgrade")]
        [TestCase("round.enterprise.upgrade")]
        public void UpgradeRequestsShareCompanyProjectionAndSelectionContract(string type)
        {
            var host = new GameObject("Enterprise Upgrade Projection Contract");
            try
            {
                var visible = new GameStateView { ViewerRole = GameStateViewerRole.Player, ViewerPlayerId = 1 };
                visible.ItemZones.Items.Add(new ItemInstanceState { ItemId = "board-one", DefinitionId = "enterprise.one" });
                visible.ItemZones.Items.Add(new ItemInstanceState { ItemId = "board-two", DefinitionId = "enterprise.two" });
                var adapter = host.AddComponent(Runtime("GameplayExpansionUi"));
                adapter.GetType().GetField("getView", Members).SetValue(adapter, (Func<GameStateView>)(() => visible));
                adapter.GetType().GetField("getPlayer", Members).SetValue(adapter, (Func<int>)(() => 1));
                var copy = Read(adapter, "Copy");
                copy.GetType().GetField("InitialEnterpriseContext").SetValue(copy, "setup-only-context");
                var request = new InteractionRequest { InteractionId = "upgrade-contract", InteractionTypeId = type,
                    PromptKey = "unrelated.source", AnsweringPlayerId = 1, Visibility = "owner", Status = "open",
                    StateRevision = 4, CandidateSetId = "upgrade-candidates", CandidateSetVersion = 2,
                    CandidateIds = new List<string> { "board-one", "board-two" }, MinSelections = 1, MaxSelections = 1,
                    AnswerSchema = "candidate_id" };
                var submitted = new List<GameCommand>();
                var coordinator = Activator.CreateInstance(Runtime("EnterpriseInteractionUiCoordinator"), new object[] {
                    adapter, null, (Func<IReadOnlyList<InteractionRequest>>)(() => new[] { request }),
                    (Func<int>)(() => 1), (Action<GameCommand>)(submitted.Add) });
                var renderer = (IInteractionRequestRenderer)coordinator;
                var projected = InteractionRequestProjector.ProjectForPlayer(request, 1);
                Assert.That(renderer.CanRender(projected), Is.True);
                var selection = (EnterpriseSelectionProjection)coordinator.GetType().GetMethod("BuildProjection").Invoke(coordinator, new object[] { projected });
                Assert.That(selection.Mode, Is.EqualTo(EnterpriseSelectionMode.Company));
                Assert.That(selection.Context, Is.Empty, "升级不能使用初始选企业的说明");
                Assert.That(selection.RequestId, Is.EqualTo(request.InteractionId));
                Assert.That(selection.Revision, Is.EqualTo(request.StateRevision));
                Assert.That(selection.CandidateSetVersion, Is.EqualTo(request.CandidateSetVersion));
                Assert.That(selection.Boards.Select(board => board.Id), Is.EquivalentTo(request.CandidateIds));
                Assert.That(selection.Targets.All(target => target.Available && target.CompanyId == target.Id), Is.True);
                var draft = new EnterpriseSelectionDraft();
                Assert.That(draft.Refresh(selection), Is.True);
                Assert.That(draft.Select("missing"), Is.False);
                Assert.That(draft.Select("board-two"), Is.True);
                Assert.That(submitted, Is.Empty, "本地选择不能提前提交正式命令");
                Assert.That(draft.TryBeginSubmit(out var intent), Is.True);
                var answer = EffectInteractionCommands.Answer(projected, 1, intent.TargetIds);
                Assert.That(answer.OptionIds, Is.EqualTo(new[] { "board-two" }));
                Assert.That(answer.PlayerId, Is.EqualTo(request.AnsweringPlayerId));
                Assert.That(request.InteractionTypeId, Is.EqualTo(type), "保留旧待答请求的协议类型");
                request.AnsweringPlayerId = 2;
                Assert.That(renderer.CanRender(InteractionRequestProjector.ProjectForPlayer(request, 1)), Is.False);
                request.AnsweringPlayerId = 1; request.Status = "answered";
                Assert.That(renderer.CanRender(InteractionRequestProjector.ProjectForPlayer(request, 1)), Is.False);
            }
            finally { Object.DestroyImmediate(host); }
        }

        [Test]
        public void ActiveDepartmentCopiesDoNotAliasCallerOrOtherBoardLocations()
        {
            var source = new EnterpriseDepartmentProjection { ItemId = "current-item", VisualKey = "current-art",
                Label = "当前科室", Summary = "当前正式科室的完整正文" };
            var input = new EnterpriseSelectionProjection { RequestId = "copy-contract", Revision = 3,
                FixedCompany = new EnterpriseBoardProjection { Id = "fixed", ActiveDepartment = source },
                Boards = new[] { new EnterpriseBoardProjection { Id = "public", ActiveDepartment = source } } };
            var draft = new EnterpriseSelectionDraft();
            Assert.That(draft.Refresh(input), Is.True);
            var publicCopy = draft.Projection.Boards[0].ActiveDepartment;
            var fixedCopy = draft.Projection.FixedCompany.ActiveDepartment;
            Assert.That(publicCopy, Is.Not.SameAs(source));
            Assert.That(fixedCopy, Is.Not.SameAs(source));
            Assert.That(publicCopy, Is.Not.SameAs(fixedCopy));
            source.ItemId = "unconfirmed-item"; source.VisualKey = "unconfirmed-art";
            source.Label = "未确认候选"; source.Summary = "不能回写旧投影";
            Assert.That(publicCopy.ItemId, Is.EqualTo("current-item"));
            Assert.That(fixedCopy.VisualKey, Is.EqualTo("current-art"));
            Assert.That(publicCopy.Label, Is.EqualTo("当前科室"));
            Assert.That(fixedCopy.Summary, Is.EqualTo("当前正式科室的完整正文"));
            publicCopy.Summary = "只改一个消费者副本";
            Assert.That(fixedCopy.Summary, Is.EqualTo("当前正式科室的完整正文"));
            Assert.That(source.Summary, Is.EqualTo("不能回写旧投影"));
        }

        [Test]
        public void NewRevisionClearsActiveDepartmentAndRejectsOlderRelation()
        {
            var input = new EnterpriseSelectionProjection { RequestId = "relation-revision", Revision = 3,
                FixedCompany = new EnterpriseBoardProjection { Id = "fixed", ActiveDepartment = new EnterpriseDepartmentProjection { ItemId = "old" } },
                Boards = new[] { new EnterpriseBoardProjection { Id = "public", ActiveDepartment = new EnterpriseDepartmentProjection { ItemId = "old" } } } };
            var draft = new EnterpriseSelectionDraft(); draft.Refresh(input);
            input.Revision = 4; input.FixedCompany.ActiveDepartment = null; input.Boards[0].ActiveDepartment = null;
            Assert.That(draft.Refresh(input), Is.True);
            Assert.That(draft.Projection.FixedCompany.ActiveDepartment, Is.Null, "无当前科室不得填造默认科室");
            Assert.That(draft.Projection.Boards[0].ActiveDepartment, Is.Null);
            input.Revision = 3; input.FixedCompany.ActiveDepartment = new EnterpriseDepartmentProjection { ItemId = "old" };
            Assert.That(draft.Refresh(input), Is.False);
            Assert.That(draft.Projection.FixedCompany.ActiveDepartment, Is.Null);
        }

        [Test]
        public void ImportedOverlayReferencesResolveToOneSharedPrefab()
        {
            var overlayPath = Folder + "EnterpriseDepartmentOverlay.prefab";
            var shared = AssetDatabase.LoadAssetAtPath<GameObject>(overlayPath);
            Assert.That(shared, Is.Not.Null);
            CheckOverlay(shared.GetComponent(Runtime("EnterpriseDepartmentOverlayView")));
            foreach (var file in new[] { "EnterpriseSelectionDialog.prefab", "DepartmentSelectionDialog.prefab" })
            {
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(Folder + file);
                Assert.That(asset, Is.Not.Null, file);
                var page = asset.GetComponent(Runtime("EnterpriseSelectionDialogView"));
                var arguments = new object[] { null };
                Assert.That((bool)page.GetType().GetMethod("TryValidateConfiguration").Invoke(page, arguments),
                    Is.True, file + ": " + arguments[0]);
                var enlarged = (Component)Read(page, "enlargedDepartmentOverlay");
                CheckShared(enlarged, overlayPath);
                var board = (Component)Read(page, file.StartsWith("Department") ? "fixedCompany" : "boardTemplate");
                Assert.That(board, Is.Not.Null);
                var overlay = (Component)Read(board, "departmentOverlay");
                CheckShared(overlay, overlayPath);
                Assert.That(overlay.transform.parent, Is.SameAs(((RawImage)Read(board, "boardImage")).transform),
                    "叠层必须属于实际原板，不能挂在候选列表上独立占位");
            }
        }

        [Test]
        public void BrowseDepartmentCannotSubmitEvenWithMalformedAvailabilityFlags()
        {
            var draft = new EnterpriseSelectionDraft();
            var projection = new EnterpriseSelectionProjection { RequestId = "browse-department-contract", Revision = 1,
                Mode = EnterpriseSelectionMode.BrowseDepartment, ReadOnly = false, SubmissionAvailable = true,
                Boards = new[] { new EnterpriseBoardProjection { Id = "candidate" } },
                Targets = new[] { new EnterpriseSelectionTarget { Id = "candidate", BoardId = "candidate", DepartmentId = "candidate", Available = true } } };
            Assert.Throws<ArgumentException>(() => draft.Refresh(projection), "非法只读标志必须在输入边界拒绝");
            projection.ReadOnly = true; projection.SubmissionAvailable = false;
            Assert.Throws<ArgumentException>(() => draft.Refresh(projection), "只读候选Available也必须拒绝");
            projection.Targets[0].Available = false;
            draft.Refresh(projection);
            Assert.That(draft.Select("candidate"), Is.False);
            Assert.That(draft.CanConfirm, Is.False);
            Assert.That(draft.TryBeginSubmit(out _), Is.False);
        }

        [Test]
        public void ThirdRoundImportedPagesUseStandardMarkersAndRemoveRetiredControls()
        {
            var department = AssetDatabase.LoadAssetAtPath<GameObject>(Folder + "DepartmentSelectionDialog.prefab");
            var page = department.GetComponent(Runtime("EnterpriseSelectionDialogView"));
            var fixedBoard = (Component)Read(page, "fixedCompany");
            var markers = (Array)Read(fixedBoard, "playerLevelMarkers");
            Assert.That(markers, Has.Length.EqualTo(4));
            foreach (Component marker in markers)
            {
                Assert.That(marker, Is.Not.Null);
                var source = PrefabUtility.GetCorrespondingObjectFromOriginalSource(marker);
                Assert.That(AssetDatabase.GetAssetPath(source), Does.EndWith("/InfluenceMarker2DNumbered.prefab"));
            }
            Assert.That((bool)Read(fixedBoard, "interactionEnabled"), Is.False);
            Assert.That(Read(fixedBoard, "boardPreviewButton") as Object, Is.Null);
            Assert.That(Read(fixedBoard, "detailsButton") as Object, Is.Null);
            Assert.That(Read(page, "browseReturnButton") as Object, Is.Not.Null);
            var candidate = AssetDatabase.LoadAssetAtPath<GameObject>(Folder + "DepartmentCandidate.prefab");
            Assert.That(candidate.GetComponentsInChildren<Transform>(true).Any(t => t.name == "Board Footer"), Is.False);
            var details = department.GetComponentsInChildren<Transform>(true).Single(t => t.name == "Selection Details");
            Assert.That(details.GetComponent<Image>(), Is.Null);
            foreach (var name in new[] { "Description", "Detail Title", "Detail Artwork" })
                Assert.That(details.GetComponentsInChildren<Transform>(true).Any(t => t.name == name), Is.False);
            var enterprise = AssetDatabase.LoadAssetAtPath<GameObject>(Folder + "EnterpriseSelectionDialog.prefab");
            Assert.That(enterprise.GetComponentsInChildren<Transform>(true).Any(t => t.name == "Detail Artwork"), Is.False);
            foreach (var asset in new[] { department, enterprise, AssetDatabase.LoadAssetAtPath<GameObject>(Folder + "EnterprisePickerTarget.prefab") })
                foreach (var image in asset.GetComponentsInChildren<Image>(true).Where(i => i.name == "Frame" || i.name == "State Frame"))
                    Assert.That(image.pixelsPerUnitMultiplier, Is.EqualTo(1f));
        }

        [Test]
        public void ImportedModuleBackgroundsHaveNoOutlineOnTheSameObject()
        {
            var inspected = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/YC/Presentation/Prefabs" }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid); var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                foreach (var image in asset.GetComponentsInChildren<Image>(true))
                {
                    if (image.sprite == null || AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(image.sprite)) != "3b717c670b1a52843bcc7592be6fe504") continue;
                    inspected++;
                    Assert.That(image.GetComponent<Outline>(), Is.Null, path + " / " + image.name);
                }
            }
            Assert.That(inspected, Is.GreaterThan(0), "扫描未覆盖任何实际module-bg");
        }

        private static void CheckShared(Component overlay, string expectedPath)
        {
            CheckOverlay(overlay);
            var original = PrefabUtility.GetCorrespondingObjectFromOriginalSource(overlay) ?? overlay;
            Assert.That(AssetDatabase.GetAssetPath(original), Is.EqualTo(expectedPath), "叠层未复用同一资产源");
        }
        private static void CheckOverlay(Component overlay)
        {
            Assert.That(overlay, Is.Not.Null);
            Assert.That((bool)Read(overlay, "IsConfigured"), Is.True);
            Assert.That(Read(overlay, "artwork") as Object, Is.Not.Null);
            Assert.That(Read(overlay, "previewButton") as Object, Is.Not.Null);
            Assert.That(overlay.GetComponentsInChildren<Mask>(true), Is.Empty);
            Assert.That(overlay.GetComponentsInChildren<RectMask2D>(true), Is.Empty);
        }
        private static object Read(object owner, string name) => owner.GetType().GetField(name, Members)?.GetValue(owner) ??
            owner.GetType().GetProperty(name, Members)?.GetValue(owner);
        private static Type Runtime(string name) => AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("YC.Presentation." + name)).First(t => t != null);
    }
}
