using Rhino.ApplicationSettings;

namespace Project2.VisibleLines
{
    internal static class UiText
    {
        internal static string Get(string english, string chinese, string japanese) =>
            AppearanceSettings.LanguageIdentifier switch
            {
                2052 => chinese,
                1041 => japanese,
                _ => english
            };
    }
}
