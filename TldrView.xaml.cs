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

    // a topic's discussion is kept while its panel is swapped for another, so the conversation comes back
    private readonly Dictionary<string, ChatPanel> _chats = [];

    // what each topic shows beside the TL;DR: its discussion or a save-to-Claude panel
    private readonly Dictionary<string, UserControl> _panels = [];
    private TldrContent? _content;

    /// <summary>The panel to show beside the TL;DR changed: opened, closed, or another topic picked (null = none).</summary>
    public event Action<UserControl?>? ChatChanged;

    /// <summary>The open side panel of the topic on screen, if any.</summary>
    public UserControl? CurrentChat =>
        Tabs.SelectedItem != null && _panels.TryGetValue(Current.Name, out UserControl? panel) ? panel : null;

    // bumped on every tab change or refresh, so a slow reply for an old selection is dropped
    private int _version;

    public TldrView()
    {
        InitializeComponent();
        AddChips();
        AddSectionTabs();
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
        ChatChanged?.Invoke(CurrentChat);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (Tabs.SelectedItem != null) Load(refresh: true);
    }

    private void Discuss_Click(object sender, RoutedEventArgs e)
    {
        if (_content == null || Tabs.SelectedItem == null) return;
        string topic = Current.Name;
        if (!_chats.TryGetValue(topic, out ChatPanel? chat))
        {
            ChatPanel created = new(topic + " - TL;DR", _content);
            created.CloseRequested += () =>
            {
                _chats.Remove(topic);
                ClosePanel(topic, created);
            };
            _chats[topic] = chat = created;
        }
        _panels[topic] = chat;
        ChatChanged?.Invoke(chat);
        chat.FocusInput();
    }

    /// <summary>Opens the save-to-Claude panel for one item: Claude suggests which of the user's projects it belongs to.</summary>
    private void Share(string headline, string item)
    {
        if (_content == null || Tabs.SelectedItem == null) return;
        string topic = Current.Name;
        var share = new SharePanel(topic, headline, item, _content.Sources ?? []);
        share.CloseRequested += () => ClosePanel(topic, share);
        _panels[topic] = share;
        ChatChanged?.Invoke(share);
    }

    private void ClosePanel(string topic, UserControl panel)
    {
        if (!_panels.TryGetValue(topic, out UserControl? open) || open != panel) return;
        _panels.Remove(topic);
        if (Tabs.SelectedItem != null && Current.Name == topic) ChatChanged?.Invoke(null);
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

    private static readonly string[] SectionLabels =
        ["Latest releases", "Hot topics", "Quirks", "Senior must-know", "Optimization", "Sources"];

    // the explanation lines of an item, as Tldr asks for them
    private static readonly string[] FieldLabels = ["Context", "Why", "Good for", "For you"];

    // the open sub-tab; stays the same when another topic is picked
    private int _section;
    private string? _error;

    private void AddSectionTabs()
    {
        for (int i = 0; i < SectionLabels.Length; i++)
        {
            var tab = new RadioButton
            {
                Content = SectionLabels[i], GroupName = "TldrSection", Tag = i, IsChecked = i == _section,
                Style = (Style)FindResource("ModeTab"), Margin = new Thickness(i == 0 ? 0 : 4, 0, 0, 0),
            };
            tab.Checked += (s, _) =>
            {
                _section = (int)((RadioButton)s).Tag;
                ShowSection();
            };
            SectionTabs.Children.Add(tab);
        }
    }

    private void Show(TldrContent? content, string? error)
    {
        _content = content;
        _error = error;
        Loading.Visibility = Visibility.Collapsed;
        RefreshButton.IsEnabled = Tabs.SelectedItem != null;
        DiscussButton.IsEnabled = content != null;
        if (Tabs.SelectedItem == null) TldrTitle.Text = "Pick a topic";
        int sources = content?.Sources?.Count ?? 0;
        WrittenText.Text = content == null ? "" : $"UPDATED {content.Written:g} · {sources} SOURCE{(sources == 1 ? "" : "S")}";
        ((RadioButton)SectionTabs.Children[^1]).Content = sources > 0 ? $"Sources ({sources})" : "Sources";
        ShowSection();
    }

    /// <summary>Shows the open sub-tab of the TL;DR on screen: one section, or the sources.</summary>
    private void ShowSection()
    {
        if (_content == null)
        {
            TldrBox.Document = CodeRenderer.Render(_error ?? "");
            return;
        }
        FlowDocument document = CodeRenderer.Render("");
        if (_section < _content.Sections.Count) RenderSection(document, _section);
        else RenderSources(document, _content.Sources ?? []);
        TldrBox.Document = document;
        TldrBox.ScrollToHome();
    }

    /// <summary>A section: its items, each a headline with its Context / Why / Good for / For you lines.</summary>
    private void RenderSection(FlowDocument document, int index)
    {
        TldrSection section = _content!.Sections[index];
        document.Blocks.Add(Eyebrow($"{index + 1:00} · {section.Title.ToUpperInvariant()}"));
        var items = Items(section.Text);
        if (items.Count == 0)
        {
            // written without item headlines: show it as it is
            MoveBlocks(CodeRenderer.Render(section.Text), document.Blocks);
            return;
        }
        Brush accent = Res<Brush>(Accents[index % Accents.Length]);
        foreach ((string headline, List<(string Label, string Text)> fields) in items)
        {
            var block = new Section
            {
                BorderBrush = accent, BorderThickness = new Thickness(4, 0, 0, 0),
                Padding = new Thickness(16, 2, 6, 6), Margin = new Thickness(0, 0, 0, 20),
            };
            var title = new Paragraph(new Run(headline))
            {
                FontFamily = Res<FontFamily>("DisplayFont"), FontSize = 18, FontWeight = FontWeights.ExtraBold,
                Margin = new Thickness(0, 0, 0, 4),
            };
            title.Inlines.Add(new Run("   "));
            title.Inlines.Add(new InlineUIContainer(ShareButton(headline, fields)) { BaselineAlignment = BaselineAlignment.Center });
            block.Blocks.Add(title);
            foreach ((string label, string text) in fields)
            {
                // "For you" is about the learner's own stack, so it stands out
                bool mine = label == "For you";
                var part = mine
                    ? new Section { Background = Res<Brush>("VioletTintBrush"), Padding = new Thickness(12, 6, 12, 8), Margin = new Thickness(0, 8, 0, 0) }
                    : new Section { Margin = new Thickness(0, 6, 0, 0) };
                if (label.Length > 0)
                {
                    Paragraph tag = Eyebrow(label.ToUpperInvariant());
                    tag.Margin = new Thickness(0, 0, 0, 1);
                    tag.FontSize = 11.5;
                    if (mine) tag.Foreground = Res<Brush>("VioletBrush");
                    part.Blocks.Add(tag);
                }
                MoveBlocks(CodeRenderer.Render(text.Trim()), part.Blocks);
                block.Blocks.Add(part);
            }
            document.Blocks.Add(block);
        }
    }

    /// <summary>The pages the TL;DR was written from, each a link that opens in the browser.</summary>
    private void RenderSources(FlowDocument document, IReadOnlyList<TldrSource> sources)
    {
        document.Blocks.Add(Eyebrow($"{SectionLabels.Length:00} · SOURCES"));
        foreach (TldrSource source in sources)
        {
            var link = new Hyperlink(new Run(source.Title.Length > 0 ? source.Title : source.Url))
            {
                NavigateUri = new Uri(source.Url), ToolTip = source.Url, Foreground = Res<Brush>("VioletBrush"),
            };
            link.Click += (_, _) => Process.Start(new ProcessStartInfo(source.Url) { UseShellExecute = true });
            var line = new Paragraph { Margin = new Thickness(0, 0, 0, 6) };
            line.Inlines.Add(new Run("↗  ") { Foreground = Res<Brush>("InkSoftBrush") });
            line.Inlines.Add(link);
            line.Inlines.Add(new Run("   " + new Uri(source.Url).Host)
            {
                FontFamily = Res<FontFamily>("MonoFont"), FontSize = 12, Foreground = Res<Brush>("InkSoftBrush"),
            });
            document.Blocks.Add(line);
        }
    }

    /// <summary>Splits a section into its items: a "* headline" line, then "Label: text" lines (text may run on).</summary>
    private static List<(string Headline, List<(string Label, string Text)> Fields)> Items(string text)
    {
        var items = new List<(string Headline, List<(string Label, string Text)> Fields)>();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("* ", StringComparison.Ordinal))
            {
                items.Add((line[2..].Trim(), []));
                continue;
            }
            if (items.Count == 0) continue;
            List<(string Label, string Text)> fields = items[^1].Fields;
            string? label = FieldLabels.FirstOrDefault(l => line.StartsWith(l + ":", StringComparison.Ordinal));
            if (label != null) fields.Add((label, line[(label.Length + 1)..].Trim()));
            else if (fields.Count > 0) fields[^1] = (fields[^1].Label, fields[^1].Text + "\n" + line);
            else if (line.Trim().Length > 0) fields.Add(("", line));
        }
        return items;
    }

    /// <summary>The small "+ my Claude" button on an item: suggests saving it to the user's own Claude Code projects.</summary>
    private Button ShareButton(string headline, List<(string Label, string Text)> fields)
    {
        string item = "* " + headline + "\n" + string.Join("\n", fields.Select(f => f.Label.Length > 0 ? $"{f.Label}: {f.Text.Trim()}" : f.Text.Trim()));
        var button = new Button
        {
            Content = "+ my Claude", Style = Res<Style>("SecondaryButton"), Height = 28, Padding = new Thickness(10, 0, 10, 0),
            FontFamily = Res<FontFamily>("BodyFont"), FontSize = 12, FontWeight = FontWeights.SemiBold,
            ToolTip = "Save this to the Claude Code memory or skills of the projects it fits - Claude suggests, you approve",
        };
        System.Windows.Automation.AutomationProperties.SetName(button, "Save to my Claude: " + headline);
        button.Click += (_, _) => Share(headline, item);
        return button;
    }

    private Paragraph Eyebrow(string text) => new(new Run(text))
    {
        FontFamily = Res<FontFamily>("MonoFont"), FontSize = 12.5, Foreground = Res<Brush>("InkSoftBrush"),
        Margin = new Thickness(0, 0, 0, 12),
    };

    private T Res<T>(string key) => (T)FindResource(key);

    private static void MoveBlocks(FlowDocument from, BlockCollection to)
    {
        foreach (Block b in from.Blocks.ToList())
        {
            from.Blocks.Remove(b);
            to.Add(b);
        }
    }
}

