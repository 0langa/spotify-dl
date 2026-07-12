using System.Collections.Specialized;
using PlaylistDl.App.Models;
using Xunit;

namespace PlaylistDl.App.Tests;

public sealed class RangeObservableCollectionTests
{
    [Fact]
    public void ReplacesLargeTrackSetWithSingleResetNotification()
    {
        var collection = new RangeObservableCollection<int> { 1, 2 };
        var notifications = new List<NotifyCollectionChangedEventArgs>();
        collection.CollectionChanged += (_, args) => notifications.Add(args);

        collection.ReplaceAll(Enumerable.Range(1, 1200));

        Assert.Equal(1200, collection.Count);
        Assert.Single(notifications);
        Assert.Equal(NotifyCollectionChangedAction.Reset, notifications[0].Action);
    }
}
