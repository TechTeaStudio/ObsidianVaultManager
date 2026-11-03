using VaultManager.App.Localization;
using VaultManager.Core.Obsidian;

namespace VaultManager.Tests.App;

public sealed class PlatformStringsTests
{
	[Fact]
	public void Every_kind_of_Obsidian_install_has_words_in_both_languages()
	{
		foreach (ObsidianInstall install in Enum.GetValues<ObsidianInstall>())
		{
			Assert.False(string.IsNullOrEmpty(Texts.Find(EnumTexts.KeyOf(install), AppLanguage.English)), install.ToString());
			Assert.False(string.IsNullOrEmpty(Texts.Find(EnumTexts.KeyOf(install), AppLanguage.Russian)), install.ToString());
		}
	}
}
