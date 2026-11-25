using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SteamEyaWinUI.Localization;
using SteamEyaWinUI.Services;

namespace SteamEyaWinUI.Pages;

public sealed partial class SettingsPage
{
    private bool _pickingAutoBackground;

    private async void ChooseAutoBackgroundImageButton_Click(object sender, RoutedEventArgs e)
    {
        await PickAndApplyAutoBackgroundAsync();
    }

    private async Task HandleAutoBackgroundToggleAsync()
    {
        if (_syncing)
        {
            return;
        }

        var settings = AppState.SettingsService.Load();
        if (!AutoBackgroundToggle.IsOn)
        {
            settings.AutoBackgroundEnabled = false;
            AppState.SettingsService.Save(settings);
            MainWindow.Instance?.ApplyCustomBackground(settings);
            UpdateCustomBackgroundControls();
            return;
        }

        await ShowAutoBackgroundNetworkNoticeOnceAsync();
        CustomBackgroundToggle.IsOn = false;
        var selectedPaths = GetConfiguredAutoBackgroundPaths(settings);
        if (selectedPaths.Count == 0)
        {
            if (!await PickAndApplyAutoBackgroundAsync())
            {
                _syncing = true;
                try
                {
                    AutoBackgroundToggle.IsOn = false;
                }
                finally
                {
                    _syncing = false;
                }

                UpdateCustomBackgroundControls();
            }

            return;
        }

        ApplyAutoBackgroundSelection(selectedPaths);
    }

    private async Task<bool> PickAndApplyAutoBackgroundAsync()
    {
        if (_pickingAutoBackground)
        {
            return false;
        }

        _pickingAutoBackground = true;
        UpdateCustomBackgroundControls();
        try
        {
            await ShowAutoBackgroundNetworkNoticeOnceAsync();
            IReadOnlyList<AlcyBackgroundType> types;
            try
            {
                types = await AlcyBackgroundService.GetImageTypesAsync();
            }
            catch (Exception ex)
            {
                AppLog.Warn($"读取自动背景图类型失败：{ex.Message}");
                AppState.ShowStatus(
                    Loc.Tf("Settings_Background_AutoPicker_LoadFailed", ex.Message),
                    InfoBarSeverity.Error);
                return false;
            }

            var selectedPaths = await ShowAutoBackgroundTypeDialogAsync(types);
            if (selectedPaths is null || selectedPaths.Count == 0)
            {
                return false;
            }

            ApplyAutoBackgroundSelection(selectedPaths);
            return true;
        }
        finally
        {
            _pickingAutoBackground = false;
            UpdateCustomBackgroundControls();
        }
    }

