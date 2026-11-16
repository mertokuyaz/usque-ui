using System.ComponentModel;

namespace Usque.Desktop;

// Keep selector item identities stable when the language changes. Replacing
// ItemsSource while SelectedValue is two-way bound can reset selection.
public sealed class LocalizedChoice<T>(T value, string resourceKey) : INotifyPropertyChanged where T : struct, Enum
{
    public T Value { get; } = value;
    public string Label => AppLocalization.Instance[resourceKey];
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Refresh() => PropertyChanged?.Invoke(this, new(nameof(Label)));
    public override string ToString() => Label;
}
