using System.IO.Compression;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

const string UpstreamMasterUrl =
	"https://raw.githubusercontent.com/FFXIV-CombatReborn/CombatRebornRepo/main/pluginmaster.json";
const string RepositoryUrl = "https://github.com/zhui-zi/DalamudPlugins";
const string DownloadBase =
	"https://raw.githubusercontent.com/zhui-zi/DalamudPlugins/main/plugins/RotationSolver/latest.zip";

var force = args.Any(argument => argument.Equals("--force", StringComparison.OrdinalIgnoreCase));
var dumpConfigs = args.Any(argument => argument.Equals("--dump-configs", StringComparison.OrdinalIgnoreCase));
var repositoryArgument = args.FirstOrDefault(argument => !argument.StartsWith("--", StringComparison.Ordinal));
var repositoryRoot = repositoryArgument is not null
	? Path.GetFullPath(repositoryArgument)
	: Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
var translationPath = Path.Combine(repositoryRoot, "plugins", "RotationSolver", "translations.zh-CN.json");
var configTranslationPath =
	Path.Combine(repositoryRoot, "plugins", "RotationSolver", "config-names.zh-CN.json");
var outputDirectory = Path.Combine(repositoryRoot, "plugins", "RotationSolver");
var outputZip = Path.Combine(outputDirectory, "latest.zip");
var pluginMasterPath = Path.Combine(repositoryRoot, "pluginmaster.json");

if (!File.Exists(translationPath))
	throw new FileNotFoundException("Translation file was not found.", translationPath);
if (!File.Exists(configTranslationPath))
	throw new FileNotFoundException("Config translation file was not found.", configTranslationPath);

Directory.CreateDirectory(outputDirectory);
var translations = JsonSerializer.Deserialize<Dictionary<string, string>>(
	await File.ReadAllTextAsync(translationPath, Encoding.UTF8))
	?? throw new InvalidDataException("Translation file is empty.");
var configTranslations = JsonSerializer.Deserialize<Dictionary<string, string>>(
	await File.ReadAllTextAsync(configTranslationPath, Encoding.UTF8))
	?? throw new InvalidDataException("Config translation file is empty.");

using var http = new HttpClient();
http.DefaultRequestHeaders.UserAgent.ParseAdd("zhui-zi/DalamudPlugins");
var upstreamMaster = JsonNode.Parse(await http.GetStringAsync(UpstreamMasterUrl))?.AsArray()
	?? throw new InvalidDataException("Upstream pluginmaster is invalid.");
var upstream = upstreamMaster
	.Select(node => node as JsonObject)
	.FirstOrDefault(node => node?["InternalName"]?.GetValue<string>() == "RotationSolver")
	?? throw new InvalidDataException("RotationSolver was not found in the upstream pluginmaster.");
var upstreamVersion = upstream["AssemblyVersion"]?.GetValue<string>()
	?? throw new InvalidDataException("Upstream AssemblyVersion is missing.");
var downloadUrl = upstream["DownloadLinkUpdate"]?.GetValue<string>()
	?? upstream["DownloadLinkInstall"]?.GetValue<string>()
	?? throw new InvalidDataException("Upstream download URL is missing.");

if (!force && File.Exists(outputZip) && GetLocalVersion(pluginMasterPath) == upstreamVersion)
{
	Console.WriteLine($"RotationSolver {upstreamVersion} is already localized.");
	return;
}

var tempRoot = Path.Combine(Path.GetTempPath(), $"RotationSolverLocalizer-{Guid.NewGuid():N}");
var extractedDirectory = Path.Combine(tempRoot, "extracted");
var upstreamZip = Path.Combine(tempRoot, "upstream.zip");

