using System.Security.Cryptography;
using System.Text;

namespace VaultManager.Core.Foundation;

public enum ReadStatus { Ok, Missing, Unreadable }

/// <summary>A failed read is unknown, never an empty answer: writers must refuse to act on it.</summary>
public readonly record struct ReadResult<T>(ReadStatus Status, T? Value, string? Error = null)
{
	public static ReadResult<T> Ok(T value) => new(ReadStatus.Ok, value);
	public static ReadResult<T> Missing() => new(ReadStatus.Missing, default);
	public static ReadResult<T> Unreadable(string error) => new(ReadStatus.Unreadable, default, error);
}

/// <summary>
/// NTFS and APFS ignore case by default, Linux file systems do not: there, two vaults may differ only in case,
/// and treating them as one would put a change into the wrong vault.
/// </summary>
public static class PathRules
{
	public static readonly StringComparison Comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
	public static readonly StringComparer Comparer = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

	public static bool Same(string a, string b) => string.Equals(a, b, Comparison);

	public static string Key(string path) => OperatingSystem.IsLinux() ? path : path.ToUpperInvariant();
}

public static class AtomicFile
{
	/// <summary>Writes beside the target and renames, so a crash or a dropped USB hub cannot leave half a file.</summary>
	public static void WriteAllBytes(string path, byte[] content)
	{
		string directory = Path.GetDirectoryName(path)!;
		Directory.CreateDirectory(directory);
		string temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
		try
		{
			using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
			{
				stream.Write(content, 0, content.Length);
				stream.Flush(flushToDisk: true);
			}

			File.Move(temp, path, overwrite: true);
		}
		finally
		{
			if (File.Exists(temp))
			{
				File.Delete(temp);
			}
		}
	}

	public static void WriteAllText(string path, string text) =>
		WriteAllBytes(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text));

	public static void Copy(string source, string target) => WriteAllBytes(target, SharedFile.ReadAllBytes(source));
}

public static class SharedFile
{
	// Obsidian and sync tools keep these files open; a plain read would fail while they run.
	static FileStream Open(string path) =>
		new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

	public static string ReadAllText(string path)
	{
		using var reader = new StreamReader(Open(path), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
		return reader.ReadToEnd();
	}

	public static byte[] ReadAllBytes(string path)
	{
		using var stream = Open(path);
		using var memory = new MemoryStream();
		stream.CopyTo(memory);
		return memory.ToArray();
	}

	public static string Hash(string path)
	{
		using var stream = Open(path);
		return Convert.ToHexString(SHA256.HashData(ReadToEnd(stream))).ToLowerInvariant();
	}

	public static string Hash(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

	static byte[] ReadToEnd(Stream stream)
	{
		using var memory = new MemoryStream();
		stream.CopyTo(memory);
		return memory.ToArray();
	}
}

public static class Folder
{
	public static void Copy(string source, string target, Func<string, bool>? skipRelative = null)
	{
		Directory.CreateDirectory(target);
		foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
		{
			string relative = Path.GetRelativePath(source, file);
			if (skipRelative?.Invoke(relative) == true)
			{
				continue;
			}

			string destination = Path.Combine(target, relative);
			Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
			File.WriteAllBytes(destination, SharedFile.ReadAllBytes(file));
		}
	}

	public static long Size(string path)
	{
		long total = 0;
		foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
		{
			total += new FileInfo(file).Length;
		}

		return total;
	}

	/// <summary>Clears the read-only flag git and sync tools leave behind, which would otherwise block a delete.</summary>
	public static void Delete(string path)
	{
		if (!Directory.Exists(path))
		{
			return;
		}

		foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
		{
			File.SetAttributes(file, FileAttributes.Normal);
		}

		Directory.Delete(path, recursive: true);
	}
}
