using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using System.Text.Json;

internal sealed record FrozenFile(string Path, byte[] Bytes);
internal sealed record PackageDependency(string Id, string Range, bool CoreCompatible);
internal sealed record GameplayCapabilityIssue(string Kind, string Id, IReadOnlyList<string> MissingCapabilities);
internal sealed record GameplayCapabilityScope(string Kind, string Id, IReadOnlyList<string> Capabilities, IReadOnlyList<string> Requires);
internal sealed record FrozenPackage(string PackId, string Version, string Name, string Author, string Description,
    string ContentHash, IReadOnlyList<string> RequiredCapabilities, IReadOnlyList<PackageDependency> Dependencies,
    IReadOnlyList<FrozenFile> Files, IReadOnlyList<GameplayCapabilityIssue> UnavailableGameplay);

/// <summary>从目标构建加载游戏自身的包解析器、限制和能力，不维护第二套 DIY 清单解析器。</summary>
internal sealed class GameContentBridge
{
    private readonly string managed;
    private readonly Type snapshotType, pathsType, identifiersType, versionType;
    public string Company { get; }
    public string Product { get; }
    public string CoreVersion { get; }
    public int MaximumFiles { get; }
    public long MaximumFileBytes { get; }
    public long MaximumTotalBytes { get; }
    public IReadOnlySet<string> Capabilities { get; }

    public GameContentBridge(string gameDirectory, string? expectedProduct = null)
    {
        string game = Path.GetFullPath(gameDirectory);
        SafePaths.RejectLinks(game);
        if (!File.Exists(Path.Combine(game, "tuohuang.exe"))) throw new InvalidDataException("目标目录中没有游戏主程序。");
        SafePaths.RejectLinks(Path.Combine(game, "tuohuang.exe"));
        string data = Path.Combine(game, "tuohuang_Data");
        SafePaths.RejectLinks(Path.Combine(data, "app.info"));
        string[] identity = File.ReadAllLines(Path.Combine(data, "app.info"));
        if (identity.Length != 2) throw new InvalidDataException("目标游戏 app.info 身份无效。");
        Company = identity[0].TrimStart('\uFEFF'); Product = identity[1];
        ValidateIdentityComponent(Company, allowEmpty: true); ValidateIdentityComponent(Product, allowEmpty: false);
        if (!string.IsNullOrEmpty(expectedProduct) && !string.Equals(Product, expectedProduct, StringComparison.Ordinal))
            throw new InvalidDataException("目标游戏产品身份不匹配：" + Product);
        managed = Path.Combine(data, "Managed");
        if (!Directory.Exists(managed)) throw new InvalidDataException("目标游戏缺少 Managed 内容目录。");
        SafePaths.RejectLinks(managed);
        AssemblyLoadContext.Default.Resolving += Resolve;
        try
        {
            string luaPath = Path.Combine(managed, "YC.Infrastructure.Lua.dll"); SafePaths.RejectLinks(luaPath);
            var lua = AssemblyLoadContext.Default.LoadFromAssemblyPath(luaPath);
            snapshotType = lua.GetType("YC.Infrastructure.Lua.DiyPackSnapshot", true)!;
            pathsType = lua.GetType("YC.Infrastructure.Lua.DiyPackPaths", true)!;
            identifiersType = lua.GetType("YC.Infrastructure.Lua.DiyIdentifiers", true)!;
            versionType = lua.GetType("YC.Infrastructure.Lua.DiyVersion", true)!;
            var limits = lua.GetType("YC.Infrastructure.Lua.DiyPackLimits", true)!;
            MaximumFiles = Convert.ToInt32(limits.GetField("MaximumFiles")!.GetRawConstantValue());
            MaximumFileBytes = Convert.ToInt64(limits.GetField("MaximumFileBytes")!.GetRawConstantValue());
            MaximumTotalBytes = Convert.ToInt64(limits.GetField("MaximumTotalBytes")!.GetRawConstantValue());
            string domainPath = Path.Combine(managed, "YC.Domain.dll"); SafePaths.RejectLinks(domainPath);
            var domain = AssemblyLoadContext.Default.LoadFromAssemblyPath(domainPath);
            var readiness = domain.GetType("YC.Domain.Rules.RuleModeReadiness", true)!;
            Capabilities = new HashSet<string>(((IEnumerable)readiness.GetField("ImplementedCapabilities")!.GetValue(null)!).Cast<string>(), StringComparer.Ordinal);
            string core = Path.Combine(data, "StreamingAssets", "Content", "core", "pack.json");
            SafePaths.RejectLinks(core);
            using var document = JsonDocument.Parse(File.ReadAllBytes(core));
            if (document.RootElement.GetProperty("packId").GetString() != "core") throw new InvalidDataException("目标游戏核心包身份无效。");
            CoreVersion = document.RootElement.GetProperty("version").GetString() ?? throw new InvalidDataException("目标游戏核心版本缺失。");
        }
        catch (Exception error) when (error is FileNotFoundException || error is TypeLoadException || error is MissingMemberException)
        { throw new InvalidDataException("目标游戏不支持此 DIY 包体合同：" + error.Message, error); }
    }

