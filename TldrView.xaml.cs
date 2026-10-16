using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace DevBreak;

/// <summary>The TL;DR view: a briefing per tab, fetched from the web by Claude, with its sources, Refresh and Discuss.</summary>
public partial class TldrView : UserControl
{
    // each section's accent, in the order Tldr writes them
    private static readonly string[] Accents = ["VioletBrush", "CoralBrush", "MarkerBrush", "VioletDarkBrush", "InkBrush"];

    private readonly Dictionary<string, ChatWindow> _chats = [];
    private TldrContent? _content;

    // bumped on every tab change or refresh, so a slow reply for an old selection is dropped
    private int _version;

    public TldrView()
    {
        InitializeComponent();
        AddChips();
        Show(null, null);
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

    private void Discuss_Click(object sender, RoutedEventArgs e)
    {
        if (_content == null || Tabs.SelectedItem == null) return;
        string topic = Current.Name;
        if (!_chats.TryGetValue(topic, out ChatWindow? chat))
        {
            chat = new ChatWindow(topic + " - TL;DR", _content);
            chat.Closed += (_, _) => _chats.Remove(topic);
            Window owner = Window.GetWindow(this);
            chat.Left = Math.Max(SystemParameters.WorkArea.Left + 16, owner.Left - chat.Width - 12);
            chat.Top = owner.Top;
            _chats[topic] = chat;
            chat.Show();
        }
        chat.Activate();
    }

    private async void Load(bool refresh)
    {
        int version = ++_version;
        Category category = Current;
        Show(null, null);
        TldrTitle.Text = category.Name;
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
        _content = content;
        Loading.Visibility = Visibility.Collapsed;
        RefreshButton.IsEnabled = Tabs.SelectedItem != null;
        DiscussButton.IsEnabled = content != null;
        if (Tabs.SelectedItem == null) TldrTitle.Text = "Pick a topic";
        if (content == null)
        {
            WrittenText.Text = "";
            TldrBox.Document = CodeRenderer.Render(error ?? "");
            return;
        }
        int sources = content.Sources?.Count ?? 0;
        WrittenText.Text = $"UPDATED {content.Written:g} · {sources} SOURCE{(sources == 1 ? "" : "S")}";

        FlowDocument document = CodeRenderer.Render("");
        for (int i = 0; i < content.Sections.Count; i++)
        {
            Section block = Block(i + 1, content.Sections[i].Title, Accents[i % Accents.Length]);
            MoveBlocks(CodeRenderer.Render(content.Sections[i].Text), block);
            document.Blocks.Add(block);
        }
        if (sources > 0) document.Blocks.Add(SourcesBlock(content.Sources!));
        TldrBox.Document = document;
    }

    /// <summary>A section: an accent bar on the left, a numbered label and the section's title.</summary>
    private Section Block(int number, string title, string accent)
    {
        var block = new Section
        {
            BorderBrush = (Brush)FindResource(accent),
            BorderThickness = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(14, 2, 6, 4),
            Margin = new Thickness(0, 0, 0, 16),
        };
        var heading = new Paragraph { Margin = new Thickness(0, 0, 0, 6) };
        heading.Inlines.Add(new Run($"{number:00}  ")
        {
            FontFamily = (FontFamily)FindResource("MonoFont"), FontSize = 13, Foreground = (Brush)FindResource("InkSoftBrush"),
        });
        heading.Inlines.Add(new Run(title)
        {
            FontFamily = (FontFamily)FindResource("DisplayFont"), FontSize = 18, FontWeight = FontWeights.ExtraBold,
        });
        block.Blocks.Add(heading);
        return block;
    }

    /// <summary>The pages the TL;DR was written from, each a link that opens in the browser.</summary>
    private Section SourcesBlock(IReadOnlyList<TldrSource> sources)
    {
        Section block = Block(Accents.Length + 1, "Sources", "LineBrush");
        foreach (TldrSource source in sources)
        {
            var link = new Hyperlink(new Run(source.Title.Length > 0 ? source.Title : source.Url))
            {
                NavigateUri = new Uri(source.Url), ToolTip = source.Url, Foreground = (Brush)FindResource("VioletBrush"),
            };
            link.Click += (_, _) => Process.Start(new ProcessStartInfo(source.Url) { UseShellExecute = true });
            var line = new Paragraph { Margin = new Thickness(0, 0, 0, 4) };
            line.Inlines.Add(new Run("↗  ") { Foreground = (Brush)FindResource("InkSoftBrush") });
            line.Inlines.Add(link);
            line.Inlines.Add(new Run("   " + new Uri(source.Url).Host)
            {
                FontFamily = (FontFamily)FindResource("MonoFont"), FontSize = 12, Foreground = (Brush)FindResource("InkSoftBrush"),
            });
            block.Blocks.Add(line);
        }
        return block;
    }

    private static void MoveBlocks(FlowDocument from, Section to)
    {
        foreach (Block b in from.Blocks.ToList())
        {
            from.Blocks.Remove(b);
            to.Blocks.Add(b);
        }
    }
}
