// The probe compiles the app's Core files on their own, but DownloadItem inherits the window's
// ObservableObject, and pulling in the real ViewModels drags in the Shell dialogs and their XAML
// code-behind, which do not build outside the app. So the small base class is mirrored here.
// If the Core comes to need more of the base, add it here — the probe will not compile until it catches up.
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BasaltUI.ViewModels
{
    public abstract class ObservableObject
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            return true;
        }

        protected void Raise([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        protected void RaiseAll(params string[] names)
        {
            var handler = PropertyChanged;
            if (handler is null)
                return;
            foreach (var name in names)
                handler(this, new PropertyChangedEventArgs(name));
        }
    }
}