try
{
	Directory.CreateDirectory(extractedDirectory);
	await using (var source = await http.GetStreamAsync(downloadUrl))
	await using (var destination = File.Create(upstreamZip))
		await source.CopyToAsync(destination);

	ZipFile.ExtractToDirectory(upstreamZip, extractedDirectory);

	var assemblyPath = Path.Combine(extractedDirectory, "RotationSolver.dll");
	var basicAssemblyPath = Path.Combine(extractedDirectory, "RotationSolver.Basic.dll");
	var manifestPath = Path.Combine(extractedDirectory, "RotationSolver.json");
	if (!File.Exists(assemblyPath) || !File.Exists(basicAssemblyPath) || !File.Exists(manifestPath))
		throw new InvalidDataException(
			"Upstream package does not contain RotationSolver.dll, RotationSolver.Basic.dll, and RotationSolver.json.");

	if (dumpConfigs)
		DumpConfigStrings(basicAssemblyPath);

	var patchedCount = PatchUiStrings(assemblyPath, translations);
	if (patchedCount != translations.Count)
		throw new InvalidDataException(
			$"Translation coverage mismatch: patched {patchedCount}, expected {translations.Count}.");
	var patchedConfigCount = PatchConfigNames(basicAssemblyPath, configTranslations);
	if (patchedConfigCount < configTranslations.Count * 0.85)
		throw new InvalidDataException(
			$"Config translation coverage is unexpectedly low: {patchedConfigCount}/{configTranslations.Count}.");

	var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath, Encoding.UTF8))?.AsObject()
		?? throw new InvalidDataException("Upstream manifest is invalid.");
	ApplyLocalizedMetadata(manifest);
	await File.WriteAllTextAsync(
		manifestPath,
		manifest.ToJsonString(CreateJsonOptions()) + Environment.NewLine,
		new UTF8Encoding(false));

	var temporaryOutputZip = Path.Combine(tempRoot, "localized.zip");
	ZipFile.CreateFromDirectory(extractedDirectory, temporaryOutputZip, CompressionLevel.Optimal, false);
	File.Copy(temporaryOutputZip, outputZip, true);

	UpdatePluginMaster(pluginMasterPath, upstream);
	Console.WriteLine(
		$"Localized RotationSolver {upstreamVersion}: {patchedCount} UI strings, " +
		$"{patchedConfigCount} config names.");
	Console.WriteLine(outputZip);
}
finally
{
	if (Directory.Exists(tempRoot))
		Directory.Delete(tempRoot, true);
}

static int PatchUiStrings(string assemblyPath, IReadOnlyDictionary<string, string> translations)
{
	var temporaryAssembly = assemblyPath + ".localized";
	AssemblySnapshot before;
	using (var module = ModuleDefMD.Load(assemblyPath))
	{
		before = CaptureStructure(module);
		var uiString = module.Find("RotationSolver.Data.UiString", false)
			?? throw new InvalidDataException("RotationSolver.Data.UiString was not found.");
		var patchedKeys = new HashSet<string>(StringComparer.Ordinal);

		foreach (var field in uiString.Fields)
		{
			if (!translations.TryGetValue(field.Name, out var localized))
				continue;

			var description = field.CustomAttributes.FirstOrDefault(attribute =>
				attribute.AttributeType.FullName == "System.ComponentModel.DescriptionAttribute");
			if (description is null || description.ConstructorArguments.Count != 1)
				throw new InvalidDataException($"DescriptionAttribute is missing on UiString.{field.Name}.");

			description.ConstructorArguments[0] =
				new CAArgument(module.CorLibTypes.String, localized);
			patchedKeys.Add(field.Name);
		}

		var missing = translations.Keys.Where(key => !patchedKeys.Contains(key)).Order().ToArray();
		if (missing.Length > 0)
			throw new InvalidDataException($"Upstream UiString members are missing: {string.Join(", ", missing)}");

		module.Write(temporaryAssembly);
	}

	using (var localizedModule = ModuleDefMD.Load(temporaryAssembly))
	{
		var after = CaptureStructure(localizedModule);
		if (before != after)
			throw new InvalidDataException($"Assembly structure changed unexpectedly.\nBefore: {before}\nAfter: {after}");

		VerifyTranslations(localizedModule, translations);
	}

	File.Move(temporaryAssembly, assemblyPath, true);
	return translations.Count;
}

static int PatchConfigNames(string assemblyPath, IReadOnlyDictionary<string, string> translations)
{
	var temporaryAssembly = assemblyPath + ".localized";
	AssemblySnapshot before;
	var patched = new Dictionary<string, string>(StringComparer.Ordinal);
	using (var module = ModuleDefMD.Load(assemblyPath))
	{
		before = CaptureStructure(module);
		var configs = module.Find("RotationSolver.Basic.Configuration.Configs", false)
			?? throw new InvalidDataException("RotationSolver.Basic.Configuration.Configs was not found.");
		foreach (var property in configs.Properties)
		{
			if (!translations.TryGetValue(property.Name, out var localized))
				continue;

			var ui = property.CustomAttributes.FirstOrDefault(attribute =>
				attribute.AttributeType.FullName == "RotationSolver.Basic.Attributes.UIAttribute");
			if (ui is null || ui.ConstructorArguments.Count != 1)
				continue;

			ui.ConstructorArguments[0] = new CAArgument(module.CorLibTypes.String, localized);
			patched[property.Name] = localized;
		}

		module.Write(temporaryAssembly);
	}

	using (var localizedModule = ModuleDefMD.Load(temporaryAssembly))
	{
		var after = CaptureStructure(localizedModule);
		if (before != after)
			throw new InvalidDataException(
				$"Basic assembly structure changed unexpectedly.\nBefore: {before}\nAfter: {after}");

		VerifyConfigNames(localizedModule, patched);
	}

	File.Move(temporaryAssembly, assemblyPath, true);
	return patched.Count;
}

