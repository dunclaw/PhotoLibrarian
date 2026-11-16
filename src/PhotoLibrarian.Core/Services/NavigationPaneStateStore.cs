// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Diagnostics;
using System.Text.Json;

namespace PhotoLibrarian.Core.Services;

public sealed record NavigationNodeState(bool Expanded, bool Checked);

/// <summary>Local UI preferences, keyed by tree and stable node identity, not display text.</summary>
public sealed class NavigationPaneState
{
    public int Version { get; set; } = 1;
    public Dictionary<string, Dictionary<string, NavigationNodeState>> Trees { get; set; } = [];

    public NavigationNodeState? GetNode(string tree, string key) =>
        Trees.TryGetValue(tree, out var nodes) ? nodes.GetValueOrDefault(key) : null;

    public void SetNode(string tree, string key, NavigationNodeState state)
    {
        if (!Trees.TryGetValue(tree, out var nodes))
        {
            nodes = new Dictionary<string, NavigationNodeState>(StringComparer.Ordinal);
            Trees.Add(tree, nodes);
        }

        nodes[key] = state;
    }

    public bool HasFolderDescendant(string tree, string key)
    {
        if (!Trees.TryGetValue(tree, out var nodes)) return false;

        var prefix = key.TrimEnd('\\') + "\\";
        return nodes.Any(node =>
            (node.Value.Expanded || node.Value.Checked) &&
            node.Key.StartsWith(prefix, StringComparison.Ordinal));
    }
}

public sealed class NavigationPaneStateStore
{
    private readonly string _path;

    public NavigationPaneStateStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhotoLibrarian",
            "navigation-pane.json");
    }

    public NavigationPaneState Load()
    {
        try
        {
            if (!File.Exists(_path)) return new NavigationPaneState();
            var state = JsonSerializer.Deserialize<NavigationPaneState>(File.ReadAllText(_path));
            if (state is null || state.Version != 1 || state.Trees is null)
                throw new JsonException("Invalid or unsupported navigation preferences.");

            foreach (var nodes in state.Trees.Values)
            {
                if (nodes is null || nodes.Any(node => node.Value is null))
                    throw new JsonException("Invalid navigation node preferences.");
            }

            return state;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            DebugLog.WriteLine($"Could not load navigation preferences: {exception.Message}");
            return new NavigationPaneState();
        }
    }

    public void Save(NavigationPaneState state)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temporaryPath = _path + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
