namespace Gitfs.App;

/// <summary>Действие как ICommand. Нужно там, где Avalonia не принимает
/// ничего другого: KeyBinding и NativeMenuItem. Отдельным типом, а не двумя
/// одинаковыми классами в App и MainWindow, — иначе жест и пункт меню,
/// ведущие в одно действие, разъезжаются по поведению.</summary>
internal sealed class RelayCommand(Action run) : System.Windows.Input.ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => run();
}
