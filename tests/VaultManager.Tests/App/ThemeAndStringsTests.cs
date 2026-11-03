using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using VaultManager.App.Localization;
using VaultManager.Core.Foundation;

namespace VaultManager.Tests.App;

static class Repo
{
	public static string Root { get; } = Find();

	static string Find()
	{
		string? dir = AppContext.BaseDirectory;
		while (dir is not null && !File.Exists(Path.Combine(dir, "VaultManager.slnx")))
		{
			dir = Path.GetDirectoryName(dir);
		}

		return dir ?? throw new InvalidOperationException("Repository root not found.");
	}

	public static string App => Path.Combine(Root, "src", "VaultManager.App");

	public static IEnumerable<string> AppFiles(params string[] extensions) =>
		Directory.EnumerateFiles(App, "*", SearchOption.AllDirectories)
			.Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
			.Where(f => extensions.Contains(Path.GetExtension(f)));
}

public sealed class LocalizationTests
{
	static Dictionary<string, string> Load(string file) =>
		XDocument.Load(Path.Combine(Repo.App, "Localization", file)).Root!.Elements("data")
			.ToDictionary(d => d.Attribute("name")!.Value, d => d.Element("value")!.Value);

	static readonly Dictionary<string, string> English = Load("Strings.resx");
	static readonly Dictionary<string, string> Russian = Load("Strings.ru.resx");

	static IEnumerable<string> Placeholders(string text) =>
		Regex.Matches(text, @"\{\d+\}").Select(m => m.Value).Distinct().OrderBy(p => p, StringComparer.Ordinal);

	[Fact]
	public void Russian_has_exactly_the_English_keys() =>
		Assert.Equal(English.Keys.OrderBy(k => k, StringComparer.Ordinal), Russian.Keys.OrderBy(k => k, StringComparer.Ordinal));

	[Fact]
	public void Every_translation_keeps_every_placeholder_and_line_break()
	{
		foreach ((string key, string text) in English)
		{
			Assert.True(Placeholders(text).SequenceEqual(Placeholders(Russian[key])), $"{key}: placeholders differ");
			Assert.True(text.Count(c => c == '\n') == Russian[key].Count(c => c == '\n'), $"{key}: line breaks differ");
		}
	}

	[Fact]
	public void No_string_is_empty_and_none_has_a_long_dash_or_an_exclamation_mark()
	{
		foreach ((string key, string text) in English.Concat(Russian))
		{
			Assert.False(string.IsNullOrWhiteSpace(text), key);
			Assert.False(text.Contains('\u2014') || text.Contains('\u2013') || text.Contains('!'), key);
		}
	}

	[Fact]
	public void Every_key_constant_resolves_in_both_languages()
	{
		foreach (FieldInfo field in typeof(K).GetFields(BindingFlags.Public | BindingFlags.Static))
		{
			string key = (string)field.GetValue(null)!;
			Assert.Equal(field.Name, key);
			Assert.False(string.IsNullOrEmpty(Texts.Find(key, AppLanguage.English)), key);
			Assert.False(string.IsNullOrEmpty(Texts.Find(key, AppLanguage.Russian)), key);
		}

		foreach (FieldInfo field in typeof(K.Plural).GetFields(BindingFlags.Public | BindingFlags.Static))
		{
			string key = (string)field.GetValue(null)!;
			foreach (PluralForm form in Enum.GetValues<PluralForm>())
			{
				Assert.False(string.IsNullOrEmpty(Texts.Find($"{key}_{form}", AppLanguage.English)), $"{key}_{form}");
				Assert.False(string.IsNullOrEmpty(Texts.Find($"{key}_{form}", AppLanguage.Russian)), $"{key}_{form}");
				Assert.Contains("{0}", English[$"{key}_{form}"]);
			}
		}
	}

	[Fact]
	public void Every_key_used_in_markup_exists()
	{
		foreach (string file in Repo.AppFiles(".axaml"))
		{
			foreach (Match match in Regex.Matches(File.ReadAllText(file), @"\{l:(?:T|Caps)\s+([^}\s]+)\}"))
			{
				Assert.True(English.ContainsKey(match.Groups[1].Value), $"{Path.GetFileName(file)}: {match.Groups[1].Value}");
			}
		}
	}

