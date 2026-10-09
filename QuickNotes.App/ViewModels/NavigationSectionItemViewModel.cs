using QuickNotes.App.Models;

namespace QuickNotes.App.ViewModels;

public class NavigationSectionItemViewModel : ViewModelBase
{
    private bool _isSelected;
    private int _badgeCount;

    public NavigationSection Section { get; }
    public string Title { get; }
    public string Icon { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public int BadgeCount
    {
        get => _badgeCount;
        set
        {
            if (SetProperty(ref _badgeCount, value))
            {
                OnPropertyChanged(nameof(HasBadge));
                OnPropertyChanged(nameof(BadgeText));
            }
        }
    }

    public bool HasBadge => BadgeCount > 0;
    public string BadgeText => BadgeCount.ToString();
    public bool IsTaskIndex => Section == NavigationSection.Tasks;

    public string CompactText => !string.IsNullOrEmpty(Icon) ? Icon : GetDefaultCompactText(Section);

    public static string GetDefaultCompactText(NavigationSection section) => section switch
    {
        NavigationSection.All => "Все",
        NavigationSection.Tasks => "✓",
        NavigationSection.Inbox => "Вх",
        NavigationSection.Favorites => "★",
        NavigationSection.Today => "Сег",
        NavigationSection.Recent => "Нед",
        NavigationSection.Untagged => "#",
        NavigationSection.Trash => "Кор",
        _ => "•"
    };

    public NavigationSectionItemViewModel(NavigationSection section, string title, string icon)
    {
        Section = section;
        Title = title;
        Icon = icon;
    }
}
