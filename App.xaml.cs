using System.Windows;

namespace Cutwright
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            Log.StartSession();
            base.OnStartup(e);
        }

        // Last resort for anything not handled where it happened.
        //
        // This used to show Exception.Message and nothing else - no stack, nothing written down -
        // so a fault on someone else's machine left no trace to follow. The full exception now goes
        // to the log and the message names the file, which is the difference between a report that
        // can be acted on and "it crashed".
        //
        // Handling the exception and carrying on is a deliberate choice, not an oversight. Closing
        // would be the tidier state to be in, but it would also throw away a loaded bill of
        // materials and whatever the estimator had set up, and most faults here come from a single
        // UI action rather than from anything structurally broken. The compromise is to keep
        // running while saying plainly that the figures on screen may be incomplete - the numbers
        // are the point of the tool, so quietly resuming and letting someone quote from a
        // half-finished nest is the outcome worth avoiding.
        private void HandleGlobalException(object sender,
            System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            Log.Error("Unhandled exception", e.Exception);

            string where = Log.CurrentFile is null
                ? string.Empty
                : Environment.NewLine + Environment.NewLine + $"Details were written to:{Environment.NewLine}{Log.CurrentFile}";

            MessageBox.Show(
                $"Something went wrong and the last action did not finish:{Environment.NewLine}{Environment.NewLine}" +
                e.Exception.Message + Environment.NewLine + Environment.NewLine +
                "Anything already on screen may be incomplete. Re-run the nest before relying on " +
                "the numbers." + where,
                "Cutwright hit a problem", MessageBoxButton.OK, MessageBoxImage.Error);

            e.Handled = true;
        }
    }
}
