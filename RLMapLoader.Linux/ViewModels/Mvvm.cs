using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;

namespace RLMapLoader.Linux.ViewModels
{
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }
    }

    /// <summary>ICommand with explicit re-query (Avalonia has no CommandManager).</summary>
    public sealed class Command : ICommand
    {
        private readonly Func<object?, Task> _run;
        private readonly Func<object?, bool>? _can;
        private bool _running;

        public Command(Func<object?, Task> run, Func<object?, bool>? can = null) { _run = run; _can = can; }
        public Command(Action<object?> run, Func<object?, bool>? can = null) : this(p => { run(p); return Task.CompletedTask; }, can) { }

        public event EventHandler? CanExecuteChanged;
        public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

        public bool CanExecute(object? parameter) => !_running && (_can?.Invoke(parameter) ?? true);

        public async void Execute(object? parameter)
        {
            _running = true;
            Refresh();
            try { await _run(parameter); }
            finally
            {
                _running = false;
                Refresh();
            }
        }
    }
}
