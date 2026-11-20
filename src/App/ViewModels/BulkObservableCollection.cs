using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace PPlusTracklistEditor.App.ViewModels;

/// <summary>
/// An ObservableCollection that can swap all its items with one "Reset" notification. Adding 500 items one by one
/// would make the list view re-filter and re-sort 500 times.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
