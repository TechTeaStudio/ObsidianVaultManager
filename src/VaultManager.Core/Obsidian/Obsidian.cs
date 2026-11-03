using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VaultManager.Core.Foundation;

namespace VaultManager.Core.Obsidian;

public static class VaultLayout
{
	public const string ConfigFolderName = ".obsidian";
	public const string DataFile = "data.json";
	public const string ManifestFile = "manifest.json";
	public const string MainFile = "main.js";

	public static string ConfigDir(string vault) => Path.Combine(vault, ConfigFolderName);
	public static string PluginsDir(string vault) => Path.Combine(ConfigDir(vault), "plugins");
	public static string PluginDir(string vault, string id) => Path.Combine(PluginsDir(vault), id);
	public static string CommunityPluginsFile(string vault) => Path.Combine(ConfigDir(vault), "community-plugins.json");
}

public interface IObsidianProcess
{
	bool IsRunning { get; }
}

public sealed class SystemObsidianProcess : IObsidianProcess
{
	public bool IsRunning
	{
		get
		{
			bool found = false;
			foreach (Process process in Process.GetProcesses())
			{
				try
				{
					found |= IsObsidian(process.ProcessName);
				}
				catch (InvalidOperationException)
				{
					// The process ended while the list was read.
				}
				finally
				{
					process.Dispose();
				}
			}

			return found;
		}
	}

	/// <summary>
	/// Windows names the process Obsidian, Linux obsidian, and Linux compares process names by case. A plain lookup
	/// for "Obsidian" found nothing there, so an open vault looked closed and got written into.
	/// </summary>
	public static bool IsObsidian(string processName) => string.Equals(processName, "obsidian", StringComparison.OrdinalIgnoreCase);
}

public sealed record RegistryEntry(string Id, string Path, bool Open);

/// <summary>Obsidian's own list of vaults, %APPDATA%\obsidian\obsidian.json.</summary>
public sealed class VaultRegistry(string obsidianConfigDir)
{
	public string FilePath => Path.Combine(obsidianConfigDir, "obsidian.json");

	public ReadResult<IReadOnlyList<RegistryEntry>> Read()
	{
		ReadResult<JObject> root = ReadRoot();
		if (root.Status != ReadStatus.Ok)
		{
			return root.Status == ReadStatus.Missing
				? ReadResult<IReadOnlyList<RegistryEntry>>.Missing()
				: ReadResult<IReadOnlyList<RegistryEntry>>.Unreadable(root.Error!);
		}

		var entries = new List<RegistryEntry>();
		if (root.Value!["vaults"] is JObject vaults)
		{
			foreach (JProperty vault in vaults.Properties())
			{
				if (vault.Value is JObject body && body.Value<string>("path") is { Length: > 0 } path)
				{
					entries.Add(new RegistryEntry(vault.Name, path, body.Value<bool?>("open") == true));
				}
			}
		}

		return ReadResult<IReadOnlyList<RegistryEntry>>.Ok(entries);
	}

	/// <summary>
	/// Adds a vault and keeps every other key as it is. Call only while Obsidian is closed:
	/// the app keeps this file in memory, so what happens to a write made while it runs is not established.
	/// </summary>
	public string Register(string vaultPath)
	{
		ReadResult<JObject> root = ReadRoot();
		if (root.Status == ReadStatus.Unreadable)
		{
			throw new ChangeRefusedException(ChangeFailure.RegistryUnreadable);
		}

		JObject document = root.Value ?? new JObject();
		if (document["vaults"] is not JObject vaults)
		{
			vaults = new JObject();
			document["vaults"] = vaults;
		}

		string wanted = Path.GetFullPath(vaultPath);
		foreach (JProperty vault in vaults.Properties())
		{
			if (vault.Value is JObject body && body.Value<string>("path") is { } path
				&& PathRules.Same(Path.GetFullPath(path), wanted))
			{
				return vault.Name;
			}
		}

		string id = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
		vaults[id] = new JObject
		{
			["path"] = wanted,
			["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
		};
		AtomicFile.WriteAllText(FilePath, document.ToString(Formatting.None));
		return id;
	}

	ReadResult<JObject> ReadRoot()
	{
		if (!File.Exists(FilePath))
		{
			return ReadResult<JObject>.Missing();
		}

		try
		{
			return JToken.Parse(SharedFile.ReadAllText(FilePath)) is JObject root
				? ReadResult<JObject>.Ok(root)
				: ReadResult<JObject>.Unreadable("not an object");
		}
		catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
		{
			return ReadResult<JObject>.Unreadable(ex.GetType().Name);
		}
	}
}

/// <summary>The enabled list of one vault. Never rewrites a file it could not read.</summary>
public static class CommunityPluginList
{
	public static ReadResult<IReadOnlyList<string>> Read(string vault)
	{
		string path = VaultLayout.CommunityPluginsFile(vault);
		if (!File.Exists(path))
		{
			return ReadResult<IReadOnlyList<string>>.Missing();
		}

		try
		{
			if (JToken.Parse(SharedFile.ReadAllText(path)) is not JArray array)
			{
				return ReadResult<IReadOnlyList<string>>.Unreadable("not an array");
			}

			var ids = new List<string>();
			foreach (JToken token in array)
			{
				if (token.Type != JTokenType.String)
				{
					return ReadResult<IReadOnlyList<string>>.Unreadable("not a list of ids");
				}

				ids.Add(token.Value<string>()!);
			}

			return ReadResult<IReadOnlyList<string>>.Ok(ids);
		}
		catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
		{
			return ReadResult<IReadOnlyList<string>>.Unreadable(ex.GetType().Name);
		}
	}

