using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using VaultManager.App.Logic;
using VaultManager.Core.Changes;
using VaultManager.Core.Foundation;
using VaultManager.Core.Obsidian;
using VaultManager.Core.Profiles;
using VaultManager.Core.Sync;

namespace VaultManager.App.Localization;

/// <summary>A string by key in markup: {l:T Nav_Plugins}. The main view is rebuilt on a language switch, so one read is enough.</summary>
public sealed class T(string key) : MarkupExtension
{
	public string Key { get; set; } = key;

	public override object ProvideValue(IServiceProvider serviceProvider) => Texts.Get(Key);
}

/// <summary>The condensed caps role: {l:Caps Nav_Plugins}. Strings are stored in sentence case.</summary>
public sealed class Caps(string key) : MarkupExtension
{
	public string Key { get; set; } = key;

	public override object ProvideValue(IServiceProvider serviceProvider) => Texts.Caps(Key);
}

/// <summary>Button faces are caps by role, wherever their text comes from.</summary>
public sealed class CapsConverter : IValueConverter
{
	public static readonly CapsConverter Instance = new();

	public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
		value is string text ? text.ToUpper(Texts.Culture) : value;

	public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value;
}

public enum SegRole { Plain, Strong, Value }

public sealed record Seg(string Text, SegRole Role = SegRole.Plain)
{
	public static Seg Strong(string text) => new(text, SegRole.Strong);
	public static Seg Value(string? text) => new(text ?? string.Empty, SegRole.Value);
}

/// <summary>Fills a sentence from the string table with parts in their own type role.</summary>
internal static class Segments
{
	/// <param name="args">A string, a Seg, or a list of Seg, by placeholder number.</param>
	public static IReadOnlyList<Seg> Of(string key, params object?[] args) => Parse(Texts.Get(key), args);

	internal static IReadOnlyList<Seg> Parse(string format, IReadOnlyList<object?> args)
	{
		var result = new List<Seg>();
		int at = 0;
		while (at < format.Length)
		{
			int open = format.IndexOf('{', at);
			if (open < 0)
			{
				result.Add(new Seg(format[at..]));
				break;
			}

			if (open > at)
			{
				result.Add(new Seg(format[at..open]));
			}

			int close = format.IndexOf('}', open);
			int index = int.Parse(format[(open + 1)..close], CultureInfo.InvariantCulture);
			switch (args[index])
			{
				case Seg seg:
					result.Add(seg);
					break;
				case IEnumerable<Seg> many:
					result.AddRange(many);
					break;
				case { } plain:
					result.Add(new Seg(System.Convert.ToString(plain, Texts.Culture) ?? string.Empty));
					break;
			}

			at = close + 1;
		}

		return result;
	}

	/// <summary>"a", "a and b", "a, b and c", with each item already in its role.</summary>
	public static IReadOnlyList<Seg> JoinList(IReadOnlyList<IReadOnlyList<Seg>> items)
	{
		var result = new List<Seg>();
		for (int i = 0; i < items.Count; i++)
		{
			if (i > 0)
			{
				result.Add(new Seg(i == items.Count - 1 ? " " + Texts.Get(K.List_And) + " " : ", "));
			}

			result.AddRange(items[i]);
		}

		return result;
	}
}

/// <summary>The one place an enum becomes words. Nothing goes the other way.</summary>
internal static class EnumTexts
{
	public static string KeyOf(ChangeStatus value) => "ChangeStatus_" + value;
	public static string KeyOf(ChangeFailure value) => "ChangeFailure_" + value;
	public static string KeyOf(ChangeNote value) => "ChangeNote_" + value;
	public static string KeyOf(SyncDecision value) => "SyncDecision_" + value;
	public static string KeyOf(ProfileProblem value) => "ProfileProblem_" + value;
	public static string KeyOf(CommunityPluginsState value) => "CommunityPlugins_" + value;
	public static string KeyOf(SettingsCategory value) => "SettingsCategory_" + value;
	public static string KeyOf(SettingsFate value) => "SettingsFate_" + value;
	public static string KeyOf(NewVaultProblem value) => "NewVaultProblem_" + value;
	public static string KeyOf(CellKind value) => "Cell_" + value;
	public static string KeyOf(VaultManager.Core.Obsidian.ObsidianInstall value) => "ObsidianInstall_" + value;

	public static string Of(ChangeStatus value) => Texts.Get(KeyOf(value));
	public static string Of(ChangeFailure value) => Texts.Get(KeyOf(value));
	public static string Of(ChangeNote value) => Texts.Get(KeyOf(value));
	public static string Of(SyncDecision value) => Texts.Get(KeyOf(value));
	public static string Of(ProfileProblem value) => Texts.Get(KeyOf(value));
	public static string Of(CommunityPluginsState value) => Texts.Get(KeyOf(value));
	public static string Of(SettingsCategory value) => Texts.Get(KeyOf(value));
	public static string Of(SettingsFate value) => Texts.Get(KeyOf(value));
	public static string Of(NewVaultProblem value) => Texts.Get(KeyOf(value));
	public static string Of(CellKind value) => Texts.Get(KeyOf(value));
	public static string Of(VaultManager.Core.Obsidian.ObsidianInstall value) => Texts.Get(KeyOf(value));

	/// <summary>What one undone line was, for the log. Kind is a code Core wrote, not a sentence.</summary>
	public static string UndoSubject(UndoRecord record)
	{
		string vault = Path.GetFileName(record.Vault.TrimEnd('\\', '/'));
		return record.Kind switch
		{
			"plugin" or "toggle" => $"{record.PluginId} [{vault}]",
			"settings" => $"{record.RelativePath} [{vault}]",
			"parts" => Texts.Format(K.Undo_Parts, vault),
			_ => vault,
		};
	}
}

/// <summary>Values as a person reads them: sizes and times follow the language.</summary>
internal static class Formats
{
	public static string Size(long bytes)
	{
		if (bytes < 1000)
		{
			return Texts.Format(K.Unit_Bytes, bytes);
		}

		if (bytes < 1_000_000)
		{
			return Texts.Format(K.Unit_KB, Math.Round(bytes / 1000d));
		}

		return Texts.Format(K.Unit_MB, (bytes / 1_000_000d).ToString("0.##", Texts.Culture));
	}

	public static string Time(DateTimeOffset when)
	{
		DateTimeOffset local = when.ToLocalTime();
		return local.Date == DateTimeOffset.Now.Date
			? local.ToString("HH:mm", CultureInfo.InvariantCulture)
			: local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
	}

	public static string Clock(DateTimeOffset when) => when.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
}
