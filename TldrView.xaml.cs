using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace DevBreak;

/// <summary>The TL;DR view: a briefing per tab, fetched from the web by Claude, with a Refresh button.</summary>
public partial class TldrView : UserControl
{
    // bumped on every tab change or refresh, so a slow reply for an old selection is dropped
    private int _version;

    public TldrView()
    {
        InitializeComponent();
        AddChips();
    }

    private void AddChips()
    {
        foreach (Category category in LearnPath.Courses)
        {
            Tabs.Items.Add(new ListBoxItem { Content = category.Name, Tag = category });
        }
    }

    /// <summary>Rebuilds the chips after the topics change; the first one opens again when the view is shown.</summary>
    public void Reload()
    {
        bool wasStarted = Tabs.SelectedIndex >= 0;
        ++_version;
        Tabs.SelectionChanged -= Tabs_SelectionChanged;
        Tabs.Items.Clear();
        AddChips();
        Tabs.SelectionChanged += Tabs_SelectionChanged;
        Show(null, null);
        if (wasStarted && IsVisible) Start();
    }

    /// <summary>Opens the first tab the first time the view is shown, so a quiz popup never writes a TL;DR.</summary>
    public void Start()
    {
        if (Tabs.SelectedIndex < 0) Tabs.SelectedIndex = 0;
    }

    private Category Current => (Category)((ListBoxItem)Tabs.SelectedItem).Tag;

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != Tabs || Tabs.SelectedItem == null) return;
        Load(refresh: false);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (Tabs.SelectedItem != null) Load(refresh: true);
    }

    private async void Load(bool refresh)
    {
        int version = ++_version;
        Category category = Current;
        Show(null, null);
        TldrTitle.Text = $"{category.Name} - TL;DR";
        LoadingText.Text = $"Claude is reading the latest on {category.Name} online...";
        Loading.Visibility = Visibility.Visible;
        RefreshButton.IsEnabled = false;
        try
        {
            TldrContent content = await Tldr.GetAsync(category, refresh);
            if (version != _version) return;
            Show(content, null);
        }
        catch (Exception ex)
        {
            if (version != _version) return;
            Show(null, "Could not get the TL;DR from the claude CLI:\n\n" + ex.Message);
        }
    }

    private void Show(TldrContent? content, string? error)
    {
        Loading.Visibility = Visibility.Collapsed;
        RefreshButton.IsEnabled = Tabs.SelectedItem != null;
        WrittenText.Text = content == null ? "" : $"Updated {content.Written:g} from the web";
        if (content == null)
        {
            TldrBox.Document = CodeRenderer.Render(error ?? "");
            if (Tabs.SelectedItem == null) TldrTitle.Text = "";
            return;
        }
        FlowDocument document = CodeRenderer.Render("");
        foreach (TldrSection section in content.Sections)
        {
            document.Blocks.Add(new Paragraph(new Run(section.Title))
            {
                FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4),
            });
            CodeRenderer.Append(document, section.Text);
        }
        TldrBox.Document = document;
    }
}