	public static void Set(string vault, string pluginId, bool enabled)
	{
		ReadResult<IReadOnlyList<string>> current = Read(vault);
		if (current.Status == ReadStatus.Unreadable)
		{
			throw new ChangeRefusedException(ChangeFailure.ListUnreadable);
		}

		var ids = new List<string>(current.Value ?? []);
		bool has = ids.Contains(pluginId, StringComparer.Ordinal);
		if (has == enabled)
		{
			return;
		}

		if (enabled)
		{
			ids.Add(pluginId);
		}
		else
		{
			ids.RemoveAll(id => string.Equals(id, pluginId, StringComparison.Ordinal));
		}

		AtomicFile.WriteAllText(VaultLayout.CommunityPluginsFile(vault), new JArray(ids).ToString(Formatting.Indented));
	}
}

public enum CommunityPluginsState { Unknown, On, Off, NeverEnabled }

/// <summary>
/// Restricted mode is not in app.json: Obsidian keeps it in its browser storage as
/// enable-plugin-[vault id] = "true" or "false" (checked against Obsidian 1.14.5).
/// The reader is best effort: a compacted LevelDB file may compress a key, and then it looks absent.
/// So NeverEnabled means "no key seen", and the window words it as likely rather than as a fact.
/// </summary>
public sealed class CommunityPluginsReader(string obsidianConfigDir)
{
	static readonly Regex KeyPattern = new(@"enable-plugin-([0-9a-f]{16}).{0,8}?(true|false)", RegexOptions.Singleline | RegexOptions.CultureInvariant);