	[Fact]
	public void Markup_holds_no_words_of_its_own()
	{
		var attribute = new Regex(@"\b(Text|Content|Watermark|PlaceholderText|ToolTip\.Tip|Header)=""([^""{]*[\p{L}][^""]*)""");
		foreach (string file in Repo.AppFiles(".axaml"))
		{
			foreach (Match match in attribute.Matches(File.ReadAllText(file)))
			{
				Assert.Fail($"{Path.GetFileName(file)}: {match.Value}");
			}
		}
	}

	[Fact]
	public void No_source_file_has_a_long_dash()
	{
		foreach (string file in Repo.AppFiles(".cs", ".axaml", ".resx", ".csproj", ".manifest"))
		{
			string text = File.ReadAllText(file);
			Assert.False(text.Contains('\u2014') || text.Contains('\u2013'), file);
		}
	}
}

public sealed class ContrastFormula
{
	public static double Luminance(Color c)
	{
		static double Channel(byte value)
		{
			double v = value / 255d;
			return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
		}

		return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
	}

	public static double Ratio(Color a, Color b)
	{
		double x = Luminance(a), y = Luminance(b);
		return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
	}

	[Theory]
	[InlineData("#000000", "#FFFFFF", 21.0)]
	[InlineData("#FFFFFF", "#FFFFFF", 1.0)]
	[InlineData("#777777", "#FFFFFF", 4.48)]
	[InlineData("#FF0000", "#FFFFFF", 4.0)]
	[InlineData("#0000FF", "#FFFFFF", 8.59)]
	public void The_formula_gives_known_answers(string a, string b, double expected) =>
		Assert.Equal(expected, Ratio(Color.Parse(a), Color.Parse(b)), 2);
}

[Collection(AppCollection.Name)]
public sealed class ThemeTests
{
	static readonly string ThemePath = Path.Combine(Repo.App, "Themes", "Basalt.axaml");

	static readonly Dictionary<string, Color> Tokens = Regex
		.Matches(File.ReadAllText(ThemePath), @"<Color x:Key=""Color\.(\w+)"">(#[0-9A-Fa-f]{6,8})</Color>")
		.ToDictionary(m => m.Groups[1].Value, m => Color.Parse(m.Groups[2].Value));

	static readonly string[] TextTokens = ["Text", "Soft", "Dim", "Sulfur", "Match", "Drift", "Blocked", "Info"];
	static readonly string[] Surfaces = ["Well", "Base", "Plate", "Raised"];

	[Fact]
	public void All_text_holds_4_5_to_1_on_all_four_surfaces()
	{
		foreach (string text in TextTokens)
		{
			foreach (string surface in Surfaces)
			{
				double ratio = ContrastFormula.Ratio(Tokens[text], Tokens[surface]);
				Assert.True(ratio >= 4.5, $"{text} on {surface}: {ratio:F2}");
			}
		}
	}

	[Theory]
	[InlineData("Text", "Hover")]
	[InlineData("Soft", "Hover")]
	[InlineData("Dim", "Hover")]
	[InlineData("OnSulfur", "Sulfur")]
	[InlineData("OnSulfur", "SulfurHover")]
	public void Control_faces_keep_their_text_readable(string text, string surface)
	{
		double ratio = ContrastFormula.Ratio(Tokens[text], Tokens[surface]);
		Assert.True(ratio >= 4.5, $"{text} on {surface}: {ratio:F2}");
	}

	[Theory]
	[InlineData("Edge", "Well")]
	[InlineData("Edge", "Base")]
	[InlineData("Edge", "Plate")]
	[InlineData("Sulfur", "Well")]
	[InlineData("Sulfur", "Plate")]
	[InlineData("Sulfur", "Raised")]
	public void Control_edges_and_the_focus_ring_hold_3_to_1(string edge, string surface)
	{
		double ratio = ContrastFormula.Ratio(Tokens[edge], Tokens[surface]);
		Assert.True(ratio >= 3, $"{edge} on {surface}: {ratio:F2}");
	}

	[Fact]
	public void No_colour_lives_outside_the_theme_file()
	{
		var hex = new Regex(@"(?<![\w&])#[0-9A-Fa-f]{3,8}\b");
		var named = new Regex(@"\b(Colors|Brushes)\.[A-Z]\w+|Color\.(FromRgb|FromArgb|Parse)\(");
		foreach (string file in Repo.AppFiles(".cs", ".axaml"))
		{
			if (file == ThemePath)
			{
				continue;
			}

			string text = File.ReadAllText(file);
			if (Path.GetExtension(file) == ".axaml")
			{
				// Text between quotes such as #IBM in a font path does not reach here: fonts live in the theme too.
				Assert.False(hex.IsMatch(text), file);
				Assert.DoesNotContain("avares://VaultManager/Assets/Fonts", text);
				Assert.False(Regex.IsMatch(text, @"(Background|Foreground|BorderBrush|Fill|Stroke)=""[A-Za-z]"), file);
			}

			Assert.False(named.IsMatch(text), file);
		}
	}

