using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;
using QuickNotes.App.Services;

namespace QuickNotes.App.ViewModels;

public class TagRuleViewModel : ViewModelBase
{
    private readonly ITagRuleStore? _ruleStore;
    private string _newRequiredTerm = string.Empty;
    private string _newAnyTerm = string.Empty;
    private string _newExcludedTerm = string.Empty;

    public Tag Tag { get; }
    public string Title => $"Правила автотега для «{Tag.Name}»";

    public string Explanation =>
        "Автотег назначается при базовом совпадении (по имени или синониму), только если соблюдены все условия:\n" +
        "• Обязательные (все): все перечисленные термины должны присутствовать в тексте.\n" +
        "• Любые (хотя бы одно): хотя бы один из терминов должен присутствовать (если список не пуст).\n" +
        "• Исключения (ни одного): ни один из этих терминов не должен присутствовать в тексте.";

    public string NewRequiredTerm
    {
        get => _newRequiredTerm;
        set => SetProperty(ref _newRequiredTerm, value);
    }

    public string NewAnyTerm
    {
        get => _newAnyTerm;
        set => SetProperty(ref _newAnyTerm, value);
    }

    public string NewExcludedTerm
    {
        get => _newExcludedTerm;
        set => SetProperty(ref _newExcludedTerm, value);
    }

    public ObservableCollection<string> RequiredTerms { get; } = new();
    public ObservableCollection<string> AnyTerms { get; } = new();
    public ObservableCollection<string> ExcludedTerms { get; } = new();

    public ICommand AddRequiredTermCommand { get; }
    public ICommand DeleteRequiredTermCommand { get; }
    public ICommand AddAnyTermCommand { get; }
    public ICommand DeleteAnyTermCommand { get; }
    public ICommand AddExcludedTermCommand { get; }
    public ICommand DeleteExcludedTermCommand { get; }

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }

    public event Action<bool?>? RequestClose;

    public TagRuleViewModel(Tag tag, TagRule? existingRule = null, ITagRuleStore? ruleStore = null)
    {
        Tag = tag;
        _ruleStore = ruleStore;

        if (existingRule != null)
        {
            if (existingRule.RequiredTerms != null)
            {
                foreach (var term in existingRule.RequiredTerms)
                {
                    var norm = TagDetectionService.NormalizeTerm(term);
                    if (!string.IsNullOrWhiteSpace(norm) && !RequiredTerms.Any(t => string.Equals(t, norm, StringComparison.OrdinalIgnoreCase)))
                    {
                        RequiredTerms.Add(norm);
                    }
                }
            }

            if (existingRule.AnyTerms != null)
            {
                foreach (var term in existingRule.AnyTerms)
                {
                    var norm = TagDetectionService.NormalizeTerm(term);
                    if (!string.IsNullOrWhiteSpace(norm) && !AnyTerms.Any(t => string.Equals(t, norm, StringComparison.OrdinalIgnoreCase)))
                    {
                        AnyTerms.Add(norm);
                    }
                }
            }

            if (existingRule.ExcludedTerms != null)
            {
                foreach (var term in existingRule.ExcludedTerms)
                {
                    var norm = TagDetectionService.NormalizeTerm(term);
                    if (!string.IsNullOrWhiteSpace(norm) && !ExcludedTerms.Any(t => string.Equals(t, norm, StringComparison.OrdinalIgnoreCase)))
                    {
                        ExcludedTerms.Add(norm);
                    }
                }
            }
        }

        AddRequiredTermCommand = new RelayCommand(AddRequiredTerm);
        DeleteRequiredTermCommand = new RelayCommand(param => DeleteTerm(param as string, RequiredTerms));

        AddAnyTermCommand = new RelayCommand(AddAnyTerm);
        DeleteAnyTermCommand = new RelayCommand(param => DeleteTerm(param as string, AnyTerms));

        AddExcludedTermCommand = new RelayCommand(AddExcludedTerm);
        DeleteExcludedTermCommand = new RelayCommand(param => DeleteTerm(param as string, ExcludedTerms));

        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(Cancel);
    }

    private void AddRequiredTerm()
    {
        AddTermToList(NewRequiredTerm, RequiredTerms, () => NewRequiredTerm = string.Empty);
    }

    private void AddAnyTerm()
    {
        AddTermToList(NewAnyTerm, AnyTerms, () => NewAnyTerm = string.Empty);
    }

    private void AddExcludedTerm()
    {
        AddTermToList(NewExcludedTerm, ExcludedTerms, () => NewExcludedTerm = string.Empty);
    }

    private static void AddTermToList(string input, ObservableCollection<string> list, Action clearInput)
    {
        var normalized = TagDetectionService.NormalizeTerm(input);
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        if (list.Any(t => string.Equals(t, normalized, StringComparison.OrdinalIgnoreCase)))
            return;

        list.Add(normalized);
        clearInput();
    }

    private static void DeleteTerm(string? term, ObservableCollection<string> list)
    {
        if (string.IsNullOrWhiteSpace(term))
            return;

        var existing = list.FirstOrDefault(t => string.Equals(t, term, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            list.Remove(existing);
        }
    }

    public TagRule BuildRule()
    {
        return new TagRule
        {
            TagId = Tag.Id,
            RequiredTerms = RequiredTerms.ToList(),
            AnyTerms = AnyTerms.ToList(),
            ExcludedTerms = ExcludedTerms.ToList()
        };
    }

    public void Save()
    {
        var rule = BuildRule();
        if (_ruleStore != null)
        {
            if (rule.IsEmpty)
            {
                _ruleStore.DeleteRule(Tag.Id);
            }
            else
            {
                _ruleStore.SaveRule(rule);
            }
        }

        RequestClose?.Invoke(true);
    }

    public void Cancel()
    {
        RequestClose?.Invoke(false);
    }
}
