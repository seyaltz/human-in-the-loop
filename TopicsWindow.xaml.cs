using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;

namespace DevBreak;

/// <summary>
/// Picks the topic tabs. On first run it lists topics Claude suggests from the user's own Claude Code chats;
/// from the gear menu it lists the current tabs. Either way the user can add a topic of their own with AI help.
/// </summary>
public partial class TopicsWindow : Window
{
    public TopicsWindow(bool isFirstRun)
    {
        InitializeComponent();
        Greeting.Text = isFirstRun ? "hi! let's set you up" : "your topics";
        if (isFirstRun)
        {
            IntroText.Text = "Suggested from your own Claude Code chats of the last 7 days - general tech topics only, "
                + "nothing personal. Tick the ones you want, or add your own below.";
            Loaded += async (_, _) => await LoadSuggestionsAsync();
        }
        else
        {
            IntroText.Text = "Untick a topic to remove its tab, or add a new one below.";
            Loading.Visibility = Visibility.Collapsed;
            foreach (Category category in Topics.Chosen())
            {
                AddRow(category, isChecked: true);
            }
            UpdateEmpty("No topics yet. Add the topics you want below.");
        }
    }

    private async System.Threading.Tasks.Task LoadSuggestionsAsync()
    {
        LoadingText.Text = "Reading your Claude chats to suggest topics...";
        try
        {
            IReadOnlyList<Category> suggested = await Topics.SuggestAsync();
            foreach (Category category in suggested)
            {
                AddRow(category, isChecked: false);
            }
            UpdateEmpty("No Claude Code chats found yet, so there is nothing to suggest. Add the topics you want below.");
        }
        catch (Exception ex)
        {
            UpdateEmpty("Could not get suggestions from the claude CLI:\n\n" + ex.Message);
        }
        Loading.Visibility = Visibility.Collapsed;
    }

    private void AddRow(Category category, bool isChecked)
    {
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = category.Name, FontWeight = FontWeights.Bold, FontSize = 15.5, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock
        {
            Text = string.Join(", ", category.Topics), TextWrapping = TextWrapping.Wrap, FontSize = 12.5,
            Margin = new Thickness(0, 2, 0, 0), Foreground = (Brush)FindResource("InkSoftBrush"),
        });
        var row = new CheckBox { Content = content, Tag = category, IsChecked = isChecked, Style = (Style)FindResource("TopicCard") };
        AutomationProperties.SetName(row, category.Name);
        row.Checked += (_, _) => UpdateCount();
        row.Unchecked += (_, _) => UpdateCount();
        TopicList.Children.Add(row);
        UpdateCount();
    }

    /// <summary>The handwritten "3 topics picked" next to Save.</summary>
    private void UpdateCount()
    {
        int picked = Rows.Count(r => r.IsChecked == true);
        CountText.Text = picked == 0 ? "" : picked == 1 ? "1 topic picked" : $"{picked} topics picked";
    }

    private void UpdateEmpty(string text)
    {
        EmptyText.Text = text;
        EmptyText.Visibility = TopicList.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private IEnumerable<CheckBox> Rows => TopicList.Children.OfType<CheckBox>();

    private void WishBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        AddButton.IsEnabled = WishBox.Text.Trim().Length > 0 && WishBox.IsEnabled;
        WishPlaceholder.Visibility = WishBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void WishBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && AddButton.IsEnabled) Add_Click(sender, e);
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        string wish = WishBox.Text;
        WishBox.IsEnabled = AddButton.IsEnabled = false;
        AddHint.Text = "Claude is shaping your topic...";
        try
        {
            Category category = await Topics.ShapeAsync(wish);
            CheckBox? same = Rows.FirstOrDefault(r => ((Category)r.Tag).Name.Equals(category.Name, StringComparison.OrdinalIgnoreCase));
            if (same != null) TopicList.Children.Remove(same);
            AddRow(category, isChecked: true);
            UpdateEmpty("");
            WishBox.Text = "";
            AddHint.Text = $"Added \"{category.Name}\". Add another, or Save.";
        }
        catch (Exception ex)
        {
            AddHint.Text = "Could not shape the topic with the claude CLI: " + ex.Message;
        }
        WishBox.IsEnabled = true;
        WishBox_TextChanged(sender, null!);
        WishBox.Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Topics.Save(Rows.Where(r => r.IsChecked == true).Select(r => (Category)r.Tag));
        DialogResult = true;
    }
}
