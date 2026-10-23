using CommunityToolkit.Mvvm.ComponentModel;
using VRCFaceTracking.Core.Contracts.Services;

namespace VRCFaceTracking.Core.Models;

/// <summary>
/// The module the user wants handling each part of the face. Values are module assembly names (the DLL file name
/// without its extension). An empty value means automatic: the first module to claim that part keeps it.
/// </summary>
public partial class TrackingSourceSettings : ObservableObject
{
    public const string Automatic = "";

    [ObservableProperty]
    [property: SavedSetting("EyeTrackingSource", Automatic)]
    private string _eyeSource = Automatic;

    [ObservableProperty]
    [property: SavedSetting("ExpressionTrackingSource", Automatic)]
    private string _expressionSource = Automatic;

    public TrackingSourceSettings(ILocalSettingsService localSettingsService)
    {
        PropertyChanged += (_, _) => localSettingsService.Save(this);
    }
}
