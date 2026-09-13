using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Services;

namespace SteamEyaWinUI.Pages;

public sealed partial class AboutPage : Page, INotifyPropertyChanged
{
    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

    public AboutPage()
    {
        InitializeComponent();
        Loc.LanguageChanged += OnLanguageChanged;
        Render();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>XAML 绑定入口：{x:Bind Strings.Get('Key'), Mode=OneWay}。</summary>
    internal LocalizedStrings Strings => Loc.Strings;

    private void OnLanguageChanged()
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Strings)));
            Render();
        });
    }

    private void Render()
    {
        AboutVersionText.Text = Loc.Tf("About_Version_Format", GitHubUpdateService.CurrentVersion);
    }

    private async void OpenGitHubButton_Click(object sender, RoutedEventArgs e)
    {
        await AppState.OpenUrlAsync(GitHubUpdateService.RepositoryUrl);
    }
}
