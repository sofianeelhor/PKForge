using CommunityToolkit.Mvvm.ComponentModel;

namespace PKForge.App.Services;

/// <summary>Live detail mirrored to the Thor lower screen while Poképark is open.</summary>
public partial class PokeparkJournalState : ObservableObject
{
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private ParkPokemon? _resident;
    [ObservableProperty] private string _mood = "";
    [ObservableProperty] private string _activity = "";
    [ObservableProperty] private string _trait = "";
    [ObservableProperty] private string _likes = "";
    [ObservableProperty] private string _story = "";
    public void Open() => IsOpen = true;
    public void Clear()
    {
        Resident = null;
        IsOpen = false;
    }
}
