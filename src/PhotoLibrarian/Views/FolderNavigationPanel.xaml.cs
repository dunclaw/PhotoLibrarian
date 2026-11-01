// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PhotoLibrarian.ViewModels;
using PhotoLibrarian.Diagnostics;
using PhotoLibrarian.Core.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace PhotoLibrarian.Views;

public sealed partial class FolderNavigationPanel : UserControl
{
    private FolderNavigationViewModel? ViewModel => App.ViewModel?.FolderNav;
    private readonly SemaphoreSlim _tagRefreshGate = new(1, 1);
    private bool _isRefreshingTagTree;
    private bool _isRefreshingPeopleTree;
    private readonly NavigationPaneStateStore _stateStore = new();
    private readonly NavigationPaneState _paneState;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly HashSet<TreeView> _initializedTrees = [];
    private bool _isRebuildingTree;
    private bool _isInitializing = true;
    private bool _hasLoaded;
    private bool _savePending;
    private bool _libraryRefreshQueued;

    public FolderNavigationPanel()
    {
        this.InitializeComponent();
        _paneState = _stateStore.Load();
        _saveTimer.Tick += OnSaveTimerTick;
        foreach (var tree in NavigationTrees)
        {
            tree.Expanding += OnTreeExpanding;
            tree.Collapsed += OnTreeCollapsed;
        }
        this.Loaded += OnLoaded;
        this.Unloaded += OnUnloaded;
    }

    private TreeView[] NavigationTrees => [LibraryTree, DateTree, PeopleTree, TagsTree, NotTaggedTree, FlagsTree];

    private static string GetNodeKey(TreeViewNode node) => node.Content switch
    {
        FolderNodeWrapper wrapper => GetFolderKey(node, wrapper.FolderNode.Path),
        DateNodeWrapper wrapper => GetDateNodeKey(wrapper.DateNode),
        PersonNodeWrapper { PersonNode.IsRoot: true } => "root",
        PersonNodeWrapper wrapper => $"person:{wrapper.PersonNode.PersonId}",
        TagNodeWrapper { TagNode.IsRoot: true } => "root",
        TagNodeWrapper wrapper => $"tag:{wrapper.TagNode.FullPath}",
        FlagNavigationViewModel => "flagged",
        NotTaggedNavigationViewModel => NotTaggedNavigationViewModel.NodeKey,
        _ => "root"
    };

    private static string GetFolderKey(TreeViewNode node, string path)
    {
        var watchedRoot = node;
        while (watchedRoot.Parent?.Content is FolderNodeWrapper)
            watchedRoot = watchedRoot.Parent;

        var rootPath = ((FolderNodeWrapper)watchedRoot.Content).FolderNode.Path;
        // Scope duplicate physical folders to their watched root; Windows paths are case-insensitive.
        return $"{rootPath.TrimEnd('\\')}|{path.TrimEnd('\\')}".ToUpperInvariant();
    }

    private void CaptureTreeState(TreeView tree)
    {
        if (!_initializedTrees.Contains(tree)) return;
        CaptureNodes(tree, tree.RootNodes, tree.SelectedNodes.ToHashSet());
    }

    private void CaptureNodes(TreeView tree, IList<TreeViewNode> nodes, HashSet<TreeViewNode> selected)
    {
        foreach (var node in nodes)
        {
            _paneState.SetNode(tree.Name, GetNodeKey(node),
                new NavigationNodeState(node.IsExpanded, selected.Contains(node)));
            CaptureNodes(tree, node.Children, selected);
        }
    }

    private void RebuildTree(TreeView tree, Action build)
    {
        CaptureTreeState(tree);
        _isRebuildingTree = true;
        try
        {
            tree.SelectedNodes.Clear();
            tree.RootNodes.Clear();
            build();
            RestoreNodes(tree, tree.RootNodes);
            _initializedTrees.Add(tree);
        }
        finally
        {
            _isRebuildingTree = false;
        }
    }

    private void RestoreNodes(TreeView tree, IList<TreeViewNode> nodes)
    {
        // Realize every saved folder route before checking parents, so selection can cascade consistently.
        RestoreExpansion(tree, nodes);
        RestoreChecks(tree, nodes);
    }

