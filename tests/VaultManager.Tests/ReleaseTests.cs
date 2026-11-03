using System.Diagnostics;
using System.Text.RegularExpressions;

namespace VaultManager.Tests;

/// <summary>
/// The release runs on GitHub, where a mistake costs a broken or missing download. These tests read the
/// workflow and the release script as plain text and run the script, so the mistake shows up here first.
/// </summary>
public sealed class ReleaseTests
{
	static string RepoRoot()
	{
		string? dir = AppContext.BaseDirectory;
		while (dir is not null && !File.Exists(Path.Combine(dir, "VERSION")))
		{
			dir = Path.GetDirectoryName(dir);
		}

		return dir ?? throw new InvalidOperationException("VERSION not found");
	}

	static string Text(string relative) => File.ReadAllText(Path.Combine(RepoRoot(), relative));

	static string Version() => Text("VERSION").Trim();

	[Fact]
	public void The_changelog_describes_the_version_that_a_push_to_product_would_release()
	{
		Match section = Regex.Match(Text("CHANGELOG.md"), @"(?ms)^## " + Regex.Escape(Version()) + @"[ \t]*\r?\n(.*?)(?=^## |\z)");
		Assert.True(section.Success, $"CHANGELOG.md has no '## {Version()}' section.");
		Assert.False(string.IsNullOrWhiteSpace(section.Groups[1].Value));
	}

	[Fact]
	public void The_release_runs_on_a_push_to_product_and_never_twice_for_one_version()
	{
		string release = Text(".github/workflows/release.yml");

		Assert.Matches(@"on:\s*\n\s*push:\s*\n\s*branches:\s*\[""product""\]", release);
		Assert.DoesNotContain("tags:", release);
		Assert.Contains("gh release view", release);
		Assert.Contains("steps.released.outputs.exists != 'true'", release);
		Assert.Contains("cancel-in-progress: false", release);
		Assert.Contains("release-info.ps1", release);
		Assert.Contains("--notes-file release-notes.md", release);
		Assert.Contains("--smoke", release);
		Assert.Contains("needs: build", release);
		Assert.Contains("contents: write", release);
	}

	[Fact]
	public void The_release_makes_its_own_tag_on_the_pushed_commit()
	{
		string release = Text(".github/workflows/release.yml");
		Assert.Contains("gh release create \"${{ steps.info.outputs.tag }}\"", release);
		Assert.Contains("--target \"${{ github.sha }}\"", release);
		Assert.Contains("--latest", release);
		Assert.DoesNotContain("git tag", release);
		Assert.DoesNotContain("git push", release);
		Assert.Contains("if: github.ref == 'refs/heads/product'", release);
	}

	[Fact]
	public void Tests_run_before_anything_is_published()
	{
		string release = Text(".github/workflows/release.yml");
		Assert.True(release.IndexOf("dotnet test", StringComparison.Ordinal) < release.IndexOf("dotnet publish", StringComparison.Ordinal));
		Assert.True(release.IndexOf("--smoke", StringComparison.Ordinal) < release.IndexOf("gh release create", StringComparison.Ordinal));
	}

	[Fact]
	public void A_push_to_product_is_checked_once_and_pull_requests_by_ci()
	{
		string ci = Text(".github/workflows/ci.yml");
		Assert.Contains("pull_request:", ci);
		Assert.DoesNotMatch(@"(?m)^\s*push:", ci);
	}

	[Fact]
	public void The_published_exe_is_named_after_the_tag_with_a_checksum_beside_it()
	{
		string release = Text(".github/workflows/release.yml");
		Assert.Contains("VaultManager-${{ steps.info.outputs.tag }}-win-x64.exe", release);
		Assert.Contains("Get-FileHash", release);
		Assert.Contains(".sha256", release);
	}

	static (int Code, string Output) RunScript(string root, string notes)
	{
		var start = new ProcessStartInfo("powershell.exe")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			WorkingDirectory = root,
		};
		foreach (string arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(RepoRoot(), ".github", "scripts", "release-info.ps1"), "-Root", root, "-NotesPath", notes })
		{
			start.ArgumentList.Add(arg);
		}

		start.Environment.Remove("GITHUB_OUTPUT");
		using Process process = Process.Start(start)!;
		string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
		process.WaitForExit();
		return (process.ExitCode, output);
	}

	[Fact]
	public void The_release_script_writes_this_versions_notes_and_nothing_else()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using var f = new Fixture();
		File.WriteAllText(Path.Combine(f.Root, "VERSION"), "1.2.3.4\n");
		File.WriteAllText(Path.Combine(f.Root, "CHANGELOG.md"), "# Changelog\n\n## 1.2.3.4\n\n- Новое: fixed a thing.\n\n## 1.2.3.3\n\n- Older.\n");

		(int code, string output) = RunScript(f.Root, "notes.md");

		Assert.True(code == 0, output);
		string notes = File.ReadAllText(Path.Combine(f.Root, "notes.md"));
		Assert.Equal("- Новое: fixed a thing.\n", notes);
	}

	[Theory]
	[InlineData("1.2.3.4\n", "# Changelog\n\n## 1.2.3.3\n\n- Older.\n", "no section for 1.2.3.4")]
	[InlineData("1.2.3.4\n", "# Changelog\n\n## 1.2.3.4\n\n## 1.2.3.3\n\n- Older.\n", "no section for 1.2.3.4")]
	[InlineData("1.2.3.10\n", "# Changelog\n\n## 1.2.3.10\n\n- x\n", "single-digit")]
	public void The_release_script_stops_a_release_it_cannot_describe(string version, string changelog, string expected)
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using var f = new Fixture();
		File.WriteAllText(Path.Combine(f.Root, "VERSION"), version);
		File.WriteAllText(Path.Combine(f.Root, "CHANGELOG.md"), changelog);

		(int code, string output) = RunScript(f.Root, "notes.md");

		Assert.NotEqual(0, code);
		Assert.Contains(expected, output);
		Assert.False(File.Exists(Path.Combine(f.Root, "notes.md")));
	}

	[Fact]
	public void The_real_changelog_passes_the_release_script()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		using var f = new Fixture();
		(int code, string output) = RunScript(RepoRoot(), Path.Combine(f.Root, "notes.md"));
		Assert.True(code == 0, output);
		Assert.Contains("Releases from product", File.ReadAllText(Path.Combine(f.Root, "notes.md")));
	}
}
