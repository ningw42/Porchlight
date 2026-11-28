using CommunityToolkit.WinUI;
using CommunityToolkit.WinUI.Controls;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Terminal.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;
using Porchlight.Core.Configuration;
using System.Collections.ObjectModel;

namespace Porchlight.App;

public sealed partial class TerminalSettingsPage : Page
{
    readonly ObservableCollection<string> families = [];
    readonly ObservableCollection<string> selectedTags = [];
    readonly string[] installedFonts;
    ITerminalAppearanceTarget Preview => AppearancePreview;
    SettingsWindow.EditorContext? context;
    TerminalAppearance current = null!;
    bool restoring;
    FontFeatureOverrides? features;
    string? probedFamily;
    CancellationTokenSource? probing;
    Dictionary<string, FontFeatureCandidate> candidates = new(StringComparer.Ordinal);
    Popup? featurePopup;
    long offsetSubscription;
    bool sourceReversed;

    public TerminalSettingsPage()
    {
        InitializeComponent();
        installedFonts = CanvasTextFormat.GetSystemFontFamilies().Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var shape in new[] { CursorStyle.Vintage, CursorStyle.Bar, CursorStyle.Underscore, CursorStyle.FilledBox, CursorStyle.EmptyBox })
        {
            var icon = new CursorVisual(shape) { FontSize = 16 };
            var item = new SegmentedItem { Content = icon };
            AutomationProperties.SetName(item, shape.ToString());
            ToolTipService.SetToolTip(item, shape.ToString());
            CursorShapePicker.Items.Add(item);
        }
        TerminalFontPicker.ItemsSource = families;
        FeaturePicker.ItemsSource = selectedTags;
        families.CollectionChanged += (_, _) => FamiliesChanged();
        AppearancePreview.Loaded += (_, _) => ShowInitialAppearance();
        AppearancePreview.CellMeasured += cell =>
        {
            // The samples and icons use the actual cell, including imported absolute/relative dimensions.
            var em = Math.Clamp(TerminalFontSize.Value, 1, 100) * (96d / 72);
            foreach (SegmentedItem item in CursorShapePicker.Items)
                ((CursorVisual)item.Content).UseCell(cell.Width / em, cell.Height / em);
        };
        Unloaded += (_, _) => { probing?.Cancel(); probing = null; probedFamily = null; TrackFeaturePopup(null); };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        context = (SettingsWindow.EditorContext)e.Parameter;
        current = context.Initial;
        AppearancePreview.OwningHwnd = context.Hwnd;
        restoring = true;
        try
        {
            var font = current.Font;
            if (current.FamilyOverride is { } chosen)
                foreach (var family in FontFamilyList.Parse(chosen)) families.Add(family);
            TerminalFontSize.Value = font.Size;
            features = new(context.LaunchFont.Features, font.Features);
            RestoreFeatureTokens();
            ResetFeaturesButton.IsEnabled = features.CanReset;
            CursorShapePicker.SelectedItem = CursorShapePicker.Items.Cast<SegmentedItem>()
                .First(item => ((CursorVisual)item.Content).Shape == current.Cursor);
            CursorBlinkSwitch.IsOn = current.BlinkEnabled;
        }
        finally { restoring = false; }
        ShowInitialAppearance();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => ClosePreview();

    internal void ClosePreview()
    {
        context = null;
        AppearancePreview.Close();
    }

    void ShowInitialAppearance()
    {
        if (restoring || context == null || !AppearancePreview.IsLoaded) return;
        // The live target already has this appearance. Opening Settings is not an edit.
        Preview.ApplyAppearance(current);
        UpdateFamilies();
    }

    void Apply(TerminalAppearance next)
    {
        if (next == current || context == null) return;
        current = next;
        context.Live.ApplyAppearance(next);
        if (AppearancePreview.IsLoaded) Preview.ApplyAppearance(next);
    }

    void CursorChanged(object sender, RoutedEventArgs e)
    {
        if (restoring || context == null || CursorShapePicker.SelectedItem is not SegmentedItem { Content: CursorVisual icon }) return;
        Apply(current with { Cursor = icon.Shape });
    }

    void BlinkToggled(object sender, RoutedEventArgs e)
    {
        if (!restoring && context != null && CursorBlinkSwitch.IsOn != current.BlinkEnabled)
            Apply(current with { Blink = CursorBlinkSwitch.IsOn });
    }

