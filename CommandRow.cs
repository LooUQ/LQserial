using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LQserial;

public class CommandRow : INotifyPropertyChanged
{
    string _text = "", _delay = "";
    bool _selected, _hex, _enter = true;

    public int Index { get; init; }
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
    public string Text { get => _text; set => Set(ref _text, value); }
    public bool Hex { get => _hex; set => Set(ref _hex, value); }
    public bool Enter { get => _enter; set => Set(ref _enter, value); }
    public string Delay { get => _delay; set => Set(ref _delay, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