    private Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
    {
        string candidate = Path.Combine(managed, name.Name + ".dll");
        if (!File.Exists(candidate)) return null;
        SafePaths.RejectLinks(candidate);
        return context.LoadFromAssemblyPath(candidate);
    }

    public string ValidateRelative(string path) => (string)Invoke(pathsType.GetMethod("ValidateRelative")!, null, path)!;

    public void ValidatePackageIdentity(string packId, string version)
    {
        if (packId == "core" || !(bool)Invoke(identifiersType.GetMethod("ValidPackId")!, null, packId)!)
            throw new InvalidDataException("发布清单的包 ID 无效。");
        Invoke(versionType.GetMethod("Parse")!, null, version, "version");
    }

    public FrozenPackage Load(string directory)
    {
        object snapshot = Invoke(snapshotType.GetMethod("Load", new[] { typeof(string) })!, null, Path.GetFullPath(directory))!;
        var validate = snapshotType.GetMethod("ValidateImages") ?? throw new InvalidDataException("目标游戏缺少图片验证合同。");
        Invoke(validate, snapshot, (Action<byte[], string, int, int>)NativeImageDecoder.Decode);
        object manifest = Property(snapshot, "Manifest"), frozen = Property(snapshot, "Files");
        string pack = Text(manifest, "PackId"), version = Text(manifest, "Version");
        var required = ((IEnumerable)Property(manifest, "RequiredCapabilities")).Cast<string>().ToArray();
        string[] missing = required.Where(capability => !Capabilities.Contains(capability)).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        if (missing.Length > 0) throw new InvalidDataException("目标游戏缺少所需能力：" + string.Join("、", missing));
        var scopes = new List<GameplayCapabilityScope>();
        void InspectGameplay(string collection, string identity, string kind)
        {
            foreach (object entry in (IEnumerable)Property(manifest, collection))
            {
                var declared = ((IEnumerable)Property(entry, "RequiredCapabilities")).Cast<string>();
                var inferred = ((IEnumerable)Property(entry, "RuleBindings")).Cast<object>()
                    .Select(binding => BindingCapability(Text(binding, "EventType")));
                // 优先使用目标游戏读取器推导的完整能力，包含准备流程和多模式合同；旧目标仍使用已有规则绑定推导。
                var effective = entry.GetType().GetProperty("EffectiveRequiredCapabilities")?.GetValue(entry) as IEnumerable;
                var capabilities = effective == null ? declared.Concat(inferred) : effective.Cast<string>();
                scopes.Add(new GameplayCapabilityScope(kind, Text(entry, identity), capabilities.Distinct(StringComparer.Ordinal).ToArray(),
                    ((IEnumerable)Property(entry, "Requires")).Cast<string>().ToArray()));
            }
        }
        InspectGameplay("RuleModes", "ModeId", "模式");
        InspectGameplay("Expansions", "ExpansionId", "扩展");
        var expansions = scopes.Where(item => item.Kind == "扩展").ToDictionary(item => item.Id, StringComparer.Ordinal);
        var gameplay = new List<GameplayCapabilityIssue>();
        foreach (var scope in scopes)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var absent = new SortedSet<string>(StringComparer.Ordinal);
            void Collect(GameplayCapabilityScope current)
            {
                if (!visited.Add(current.Kind + ":" + current.Id)) return;
                foreach (string capability in current.Capabilities)
                    if (!Capabilities.Contains(capability)) absent.Add(capability);
                foreach (string dependency in current.Requires)
                    if (expansions.TryGetValue(dependency, out var ownExpansion)) Collect(ownExpansion);
            }
            Collect(scope);
            if (absent.Count > 0) gameplay.Add(new GameplayCapabilityIssue(scope.Kind, scope.Id, absent.ToArray()));
        }
        if (scopes.Count == 0 || gameplay.Count == scopes.Count)
            throw new InvalidDataException("目标游戏没有满足声明能力要求的玩法：" + string.Join("；", gameplay.Select(item =>
                item.Kind + " " + item.Id + " 缺少 " + string.Join("、", item.MissingCapabilities))));
        var dependencies = new List<PackageDependency>();
        foreach (object dependency in (IEnumerable)Property(manifest, "Dependencies"))
        {
            string id = Text(dependency, "PackId"), range = Text(dependency, "VersionRange");
            bool compatible = id != "core" || (bool)Invoke(dependency.GetType().GetMethod("Accepts")!, dependency, CoreVersion)!;
            if (!compatible) throw new InvalidDataException("目标游戏核心版本不满足包依赖：" + CoreVersion + " / " + range);
            dependencies.Add(new PackageDependency(id, range, compatible));
        }
        var read = frozen.GetType().GetMethod("ReadBytes") ?? throw new MissingMethodException("ReadBytes");
        var files = ((IEnumerable)Property(frozen, "Paths")).Cast<string>()
            .Select(path => new FrozenFile(path, (byte[])Invoke(read, frozen, path)!)).ToArray();
        return new FrozenPackage(pack, version, Text(manifest, "DisplayName"), Text(manifest, "Author"), Text(manifest, "Description"),
            Text(snapshot, "ContentHash"), required, dependencies, files, gameplay);
    }

    // 目标游戏可能使用较早读取器；工具仍需核对旧清单中未显式声明的绑定能力。
    private static string BindingCapability(string eventType) => eventType switch
    {
        "PlayerCleanupStarted" => "diy.rule_bindings.cleanup.v1",
        "CandidateSetBuilding" => "diy.rule_bindings.candidate.v1",
        "EffectCompleted" => "diy.rule_bindings.completed.v1",
        "RoundStarted" or "RoundEnded" or "CharacterCardCovered" or "CharacterCoverCompleted" or "CharacterUseFinished" or
        "CollectionStarted" or "CityMoveCompleted" or "FacilityPlaced" or "FinalScoringStarted" => "diy.rule_bindings.events.v1",
        _ => throw new InvalidDataException("安装工具无法核对规则绑定入口：" + eventType)
    };

    private static object Property(object target, string name) => target.GetType().GetProperty(name)?.GetValue(target)
        ?? throw new InvalidDataException("目标游戏缺少合同字段：" + name);
    private static string Text(object target, string name) => (string)Property(target, name);
    private static object? Invoke(MethodInfo method, object? target, params object[] args)
    {
        try { return method.Invoke(target, args); }
        catch (TargetInvocationException error) when (error.InnerException != null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }

    private static void ValidateIdentityComponent(string value, bool allowEmpty)
    {
        if (value.Length == 0 && allowEmpty) return;
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.EndsWith(' ') || value.EndsWith('.') ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value.Contains('/') || value.Contains('\\'))
            throw new InvalidDataException("目标构建的产品或公司身份不能用于用户数据目录。");
    }
}
