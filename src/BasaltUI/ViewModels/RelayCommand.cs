using System.Windows.Input;

namespace BasaltUI.ViewModels;

/// <summary>A command over an Action, with a CanExecute that the view model can re-signal.</summary>
public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null, string? name = null) : ICommand
{
    public string? Name { get; } = name;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => execute();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