	[Fact]
	public void Radii_and_type_sizes_stay_on_the_scale()
	{
		string theme = File.ReadAllText(ThemePath);
		foreach (Match size in Regex.Matches(theme, @"<x:Double x:Key=""Size\.\w+"">(\d+)</x:Double>"))
		{
			Assert.Contains(size.Groups[1].Value, new[] { "12", "13", "16" });
		}

		foreach (string file in Repo.AppFiles(".axaml"))
		{
			if (file != ThemePath)
			{
				Assert.False(Regex.IsMatch(File.ReadAllText(file), @"CornerRadius=""\d"), file);
				Assert.False(Regex.IsMatch(File.ReadAllText(file), @"FontSize=""\d"), file);
			}
		}
	}

	[Fact]
	public void Theme_has_no_gradients_and_no_soft_shadows()
	{
		string theme = File.ReadAllText(ThemePath);
		Assert.DoesNotContain("Gradient", theme);
		Assert.DoesNotContain("BoxShadow", theme);
		Assert.DoesNotContain("DropShadow", theme);
	}

	[Fact]
	public Task Every_resource_key_used_anywhere_resolves() => Ui.Run(() =>
	{
		var used = new HashSet<string>(StringComparer.Ordinal);
		foreach (string file in Repo.AppFiles(".axaml"))
		{
			string text = File.ReadAllText(file);
			foreach (Match match in Regex.Matches(text, @"\{(?:Dynamic|Static)Resource\s+([^}\s]+)\}"))
			{
				// A template kept in the view's own resources is found by the compiler, not by the app.
				if (!text.Contains($"x:Key=\"{match.Groups[1].Value}\"") || file == ThemePath)
				{
					used.Add(match.Groups[1].Value);
				}
			}
		}

		foreach (string file in Repo.AppFiles(".cs"))
		{
			foreach (Match match in Regex.Matches(File.ReadAllText(file), @"""((?:Brush|Font)\.\w+)"""))
			{
				used.Add(match.Groups[1].Value);
			}
		}

		Assert.NotEmpty(used);
		foreach (string key in used)
		{
			Assert.True(Application.Current!.TryGetResource(key, ThemeVariant.Dark, out _), key);
		}
	});

	[Theory]
	[InlineData("Font.Read", "IBM Plex Sans")]
	[InlineData("Font.ReadMedium", "IBM Plex Sans Medm")]
	[InlineData("Font.ReadSemi", "IBM Plex Sans SmBld")]
	[InlineData("Font.Label", "IBM Plex Sans Cond SmBld")]
	[InlineData("Font.Value", "JetBrains Mono")]
	public Task Each_font_token_reaches_its_embedded_face_with_Cyrillic(string token, string family) => Ui.Run(() =>
	{
		Assert.True(Application.Current!.TryGetResource(token, ThemeVariant.Dark, out object? found));
		var font = Assert.IsType<FontFamily>(found);
		Assert.True(FontManager.Current.TryGetGlyphTypeface(new Typeface(font), out IGlyphTypeface? face), token);
		// Exact: an unknown family falls back to the default face, and that would still start with "IBM Plex Sans".
		Assert.Equal(family, face!.FamilyName);
		foreach (char c in "АБВЖЩЫЯабвжщыяёЁ·×→")
		{
			Assert.True(face.GetGlyph(c) != 0, $"{token}: {c}");
		}
	});

	[Fact]
	public Task The_font_files_stay_under_the_size_budget_and_carry_their_licence() => Ui.Run(() =>
	{
		string fonts = Path.Combine(Repo.App, "Assets", "Fonts");
		long total = Directory.EnumerateFiles(fonts, "*.ttf").Sum(f => new FileInfo(f).Length);
		Assert.True(total < 2_000_000, $"{total} bytes");
		Assert.Equal(5, Directory.EnumerateFiles(fonts, "*.ttf").Count());
		Assert.All(Directory.EnumerateFiles(fonts, "OFL*.txt"), f => Assert.Contains("SIL Open Font License", File.ReadAllText(f)));
		Assert.Equal(2, Directory.EnumerateFiles(fonts, "OFL*.txt").Count());
	});