    void Suggest(string query)
    {
        var prefix = query.Trim();
        TerminalFontPicker.SuggestedItemsSource = installedFonts
            .Where(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !families.Contains(name))
            .Take(12).ToArray();
    }

    void FontSizeChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        // A cleared NumberBox reports NaN. Keep the last usable size.
        if (!double.IsFinite(args.NewValue) || args.NewValue <= 0)
        {
            sender.Value = args.OldValue;
            return;
        }
        if (!restoring && context != null)
            Apply(current with { Font = current.Font with { Size = (float)args.NewValue } });
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

    void FamiliesChanged()
    {
        if (restoring || context == null) return;
        Apply(current with { FamilyOverride = families.Count == 0 ? null : FontFamilyList.Format(families) });
        UpdateFamilies();
    }

    void UpdateFamilies()
    {
        if (context == null || !AppearancePreview.IsLoaded) return;
        ResetFontsButton.IsEnabled = families.Count != 0;
        Suggest("");
        // For inherited settings an earlier listed family may not be installed.
        var primary = families.FirstOrDefault() ?? FontFamilyList.Parse(context.LaunchFont.Face)
            .FirstOrDefault(name => Array.BinarySearch(installedFonts, name, StringComparer.OrdinalIgnoreCase) >= 0) ?? "";
        if (probedFamily == primary) return; // Editing a fallback changes the preview, not the candidates.
        probedFamily = primary;
        var metrics = CursorFont.ForFamily(primary, current.Font.Weight);
        foreach (SegmentedItem item in CursorShapePicker.Items) ((CursorVisual)item.Content).UseFont(metrics);
        ProbeFeatures(primary);
    }

    async void ProbeFeatures(string family)
    {
        probing?.Cancel();
        using var request = new CancellationTokenSource();
        probing = request;
        candidates.Clear();
        FeaturePicker.Text = "";
        SuggestFeatures("");
        FeatureStatus.Visibility = Visibility.Collapsed;
        try
        {
            if (family.Length == 0) { ShowFeatureStatus("No installed font in the list."); return; }
            var result = await Task.Run(() => FontFeatureProbe.Discover(family, request.Token), request.Token);
            if (request.IsCancellationRequested) return;
            if (result.Length == 0) { ShowFeatureStatus($"No individual OpenType features found in {family}."); return; }
            candidates = result.ToDictionary(c => c.Tag, StringComparer.Ordinal);
            SuggestFeatures(FeaturePicker.Text);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (request.IsCancellationRequested) return;
            ShowFeatureStatus($"Couldn’t read OpenType features for {family}.");
        }
        finally { if (probing == request) probing = null; }
    }

    void ShowFeatureStatus(string text)
    {
        FeatureStatus.Text = text;
        FeatureStatus.Visibility = Visibility.Visible;
    }

    void SuggestFeatures(string? query)
    {
        var prefix = query?.Trim() ?? "";
        var matches = candidates.Values
            .Where(c => c.Tag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !selectedTags.Contains(c.Tag))
            .OrderBy(c => c.Sample is null)
            .ThenBy(c => c.Tag, StringComparer.Ordinal)
            .Select(c => c.Tag).ToArray();
        // WinUI's AutoSuggestBox itself reverses the items when it opens above the editor.
        sourceReversed = featurePopup is { VerticalOffset: < 0 };
        if (sourceReversed) Array.Reverse(matches);
        FeaturePicker.SuggestedItemsSource = matches;
        if (sourceReversed && matches.Length > 0) DispatcherQueue.TryEnqueue(ScrollToFirstFeature);
    }

    void ScrollToFirstFeature()
    {
        if (featurePopup is { IsOpen: true, VerticalOffset: < 0, Child: FrameworkElement root } &&
            root.FindDescendant<ListView>() is { Items.Count: > 0 } list)
            list.ScrollIntoView(list.Items[0], ScrollIntoViewAlignment.Leading);
    }

