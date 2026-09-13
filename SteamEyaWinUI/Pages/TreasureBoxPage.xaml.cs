using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Services;

namespace SteamEyaWinUI.Pages;

public sealed partial class TreasureBoxPage : Page, INotifyPropertyChanged
{
    private const string SecretUrl = "https://www.yuanshen.com/";
    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

    public TreasureBoxPage()
    {
        InitializeComponent();
        Loc.LanguageChanged += OnLanguageChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>XAML 绑定入口：{x:Bind Strings.Get('Key'), Mode=OneWay}。</summary>
    internal LocalizedStrings Strings => Loc.Strings;

    private void OnLanguageChanged()
    {
        _dispatcherQueue.TryEnqueue(() =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Strings))));
    }

    private async void SecretCardButton_Click(object sender, RoutedEventArgs e)
    {
        if (MainWindow.Instance is not { } window)
        {
            return;
        }

        await window.PlaySecretVideoAsync();
        await AppState.OpenUrlAsync(SecretUrl);
    }
}