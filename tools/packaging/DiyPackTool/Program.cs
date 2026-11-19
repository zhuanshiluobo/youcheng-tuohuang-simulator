using System.Text.Json;
using System.Text;

string? resultFile = null;
void WriteResultFile(string message)
{
    if (resultFile == null) return;
    string path = Path.GetFullPath(resultFile), parent = Path.GetDirectoryName(path)!;
    SafePaths.RejectLinks(parent);
    using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    using var writer = new StreamWriter(stream, new UnicodeEncoding(false, false));
    writer.Write(message.Replace('\r', ' ').Replace('\n', ' '));
}
void Report(string message) { Console.WriteLine(message); WriteResultFile(message); }

try
{
    if (args.Length == 0) throw new ArgumentException("用法：DiyPackTool publish|verify|install|export|inspect --参数 值。");
    string command = args[0];
    // 机器读取的 JSON 输出固定为 UTF-8，避免无控制台时按系统代码页丢失包名等字符。
    // 安装和导出继续使用原有文本／UTF-16 结果文件合同供安装器读取。
    if (Console.IsOutputRedirected && (command == "publish" || command == "verify" || command == "inspect"))
        Console.OutputEncoding = new UTF8Encoding(false);
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (int i = 1; i < args.Length; i += 2)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 == args.Length || !options.TryAdd(args[i], args[i + 1]))
            throw new ArgumentException("参数必须使用不重复的 --名称 值。 ");
    }
    if (options.Remove("--result-file", out string? selectedResult))
    {
        if (command != "install" && command != "export" || !Path.IsPathFullyQualified(selectedResult))
            throw new ArgumentException("结果文件仅供安装或导出使用，且路径必须为绝对路径。");
        resultFile = selectedResult;
    }
    string Required(string name) => options.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value) ? value :
        throw new ArgumentException("缺少参数 " + name);
    void Allow(params string[] names)
    {
        string? invalid = options.Keys.FirstOrDefault(key => !names.Contains(key, StringComparer.Ordinal));
        if (invalid != null) throw new ArgumentException("不支持的参数：" + invalid);
    }
    switch (command)
    {
        case "publish":
        {
            Allow("--game", "--source", "--output");
            var game = new GameContentBridge(Required("--game"));
            var manifest = PackArchive.Publish(game, Required("--source"), Required("--output"));
            Console.WriteLine(JsonSerializer.Serialize(manifest, PackArchive.JsonOptions));
            break;
        }
        case "export":
            Allow("--zip", "--sha256", "--output");
            Report(PackArchive.Export(Required("--zip"), Required("--sha256"), Required("--output")));
            break;
        case "verify":
        {
            Allow("--game", "--zip", "--sha256", "--pack-id", "--version", "--content-hash", "--output");
            var game = new GameContentBridge(Required("--game"));
            var verified = PackArchive.Verify(game, Required("--zip"), Required("--sha256"), Required("--pack-id"),
                Required("--version"), Required("--content-hash"), Required("--output"));
            Console.WriteLine(JsonSerializer.Serialize(new { verified.PackId, verified.Version, verified.Name, verified.Author,
                verified.Description, verified.ContentHash, verified.RequiredCapabilities, verified.Dependencies,
                files = verified.Files.Select(file => new { file.Path, bytes = file.Bytes.LongLength,
                    sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file.Bytes)).ToLowerInvariant() }).ToArray() }, PackArchive.JsonOptions));
            break;
        }
        case "inspect":
        {
            Allow("--game", "--product");
            var game = new GameContentBridge(Required("--game"), options.GetValueOrDefault("--product"));
            Console.WriteLine(JsonSerializer.Serialize(new { game.Company, game.Product, game.CoreVersion,
                capabilities = game.Capabilities.OrderBy(x => x, StringComparer.Ordinal).ToArray() }, PackArchive.JsonOptions));
            break;
        }
        case "install":
        {
#if DEBUG
            Allow("--game", "--product", "--zip", "--sha256", "--pack-id", "--version", "--content-hash", "--fixture-data-root", "--keep-stage", "--fixture-fail-receipt-after-publish");
#else
            Allow("--game", "--product", "--zip", "--sha256", "--pack-id", "--version", "--content-hash");
#endif
            var game = new GameContentBridge(Required("--game"), Required("--product"));
            string root = WindowsDataRoot.ForGame(game); bool keepStage = false, simulateReceiptFailure = false;
#if DEBUG
            if (options.TryGetValue("--fixture-data-root", out string? fixture))
            {
                if (!Path.IsPathFullyQualified(fixture)) throw new ArgumentException("测试内容根必须是绝对路径。");
                root = Path.GetFullPath(fixture);
            }
            keepStage = options.TryGetValue("--keep-stage", out string? keep) && keep == "true";
            simulateReceiptFailure = options.TryGetValue("--fixture-fail-receipt-after-publish", out string? fail) && fail == "true";
            if (simulateReceiptFailure && !options.ContainsKey("--fixture-data-root")) throw new ArgumentException("模拟回执故障必须指定隔离内容根。");
#endif
            var result = PackArchive.Install(game, Required("--zip"), Required("--sha256"), Required("--pack-id"),
                Required("--version"), Required("--content-hash"), root, keepStage, simulateReceiptFailure);
            var receipt = result.Receipt;
            string versions = receipt.ExistingVersions.Count == 0 ? "此前没有已安装版本。" : "此前已有版本：" + string.Join("、", receipt.ExistingVersions) + "。";
            string warning = result.UnavailableGameplay.Count == 0 ? "" : "部分玩法缺少目标游戏能力：" + string.Join("；", result.UnavailableGameplay.Select(item =>
                item.Kind + " " + item.Id + " 缺少 " + string.Join("、", item.MissingCapabilities))) + "。";
            Report((receipt.Outcome == "installed" ? "DIY 内容已安装；重启游戏后可使用。" : "同版本内容一致，已跳过安装。") +
                versions + "现有版本和默认绑定均保留。" + warning);
            break;
        }
        default: throw new ArgumentException("未知操作：" + command);
    }
    return 0;
}
catch (Exception error)
{
    string message = "操作未完成：" + error.Message;
    Console.Error.WriteLine(message);
    try { WriteResultFile(message); }
    catch (Exception reportError) { Console.Error.WriteLine("无法写入结果：" + reportError.Message); }
    return 1;
}