    private async Task<IReadOnlyList<string>?> ShowAutoBackgroundTypeDialogAsync(
        IReadOnlyList<AlcyBackgroundType> types)
    {
        var settings = AppState.SettingsService.Load();
        var availableWidth = XamlRoot?.Size.Width ?? 1380;
        var availableHeight = XamlRoot?.Size.Height ?? 810;
        var dialogWidth = Math.Clamp(availableWidth - 40, 1120, 1400);
        const double scrollBarSpacing = 16;
        var gridWidth = Math.Max(1020, dialogWidth - 80 - scrollBarSpacing);
        var scrollHeight = Math.Clamp(availableHeight - 310, 260, 560);
        var grid = new Grid
        {
            MinWidth = gridWidth,
            Margin = new Thickness(0, 0, scrollBarSpacing, 0),
            ColumnSpacing = 12,
            RowSpacing = 12
        };
        for (var column = 0; column < 3; column++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition());
        }
        var rowCount = (types.Count + 2) / 3;
        for (var row = 0; row < rowCount; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        var checkBoxes = new List<CheckBox>();
        ContentDialog? dialog = null;
        for (var index = 0; index < types.Count; index++)
        {
            var type = types[index];
            var preview = new Image
            {
                Height = 135,
                Stretch = Stretch.UniformToFill,
                Source = new BitmapImage
                {
                    UriSource = new Uri(type.PreviewUrl),
                    DecodePixelWidth = 480,
                    CreateOptions = BitmapCreateOptions.IgnoreImageCache
                }
            };
            var card = new Border
            {
                MinHeight = 245,
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                BorderBrush = AutoBackgroundBrush("AuroraGlassBorderBrush"),
                Background = AutoBackgroundBrush("AuroraGlassBrush"),
                Child = new StackPanel
                {
                    Children =
                    {
                        new Border
                        {
                            Height = 135,
                            CornerRadius = new CornerRadius(11, 11, 0, 0),
                            Background = AutoBackgroundBrush("AuroraPaneBrush"),
                            Child = preview
                        },
                        new StackPanel
                        {
                            Padding = new Thickness(12, 10, 12, 10),
                            Spacing = 5,
                            Children =
                            {
                                new TextBlock
                                {
                                    Text = type.Name,
                                    FontSize = 14,
                                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                                    Foreground = AutoBackgroundBrush("AuroraTextHeadingBrush"),
                                    TextTrimming = TextTrimming.CharacterEllipsis
                                },
                                new TextBlock
                                {
                                    Text = type.Description,
                                    FontSize = 11,
                                    Foreground = AutoBackgroundBrush("AuroraTextMutedBrush"),
                                    TextWrapping = TextWrapping.Wrap,
                                    MaxLines = 2
                                },
                                new Border
                                {
                                    HorizontalAlignment = HorizontalAlignment.Left,
                                    Padding = new Thickness(7, 2, 7, 2),
                                    CornerRadius = new CornerRadius(5),
                                    Background = AutoBackgroundBrush("AuroraPillBrush"),
                                    Child = new TextBlock
                                    {
                                        Text = type.IsAdaptive ? "自适应" : "横图",
                                        FontSize = 9,
                                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                                        Foreground = AutoBackgroundBrush("AuroraTextMutedBrush")
                                    }
                                }
                            }
                        }
                    }
                }
            };
            var checkBox = new CheckBox
            {
                Style = (Style)Application.Current.Resources["WhiteDialogCheckBoxStyle"],
                Content = Loc.T("Settings_Background_AutoPicker_Select"),
                Tag = type.ApiPath,
                Margin = new Thickness(4, 0, 0, 0)
            };
            checkBox.Checked += (_, _) =>
            {
                if (dialog is not null)
                {
                    dialog.IsPrimaryButtonEnabled = true;
                }
            };
            checkBox.Unchecked += (_, _) =>
            {
                if (dialog is not null)
                {
                    dialog.IsPrimaryButtonEnabled =
                        checkBoxes.Any(item => item.IsChecked == true);
                }
            };
            checkBoxes.Add(checkBox);

            var item = new StackPanel { Spacing = 7 };
            item.Children.Add(card);
            item.Children.Add(checkBox);
            Grid.SetColumn(item, index % 3);
            Grid.SetRow(item, index / 3);
            grid.Children.Add(item);
        }

        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = Loc.T("Settings_Background_AutoPicker_Hint"),
            Foreground = AutoBackgroundBrush("AuroraTextMutedBrush"),
            TextWrapping = TextWrapping.Wrap
        });
        var scroll = new ScrollViewer
        {
            Content = grid,
            MaxHeight = scrollHeight,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Enabled
        };
        ApplyAutoBackgroundScrollBarStyle(scroll);
        content.Children.Add(scroll);

        dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Loc.T("Settings_Background_AutoPicker_Title"),
            Content = content,
            PrimaryButtonText = Loc.T("Settings_Background_AutoPicker_Apply"),
            CloseButtonText = Loc.T("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false
        };
        dialog.Resources["ContentDialogMinWidth"] = dialogWidth;
        dialog.Resources["ContentDialogMaxWidth"] = dialogWidth;
        dialog.Resources["ContentDialogMaxHeight"] =
            Math.Clamp(availableHeight - 60, 540, 820);

        var currentPaths = GetConfiguredAutoBackgroundPaths(settings);
        foreach (var checkBox in checkBoxes)
        {
            if (checkBox.Tag is string path &&
                currentPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                checkBox.IsChecked = true;
            }
        }

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return null;
        }

        return checkBoxes
            .Where(item => item.IsChecked == true)
            .Select(item => item.Tag as string)
            .OfType<string>()
            .ToArray();
    }

    private void ApplyAutoBackgroundSelection(IReadOnlyList<string> apiPaths)
    {
        var settings = AppState.SettingsService.Load();
        settings.AutoBackgroundEnabled = true;
        settings.AutoBackgroundImageApiPaths = AlcyBackgroundService
            .NormalizeApiPaths(apiPaths)
            .ToList();
        settings.AutoBackgroundImageApiPath = null;
        settings.CustomBackgroundEnabled = false;
        AppState.SettingsService.Save(settings);

        _syncing = true;
        try
        {
            AutoBackgroundToggle.IsOn = true;
            CustomBackgroundToggle.IsOn = false;
        }
        finally
        {
            _syncing = false;
        }

        MainWindow.Instance?.ApplyCustomBackground(settings);
        UpdateCustomBackgroundControls();
        AppState.ShowStatus(
            Loc.T("Settings_Background_Auto_Applied"),
            InfoBarSeverity.Success);
    }

    private static Brush AutoBackgroundBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    private static void ApplyAutoBackgroundScrollBarStyle(ScrollViewer scroll)
    {
        var resources = scroll.Resources;
        resources["ScrollBarSize"] = 12d;
        resources["ScrollBarVerticalThumbMinWidth"] = 8d;
        resources["ScrollBarThumbOffset"] = 2d;
        resources["ScrollBarCornerRadius"] = new CornerRadius(3);
        resources["ScrollBarThumbFill"] = AutoBackgroundBrush("AuroraTextMutedBrush");
        resources["ScrollBarThumbFillPointerOver"] = AutoBackgroundBrush("AuroraTextBodyBrush");
        resources["ScrollBarThumbFillPressed"] = AutoBackgroundBrush("AuroraTextHeadingBrush");
        resources["ScrollBarPanningThumbBackground"] = AutoBackgroundBrush("AuroraTextMutedBrush");
        resources["ScrollBarTrackFill"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        resources["ScrollBarTrackFillPointerOver"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        resources["ScrollBarTrackFillDisabled"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        resources["ScrollBarTrackStroke"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    private static IReadOnlyList<string> GetConfiguredAutoBackgroundPaths(AppSettings settings)
    {
        var paths = AlcyBackgroundService
            .NormalizeApiPaths(settings.AutoBackgroundImageApiPaths)
            .ToList();
        if (paths.Count == 0 &&
            AlcyBackgroundService.TryNormalizeApiPath(
                settings.AutoBackgroundImageApiPath,
                out var legacyPath))
        {
            paths.Add(legacyPath);
        }

        return paths;
    }

    private async Task ShowAutoBackgroundNetworkNoticeOnceAsync()
    {
        var settings = AppState.SettingsService.Load();
        if (settings.AutoBackgroundNetworkNoticeShown)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Loc.T("Settings_Background_Auto_NetworkNotice_Title"),
            Content = new TextBlock
            {
                Text = Loc.T("Settings_Background_Auto_NetworkNotice_Message"),
                TextWrapping = TextWrapping.Wrap
            },
            CloseButtonText = Loc.T("Common_Confirm"),
            DefaultButton = ContentDialogButton.Close
        };
        await dialog.ShowAsync();

        settings = AppState.SettingsService.Load();
        settings.AutoBackgroundNetworkNoticeShown = true;
        AppState.SettingsService.Save(settings);
    }
}
