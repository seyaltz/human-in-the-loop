using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

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
        content.Children.Add(new TextBlock { Text = category.Name, FontWeight = FontWeights.SemiBold });
        content.Children.Add(new TextBlock
        {
            Text = string.Join(", ", category.Topics), TextWrapping = TextWrapping.Wrap, Opacity = 0.65, FontSize = 12,
        });
        TopicList.Children.Add(new CheckBox
        {
            Content = content, Tag = category, IsChecked = isChecked, Margin = new Thickness(0, 0, 0, 10),
            VerticalContentAlignment = VerticalAlignment.Top,
        });
    }

    private void UpdateEmpty(string text)
    {
        EmptyText.Text = text;
        EmptyText.Visibility = TopicList.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private IEnumerable<CheckBox> Rows => TopicList.Children.OfType<CheckBox>();

    private void WishBox_TextChanged(object sender, TextChangedEventArgs e) =>
        AddButton.IsEnabled = WishBox.Text.Trim().Length > 0 && WishBox.IsEnabled;

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
