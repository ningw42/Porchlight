using CommunityToolkit.WinUI.Controls;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System.Collections.ObjectModel;

namespace Porchlight.App;

public sealed partial class TerminalSettingsPage : Page
{
    readonly ObservableCollection<string> families = [];
    readonly string[] installedFonts;
    string launchFace = "";

    public TerminalSettingsPage()
    {
        InitializeComponent();
        installedFonts = CanvasTextFormat.GetSystemFontFamilies().Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        TerminalFontPicker.ItemsSource = families;
        families.CollectionChanged += (_, _) => UpdatePreview();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        launchFace = (string)e.Parameter;
        UpdatePreview();
    }

    void Suggest(string query)
    {
        var prefix = query.Trim();
        TerminalFontPicker.SuggestedItemsSource = installedFonts
            .Where(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !families.Contains(name))
            .Take(12).ToArray();
    }

    void FontQueryChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput) Suggest(sender.Text);
    }

    void FontAdding(TokenizingTextBox sender, TokenItemAddingEventArgs args)
    {
        var name = installedFonts.FirstOrDefault(name => name.Equals(args.TokenText.Trim(), StringComparison.OrdinalIgnoreCase));
        if (name == null || families.Contains(name)) args.Cancel = true;
        else args.Item = name;
    }

    void UpdatePreview()
    {
        // The launch string can contain quoted family names with commas. Leave it intact.
        var face = families.Count == 0 ? launchFace : string.Join(", ", families);
        AppearancePreview.FontFamily = new FontFamily(face);
        ResetFontsButton.IsEnabled = families.Count != 0;
        Suggest("");
    }

    async void ResetFonts(object sender, RoutedEventArgs e) => await TerminalFontPicker.ClearAsync();
}
