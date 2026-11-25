using System.Globalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace SteamEyaWinUI.Pages;

public sealed partial class Cs2TrackerPage
{
    private sealed record CompetitiveRankVisual(
        string Label,
        string Accent);

    private static CompetitiveRankVisual PerfectRankVisual(double? score)
    {
        if (score is null or <= 0)
        {
            return new CompetitiveRankVisual("未定级", "#9CA3AF");
        }

        return score.Value switch
        {
            <= 1_000 => new CompetitiveRankVisual("D", "#5BC0FF"),
            <= 1_150 => new CompetitiveRankVisual("C", "#41E0C4"),
            <= 1_300 => new CompetitiveRankVisual("C+", "#38D2FF"),
            <= 1_450 => new CompetitiveRankVisual("C+", "#F4CF5D"),
            <= 1_600 => new CompetitiveRankVisual("B", "#4AA8FF"),
            <= 1_750 => new CompetitiveRankVisual("B+", "#55D7FF"),
            <= 1_900 => new CompetitiveRankVisual("B+", "#F4C957"),
            <= 2_050 => new CompetitiveRankVisual("A", "#48DFC4"),
            <= 2_200 => new CompetitiveRankVisual("A+", "#60C8FF"),
            <= 2_400 => new CompetitiveRankVisual("A+", "#F2C451"),
            _ => new CompetitiveRankVisual("S", "#FFD24A")
        };
    }

    private static CompetitiveRankVisual FiveERankVisual(double? elo)
    {
        if (elo is null or <= 0)
        {
            return new CompetitiveRankVisual("未定级", "#9CA3AF");
        }

        return elo.Value switch
        {
            <= 1_200 => new CompetitiveRankVisual("D", "#5AC8FF"),
            <= 1_350 => new CompetitiveRankVisual("C", "#9B6CFF"),
            <= 1_500 => new CompetitiveRankVisual("C", "#B44DFF"),
            <= 1_600 => new CompetitiveRankVisual("C", "#D14CFF"),
            <= 1_750 => new CompetitiveRankVisual("B", "#ED3E91"),
            <= 1_900 => new CompetitiveRankVisual("B", "#FF2D72"),
            <= 2_000 => new CompetitiveRankVisual("B", "#FF5A8D"),
            <= 2_150 => new CompetitiveRankVisual("A", "#FF7A3D"),
            <= 2_300 => new CompetitiveRankVisual("A", "#FF5324"),
            <= 2_400 => new CompetitiveRankVisual("A", "#FF3434"),
            _ => new CompetitiveRankVisual("S", "#FFD34A")
        };
    }

    private Border BuildCompetitiveRankScoreBadge(
        CompetitiveRankVisual visual,
        double? score,
        string scoreCaption)
    {
        var accent = ColorFrom(visual.Accent);
        var icon = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(11),
            BorderBrush = new SolidColorBrush(WithAlpha(accent, 0xC0)),
            BorderThickness = new Thickness(1.5),
            Background = new SolidColorBrush(WithAlpha(accent, 0x28)),
            Child = new TextBlock
            {
                Text = visual.Label,
                FontSize = visual.Label.Length > 1 ? 13 : 18,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(accent),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var scoreColumn = new StackPanel
        {
            Spacing = 0,
            VerticalAlignment = VerticalAlignment.Center
        };
        scoreColumn.Children.Add(new TextBlock
        {
            Text = scoreCaption,
            FontSize = 9,
            Foreground = ThemeBrush("AuroraTextMutedBrush")
        });
        scoreColumn.Children.Add(new TextBlock
        {
            Text = score is > 0
                ? score.Value.ToString("N0", CultureInfo.InvariantCulture)
                : "—",
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(accent)
        });

        return new Border
        {
            Padding = new Thickness(7, 5, 12, 5),
            CornerRadius = new CornerRadius(13),
            BorderBrush = new SolidColorBrush(WithAlpha(accent, 0x55)),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(WithAlpha(accent, 0x14)),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 9,
                Children = { icon, scoreColumn }
            }
        };
    }
}