    void TrackFeaturePopup(Popup? popup)
    {
        if (featurePopup == popup) return;
        featurePopup?.UnregisterPropertyChangedCallback(Popup.VerticalOffsetProperty, offsetSubscription);
        featurePopup = popup;
        if (popup != null)
            offsetSubscription = popup.RegisterPropertyChangedCallback(Popup.VerticalOffsetProperty, (_, _) =>
            {
                // Re-publish after WinUI finishes positioning the popup, not during layout.
                if (sourceReversed != (popup.VerticalOffset < 0)) DispatcherQueue.TryEnqueue(() =>
                {
                    if (featurePopup == popup && popup.IsOpen && sourceReversed != (popup.VerticalOffset < 0))
                        SuggestFeatures(FeaturePicker.Text);
                });
            });
    }

    void FeaturePickerFocused(object sender, RoutedEventArgs e)
    {
        // Open only on entry. The token control can raise GotFocus again on Escape key-up.
        if (e.OriginalSource is TextBox editor && editor.FindAscendant<AutoSuggestBox>() is { } box &&
            box.FindDescendant<Popup>() is { } popup && popup != featurePopup)
        {
            TrackFeaturePopup(popup);
            box.IsSuggestionListOpen = box.Items.Count != 0;
        }
    }

    void FeaturePickerLostFocus(object sender, RoutedEventArgs e) => TrackFeaturePopup(null);

    void FeatureQueryChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.SuggestionChosen) SuggestFeatures(sender.Text);
    }

    void FeatureAdding(TokenizingTextBox sender, TokenItemAddingEventArgs args)
    {
        // Both typed tags and chosen suggestions are strings and take this same validation path.
        var input = args.TokenText;
        var candidate = candidates.GetValueOrDefault(input) ?? candidates.Values.FirstOrDefault(c =>
            c.Tag.Equals(input, StringComparison.OrdinalIgnoreCase));
        if (candidate == null || selectedTags.Contains(candidate.Tag)) args.Cancel = true;
        else args.Item = candidate.Tag;
    }

    void FeatureAdded(TokenizingTextBox sender, object item) => SetFeature((string)item, true);
    void FeatureRemoved(TokenizingTextBox sender, object item) => SetFeature((string)item, false);

    void SetFeature(string tag, bool enabled)
    {
        if (restoring) return;
        features!.SetEnabled(tag, enabled);
        SuggestFeatures("");
        UpdateFeatures();
    }

    void FeatureSuggestionChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (args.NewValue is not string tag || !candidates.TryGetValue(tag, out var candidate)) return;
        var row = (Grid)sender;
        var comparison = (FeatureComparison)row.Children[1];
        comparison.SetSample(probedFamily!, tag, candidate.Sample, candidate.Italic);
        AutomationProperties.SetAutomationId(row, "FeatureSuggestion-" + tag);
        AutomationProperties.SetName(row, tag);
        var help = candidate.Sample == null ? "No matching sample found" : $"Off and on: {candidate.Sample}";
        AutomationProperties.SetHelpText(row, help);
        ToolTipService.SetToolTip(row, help);
    }

    void LigaturesToggled(object sender, RoutedEventArgs e)
    {
        // Initializing or resetting the switch must not normalize a mixed launch map.
        if (restoring || features == null || CodingLigaturesSwitch.IsOn == features.LigaturesEnabled) return;
        features.SetLigaturesEnabled(CodingLigaturesSwitch.IsOn);
        UpdateFeatures();
    }

    void UpdateFeatures()
    {
        Apply(current with { Font = current.Font with { Features = new(features!.Values) } });
        ResetFeaturesButton.IsEnabled = features.CanReset;
    }

    void ResetFeatures(object sender, RoutedEventArgs e)
    {
        restoring = true;
        try { features!.Reset(); RestoreFeatureTokens(); }
        finally { restoring = false; }
        SuggestFeatures("");
        UpdateFeatures();
    }

    void RestoreFeatureTokens()
    {
        // Don't issue a collection Reset: the token control interleaves its own editable items.
        while (selectedTags.Count > 0) selectedTags.RemoveAt(selectedTags.Count - 1);
        foreach (var tag in features!.EnabledIndividualTags) selectedTags.Add(tag);
        CodingLigaturesSwitch.IsOn = features.LigaturesEnabled;
        FeaturePicker.Text = "";
    }

    async void ResetFonts(object sender, RoutedEventArgs e) => await TerminalFontPicker.ClearAsync();
}