static AssemblySnapshot CaptureStructure(ModuleDefMD module)
{
	var builder = new StringBuilder();
	var types = module.GetTypes().ToArray();
	var fieldCount = 0;
	var methodCount = 0;
	var instructionCount = 0;

	foreach (var type in types)
	{
		builder.Append("T|").Append(type.FullName).AppendLine();
		fieldCount += type.Fields.Count;
		foreach (var method in type.Methods)
		{
			methodCount++;
			builder.Append("M|").Append(method.FullName).AppendLine();
			if (!method.HasBody)
				continue;

			var instructions = method.Body.Instructions;
			var indices = instructions
				.Select((instruction, index) => (instruction, index))
				.ToDictionary(pair => pair.instruction, pair => pair.index);
			foreach (var instruction in instructions)
			{
				instructionCount++;
				builder.Append((ushort)instruction.OpCode.Code)
					.Append('|')
					.Append(NormalizeOperand(instruction.Operand, indices))
					.AppendLine();
			}
		}
	}

	var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
	var resourceNames = string.Join("|", module.Resources.Select(resource => resource.Name));
	return new AssemblySnapshot(
		module.Assembly?.Name.String ?? string.Empty,
		module.Assembly?.Version?.ToString() ?? string.Empty,
		types.Length,
		fieldCount,
		methodCount,
		instructionCount,
		module.Resources.Count,
		resourceNames,
		hash);
}

static string NormalizeOperand(object? operand, IReadOnlyDictionary<Instruction, int> indices)
	=> operand switch
	{
		null => string.Empty,
		Instruction target => $"I:{indices[target]}",
		IList<Instruction> targets => $"IS:{string.Join(",", targets.Select(target => indices[target]))}",
		Local local => $"L:{local.Index}:{local.Type.FullName}",
		Parameter parameter => $"P:{parameter.Index}:{parameter.Type.FullName}",
		IFullName fullName => $"F:{fullName.FullName}",
		string value => $"S:{value}",
		IFormattable value => $"V:{value.ToString(null, CultureInfo.InvariantCulture)}",
		_ => $"O:{operand}",
	};

static void VerifyTranslations(ModuleDefMD module, IReadOnlyDictionary<string, string> translations)
{
	var uiString = module.Find("RotationSolver.Data.UiString", false)
		?? throw new InvalidDataException("Localized RotationSolver.Data.UiString was not found.");
	foreach (var (key, expected) in translations)
	{
		var field = uiString.Fields.FirstOrDefault(candidate => candidate.Name == key)
			?? throw new InvalidDataException($"Localized UiString.{key} was not found.");
		var description = field.CustomAttributes.FirstOrDefault(attribute =>
			attribute.AttributeType.FullName == "System.ComponentModel.DescriptionAttribute");
		var actual = description?.ConstructorArguments.Count == 1
			? description.ConstructorArguments[0].Value as UTF8String
			: null;
		if (!string.Equals(actual?.String, expected, StringComparison.Ordinal))
			throw new InvalidDataException($"Localized UiString.{key} did not pass verification.");
	}
}

static void VerifyConfigNames(ModuleDefMD module, IReadOnlyDictionary<string, string> translations)
{
	var configs = module.Find("RotationSolver.Basic.Configuration.Configs", false)
		?? throw new InvalidDataException("Localized RotationSolver.Basic.Configuration.Configs was not found.");
	foreach (var (key, expected) in translations)
	{
		var property = configs.Properties.FirstOrDefault(candidate => candidate.Name == key)
			?? throw new InvalidDataException($"Localized Configs.{key} was not found.");
		var ui = property.CustomAttributes.FirstOrDefault(attribute =>
			attribute.AttributeType.FullName == "RotationSolver.Basic.Attributes.UIAttribute");
		var actual = ui?.ConstructorArguments.Count == 1
			? ui.ConstructorArguments[0].Value as UTF8String
			: null;
		if (!string.Equals(actual?.String, expected, StringComparison.Ordinal))
			throw new InvalidDataException($"Localized Configs.{key} did not pass verification.");
	}
}