	public IReadOnlyDictionary<string, bool>? ReadAll()
	{
		string store = Path.Combine(obsidianConfigDir, "Local Storage", "leveldb");
		if (!Directory.Exists(store))
		{
			return null;
		}

		var found = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
		try
		{
			// Oldest first, so the newest write of a key wins.
			IEnumerable<FileInfo> files = new DirectoryInfo(store).EnumerateFiles()
				.Where(f => f.Extension is ".log" or ".ldb")
				.OrderBy(f => f.LastWriteTimeUtc)
				.ThenBy(f => f.Name, StringComparer.Ordinal);
			foreach (FileInfo file in files)
			{
				string text = Encoding.Latin1.GetString(SharedFile.ReadAllBytes(file.FullName));
				foreach (Match match in KeyPattern.Matches(text))
				{
					found[match.Groups[1].Value] = match.Groups[2].Value == "true";
				}
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return null;
		}

		return found;
	}

	public static CommunityPluginsState StateOf(IReadOnlyDictionary<string, bool>? all, string vaultId)
	{
		if (all is null)
		{
			return CommunityPluginsState.Unknown;
		}

		return all.TryGetValue(vaultId, out bool on)
			? on ? CommunityPluginsState.On : CommunityPluginsState.Off
			: CommunityPluginsState.NeverEnabled;
	}
}

public sealed record PluginManifest(string? Id, string? Name, string? Version, string? Description, string? Author, string? MinAppVersion, bool IsDesktopOnly)
{
	public static PluginManifest? TryRead(string pluginDir)
	{
		string path = Path.Combine(pluginDir, VaultLayout.ManifestFile);
		if (!File.Exists(path))
		{
			return null;
		}

		try
		{
			return Parse(SharedFile.ReadAllText(path));
		}
		catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	public static PluginManifest? Parse(string json)
	{
		if (JToken.Parse(json) is not JObject o)
		{
			return null;
		}

		// A manifest is someone else's file: a field of the wrong type is an absent field, not a crash.
		string? Text(string key) => o[key] is JValue { Type: JTokenType.String } value ? value.Value<string>() : null;

		return new PluginManifest(Text("id"), Text("name"), Text("version"), Text("description"), Text("author"), Text("minAppVersion"),
			o["isDesktopOnly"] is JValue { Type: JTokenType.Boolean } flag && flag.Value<bool>());
	}
}

/// <summary>Compares plugin versions numerically, so 2.8.3 is older than 2.15.2. A pre-release sorts below its release.</summary>
public static class PluginVersion
{
	public static int Compare(string? a, string? b)
	{
		if (a == b)
		{
			return 0;
		}

		if (string.IsNullOrWhiteSpace(a))
		{
			return -1;
		}

		if (string.IsNullOrWhiteSpace(b))
		{
			return 1;
		}

		(long[] numbersA, string suffixA) = Split(a);
		(long[] numbersB, string suffixB) = Split(b);
		for (int i = 0; i < Math.Max(numbersA.Length, numbersB.Length); i++)
		{
			long x = i < numbersA.Length ? numbersA[i] : 0;
			long y = i < numbersB.Length ? numbersB[i] : 0;
			if (x != y)
			{
				return x.CompareTo(y);
			}
		}

		if (suffixA.Length == 0 || suffixB.Length == 0)
		{
			return suffixB.Length.CompareTo(suffixA.Length);
		}

		return string.CompareOrdinal(suffixA, suffixB);
	}

	static (long[] Numbers, string Suffix) Split(string version)
	{
		string text = version.Trim().TrimStart('v', 'V');
		int cut = text.IndexOfAny(['-', '+']);
		string suffix = cut >= 0 ? text[cut..] : string.Empty;
		string[] parts = (cut >= 0 ? text[..cut] : text).Split('.');
		var numbers = new List<long>();
		for (int i = 0; i < parts.Length; i++)
		{
			if (!long.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out long number))
			{
				return (numbers.ToArray(), "-" + string.Join('.', parts.Skip(i)) + suffix);
			}

			numbers.Add(number);
		}

		return (numbers.ToArray(), suffix);
	}
}

/// <summary>
/// Finds settings that hold a live secret, by key name and value. Names only ever leave this class:
/// settings like rememberPassword (a flag) or tokenizeUrls are not secrets, so the value has to look like one.
/// </summary>
public static class SecretScanner
{
	static readonly Regex SecretName = new(@"(token|secret|password|passphrase|api[-_ ]?key|credential|private[-_ ]?key|bearer)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

	public static IReadOnlyList<string> FindKeys(string json)
	{
		var found = new List<string>();
		try
		{
			Walk(JToken.Parse(json), found);
		}
		catch (JsonException)
		{
			// Unparsable settings cannot be cleared as safe.
			found.Add("(unreadable)");
		}

		return found.Distinct(StringComparer.Ordinal).ToList();
	}

	public static IReadOnlyList<string> FindKeysInFile(string path) => FindKeys(SharedFile.ReadAllText(path));

	static void Walk(JToken token, List<string> found)
	{
		switch (token)
		{
			case JObject o:
				foreach (JProperty property in o.Properties())
				{
					if (SecretName.IsMatch(property.Name) && HoldsText(property.Value))
					{
						found.Add(property.Name);
					}

					Walk(property.Value, found);
				}

				break;
			case JArray a:
				foreach (JToken item in a)
				{
					Walk(item, found);
				}

				break;
		}
	}

	// "apiKeys": ["sk-..."] and "token": {"value": "sk-..."} hold secrets just as a plain string does.
	static bool HoldsText(JToken token) => token switch
	{
		JValue { Type: JTokenType.String } value => value.Value<string>() is { Length: >= 8 },
		JArray a => a.Any(HoldsText),
		JObject o => o.Properties().Any(p => HoldsText(p.Value)),
		_ => false,
	};
}
