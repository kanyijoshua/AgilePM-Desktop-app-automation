using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace AutoPM_Desktop.Services;

/// <summary>
/// An ObservableCollection that can swap all its items with one change notification. Clearing and adding 800 tasks
/// one by one makes a bound DataGrid re-measure 800 times; <see cref="ReplaceAll"/> makes it do so once.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items) Items.Add(item);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
