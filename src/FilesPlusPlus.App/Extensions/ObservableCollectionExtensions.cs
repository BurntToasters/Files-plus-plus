using System.Collections.ObjectModel;

namespace FilesPlusPlus.App.Extensions;

public static class ObservableCollectionExtensions
{
    public static void ResetWith<T>(this ObservableCollection<T> collection, IEnumerable<T> items)
    {
        collection.Clear();
        foreach (var item in items)
        {
            collection.Add(item);
        }
    }
}
