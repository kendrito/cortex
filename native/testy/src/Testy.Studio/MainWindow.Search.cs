using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Testy.Core;

namespace Testy.Studio;

/// <summary>A test found by global search, with where it matched (name, purpose or a step).</summary>
internal sealed class SearchResult : INotifyPropertyChanged
{
    private bool highlighted;
    public required TestCase Test { get; init; }
    public string Name => Test.Name;
    public required string Detail { get; init; }
    public required string Glyph { get; init; }
    public int StepIndex { get; init; } = -1;
    public int Rank { get; init; }
    /// <summary>The result Enter opens (moved with the arrow keys while the search box keeps the focus).</summary>
    public bool IsHighlighted { get => highlighted; set { if (highlighted == value) return; highlighted = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHighlighted))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Global search (Ctrl+K, GlobalSearch in the app bar): test names, purposes and step text. Results drop down under the box
/// (GlobalSearchResults, ListItem name = test name); Enter opens the highlighted one, and a click or a UI Automation Select opens that one.
/// </summary>
public partial class MainWindow
{
    private List<SearchResult> _searchResults = [];
    private int _searchHighlight = -1;
    private bool _searchUpdating;

    private void GlobalSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_searchUpdating) return;
        var query = GlobalSearch.Text.Trim();
        if (query.Length == 0) { CloseSearch(); return; }
        _searchResults = FindTests(query);
        _searchUpdating = true;
        try { SearchResults.ItemsSource = _searchResults; SearchResults.SelectedIndex = -1; }
        finally { _searchUpdating = false; }
        Highlight(_searchResults.Count > 0 ? 0 : -1);
        SearchEmpty.Visibility = _searchResults.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchResults.Visibility = _searchResults.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (!SearchFlyout.IsOpen)
        {
            ChangeAppFlyout.IsOpen = false; HelpFlyout.IsOpen = false; ImproveFlyout.IsOpen = false;
            SearchFlyout.IsOpen = true;
        }
    }
    private void GlobalSearch_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down when _searchResults.Count > 0:
                if (!SearchFlyout.IsOpen) SearchFlyout.IsOpen = true;
                Highlight(Math.Min(_searchHighlight + 1, _searchResults.Count - 1)); e.Handled = true; break;
            case Key.Up when _searchResults.Count > 0:
                Highlight(Math.Max(_searchHighlight - 1, 0)); e.Handled = true; break;
            case Key.Enter:
                if (_searchHighlight >= 0 && _searchHighlight < _searchResults.Count) OpenSearchResult(_searchResults[_searchHighlight]);
                e.Handled = true; break;
            case Key.Escape:
                if (SearchFlyout.IsOpen) CloseSearch(); else if (GlobalSearch.Text.Length > 0) GlobalSearch.Text = "";
                e.Handled = true; break;
        }
    }
    private void Highlight(int index)
    {
        for (var i = 0; i < _searchResults.Count; i++) _searchResults[i].IsHighlighted = i == index;
        _searchHighlight = index;
        if (index >= 0) SearchResults.ScrollIntoView(_searchResults[index]);
    }
    private void SearchResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_searchUpdating || SearchResults.SelectedItem is not SearchResult result) return;
        OpenSearchResult(result);
    }
    /// <summary>Opens the test in Tests (Steps view), selecting the matching step when the match was in a step.</summary>
    private void OpenSearchResult(SearchResult result) => GuardSync(() =>
    {
        CloseSearch(clearText: true);
        if (!ShowPage(LibraryPage)) return;
        if (!ReferenceEquals(_selected, result.Test)) { CommitEditor(); RefreshLibrary(result.Test.Id); }
        if (_selected?.Id != result.Test.Id) return; // the current test has changes that can't be saved yet; it stays open with the error shown
        ShowEditorView(EditorView.Steps);
        if (result.StepIndex >= 0 && result.StepIndex < _steps.Count) { StepsGrid.SelectedIndex = result.StepIndex; StepsGrid.ScrollIntoView(_steps[result.StepIndex]); }
        SetStatus($"Opened “{result.Test.Name}”.", ActivityLevel.Info, "Search");
    });
    private void CloseSearch(bool clearText = false)
    {
        if (SearchFlyout is null) return;
        SearchFlyout.IsOpen = false;
        _searchUpdating = true;
        try { SearchResults.ItemsSource = null; if (clearText) GlobalSearch.Text = ""; }
        finally { _searchUpdating = false; }
        _searchResults = []; _searchHighlight = -1;
    }
    /// <summary>Names first (those that start with the text before the rest), then purposes, then step text.</summary>
    private List<SearchResult> FindTests(string query)
    {
        var results = new List<SearchResult>();
        var technical = StudioPreferences.Current.ShowTechnicalDetails;
        foreach (var test in _tests)
        {
            var at = test.Name.IndexOf(query, StringComparison.CurrentCultureIgnoreCase);
            if (at >= 0)
            {
                results.Add(new SearchResult { Test = test, Rank = at == 0 ? 0 : 1, Glyph = "", Detail = $"{(string.IsNullOrWhiteSpace(test.Category) ? "Test" : test.Category)} · {StepText.Count(test.Steps.Count, "step")}" });
                continue;
            }
            if (test.Intent.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            {
                results.Add(new SearchResult { Test = test, Rank = 2, Glyph = "", Detail = "Purpose: " + StepText.Clip(StepText.OneLine(test.Intent), 90) });
                continue;
            }
            for (var i = 0; i < test.Steps.Count; i++)
            {
                var step = test.Steps[i];
                var sentence = StepText.Sentence(step, _snapshot).Plain;
                if (sentence.Contains(query, StringComparison.CurrentCultureIgnoreCase) || step.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                    || step.Value.Contains(query, StringComparison.CurrentCultureIgnoreCase) || technical && step.Selector.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                {
                    results.Add(new SearchResult { Test = test, Rank = 3, Glyph = "", StepIndex = i, Detail = $"Step {i + 1}: {StepText.Clip(sentence, 90)}" });
                    break;
                }
            }
        }
        return results.OrderBy(r => r.Rank).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).Take(20).ToList();
    }
}