static void DumpConfigStrings(string assemblyPath)
{
	using var module = ModuleDefMD.Load(assemblyPath);
	var configs = module.Find("RotationSolver.Basic.Configuration.Configs", false)
		?? throw new InvalidDataException("RotationSolver.Basic.Configuration.Configs was not found.");
	var count = 0;
	foreach (var property in configs.Properties)
	{
		var ui = property.CustomAttributes.FirstOrDefault(attribute =>
			attribute.AttributeType.FullName == "RotationSolver.Basic.Attributes.UIAttribute");
		if (ui is null || ui.ConstructorArguments.Count != 1)
			continue;

		var name = (ui.ConstructorArguments[0].Value as UTF8String)?.String ?? string.Empty;
		var description = string.Empty;
		foreach (var argument in ui.NamedArguments)
		{
			if (argument.Name == "Description")
			{
				description = (argument.Argument.Value as UTF8String)?.String ?? string.Empty;
				break;
			}
		}
		Console.WriteLine(
			$"CONFIG\t{property.Name}\t{Convert.ToBase64String(Encoding.UTF8.GetBytes(name))}\t" +
			$"{Convert.ToBase64String(Encoding.UTF8.GetBytes(description))}");
		count++;
	}

	Console.WriteLine($"CONFIG_COUNT\t{count}");
}

static void ApplyLocalizedMetadata(JsonObject manifest)
{
	manifest["Author"] = "The Combat Reborn Team / zhui-zi";
	manifest["Name"] = "Rotation Solver Reborn 汉化版";
	manifest["Description"] =
		"逐帧分析战斗信息并选择最佳技能。\n\n" +
		"插件会分析队伍与敌对目标状态、技能冷却、角色资源与位置、目标咏唱、连击、战斗时长和玩家等级等信息，" +
		"然后在热键栏上高亮最佳技能或协助点击。\n\n" +
		"本插件面向一般战斗，并非专为零式或绝境战内容设计，请谨慎使用。";
	manifest["Punchline"] = "逐帧分析战斗信息并选择最佳技能。";
	manifest["RepoUrl"] = RepositoryUrl;
}

static string? GetLocalVersion(string path)
{
	if (!File.Exists(path))
		return null;

	var master = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8))?.AsArray();
	return master?
		.Select(node => node as JsonObject)
		.FirstOrDefault(node => node?["InternalName"]?.GetValue<string>() == "RotationSolver")?
		["AssemblyVersion"]?.GetValue<string>();
}

static void UpdatePluginMaster(string path, JsonObject upstream)
{
	var master = File.Exists(path)
		? JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8))?.AsArray()
		: new JsonArray();
	if (master is null)
		throw new InvalidDataException("Local pluginmaster is invalid.");

	var oldEntry = master
		.Select(node => node as JsonObject)
		.FirstOrDefault(node => node?["InternalName"]?.GetValue<string>() == "RotationSolver");
	if (oldEntry is not null)
		master.Remove(oldEntry);

	var localized = (JsonObject)upstream.DeepClone();
	ApplyLocalizedMetadata(localized);
	localized["DownloadLinkInstall"] = DownloadBase;
	localized["DownloadLinkUpdate"] = DownloadBase;
	localized["DownloadLinkTesting"] = DownloadBase;
	localized["TestingAssemblyVersion"] = localized["AssemblyVersion"]?.DeepClone();
	localized["LastUpdate"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
	localized["DownloadCount"] = 0;
	master.Add(localized);

	File.WriteAllText(
		path,
		master.ToJsonString(CreateJsonOptions()) + Environment.NewLine,
		new UTF8Encoding(false));
}

static JsonSerializerOptions CreateJsonOptions()
	=> new()
	{
		WriteIndented = true,
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	};

internal sealed record AssemblySnapshot(
	string AssemblyName,
	string AssemblyVersion,
	int TypeCount,
	int FieldCount,
	int MethodCount,
	int InstructionCount,
	int ResourceCount,
	string ResourceNames,
	string IlHash);
