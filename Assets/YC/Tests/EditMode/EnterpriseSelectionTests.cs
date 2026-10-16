using System;
using NUnit.Framework;
using YC.Domain.Effects;
using YC.Domain.State;
using YC.Presentation.Workflows;

namespace YC.Tests.EditMode
{
    public sealed class EnterpriseSelectionTests
    {
        [Test]
        public void InitialEnterpriseFrozenCandidatesProvideDetailsBeforePublicInstancesExist()
        {
            var core = System.Environment.GetEnvironmentVariable("YC_ENTERPRISE_UI_CORE") ??
                System.IO.Path.Combine(TestContext.CurrentContext.WorkDirectory, "Assets", "StreamingAssets", "Content", "core");
            var installed = YC.Infrastructure.Lua.ExternalContentPack.Load(core);
            var configuration = new YC.Domain.Rules.GameRuleConfiguration { PlayerCount = 3,
                EnabledExpansionIds = new System.Collections.Generic.List<string> { "core", "enterprise", "infected" } };
            var pack = installed.CreateActiveSnapshot(configuration);
            var definitions = YC.Infrastructure.Lua.LuaContentCatalog.CreateEnterpriseDefinitions(
                new YC.Domain.Effects.EffectRegistry(), pack.ActiveDefinitions, pack.GetScript);
            var identities = new[] { "infected.enterprise.rhodes_island", "infected.enterprise.maylander_foundation", "infected.enterprise.stonehoove_security" };
            var state = new GameState { GameId = "initial-enterprise-ui", CurrentPlayerId = 1,
                ContentPackHash = pack.ContentHash, RuleConfiguration = configuration,
                Players = new System.Collections.Generic.List<PlayerState> { new PlayerState { PlayerId = 1 }, new PlayerState { PlayerId = 2 } } };
            var request = new YC.Domain.State.InteractionRequest {
                InteractionId = "initial-enterprise-request", SourceNodeId = "enterprise-choice-node", OwnerEffectId = "setup-effect",
                InteractionTypeId = "setup.enterprise.choose", PromptKey = "enterprise.choose", AnsweringPlayerId = 1,
                Visibility = "owner", Status = "open", StateRevision = state.EffectRuntime.StateRevision,
                CandidateSetId = "frozen-enterprises", CandidateSetVersion = 1, MinSelections = 1, MaxSelections = 1,
                AnswerSchema = "candidate_ids", CandidateIds = new System.Collections.Generic.List<string> { "candidate-a", "candidate-b", "candidate-c" },
                PromptParameters = NormalizedValue.CreateObject(new[] { new NormalizedValueEntry {
                    Name = "candidateDefinitionIds", Value = NormalizedValue.CreateArray(System.Linq.Enumerable.ToList(System.Linq.Enumerable.Select(identities, NormalizedValue.CreateString))) } }) };
            state.EffectRuntime.InteractionRequests.Add(request);
            var visible = YC.Domain.State.GameStateViewProjector.ProjectForPlayer(state, 1);
            var projected = YC.Domain.Interactions.InteractionRequestProjector.ProjectForPlayer(request, 1);
            Assert.That(visible.ItemZones.Items, Is.Empty);
            var before = Newtonsoft.Json.JsonConvert.SerializeObject(state);
            var adapterType = System.Type.GetType("YC.Presentation.GameplayExpansionUi, Assembly-CSharp", true);
            var runtimeType = System.Type.GetType("YC.Presentation.ExternalContentRuntime, Assembly-CSharp", true);
            var coordinatorType = System.Type.GetType("YC.Presentation.EnterpriseInteractionUiCoordinator, Assembly-CSharp", true);
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var packField = runtimeType.GetField("pack", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            var previousPack = packField.GetValue(null);
            try
            {
                packField.SetValue(null, installed);
                var adapter = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(adapterType);
                adapterType.GetField("getView", flags).SetValue(adapter, (System.Func<GameStateView>)(() => visible));
                adapterType.GetField("getPlayer", flags).SetValue(adapter, (System.Func<int>)(() => 1));
                adapterType.GetField("definitions", flags).SetValue(adapter, definitions);
                foreach (var name in new[] { "boards", "content" })
                    adapterType.GetField(name, flags).SetValue(adapter, System.Array.CreateInstance(adapterType.GetField(name, flags).FieldType.GetElementType(), 0));
                adapterType.GetField("copy", flags).SetValue(adapter, System.Activator.CreateInstance(adapterType.GetNestedType("GameplayCopy")));
                adapterType.GetField("levelAreas", flags).SetValue(adapter, new UnityEngine.Rect[6]);
                adapterType.GetField("enterpriseDetails", flags).SetValue(adapter, System.Activator.CreateInstance(adapterType.GetField("enterpriseDetails", flags).FieldType));
                adapterType.GetField("enterpriseDetailPack", flags).SetValue(adapter, pack);
                adapterType.GetField("enterpriseDetailInstalledPack", flags).SetValue(adapter, installed);
                adapterType.GetField("enterpriseDetailPackGameId", flags).SetValue(adapter, visible.GameId);
                adapterType.GetField("enterpriseDetailPackHash", flags).SetValue(adapter, visible.ContentPackHash);
                var coordinator = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(coordinatorType);
                coordinatorType.GetField("adapter", flags).SetValue(coordinator, adapter);
                coordinatorType.GetField("player", flags).SetValue(coordinator, (System.Func<int>)(() => 1));
                var projection = (EnterpriseSelectionProjection)coordinatorType.GetMethod("BuildProjection").Invoke(coordinator, new object[] { projected });
                Assert.That(projection.Targets.Length, Is.EqualTo(3));
                var context = new YC.Domain.Presentation.GameplayTextContext { Catalog = pack.Presentation, ContentHash = pack.Presentation.ContentHash,
                    Locale = pack.Presentation.Locale, ViewerPlayerId = 1, StateRevision = visible.StateRevision, VisibilityScopeId = visible.GameId + ":1" };
                var resolver = new YC.Application.Presentation.GameplayTextResolver(pack.Presentation);
                var consume = adapterType.GetMethod("ResolveEnterpriseDetails", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                for (int index = 0; index < projection.Targets.Length; index++)
                {
                    var target = projection.Targets[index];
                    Assert.That(target.Id, Is.EqualTo(request.CandidateIds[index]));
                    Assert.That(target.Details, Is.Not.Empty);
                    var expectedContext = new YC.Domain.Presentation.GameplayPresentationContext { Purpose = "enterprise_detail", SourceDefinitionId = identities[index],
                        SourceItemId = string.Empty, ViewerPlayerId = 1, StateRevision = visible.StateRevision,
                        Facts = NormalizedValue.CreateObject(new System.Collections.Generic.List<NormalizedValueEntry>()) };
                    Assert.That(pack.TryPresent(definitions.FindEnterprise(identities[index]).Presentation.DetailScript, expectedContext,
                        out var expected, out var code, out var diagnostic), Is.True, diagnostic);
                    Assert.That(target.Details.Length, Is.EqualTo(expected.Details.Count));
                    Assert.That(System.Linq.Enumerable.Select(target.Details, value => value.Key), Is.EqualTo(System.Linq.Enumerable.Select(expected.Details, value => value.Key)));
                    var document = (YC.Domain.Presentation.GameplayTextDocument)consume.Invoke(null, new object[] { target.Details,
                        (System.Func<YC.Domain.Presentation.GameplayTextRef, YC.Domain.Presentation.GameplayTextDocument>)(reference => resolver.Resolve(reference, context)) });
                    Assert.That(document.Runs.Count, Is.GreaterThan(0));
                    Assert.That(document.ToPlainText(), Is.Not.Empty);
                    var draft = new EnterpriseSelectionDraft(); draft.Refresh(projection); draft.Select(target.Id);
                    Assert.That(draft.TryBeginSubmit(out var intent), Is.True);
                    Assert.That(intent.CompanyId, Is.EqualTo(target.CompanyId));
                    Assert.That(intent.TargetId, Is.EqualTo(target.Id));
                }
                var untouchedKey = projection.Targets[0].Details[0].Key;
                projection.Targets[0].Details[0].Key = "mutated-return-only";
                var fresh = (EnterpriseSelectionProjection)coordinatorType.GetMethod("BuildProjection").Invoke(coordinator, new object[] { projected });
                Assert.That(fresh.Targets[0].Details[0].Key, Is.EqualTo(untouchedKey));
                Assert.That(Newtonsoft.Json.JsonConvert.SerializeObject(state), Is.EqualTo(before));
                var original = (EnterpriseBoardProjection)adapterType.GetMethod("ProjectBoard").Invoke(adapter,
                    new object[] { visible, request.CandidateIds[0], identities[0] });
                Assert.That(original.Details, Is.Empty, "没有授权候选请求的原只读入口不能读取未展示实例详情");
                var candidate = adapterType.GetMethod("ProjectCandidateBoard", flags);
                var mutations = new System.Action<YC.Domain.Interactions.InteractionRequestProjection>[] {
                    value => value.VisibleToViewer = false, value => value.AnsweringPlayerId = 2, value => value.Status = "closed",
                    value => value.CandidateSetVersion++, value => value.StateRevision++, value => value.InteractionTypeId = "enterprise.upgrade",
                    value => value.PromptParameters.Properties[0].Value.Items[0] = NormalizedValue.CreateString("enterprise.rhine_lab") };
                foreach (var mutate in mutations)
                {
                    var invalid = GameStateCloneService.DeepClone(projected); mutate(invalid);
                    var board = (EnterpriseBoardProjection)candidate.Invoke(adapter, new object[] { visible, request.CandidateIds[0], identities[0], invalid });
                    Assert.That(board.Details, Is.Empty);
                }
                foreach (var invalidView in new[] { YC.Domain.State.GameStateViewProjector.ProjectForPlayer(state, 2),
                    YC.Domain.State.GameStateViewProjector.ProjectForHost(state), YC.Domain.State.GameStateViewProjector.ProjectForSpectator(state) })
                    Assert.That(((EnterpriseBoardProjection)candidate.Invoke(adapter, new object[] { invalidView, request.CandidateIds[0], identities[0], projected })).Details, Is.Empty);
                Assert.That(((EnterpriseBoardProjection)candidate.Invoke(adapter, new object[] { visible, "not-a-candidate", identities[0], projected })).Details, Is.Empty);
                Assert.That(((EnterpriseBoardProjection)candidate.Invoke(adapter, new object[] { visible, request.CandidateIds[0], "enterprise.rhine_lab", projected })).Details, Is.Empty);
            }
            finally { packField.SetValue(null, previousPack); }
        }

        [Test]
        public void ExternalLogoUvAndCornerGeometryKeepSourceScale()
        {
            var type = System.Type.GetType("YC.Presentation.EnterpriseBoardCatalog, Assembly-CSharp", true);
            var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
            var map = type.GetMethod("MapLogoUv", flags);
            var source = new UnityEngine.Rect(.2f, .25f, .5f, .5f);
            var local = new UnityEngine.Rect(.1f, .7f, .3f, .2f);
            var mapped = (UnityEngine.Rect)map.Invoke(null, new object[] { source, local });
            Assert.That(mapped.x, Is.EqualTo(source.x + local.x * source.width).Within(.000001));
            Assert.That(mapped.y, Is.EqualTo(source.y + local.y * source.height).Within(.000001));
            Assert.That(mapped.width, Is.EqualTo(local.width * source.width).Within(.000001));
            Assert.That(mapped.height, Is.EqualTo(local.height * source.height).Within(.000001));
            var vertices = (UnityEngine.Vector2[])type.GetMethod("BuildLogoVertices", flags).Invoke(null,
                new object[] { new UnityEngine.Vector2(245, 249), .2f });
            Assert.That(vertices.Length, Is.EqualTo(5));
            Assert.That(vertices[0], Is.EqualTo(UnityEngine.Vector2.zero));
            Assert.That(vertices[2], Is.EqualTo(new UnityEngine.Vector2(245, 249)));
            foreach (var vertex in vertices)
            {
                Assert.That(vertex.x, Is.InRange(0f, 245f));
                Assert.That(vertex.y, Is.InRange(0f, 249f));
            }
            Assert.That(vertices[3].x, Is.GreaterThan(vertices[4].x));
            Assert.That(vertices[4].y, Is.LessThan(vertices[3].y));
            var validate = type.GetMethod("ValidCrop", flags);
            Assert.That(validate.Invoke(null, new object[] { local }), Is.EqualTo(true));
            foreach (var invalid in new[] { new UnityEngine.Rect(-.1f, 0, .2f, .2f), new UnityEngine.Rect(0, 0, 0, .2f),
                new UnityEngine.Rect(.9f, 0, .2f, .2f), new UnityEngine.Rect(0, float.NaN, .2f, .2f) })
                Assert.That(validate.Invoke(null, new object[] { invalid }), Is.EqualTo(false));
        }

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator NativeFormalEnterpriseLogosCreateWithoutErrorsAndKeepCornerCrop()
        {
            // 在空测试场景中核实际运行时 Sprite 创建，不启动局内流程。
            UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                UnityEditor.SceneManagement.NewSceneSetup.EmptyScene, UnityEditor.SceneManagement.NewSceneMode.Single);
            yield return new UnityEngine.TestTools.EnterPlayMode();
            Assert.That(UnityEngine.Application.isPlaying, Is.True);
            var type = System.Type.GetType("YC.Presentation.EnterpriseBoardCatalog, Assembly-CSharp", true);
            var source = UnityEditor.AssetDatabase.LoadAssetAtPath(
                "Assets/YC/Presentation/Enterprise/EnterpriseBoardCatalog.asset", type);
            Assert.That(source, Is.Not.Null);
            var catalog = UnityEngine.Object.Instantiate(source);
            var find = type.GetMethod("Find");
            var create = type.GetMethod("Logo");
            var identities = new[] { "infected.enterprise.rhodes_island", "infected.enterprise.maylander_foundation", "infected.enterprise.stonehoove_security" };
            var clipped = 0;
            try
            {
                foreach (var key in identities)
                {
                    var board = find.Invoke(catalog, new object[] { key });
                    Assert.That(board, Is.Not.Null, key);
                    var texture = (UnityEngine.Texture2D)board.GetType().GetField("texture").GetValue(board);
                    var crop = (UnityEngine.Rect)board.GetType().GetField("logoUv").GetValue(board);
                    var clip = (float)board.GetType().GetField("logoClipTopLeft").GetValue(board);
                    Assert.That(texture.width, Is.EqualTo(650), key);
                    Assert.That(texture.height, Is.EqualTo(1850), key);
                    var logo = (UnityEngine.Sprite)create.Invoke(catalog, new object[] { key });
                    Assert.That(logo, Is.Not.Null, key);
                    Assert.That(logo.texture, Is.SameAs(texture), key);
                    Assert.That(create.Invoke(catalog, new object[] { key }), Is.SameAs(logo), key);
                    Assert.That(logo.rect.x, Is.EqualTo(crop.x * texture.width).Within(.0002f), key);
                    Assert.That(logo.rect.y, Is.EqualTo(crop.y * texture.height).Within(.0002f), key);
                    Assert.That(logo.rect.width, Is.EqualTo(crop.width * texture.width).Within(.0002f), key);
                    Assert.That(logo.rect.height, Is.EqualTo(crop.height * texture.height).Within(.0002f), key);
                    var pixels = Array.ConvertAll(logo.vertices, vertex => vertex * logo.pixelsPerUnit + logo.pivot);
                    var uvs = logo.uv;
                    Assert.That(uvs.Length, Is.EqualTo(pixels.Length), key);
                    for (var i = 0; i < pixels.Length; i++)
                    {
                        var vertex = pixels[i];
                        Assert.That(vertex.x, Is.InRange(-.0001f, logo.rect.width + .0001f), key);
                        Assert.That(vertex.y, Is.InRange(-.0001f, logo.rect.height + .0001f), key);
                        Assert.That(uvs[i].x, Is.EqualTo((logo.rect.x + vertex.x) / texture.width).Within(.000001f), key);
                        Assert.That(uvs[i].y, Is.EqualTo((logo.rect.y + vertex.y) / texture.height).Within(.000001f), key);
                    }
                    if (clip > 0)
                    {
                        clipped++;
                        Assert.That(key, Is.EqualTo("infected.enterprise.maylander_foundation"));
                        Assert.That(pixels.Length, Is.EqualTo(5));
                        Assert.That(logo.triangles, Is.EqualTo(new ushort[] { 0, 1, 2, 0, 2, 3, 0, 3, 4 }));
                        var expected = new[] { UnityEngine.Vector2.zero, new UnityEngine.Vector2(logo.rect.width, 0),
                            logo.rect.size, new UnityEngine.Vector2(clip * logo.rect.width, logo.rect.height),
                            new UnityEngine.Vector2(0, (1 - clip) * logo.rect.height) };
                        for (var i = 0; i < pixels.Length; i++)
                            Assert.That(UnityEngine.Vector2.Distance(pixels[i], expected[i]), Is.LessThan(.0001f), key);
                        Assert.That(pixels[3].x, Is.GreaterThan(0));
                        Assert.That(pixels[4].y, Is.LessThan(logo.rect.height));
                    }
                    TestContext.Progress.WriteLine("正式Logo：" + key + "，纹理=" + texture.width + "×" + texture.height +
                        "，格式=" + texture.format + "，可读=" + texture.isReadable +
                        "，Sprite矩形=" + logo.rect + "，顶点=" + pixels.Length + "，角部裁切=" + clip);
                }
                Assert.That(clipped, Is.EqualTo(1));
                UnityEngine.TestTools.LogAssert.NoUnexpectedReceived();
            }
            finally { UnityEngine.Object.DestroyImmediate(catalog); }
            yield return null;
            UnityEngine.TestTools.LogAssert.NoUnexpectedReceived();
            yield return new UnityEngine.TestTools.ExitPlayMode();
        }

        private static EnterpriseSelectionProjection Projection(EnterpriseSelectionMode mode = EnterpriseSelectionMode.Company,
            string request = "a", int revision = 1) => new EnterpriseSelectionProjection
        {
            RequestId = request, Revision = revision, Mode = mode, SubmissionAvailable = true,
            Boards = new[] { new EnterpriseBoardProjection { Id = "board" } },
            Targets = new[] { new EnterpriseSelectionTarget { Id = "first", BoardId = "board", CompanyId = "company",
                DepartmentId = "department", EffectId = "special", Tier = 2, Available = true },
                new EnterpriseSelectionTarget { Id = "locked", BoardId = "board", CompanyId = "company-2",
                    DepartmentId = "department-2", EffectId = "special-2", Tier = 3, Available = false } }
        };

        [TestCase(EnterpriseSelectionMode.Company)] [TestCase(EnterpriseSelectionMode.Effect)]
        [TestCase(EnterpriseSelectionMode.InitialDepartment)] [TestCase(EnterpriseSelectionMode.SwitchDepartment)]
        public void Draft_PreservesStableSelectionButNeverConfirmsAnUnavailableTarget(EnterpriseSelectionMode mode)
        {
            var draft = new EnterpriseSelectionDraft(); var p = Projection(mode);
            draft.Refresh(p); Assert.That(draft.Select("first"),Is.True);
            draft.Inspect("locked"); Assert.That(draft.SelectedId,Is.EqualTo("first"));
            var next = Projection(mode,revision:2); Array.Reverse(next.Targets); draft.Refresh(next);
            Assert.That(draft.SelectedId,Is.EqualTo("first"));
            draft.Select("locked"); Assert.That(draft.CanConfirm,Is.False); Assert.That(draft.InspectedId,Is.EqualTo("locked"));
            draft.Select("first"); next = Projection(mode,revision:3); next.Targets = Array.Empty<EnterpriseSelectionTarget>();
            draft.Refresh(next); Assert.That(draft.SelectedId,Is.Null);
        }

        [Test] public void SharedDepartment_CurrentIsViewableButNotConfirmable_InitialMayHaveNoCurrent()
        {
            var draft = new EnterpriseSelectionDraft(); var p = Projection(EnterpriseSelectionMode.SwitchDepartment);
            p.CurrentDepartmentId = "department"; draft.Refresh(p);
            Assert.That(draft.Select("first"),Is.False); Assert.That(draft.InspectedId,Is.EqualTo("first"));
            p = Projection(EnterpriseSelectionMode.InitialDepartment); draft.Refresh(p);
            Assert.That(draft.Select("first"),Is.True);
        }

        [Test] public void Pending_DuplicateAndLateCompletionsCannotAffectNewRequest()
        {
            var draft = new EnterpriseSelectionDraft(); draft.Refresh(Projection()); draft.Select("first");
            Assert.That(draft.TryBeginSubmit(out var old),Is.True);
            Assert.That(draft.TryBeginSubmit(out _),Is.False); Assert.That(draft.Select("locked"),Is.False);
            draft.Refresh(Projection(request:"b")); draft.Select("first");
            Assert.That(draft.Complete(old),Is.False); Assert.That(draft.SelectedId,Is.EqualTo("first"));
            draft.TryBeginSubmit(out var current); Assert.That(draft.Complete(current),Is.True);
            Assert.That(draft.Complete(current),Is.False); Assert.That(draft.CanConfirm,Is.True);
        }

        [Test] public void MissingCapabilityAndOlderProjectionFailClosed()
        {
            var draft = new EnterpriseSelectionDraft(); var p = Projection(revision:5); p.SubmissionAvailable = false;
            draft.Refresh(p); draft.Select("first"); Assert.That(draft.TryBeginSubmit(out _),Is.False);
            Assert.That(draft.Refresh(Projection(revision:4)),Is.False); Assert.That(draft.CanConfirm,Is.False);
        }

        [Test] public void ProjectionMutationCannotRewriteDraftAndRemovedPendingTargetInvalidatesReply()
        {
            var p = Projection(); var draft = new EnterpriseSelectionDraft(); draft.Refresh(p); draft.Select("first");
            draft.TryBeginSubmit(out var intent);
            Assert.That(intent.CompanyId,Is.EqualTo("company")); Assert.That(intent.EffectId,Is.Null);
            Assert.That(intent.DepartmentId,Is.Null);
            p.RequestId="changed"; p.Targets[0].Available=false;
            Assert.That(draft.Projection.RequestId,Is.EqualTo("a")); Assert.That(draft.Selected.Available,Is.True);
            var next=Projection();next.Targets=Array.Empty<EnterpriseSelectionTarget>();draft.Refresh(next);
            Assert.That(draft.Complete(intent),Is.False);Assert.That(draft.IsPending,Is.False);
        }

        [TestCase(LuaDomainEffectTypeIds.UpgradeEnterprise)]
        [TestCase(LuaDomainEffectTypeIds.ActivateEnterpriseSpecial)]
        [TestCase(LuaDomainEffectTypeIds.SwitchDepartment)]
        public void HostCapabilityRejectsIncompleteInputWithoutApplyingRewards(string type)
        {
            var registry = new EffectRegistry(); GenericLuaEffectExecutor.Register(registry);
            var state = new GameState(); var executor = new EffectTreeExecutor(state,registry);
            var id = executor.CreateRoot(new EffectSpec(type)); var result = executor.RunUntilQuiescent();
            Assert.That(result.Faulted,Is.False); Assert.That(executor.GetNode(id).Status,Is.EqualTo(EffectNodeStatus.Failed));
            Assert.That(executor.GetNode(id).FailureReason,Is.Not.Empty);
            Assert.That(state.EffectRuntime.RuleEvents,Has.Count.EqualTo(1));
            Assert.That(state.EffectRuntime.RuleEvents[0].EventType,Is.EqualTo("EffectCompleted"));
            Assert.That(state.EffectRuntime.RuleEvents[0].Payload.Properties.Find(field => field.Name == "outcome").Value.StringValue,
                Is.EqualTo("failed"));
            Assert.That(state.Players,Is.Empty);
        }
    }
}
