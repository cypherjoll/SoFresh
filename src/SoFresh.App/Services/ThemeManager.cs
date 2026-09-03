using System.Windows;

namespace SoFresh.App.Services;

public static class ThemeManager
{
    private const string DarkPalette = "Themes/Colors.Dark.xaml";
    private const string LightPalette = "Themes/Colors.Light.xaml";

    public static bool IsDarkTheme { get; private set; } = true;

    public static void Apply(bool useDarkTheme)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var paletteIndex = -1;
        for (var index = 0; index < dictionaries.Count; index++)
        {
            var source = dictionaries[index].Source?.OriginalString;
            if (source?.EndsWith("Colors.Dark.xaml", StringComparison.OrdinalIgnoreCase) == true
                || source?.EndsWith("Colors.Light.xaml", StringComparison.OrdinalIgnoreCase) == true)
            {
                paletteIndex = index;
                break;
            }
        }

        if (paletteIndex < 0)
        {
            throw new InvalidOperationException("The application color dictionary could not be found.");
        }

        dictionaries[paletteIndex] = new ResourceDictionary
        {
            Source = new Uri(useDarkTheme ? DarkPalette : LightPalette, UriKind.Relative)
        };
        IsDarkTheme = useDarkTheme;
    }
}
