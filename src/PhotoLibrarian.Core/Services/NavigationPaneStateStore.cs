// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Diagnostics;

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
        _path = path ?? AppDataPaths.SettingsFile("navigation-pane.json");
    }

    public NavigationPaneState Load()
    {
        try
        {
            var result = AppSettingsFile.ReadJson<NavigationPaneState>(_path, IsValid);
            if (result.Status == AppSettingsReadStatus.Missing)
                return new NavigationPaneState();
            if (result.Value is not { } state)
            {
                DebugLog.WriteLine("Invalid navigation preferences; using defaults.");
                return new NavigationPaneState();
            }

            if (result.Status == AppSettingsReadStatus.RecoveredFromBackup)
                DebugLog.WriteLine("Recovered navigation preferences from backup.");
            return state;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            DebugLog.WriteLine($"Could not load navigation preferences: {exception.Message}");
            return new NavigationPaneState();
        }
    }

    public void Save(NavigationPaneState state)
    {
        AppSettingsFile.WriteJson(_path, state, isValid: IsValid);
    }

    private static bool IsValid(NavigationPaneState state) =>
        state.Version == 1 &&
        state.Trees is not null &&
        state.Trees.Values.All(nodes => nodes is not null && nodes.All(node => node.Value is not null));
}
