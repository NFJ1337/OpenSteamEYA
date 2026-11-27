using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using SteamEyaWinUI.Localization;

namespace SteamEyaWinUI.Models;

internal sealed partial class ClashNodeViewModel : INotifyPropertyChanged
{
    private readonly SolidColorBrush _delayBrush = new(Colors.Gray);
    private string _delayText = Loc.T("Clash_Delay_Untested");
    private bool _isCurrent;
    private bool _isTesting;
    private bool _hasDelayResult;
    private int? _delayMilliseconds;

    public ClashNodeViewModel(string name, string type, bool canSelect)
    {
        Name = name;
        Type = type;
        CanSelect = canSelect;
    }

    public string Name { get; }

    public string Type { get; }

    public bool CanSelect { get; }

    public string TestButtonText => Loc.T("Clash_Test");

    public int? DelayMilliseconds => _delayMilliseconds;

    public string DelayText
    {
        get => _delayText;
        private set
        {
            if (_delayText == value)
            {
                return;
            }

            _delayText = value;
            Raise(nameof(DelayText));
        }
    }

    public Brush DelayBrush => _delayBrush;

    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value)
            {
                return;
            }

            _isCurrent = value;
            Raise(nameof(IsCurrent));
            Raise(nameof(CurrentVisibility));
        }
    }

    public Visibility CurrentVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;

    public bool IsTesting
    {
        get => _isTesting;
        set
        {
            if (_isTesting == value)
            {
                return;
            }

            _isTesting = value;
            Raise(nameof(IsTesting));
            Raise(nameof(TestingVisibility));
        }
    }

    public Visibility TestingVisibility => IsTesting ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetDelay(int? milliseconds, string text, Windows.UI.Color color)
    {
        _hasDelayResult = true;
        _delayMilliseconds = milliseconds;
        _delayBrush.Color = color;
        DelayText = text;
        Raise(nameof(DelayBrush));
    }

    public void RefreshLocalization()
    {
        Raise(nameof(TestButtonText));
        if (!_hasDelayResult)
        {
            DelayText = Loc.T("Clash_Delay_Untested");
        }
        else if (!_delayMilliseconds.HasValue)
        {
            DelayText = Loc.T("Clash_Delay_Failed");
        }
    }

    private void Raise(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