	[Fact]
	public Task The_app_icon_loads() => Ui.Run(() =>
	{
		using Stream stream = AssetLoader.Open(new Uri("avares://VaultManager/Assets/vault-manager.ico"));
		var reader = new BinaryReader(stream);
		Assert.Equal(0, reader.ReadUInt16());
		Assert.Equal(1, reader.ReadUInt16());
		ushort frames = reader.ReadUInt16();
		Assert.True(frames > 0);

		var window = new Avalonia.Controls.WindowIcon(AssetLoader.Open(new Uri("avares://VaultManager/Assets/vault-manager.ico")));
		Assert.NotNull(window);
	});

	[Fact]
	public void The_Windows_loader_itself_takes_the_icon()
	{
		// The window icon goes through Avalonia's Win32 loader, which the headless platform replaces with a stub. It makes no window.
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		Type icon = Assembly.Load("Avalonia.Win32").GetType("Avalonia.Win32.Interop.Win32Icon")!;
		byte[] file = File.ReadAllBytes(Path.Combine(Repo.App, "Assets", "vault-manager.ico"));

		object loaded = Activator.CreateInstance(icon, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [file, new PixelSize(32, 32)], null)!;

		Assert.NotEqual(IntPtr.Zero, (IntPtr)icon.GetProperty("Handle")!.GetValue(loaded)!);
		(loaded as IDisposable)?.Dispose();
	}

	[Fact]
	public void Each_icon_frame_is_an_image_the_Windows_loader_can_take()
	{
		// Reads the file as Windows does: a directory of frames, each a PNG or a DIB. Win32Icon in Avalonia hands these frames to the OS.
		byte[] file = File.ReadAllBytes(Path.Combine(Repo.App, "Assets", "vault-manager.ico"));
		int count = BitConverter.ToUInt16(file, 4);
		Assert.True(count > 0);
		for (int i = 0; i < count; i++)
		{
			int entry = 6 + (16 * i);
			int size = BitConverter.ToInt32(file, entry + 8);
			int offset = BitConverter.ToInt32(file, entry + 12);
			Assert.True(size > 0 && offset + size <= file.Length);
			bool png = file[offset] == 0x89 && file[offset + 1] == (byte)'P';
			bool dib = BitConverter.ToInt32(file, offset) == 40;
			Assert.True(png || dib, $"frame {i}");
		}
	}
}

/// <summary>
/// The release shape: one self-contained exe in which SQLite, Skia and the fonts load. Slow, so it runs when asked:
/// set VM_PUBLISH_CHECK to an empty folder outside the repo.
/// </summary>
public sealed class PublishCheck
{
	[Fact]
	public async Task The_published_build_is_one_exe_and_SQLite_loads_from_it()
	{
		string? output = Environment.GetEnvironmentVariable("VM_PUBLISH_CHECK");
		if (string.IsNullOrEmpty(output))
		{
			return;
		}

		Assert.False(Path.GetFullPath(output).StartsWith(Repo.Root, StringComparison.OrdinalIgnoreCase), "The publish folder must be outside the repo.");
		var publish = new ProcessStartInfo("dotnet")
		{
			ArgumentList =
			{
				"publish", Path.Combine(Repo.App, "VaultManager.App.csproj"), "-c", "Release", "-f", "net10.0", "-r", "win-x64", "--self-contained", "true",
				"-p:PublishSingleFile=true", "-p:EnableCompressionInSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-o", output,
			},
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		using (Process process = Process.Start(publish)!)
		{
			Task<string> log = process.StandardOutput.ReadToEndAsync();
			Task<string> errors = process.StandardError.ReadToEndAsync();
			await process.WaitForExitAsync();
			Assert.True(process.ExitCode == 0, await log + await errors);
		}

		string exe = Path.Combine(output, "VaultManager.exe");
		Assert.True(File.Exists(exe));
		Assert.Equal(["VaultManager.exe"], Directory.EnumerateFiles(output).Where(f => !f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)).Select(Path.GetFileName));

		var smoke = new ProcessStartInfo(exe, "--smoke") { UseShellExecute = false, CreateNoWindow = true };
		using Process check = Process.Start(smoke)!;
		using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(1));
		await check.WaitForExitAsync(limit.Token);
		Assert.Equal(0, check.ExitCode);
	}
}
