using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;

namespace LittleLauncher.ViewModels;

/// <summary>
/// One row in the unified add/edit-item app picker: an installed application or a
/// Progressive Web App, shown together in a single searchable list with an icon.
/// </summary>
public partial class AppPickerEntry : ObservableObject
{
    /// <summary>Display name shown in the list and used to seed the item name.</summary>
    public string Name { get; }

    /// <summary>
    /// Value stored into <c>LauncherItem.Path</c> when this entry is chosen.
    /// For apps this is an exe path or a <c>shell:AppsFolder\{AUMID}</c> path; for
    /// PWAs it is the bare AUMID (launched via <c>explorer shell:AppsFolder\{Path}</c>).
    /// </summary>
    public string LaunchPath { get; }

    /// <summary>True when this entry is a Chromium-registered PWA.</summary>
    public bool IsPwa { get; }

    /// <summary>Shell parsing name used to extract the list icon via <c>ShellIcons</c>.</summary>
    public string IconTarget { get; }

    /// <summary>List icon — assigned asynchronously after enumeration (background STA thread).</summary>
    [ObservableProperty]
    public partial ImageSource? Icon { get; set; }

    public AppPickerEntry(string name, string launchPath, bool isPwa, string iconTarget)
    {
        Name = name;
        LaunchPath = launchPath;
        IsPwa = isPwa;
        IconTarget = iconTarget;
    }
}
