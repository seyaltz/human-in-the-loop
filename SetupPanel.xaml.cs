using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DevBreak;

/// <summary>
/// A session folder's Claude Code setup, as the CLI sees it: memory (CLAUDE.md files and memory notes), skills,
/// plugins, MCP servers with their status, and agents. Each item opens on the right; a skill can be used in the
/// session (it puts "/name " in the message box).
/// </summary>
public partial class SetupPanel : UserControl
{
    private static readonly string[] PartNames = ["Memory", "Skills", "Plugins", "MCP servers", "Agents"];

    // loaded once per folder; Refresh reads it again
    private static readonly Dictionary<string, SetupInfo> Cache = new(StringComparer.OrdinalIgnoreCase);

    private string? _folder;
    private SetupInfo? _info;
    private int _part;
    private SetupItem? _selected;

    /// <summary>Back to chat was pressed.</summary>
    public event Action? BackRequested;

    /// <summary>Use was pressed on a skill: the text to put in the message box.</summary>
    public event Action<string>? UseRequested;

    public SetupPanel()
    {
        InitializeComponent();
        for (int i = 0; i < PartNames.Length; i++)
        {
            var tab = new RadioButton
            {
                Content = PartNames[i], GroupName = "SetupPart", Tag = i, IsChecked = i == 0,
                Style = (Style)FindResource("ModeTab"), Margin = new Thickness(i == 0 ? 0 : 4, 0, 0, 0),
            };
            tab.Checked += (s, _) =>
            {
                _part = (int)((RadioButton)s).Tag;
                ShowList();
            };
            Parts.Children.Add(tab);
        }
    }

    /// <summary>Shows the setup of a folder (read from the CLI the first time).</summary>
    public void Show(string folder)
    {
        _folder = folder;
        if (Cache.TryGetValue(folder, out SetupInfo? info)) Fill(info);
        else Load(refresh: false);
    }

    private async void Load(bool refresh)
    {
        string folder = _folder!;
        if (refresh) Cache.Remove(folder);
        _info = null;
        ItemList.Items.Clear();
        Facts.Text = folder;
        ShowDetail(null);
        Loading.Visibility = Visibility.Visible;
        RefreshButton.IsEnabled = false;
        try
        {
            SetupInfo info = await CliSetup.LoadAsync(folder);
            Cache[folder] = info;
            if (_folder == folder) Fill(info);
        }
        catch (Exception ex)
        {
            if (_folder == folder) DetailBox.Document = CodeRenderer.Render("Could not read the setup from the claude CLI:\n\n" + ex.Message);
        }
        Loading.Visibility = Visibility.Collapsed;
        RefreshButton.IsEnabled = true;
    }

    private void Fill(SetupInfo info)
    {
        _info = info;
        Facts.Text = $"{info.Folder}   ·   {info.Model}   ·   Claude Code {info.Version}   ·   {info.Tools} tools   ·   output style {info.OutputStyle}"
            + "   ·   in Dev break sessions: full access";
        var counts = new[] { info.Memory.Count, info.Skills.Count, info.Plugins.Count, info.Mcp.Count, info.Agents.Count };
        for (int i = 0; i < PartNames.Length; i++) ((RadioButton)Parts.Children[i]).Content = $"{PartNames[i]} ({counts[i]})";
        ShowList();
    }

    private IReadOnlyList<SetupItem> Current => _info == null ? [] : _part switch
    {
        0 => _info.Memory, 1 => _info.Skills, 2 => _info.Plugins, 3 => _info.Mcp, _ => _info.Agents,
    };

    private void ShowList()
    {
        ItemList.Items.Clear();
        string filter = FilterBox.Text.Trim();
        foreach (SetupItem item in Current.Where(i => filter.Length == 0
                     || i.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                     || i.Description.Contains(filter, StringComparison.OrdinalIgnoreCase)
                     || i.Source.Contains(filter, StringComparison.OrdinalIgnoreCase)))
        {
            ItemList.Items.Add(new ListBoxItem { Content = Row(item), Tag = item, ToolTip = item.File });
        }
        ShowDetail(null);
        if (ItemList.Items.Count > 0) ItemList.SelectedIndex = 0;
    }

    private UIElement Row(SetupItem item)
    {
        var text = new StackPanel { Margin = new Thickness(2, 5, 4, 5) };
        var top = new DockPanel();
        if (item.Status != null)
        {
            var chip = new Border
            {
                CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 0, 7, 1), Margin = new Thickness(8, 0, 0, 0),
                Background = Res(item.Status == "connected" ? "MarkerBrush" : item.Status == "needs-auth" ? "CoralTintBrush" : "VioletTintBrush"),
                Child = new TextBlock { Text = item.Status, FontSize = 11, FontFamily = (FontFamily)FindResource("MonoFont") },
            };
            DockPanel.SetDock(chip, Dock.Right);
            top.Children.Add(chip);
        }
        top.Children.Add(new TextBlock { Text = item.Name, FontWeight = FontWeights.SemiBold, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(top);
        string sub = item.Description.Length > 0 ? item.Description : item.Source;
        text.Children.Add(new TextBlock
        {
            Text = sub, FontSize = 11.5, Foreground = Res("InkSoftBrush"), TextWrapping = TextWrapping.Wrap, MaxHeight = 32,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        return text;
    }

    private void ItemList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ShowDetail((ItemList.SelectedItem as ListBoxItem)?.Tag as SetupItem);

    private void ShowDetail(SetupItem? item)
    {
        _selected = item;
        DetailTitle.Text = item?.Name ?? "";
        DetailMeta.Text = item == null ? "" : string.Join("   ·   ", new[] { item.Source, item.Status ?? "", item.File ?? "" }.Where(s => s.Length > 0));
        UseButton.Visibility = item != null && _part == 1 ? Visibility.Visible : Visibility.Collapsed;
        OpenFileButton.Visibility = item?.File != null ? Visibility.Visible : Visibility.Collapsed;
        string body = item == null ? "" : item.Description;
        // a memory file or skill shows its own text; a plugin shows its folder
        if (item?.File != null && File.Exists(item.File))
        {
            try { body = WithoutFrontMatter(File.ReadAllText(item.File)); } catch (IOException) { }
        }
        else if (item != null && _part == 3)
        {
            body = item.Status switch
            {
                "connected" => "Connected: its tools are available to Claude in this folder.",
                "needs-auth" => "Needs sign-in: run `claude` in a terminal and use /mcp to authenticate it; until then its tools are not available.",
                "failed" => "Failed to start: see `claude --debug` or /mcp in a terminal.",
                _ => item.Status ?? "",
            };
        }
        DetailBox.Document = CodeRenderer.Render(body, softWrap: item?.File != null);
    }

    // a skill or memory file shows its text; its front matter (name, description) is already in the list and title
    private static string WithoutFrontMatter(string text)
    {
        string t = text.TrimStart('﻿');
        if (!t.StartsWith("---")) return text;
        int end = t.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0) return text;
        int after = t.IndexOf('\n', end + 4);
        return after < 0 ? "" : t[(after + 1)..].TrimStart();
    }

    private void Filter_TextChanged(object sender, TextChangedEventArgs e) => ShowList();

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_folder != null) Load(refresh: true);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke();

    private void Use_Click(object sender, RoutedEventArgs e)
    {
        if (_selected != null) UseRequested?.Invoke("/" + _selected.Name + " ");
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (_selected?.File == null) return;
        // a file opens in its app, a plugin's folder in Explorer
        Process.Start(new ProcessStartInfo(_selected.File) { UseShellExecute = true });
    }

    private Brush Res(string key) => (Brush)FindResource(key);
}
