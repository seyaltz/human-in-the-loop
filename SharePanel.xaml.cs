using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DevBreak;

/// <summary>
/// Shown beside the TL;DR: Claude suggests which of the user's Claude Code projects an item belongs to (as a memory
/// note or a skill), the user can edit or untick each, and only Save writes anything.
/// </summary>
public partial class SharePanel : UserControl
{
    private readonly List<(ShareSuggestion Suggestion, CheckBox Pick, TextBox Text)> _rows = [];

    /// <summary>The close button was pressed; the owner removes the panel.</summary>
    public event Action? CloseRequested;

    public SharePanel(string topic, string headline, string item, IReadOnlyList<TldrSource> sources)
    {
        InitializeComponent();
        ItemText.Text = headline;
        Load(topic, item, sources);
    }

    private async void Load(string topic, string item, IReadOnlyList<TldrSource> sources)
    {
        try
        {
            IReadOnlyList<ShareSuggestion> suggestions = await ClaudeProjects.SuggestAsync(topic, item, sources);
            Loading.Visibility = Visibility.Collapsed;
            if (suggestions.Count == 0)
            {
                StatusText.Text = "Claude found no project of yours this is useful for.";
                return;
            }
            foreach (ShareSuggestion s in suggestions) AddRow(s);
            SaveButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            Loading.Visibility = Visibility.Collapsed;
            StatusText.Text = "Could not get suggestions from the claude CLI: " + ex.Message;
        }
    }

    private void AddRow(ShareSuggestion s)
    {
        var pick = new CheckBox { IsChecked = true, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) };
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new Border
        {
            Background = Res<Brush>(s.Kind == "skill" ? "CoralTintBrush" : "VioletTintBrush"), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = s.Kind.ToUpperInvariant(), FontFamily = Res<FontFamily>("MonoFont"), FontSize = 11 },
        });
        title.Children.Add(new TextBlock { Text = s.Project.Folder, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
        pick.Content = title;

        var text = new TextBox
        {
            Text = s.Content, Style = Res<Style>("NotebookBox"), TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
            MinHeight = 90, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalContentAlignment = VerticalAlignment.Top, FontSize = 13,
        };
        AutomationPropertiesHelper(text, $"Content for {s.Project.Folder}");
        var card = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        card.Children.Add(pick);
        card.Children.Add(new TextBlock
        {
            Text = $"{s.Name} - {s.Description}", Style = Res<Style>("Caption"), Margin = new Thickness(26, 0, 0, 6),
        });
        card.Children.Add(text);
        Suggestions.Children.Add(card);
        _rows.Add((s, pick, text));
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var saved = new List<string>();
        try
        {
            foreach ((ShareSuggestion s, CheckBox pick, TextBox text) in _rows.Where(r => r.Pick.IsChecked == true))
            {
                saved.Add(ClaudeProjects.Save(s, text.Text));
                pick.IsEnabled = text.IsReadOnly = true;
                pick.IsChecked = false;
            }
            StatusText.Text = saved.Count == 0 ? "Nothing ticked." : "Saved: " + string.Join("  ·  ", saved);
            if (saved.Count > 0) SaveButton.IsEnabled = _rows.Any(r => r.Pick.IsEnabled);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Saved {saved.Count}, then failed: {ex.Message}";
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    private static void AutomationPropertiesHelper(DependencyObject element, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(element, name);

    private T Res<T>(string key) => (T)FindResource(key);
}
