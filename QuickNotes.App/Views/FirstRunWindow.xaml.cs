using System;
using System.IO;
using System.Windows;
using QuickNotes.App.Data;
using QuickNotes.App.ViewModels;

namespace QuickNotes.App.Views;

public partial class FirstRunWindow : Window
{
    private readonly MainViewModel _mainViewModel;

    public string HotkeySummary { get; }
    public string DataLocationText { get; }

    public FirstRunWindow(MainViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));
        HotkeySummary = mainViewModel.HotkeyHint +
            ". Первое открывает редактор с выделенным текстом, второе сразу сохраняет его во «Входящие».";
        DataLocationText = "Локальная папка: " + Path.GetDirectoryName(QuickNotesDbContext.GetDefaultDbPath());
        DataContext = this;
        InitializeComponent();
    }

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        var help = new HelpWindow { Owner = this };
        help.ShowDialog();
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        _mainViewModel.CompleteOnboarding(StarterTagsCheck.IsChecked == true);
        DialogResult = true;
        Close();
    }
}
