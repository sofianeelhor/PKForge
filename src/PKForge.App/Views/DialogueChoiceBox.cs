using Microsoft.Maui.Controls.Shapes;
using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.Chrome;

namespace PKForge.App.Views;

/// <summary>
/// One typed Pokémon-style message with its in-game answer window: the dialogue stays
/// visible while a compact vertical list of plain-text answers appears beside it.
/// No menu chrome, header, hint bar, or icons.
/// </summary>
public sealed class DialogueChoiceBox : IPadHandler
{
    private readonly TaskCompletionSource<string?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Grid _host;
    private readonly Grid _overlay;
    private readonly GamepadRouter? _router;
    private readonly Label _message;
    private readonly Border _answers;
    private readonly Label[] _labels;
    private readonly BoxView[] _cursors;
    private readonly string _text;
    private readonly IDispatcherTimer _typer;
    private int _index;
    private int _charIndex;
    private bool _choicesVisible;

    public static Task<string?> ShowAsync(Grid host, string message, params string[] choices) =>
        new DialogueChoiceBox(host, message, choices)._result.Task;

    private DialogueChoiceBox(Grid host, string message, string[] choices)
    {
        _host = host;
        _text = message;
        _router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();
        _labels = new Label[choices.Length];
        _cursors = new BoxView[choices.Length];

        _message = new Label
        {
            FontFamily = DsChrome.PixelFont,
            FontSize = 16,
            TextColor = Colors.White,
            LineBreakMode = LineBreakMode.WordWrap,
            VerticalOptions = LayoutOptions.Start,
        };

        var answerRows = new VerticalStackLayout { Spacing = 2 };
        for (var i = 0; i < choices.Length; i++)
        {
            var index = i;
            _labels[i] = new Label
            {
                Text = choices[i],
                FontFamily = DsChrome.PixelFont,
                FontSize = 15,
                TextColor = UiTokens.Ink0,
                VerticalTextAlignment = TextAlignment.Center,
                Padding = new Thickness(2, 4),
            };
            _cursors[i] = new BoxView
            {
                Color = UiTokens.MaroonDeep,
                WidthRequest = 3,
                HeightRequest = 15,
                VerticalOptions = LayoutOptions.Center,
                InputTransparent = true,
            };
            var row = new Grid
            {
                ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)],
                ColumnSpacing = 5,
                Children = { _cursors[i], _labels[i] },
            };
            Grid.SetColumn(_labels[i], 1);
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) =>
            {
                if (!_choicesVisible) { Reveal(); return; }
                _index = index;
                Highlight();
                Close(choices[index]);
            };
            row.GestureRecognizers.Add(tap);
            answerRows.Children.Add(row);
        }

        // The answer window is deliberately not a device panel or menu: it is a small
        // paper response sheet anchored above the still-visible dialogue, as in the games.
        _answers = new Border
        {
            BackgroundColor = UiTokens.Paper,
            Stroke = UiTokens.MaroonDeep,
            StrokeThickness = 3,
            StrokeShape = new RoundRectangle { CornerRadius = 8 },
            Padding = new Thickness(10, 7),
            WidthRequest = Math.Max(160, choices.Max(c => c.Length) * 11 + 44),
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.End,
            Margin = new Thickness(18, 0, 18, 126),
            Content = answerRows,
            IsVisible = false,
        };

        var dialogue = new Border
        {
            BackgroundColor = UiTokens.Maroon,
            Stroke = UiTokens.MaroonDeep,
            StrokeThickness = 3,
            StrokeShape = new RoundRectangle { CornerRadius = 10 },
            Shadow = new Shadow { Brush = Brush.Black, Opacity = 0.28f, Radius = 10, Offset = new Point(0, 4) },
            Padding = new Thickness(16, 13),
            Margin = new Thickness(18, 0, 18, 16),
            MinimumHeightRequest = 100,
            VerticalOptions = LayoutOptions.End,
            Content = _message,
        };

        var revealMessage = new TapGestureRecognizer();
        revealMessage.Tapped += (_, _) =>
        {
            if (!_choicesVisible) Reveal();
        };
        dialogue.GestureRecognizers.Add(revealMessage);

        _overlay = new Grid { Children = { dialogue, _answers } };
        host.Add(_overlay);
        Grid.SetRowSpan(_overlay, Math.Max(1, host.RowDefinitions.Count));
        Grid.SetColumnSpan(_overlay, Math.Max(1, host.ColumnDefinitions.Count));

        _typer = host.Dispatcher.CreateTimer();
        _typer.Interval = TimeSpan.FromMilliseconds(24);
        _typer.Tick += (_, _) =>
        {
            if (_charIndex >= _text.Length)
            {
                _typer.Stop();
                ShowChoices();
                return;
            }
            _charIndex++;
            _message.Text = _text[.._charIndex];
        };
        _typer.Start();
        _router?.Push(this);
    }

    public bool OnPadButton(PadButton button)
    {
        if (!_choicesVisible)
        {
            if (button is PadButton.A or PadButton.B or PadButton.Start) Reveal();
            return true;
        }

        switch (button)
        {
            case PadButton.Up:
                _index = (_index - 1 + _labels.Length) % _labels.Length;
                Highlight();
                return true;
            case PadButton.Down:
                _index = (_index + 1) % _labels.Length;
                Highlight();
                return true;
            case PadButton.A:
                Close(_labels[_index].Text!);
                return true;
            case PadButton.B:
                Close(null);
                return true;
            default:
                return true;
        }
    }

    private void Reveal()
    {
        _typer.Stop();
        _charIndex = _text.Length;
        _message.Text = _text;
        ShowChoices();
    }

    private void ShowChoices()
    {
        if (_choicesVisible) return;
        _choicesVisible = true;
        _answers.IsVisible = true;
        _answers.Opacity = 0;
        _ = _answers.FadeToAsync(1, 100, Easing.CubicOut);
        Highlight();
    }

    private void Highlight()
    {
        for (var i = 0; i < _labels.Length; i++)
        {
            var selected = i == _index;
            _labels[i].TextColor = selected ? UiTokens.MaroonDeep : UiTokens.Ink0;
            _labels[i].FontAttributes = selected ? FontAttributes.Bold : FontAttributes.None;
            _cursors[i].IsVisible = selected;
        }
    }

    private void Close(string? choice)
    {
        _typer.Stop();
        _router?.Remove(this);
        _host.Remove(_overlay);
        _result.TrySetResult(choice);
    }
}
