using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace PlaylistDl.App.Models;

public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        using (BlockReentrancy())
        {
            Items.Clear();
            foreach (var item in items)
            {
                Items.Add(item);
            }
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
