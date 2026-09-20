using System.ComponentModel;
using Microsoft.Maui.Controls.Shapes;
using PKForge.App.Services;
using PKForge.App.Theme;

namespace PKForge.App.Views;

/// <summary>Lightweight lower-screen surface used while Poképark is open.</summary>
public sealed class PokeparkJournalPage : ContentPage
{
    private readonly PokeparkJournalState? _state;
    private readonly Label _name = new() { FontSize = 28, FontAttributes = FontAttributes.Bold, TextColor = UiTokens.IndigoInk };
    private readonly Label _mood = new() { FontSize = 15, TextColor = UiTokens.Ink1 };
    private readonly Label _activity = new() { FontSize = 18, TextColor = UiTokens.Ink0 };
    private readonly Label _journal = new() { FontSize = 16, TextColor = UiTokens.Ink0, LineBreakMode = LineBreakMode.WordWrap };
    private readonly Label _likes = new() { FontSize = 16, TextColor = UiTokens.Ink0, LineBreakMode = LineBreakMode.WordWrap };
    private readonly PropertyChangedEventHandler? _handler;
    private bool _cleanedUp;

    public PokeparkJournalPage()
    {
        BackgroundColor = UiTokens.Housing;
        _state = IPlatformApplication.Current?.Services.GetService<PokeparkJournalState>();
        var card = new Border
        {
            BackgroundColor = UiTokens.Paper, Stroke = UiTokens.ShellEdge, StrokeThickness = 2,
            StrokeShape = new RoundRectangle { CornerRadius = 12 }, Padding = 18, Margin = 14,
            Content = new VerticalStackLayout
            {
                Spacing = 10,
                Children =
                {
                    new Label { Text = "POKÉPARK  /  FIELD JOURNAL", FontSize = 18, FontAttributes = FontAttributes.Bold, TextColor = UiTokens.Ink0 },
                    _name, _mood, new BoxView { HeightRequest = 2, Color = UiTokens.SelectBorder }, _activity, _journal, _likes,
                    new Label { Text = "This journal is a playful Poképark story. Game data stays unchanged.", FontSize = 12, TextColor = UiTokens.InkSoft },
                },
            },
        };
        Content = new Grid { Children = { DsChrome.GridBackground(), card } };
        if (_state is not null)
        {
            _handler = (_, _) => MainThread.BeginInvokeOnMainThread(Update);
            _state.PropertyChanged += _handler;
        }
        Update();
    }

    public ValueTask RefreshAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_cleanedUp) MainThread.BeginInvokeOnMainThread(Update);
        return ValueTask.CompletedTask;
    }

    public void Cleanup()
    {
        if (_cleanedUp) return;
        _cleanedUp = true;
        if (_state is not null && _handler is not null) _state.PropertyChanged -= _handler;
    }

    private void Update()
    {
        if (_cleanedUp) return;
        var resident = _state?.Resident;
        _name.Text = resident is null ? "Poképark" : resident.Name + (resident.Shiny ? " ★" : "");
        _mood.Text = resident is null ? "" : $"Mood: {_state!.Mood}";
        _activity.Text = resident is null ? "" : $"Right now: {_state!.Activity}";
        _journal.Text = resident is null ? "" : $"PERSONALITY  {_state!.Trait}\n\nMEADOW MEMORY  {_state!.Story}";
        _likes.Text = resident is null ? "" : $"FAVORITE LITTLE THINGS  {_state!.Likes}";
    }
}