    private void RestoreExpansion(TreeView tree, IList<TreeViewNode> nodes)
    {
        foreach (var node in nodes)
        {
            var key = GetNodeKey(node);
            var state = _paneState.GetNode(tree.Name, key);
            if (node.Content is FolderNodeWrapper &&
                (state?.Expanded == true || _paneState.HasFolderDescendant(tree.Name, key)))
            {
                RealizeFolderChildren(node);
            }

            if (state is not null) node.IsExpanded = state.Expanded;
            RestoreExpansion(tree, node.Children);
        }
    }

    private void RestoreChecks(TreeView tree, IList<TreeViewNode> nodes)
    {
        foreach (var node in nodes)
        {
            var state = _paneState.GetNode(tree.Name, GetNodeKey(node));
            if (state?.Checked == true && !tree.SelectedNodes.Contains(node))
                tree.SelectedNodes.Add(node);
            RestoreChecks(tree, node.Children);
        }
    }

    private void OnTreeExpanding(TreeView sender, TreeViewExpandingEventArgs args) => ScheduleSave();

    private void OnTreeCollapsed(TreeView sender, TreeViewCollapsedEventArgs args) => ScheduleSave();

    private void ScheduleSave()
    {
        if (_isInitializing || _isRebuildingTree) return;
        _savePending = true;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void OnSaveTimerTick(object? sender, object e) => SaveState();

    private void OnUnloaded(object sender, RoutedEventArgs e) => SaveState();

    public void SaveState()
    {
        _saveTimer.Stop();
        if (!_savePending || _isInitializing) return;
        foreach (var tree in NavigationTrees) CaptureTreeState(tree);
        try
        {
            _stateStore.Save(_paneState);
            _savePending = false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DebugLog.WriteLine($"Could not save navigation preferences: {exception.Message}");
            if (App.ViewModel is not null)
                App.ViewModel.StatusText = "Could not save Browse preferences.";
        }
    }

    private Task RunOnDispatcherAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        }))
            completion.SetException(new InvalidOperationException("The navigation tree could not be refreshed."));
        return completion.Task;
    }

    public async Task RefreshAllTreesAsync()
    {
        RefreshLibraryTree();
        await RefreshDateTreeAsync();
        await RefreshPeopleTreeAsync();
        await RefreshTagsTreeAsync();
        await RunOnDispatcherAsync(RefreshFlagTreeCore);
    }

    public async Task RefreshMetadataTreesAsync()
    {
        await RefreshDateTreeAsync();
        await RefreshPeopleTreeAsync();
        await RefreshTagsTreeAsync();
        RefreshFlagTree();
    }

    /// <summary>
    /// Ensures the single "Flagged" node exists. The node's label is data-bound to
    /// <see cref="FlagNavigationViewModel.Label"/>, so the count repaints on its own.
    /// </summary>
    public void RefreshFlagTree()
    {
        DispatcherQueue.TryEnqueue(RefreshFlagTreeCore);
    }

    private void RefreshFlagTreeCore()
    {
        var flagNav = App.ViewModel?.FlagNav;
        if (flagNav is null) return;

        // Never rebuild an existing flag node: its label is bound to the live count.
        if (FlagsTree.RootNodes.Any(n => n.Content is FlagNavigationViewModel)) return;

        RebuildTree(FlagsTree, () => FlagsTree.RootNodes.Add(new TreeViewNode { Content = flagNav }));
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || _hasLoaded) return;
        _hasLoaded = true;
        try
        {
            await App.LibraryInitialization;
            ViewModel.RootFolders.CollectionChanged += OnRootFoldersChanged;
            await RefreshAllTreesAsync();
        }
        catch (Exception exception)
        {
            DebugLog.WriteLine($"Could not restore Browse preferences: {exception}");
            App.ViewModel!.StatusText = "Could not restore Browse preferences.";
        }
        finally
        {
            _isInitializing = false;
        }

        if (NavigationTrees.Any(tree => tree.SelectedNodes.Count > 0))
            UpdateGridFromSelection();
        ScheduleSave();
    }

    private void OnRootFoldersChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // Coalesce the clear/add sequence: rebuild from the complete watched-folder collection.
        if (_libraryRefreshQueued) return;
        _libraryRefreshQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            _libraryRefreshQueued = false;
            RefreshLibraryTree();
            if (!_isInitializing) UpdateGridFromSelection();
        }))
        {
            _libraryRefreshQueued = false;
            DebugLog.WriteLine("The library tree could not be refreshed: the dispatcher is unavailable.");
        }
    }

    private void RefreshLibraryTree()
    {
        if (ViewModel is null) return;

        RebuildTree(LibraryTree, () =>
        {
            var photoLibraryRoot = new TreeViewNode
            {
                Content = "📚 Photo Library",
                IsExpanded = true
            };

            foreach (var rootFolder in ViewModel.RootFolders)
            {
                var rootNode = BuildFolderNode(rootFolder, isRootFolder: true);
                photoLibraryRoot.Children.Add(rootNode);
            }

            LibraryTree.RootNodes.Add(photoLibraryRoot);
        });
    }

    private async Task RefreshDateTreeAsync()
    {
        if (App.ViewModel?.DateNav is null) return;

        await App.ViewModel.DateNav.LoadDatesAsync();
        
        // Must update UI on dispatcher queue
        await RunOnDispatcherAsync(() =>
        {
            // Snapshot expansion + selection so editing date taken (or any other refresh trigger)
            // doesn't collapse the user's open year/month and doesn't drop the active filter.
            RebuildTree(DateTree, () =>
            {
                foreach (var rootNode in App.ViewModel.DateNav.RootNodes)
                {
                    var treeNode = BuildDateNode(rootNode);
                    DateTree.RootNodes.Add(treeNode);
                }
            });
            if (!_isInitializing) UpdateGridFromSelection();
        });
    }

    private static string GetDateNodeKey(DateNode n)
    {
        if (n.IsRoot) return "__root__";
        if (n.Month.HasValue) return $"{n.Year:D4}-{n.Month.Value:D2}";
        return $"{n.Year:D4}";
    }

    private TreeViewNode BuildDateNode(DateNode dateNode)
    {
        var treeNode = new TreeViewNode
        {
            Content = new DateNodeWrapper(dateNode),
            IsExpanded = false
        };

        foreach (var child in dateNode.Children)
        {
            treeNode.Children.Add(BuildDateNode(child));
        }

        return treeNode;
    }

    public async Task RefreshPeopleTreeAsync()
    {
        if (App.ViewModel?.PeopleNav is null) return;

        await App.ViewModel.PeopleNav.LoadPeopleAsync();

        await RunOnDispatcherAsync(() =>
        {
            _isRefreshingPeopleTree = true;
            try
            {
                RebuildTree(PeopleTree, () =>
                {
                    foreach (var personNode in App.ViewModel.PeopleNav.RootNodes)
                    {
                        PeopleTree.RootNodes.Add(BuildPersonNode(personNode));
                    }
                });
            }
            finally
            {
                _isRefreshingPeopleTree = false;
            }

            if (!_isInitializing)
                UpdateGridFromSelection();
        });
    }

    private static TreeViewNode BuildPersonNode(PersonNode personNode)
    {
        var treeNode = new TreeViewNode
        {
            Content = new PersonNodeWrapper(personNode),
            IsExpanded = personNode.IsRoot
        };

        foreach (var child in personNode.Children)
        {
            treeNode.Children.Add(BuildPersonNode(child));
        }

        return treeNode;
    }

    public async Task RefreshTagsTreeAsync()
    {
        if (App.ViewModel?.TagNav is null) return;

        await _tagRefreshGate.WaitAsync();
        try
        {
            await App.ViewModel.TagNav.LoadTagsAsync();
            await App.ViewModel.NotTaggedNav.LoadAsync();
            await RunOnDispatcherAsync(() =>
            {
                _isRefreshingTagTree = true;
                try
                {
                    RebuildTree(TagsTree, () =>
                    {
                        foreach (var tagNode in App.ViewModel.TagNav.RootTags)
                        {
                            TagsTree.RootNodes.Add(BuildTagNode(tagNode));
                        }
                    });
                    RefreshNotTaggedTreeCore();
                }
                finally
                {
                    _isRefreshingTagTree = false;
                }
                if (!_isInitializing) UpdateGridFromSelection();
            });
        }
        finally
        {
            _tagRefreshGate.Release();
        }
    }

    private void RefreshNotTaggedTreeCore()
    {
        var notTaggedNav = App.ViewModel?.NotTaggedNav;
        if (notTaggedNav is null ||
            NotTaggedTree.RootNodes.Any(n => n.Content is NotTaggedNavigationViewModel))
            return;

        RebuildTree(NotTaggedTree, () =>
            NotTaggedTree.RootNodes.Add(new TreeViewNode { Content = notTaggedNav }));
    }

    private static TreeViewNode BuildTagNode(TagNode tagNode)
    {
        var treeNode = new TreeViewNode
        {
            Content = new TagNodeWrapper(tagNode),
            HasUnrealizedChildren = tagNode.Children.Count > 0
        };

        // Add children
        foreach (var child in tagNode.Children)
        {
            treeNode.Children.Add(BuildTagNode(child));
        }

        return treeNode;
    }

    private static TreeViewNode BuildFolderNode(FolderNode folderNode, bool isRootFolder = false)
    {
        // Create display text with icon
        string icon = "📁";
        string displayName = folderNode.Name;
        string displayText = isRootFolder 
            ? $"{icon} {folderNode.Path}"  // Show full path for root folders
            : $"{icon} {displayName}";     // Show just name for subfolders

        // Store FolderNode in a wrapper for event handlers to retrieve
        var treeNode = new TreeViewNode 
        { 
            Content = new FolderNodeWrapper(folderNode, displayText),
            IsExpanded = false
        };

        // Check if this node has placeholder children (lazy-load marker)
        bool hasPlaceholder = folderNode.Children.Count == 1 && folderNode.Children[0].Path == "";

        if (hasPlaceholder)
        {
            treeNode.HasUnrealizedChildren = true;
        }
        else if (folderNode.Children.Count > 0)
        {
            // Add realized children
            foreach (var child in folderNode.Children)
            {
                treeNode.Children.Add(BuildFolderNode(child, isRootFolder: false));
            }
        }

        return treeNode;
    }

    private async void OnManageFoldersClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ManageFoldersDialog
        {
            XamlRoot = this.XamlRoot
        };
        await dialog.ShowAsync();
        
        // Refresh tree after dialog closes
        RefreshLibraryTree();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.RefreshCommand.CanExecute(null) == true)
            await ViewModel.RefreshCommand.ExecuteAsync(null);
    }

    private void OnLibraryItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        // When user clicks on a folder (not checkbox), toggle its selection
        if (args.InvokedItem is TreeViewNode node)
        {
            if (sender.SelectedNodes.Contains(node))
            {
                // Already selected, deselect it
                sender.SelectedNodes.Remove(node);
            }
            else
            {
                // Not selected, add it
                sender.SelectedNodes.Add(node);
            }

            // Manually trigger grid update since programmatic selection change
            // might not fire SelectionChanged event
            UpdateGridFromSelection();
        }
    }

    private void OnDateItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is TreeViewNode node)
        {
            DebugLog.WriteLine($"OnDateItemInvoked: Node={node.Content}, IsSelected={sender.SelectedNodes.Contains(node)}");
            
            if (sender.SelectedNodes.Contains(node))
            {
                sender.SelectedNodes.Remove(node);
            }
            else
            {
                sender.SelectedNodes.Add(node);
            }
            
            DebugLog.WriteLine($"  After toggle: IsSelected={sender.SelectedNodes.Contains(node)}, TotalSelected={sender.SelectedNodes.Count}");
            UpdateGridFromSelection();
        }
    }

    private void OnTagsItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is TreeViewNode node)
        {
            if (sender.SelectedNodes.Contains(node))
            {
                sender.SelectedNodes.Remove(node);
            }
            else
            {
                sender.SelectedNodes.Add(node);
            }
            UpdateGridFromSelection();
        }
    }

    private void OnPeopleItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is not TreeViewNode node) return;

        if (sender.SelectedNodes.Contains(node))
            sender.SelectedNodes.Remove(node);
        else
            sender.SelectedNodes.Add(node);

        UpdateGridFromSelection();
    }

    private void OnLibraryExpanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (!_isRebuildingTree) CaptureTreeState(sender);
        RealizeFolderChildren(args.Node);
        if (_isRebuildingTree) return;

        var selectedCount = sender.SelectedNodes.Count;
        _isRebuildingTree = true;
        try
        {
            RestoreExpansion(sender, args.Node.Children);
            RestoreChecks(sender, [args.Node]);
        }
        finally
        {
            _isRebuildingTree = false;
        }
        if (selectedCount != sender.SelectedNodes.Count) UpdateGridFromSelection();
    }

    private static void RealizeFolderChildren(TreeViewNode node)
    {
        if (node.Content is not FolderNodeWrapper wrapper) return;

        var folderNode = wrapper.FolderNode;

        // Check if we need to load placeholder children
        bool hasPlaceholder = folderNode.Children.Count == 1 && folderNode.Children[0].Path == "";
        
        if (hasPlaceholder)
        {
            // Clear placeholder and load real children
            folderNode.Children.Clear();
            FolderNavigationViewModel.BuildChildNodes(folderNode);

            // Rebuild the TreeViewNode children
            node.Children.Clear();
            node.HasUnrealizedChildren = false;

            foreach (var child in folderNode.Children)
            {
                node.Children.Add(BuildFolderNode(child, isRootFolder: false));
            }
        }
    }

    private void OnLibrarySelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        UpdateGridFromSelection();
    }

    private void OnDateSelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        DebugLog.WriteLine($"OnDateSelectionChanged: AddedItems={args.AddedItems.Count}, RemovedItems={args.RemovedItems.Count}, TotalSelected={sender.SelectedNodes.Count}");
        UpdateGridFromSelection();
    }

    private void OnTagsSelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        if (_isRefreshingTagTree)
        {
            return;
        }

        DebugLog.WriteLine($"OnTagsSelectionChanged: AddedItems={args.AddedItems.Count}, RemovedItems={args.RemovedItems.Count}, TotalSelected={sender.SelectedNodes.Count}");
        UpdateGridFromSelection();
    }

    private void OnPeopleSelectionChanged(
        TreeView sender,
        TreeViewSelectionChangedEventArgs args)
    {
        if (_isRefreshingPeopleTree)
            return;

        UpdateGridFromSelection();
    }

    private void OnFlagsItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is TreeViewNode node)
        {
            if (sender.SelectedNodes.Contains(node))
                sender.SelectedNodes.Remove(node);
            else
                sender.SelectedNodes.Add(node);
            UpdateGridFromSelection();
        }
    }

    private void OnFlagsSelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        UpdateGridFromSelection();
    }

    private void UpdateGridFromSelection()
    {
        if (_isInitializing || _isRebuildingTree) return;
        if (App.ViewModel?.ImageGrid is null) return;
        ScheduleSave();

        // Collect all selected folder paths
        var selectedFolders = new List<string>();
        bool photoLibraryRootSelected = false;
        
        foreach (var node in LibraryTree.SelectedNodes)
        {
            if (node.Content is string str && str.StartsWith("📚"))
            {
                // Photo Library root node selected - means "show all folders"
                photoLibraryRootSelected = true;
            }
            else if (node.Content is FolderNodeWrapper wrapper && wrapper.FolderNode != null)
            {
                selectedFolders.Add(wrapper.FolderNode.Path);
            }
        }

        // Collect selected date ranges (year/month/root)
        var selectedYears = new List<int>();
        var selectedMonths = new List<(int Year, int Month)>();
        bool dateRootSelected = false;
        foreach (var node in DateTree.SelectedNodes)
        {
            if (node.Content is DateNodeWrapper wrapper)
            {
                DebugLog.WriteLine($"  Date node selected: IsRoot={wrapper.DateNode.IsRoot}, Year={wrapper.DateNode.Year}, Month={wrapper.DateNode.Month}, Count={wrapper.DateNode.Count}");
                
                if (wrapper.DateNode.IsRoot)
                {
                    // Root "Dates" node - show all dated images
                    dateRootSelected = true;
                }
                else if (wrapper.DateNode.Month.HasValue)
                {
                    // Specific month
                    selectedMonths.Add((wrapper.DateNode.Year, wrapper.DateNode.Month.Value));
                }
                else
                {
                    // Whole year
                    selectedYears.Add(wrapper.DateNode.Year);
                }
            }
        }

        // Collect selected tags
        var selectedTags = new List<string>();
        bool tagRootSelected = false;
        foreach (var node in TagsTree.SelectedNodes)
        {
            if (node.Content is TagNodeWrapper wrapper)
            {
                if (wrapper.TagNode.IsRoot)
                {
                    // Root "Tags" node - show all tagged images
                    tagRootSelected = true;
                }
                else
                {
                    selectedTags.Add(wrapper.TagNode.FullPath);
                }
            }
        }

        var selectedPeople = new List<long>();
        bool peopleRootSelected = false;
        foreach (var node in PeopleTree.SelectedNodes)
        {
            if (node.Content is not PersonNodeWrapper wrapper) continue;

            if (wrapper.PersonNode.IsRoot)
                peopleRootSelected = true;
            else if (wrapper.PersonNode.PersonId is long personId)
                selectedPeople.Add(personId);
        }

        DebugLog.WriteLine($"UpdateGridFromSelection: PhotoLibraryRoot={photoLibraryRootSelected}, Folders={selectedFolders.Count}, DateRoot={dateRootSelected}, Years={selectedYears.Count}, Months={selectedMonths.Count}, PeopleRoot={peopleRootSelected}, People={selectedPeople.Count}, TagRoot={tagRootSelected}, Tags={selectedTags.Count}");

        // Flagged working set
        bool flaggedSelected = FlagsTree.SelectedNodes.Any(n => n.Content is FlagNavigationViewModel);
        bool notTaggedSelected = NotTaggedTree.SelectedNodes.Any(
            n => n.Content is NotTaggedNavigationViewModel);

        // If nothing selected anywhere, clear filters to show empty grid
        if (!photoLibraryRootSelected && !dateRootSelected && !peopleRootSelected &&
            !tagRootSelected && !flaggedSelected && !notTaggedSelected &&
            selectedFolders.Count == 0 && selectedYears.Count == 0 && 
            selectedMonths.Count == 0 && selectedPeople.Count == 0 &&
            selectedTags.Count == 0)
        {
            DebugLog.WriteLine("  No selections - clearing filter");
            App.ViewModel.ImageGrid.ClearFilterCommand.Execute(null);
            return;
        }

        // If Photo Library root is selected and nothing else, treat as "show all from all folders"
        if (photoLibraryRootSelected && selectedFolders.Count == 0)
        {
            // Add all root folder paths
            selectedFolders.AddRange(ViewModel?.RootFolders.Select(f => f.Path) ?? []);
        }

        // Apply multi-criteria filter
        _ = App.ViewModel.ImageGrid.FilterByMultipleCriteriaAsync(
            selectedFolders.Count > 0 ? selectedFolders : null,
            dateRootSelected,
            selectedYears.Count > 0 ? selectedYears : null,
            selectedMonths.Count > 0 ? selectedMonths : null,
            peopleRootSelected,
            selectedPeople.Count > 0 ? selectedPeople : null,
            tagRootSelected,
            selectedTags.Count > 0 ? selectedTags : null,
            flaggedSelected,
            notTaggedSelected);
    }

    // ============================================================================
    //  Drag-and-drop: drop photos from the grid onto a tag node to apply that tag.
    // ============================================================================

    private void OnTagsTreeDragOver(object sender, DragEventArgs e)
    {
        var hasMarker = e.DataView.Properties.ContainsKey(Services.PhotoOperationsService.TagDropFormatId);
        if (!hasMarker)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        var tagNode = FindTagNodeAt(e);
        if (tagNode == null || tagNode.IsRoot)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = $"Apply tag '{tagNode.FullPath}'";
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsContentVisible = true;
        e.DragUIOverride.IsGlyphVisible = true;
        e.Handled = true;
    }

    private async void OnTagsTreeDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Properties.ContainsKey(Services.PhotoOperationsService.TagDropFormatId)) return;

        var tagNode = FindTagNodeAt(e);
        DebugLog.WriteLine($"OnTagsTreeDrop: tagNode={tagNode?.FullPath ?? "null"}");
        if (tagNode == null || tagNode.IsRoot) return;

        e.Handled = true;
        var deferral = e.GetDeferral();
        try
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                DebugLog.WriteLine("OnTagsTreeDrop: no StorageItems in DataView");
                return;
            }
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items.OfType<StorageFile>().Select(f => f.Path).ToList();
            DebugLog.WriteLine($"OnTagsTreeDrop: applying tag '{tagNode.FullPath}' to {paths.Count} path(s)");
            if (paths.Count == 0) return;

            if (App.ViewModel != null)
            {
                await App.ViewModel.ApplyTagToImagePathsAsync(tagNode.FullPath, paths);
            }
        }
        catch (Exception ex)
        {
            DebugLog.WriteLine($"OnTagsTreeDrop: ERROR {ex.Message}");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private TagNode? FindTagNodeAt(DragEventArgs e)
    {
        // WinUI 3 TreeViewItems don't have AllowDrop=true by default, so the OS drag hit-test
        // stops at the TreeView itself and e.OriginalSource is always the TreeView. We work around
        // this by hit-testing from the cursor position (in xaml-root coords) limited to TagsTree's
        // subtree, which finds the actual TreeViewItem under the pointer regardless of AllowDrop.
        try
        {
            var elements = VisualTreeHelper.FindElementsInHostCoordinates(
                e.GetPosition(null),
                TagsTree);

            foreach (var el in elements)
            {
                if (el is TreeViewItem item)
                {
                    var node = TagsTree.NodeFromContainer(item);
                    if (node?.Content is TagNodeWrapper w1) return w1.TagNode;
                    if (item.DataContext is TreeViewNode tvn && tvn.Content is TagNodeWrapper w2) return w2.TagNode;
                    if (item.DataContext is TagNodeWrapper w3) return w3.TagNode;
                }
            }
        }
        catch (Exception ex)
        {
            DebugLog.WriteLine($"FindTagNodeAt: hit-test failed: {ex.Message}");
        }
        return null;
    }

    // Helper class to wrap FolderNode with display text for TreeView
    private class FolderNodeWrapper
    {
        public FolderNode? FolderNode { get; }
        public string DisplayText { get; }

        public FolderNodeWrapper(FolderNode? folderNode, string displayText)
        {
            FolderNode = folderNode;
            DisplayText = displayText;
        }

        public override string ToString() => DisplayText;
    }

    // Helper class to wrap DateNode for TreeView
    private class DateNodeWrapper
    {
        public DateNode DateNode { get; }

        public DateNodeWrapper(DateNode dateNode)
        {
            DateNode = dateNode;
        }

        public override string ToString()
        {
            // Root node already has emoji in DisplayName, others need the calendar icon
            if (DateNode.IsRoot)
                return $"{DateNode.DisplayName} ({DateNode.Count})";
            else
                return $"📅 {DateNode.DisplayName} ({DateNode.Count})";
        }
    }

    private sealed class PersonNodeWrapper
    {
        public PersonNode PersonNode { get; }

        public PersonNodeWrapper(PersonNode personNode)
        {
            PersonNode = personNode;
        }

        public override string ToString()
        {
            if (PersonNode.IsRoot)
                return $"{PersonNode.DisplayName} ({PersonNode.Count})";

            return $"👤 {PersonNode.DisplayName} ({PersonNode.Count})";
        }
    }

    // Helper class to wrap TagNode for TreeView
    private class TagNodeWrapper
    {
        public TagNode TagNode { get; }

        public TagNodeWrapper(TagNode tagNode)
        {
            TagNode = tagNode;
        }

        public override string ToString()
        {
            // Root node already has emoji in Name, others need the tag icon
            if (TagNode.IsRoot)
                return $"{TagNode.Name} ({TagNode.Count})";
            else
                return $"🏷️ {TagNode.Name} ({TagNode.Count})";
        }
    }
}
