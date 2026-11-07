using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace OCStartup;

public abstract class Notify : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class ModelCard : Notify
{
    private string _size = "";
    private string _params = "";
    private string _quant = "";
    private bool _busy;
    private bool _paused;
    private double _progress;
    private string _status = "";

    public ModelCard(string name) => Name = name;

    public string Name { get; }
    public string Short => Name.Split(':')[0].Split('/')[^1];
    public string Tag => Name.Contains(':') ? Name[(Name.IndexOf(':') + 1)..] : "latest";

    public string Size { get => _size; set => Set(ref _size, value); }
    public long Bytes { get; set; }
    public string Params { get => _params; set => Set(ref _params, value); }
    public string Quant { get => _quant; set => Set(ref _quant, value); }

    public bool Busy { get => _busy; set { Set(ref _busy, value); Raise(nameof(Idle)); Raise(nameof(Running)); } }
    public bool Idle => !_busy;

    public bool Paused
    {
        get => _paused;
        set { Set(ref _paused, value); Raise(nameof(Running)); }
    }
    public bool Running => _busy && !_paused;

    internal System.Threading.CancellationTokenSource? Cancellation { get; set; }
    /// <summary>Отмену от паузы отличает только намерение: обрыв запроса одинаковый.</summary>
    internal bool Cancelled { get; set; }
    public double Progress { get => _progress; set => Set(ref _progress, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
}

public sealed class ParamRow : Notify
{
    private string _value;

    public ParamRow(string name, string origin, string value)
    {
        Name = name;
        Origin = origin;
        _value = value;
    }

    public string Name { get; }
    public string Baseline { get; init; } = "";
    public string Origin { get; }
    public bool Touched => _value.Trim() != Baseline.Trim();
    public string Note => Touched ? Loc.S("L_FromMine") : Origin;

    public string Value
    {
        get => _value;
        set { Set(ref _value, value); Raise(nameof(Note)); Raise(nameof(Touched)); }
    }
}

public sealed class FlagRow : Notify
{
    private bool _value;

    public FlagRow(string name, string note, bool value)
    {
        Name = name;
        Note = note;
        _value = value;
    }

    public string Name { get; }
    public string Note { get; }
    public bool Baseline { get; init; }
    public bool Value { get => _value; set => Set(ref _value, value); }
}

public sealed class UpdateRow : Notify
{
    private string _state = Loc.S("L_Checking");
    private string _current = "";
    private string _available = "";
    private bool _canUpdate;
    private bool _busy;
    private double _percent = -1;
    private string _action = Loc.S("L_Update");

    public UpdateRow(string title, string kind)
    {
        Title = title;
        Kind = kind;
    }

    public string Title { get; }
    public string Kind { get; }
    public string ActionText { get => _action; set => Set(ref _action, value); }

    public string Current { get => _current; set { Set(ref _current, value); Raise(nameof(Line)); } }
    public string Available { get => _available; set { Set(ref _available, value); Raise(nameof(Line)); } }
    public string State { get => _state; set { Set(ref _state, value); Raise(nameof(Line)); } }

    public bool CanUpdate { get => _canUpdate; set { Set(ref _canUpdate, value); Raise(nameof(Waiting)); } }
    public bool Busy { get => _busy; set { Set(ref _busy, value); Raise(nameof(Waiting)); Raise(nameof(Measured)); } }
    public bool Waiting => _canUpdate && !_busy;

    public double Percent { get => _percent; private set { Set(ref _percent, value); Raise(nameof(Measured)); } }
    /// <summary>Полоса только там, где доля известна: без неё шаблон рисует полную.</summary>
    public bool Measured => _busy && _percent >= 0;

    public void Show(string state, double percent)
    {
        State = state;
        Percent = percent;
    }

    public string Line => Available.Length > 0 && Available != Current
        ? $"{State}: {Current} -> {Available}"
        : Current.Length > 0 ? $"{State}: {Current}" : State;
}

public sealed record SessionRow(string Id, string Title, long Stamp)
{
    public static SessionRow Fresh { get; } = new("", "", 0);

    public bool IsNew => Id.Length == 0;

    public string Name => IsNew ? Loc.S("L_NewSession")
                        : Title.Length > 0 ? Title
                        : Loc.S("L_Untitled");

    public string When
    {
        get
        {
            if (IsNew || Stamp <= 0) return "";
            var moment = System.DateTimeOffset.FromUnixTimeMilliseconds(Stamp).LocalDateTime;
            return moment.Date == System.DateTime.Today
                ? moment.ToString("HH:mm")
                : moment.ToString("dd.MM HH:mm");
        }
    }
}

public sealed class StepCard : Notify
{
    private string _caption;
    private bool _active;
    private bool _done;

    public StepCard(string caption) => _caption = caption;

    public string Caption { get => _caption; set => Set(ref _caption, value); }
    public bool Active { get => _active; set { Set(ref _active, value); Raise(nameof(Icon)); Raise(nameof(Dim)); } }
    public bool Done { get => _done; set { Set(ref _done, value); Raise(nameof(Icon)); Raise(nameof(Dim)); } }

    private const string Check = "M3.6,8.4 L6.6,11.4 L12.4,4.8";
    private const string Ring = "M8,8 m-4.6,0 a4.6,4.6 0 1,0 9.2,0 a4.6,4.6 0 1,0 -9.2,0";
    private const string Dot = Ring + " M8,8 m-2,0 a2,2 0 1,0 4,0 a2,2 0 1,0 -4,0";

    public Geometry Icon => Geometry.Parse(Done ? Check : Active ? Dot : Ring);
    public double Dim => Done || Active ? 1.0 : 0.45;
}
