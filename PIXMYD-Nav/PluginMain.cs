using System;
using System.Windows;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Plugins;
using NavApp = Autodesk.Navisworks.Api.Application;

namespace PIXMYD_Nav
{
    /// <summary>
    /// PIXMYD-Nav entry point — appears on the Add-Ins ribbon tab.
    ///
    /// One ribbon button opens one window with three tools: Points, Field Marker
    /// Export, AR Model Export. It is the bridge between the Navisworks model and
    /// the PIXMYD phone app.
    ///
    /// The pure-logic layers (point set / JSON writer, level-name normalisation,
    /// QR encoder, marker page HTML) live in Core/ and are offline-tested under
    /// tools/writer-tests. This window owns the Navisworks-facing work: capturing
    /// the selection as points, capturing the viewport for photos (no managed
    /// screenshot API exists — GDI capture, see Core/NavBridge/ViewportCapture.cs),
    /// and the AR bounding-box export.
    /// </summary>
    [Plugin("PIXMYD-Nav",
        "ACLP_VDC",
        ToolTip = "PIXMYD-Nav: Points, Field Marker Export, AR Model Export",
        DisplayName = "PIXMYD-Nav")]
    [AddInPlugin(AddInLocation.AddIn)]
    public class PluginMain : AddInPlugin
    {
        private static MainWindow _openWindow;

        public override int Execute(params string[] parameters)
        {
            try
            {
                // One window per session: pressing the ribbon button again
                // brings the open one forward rather than stacking a second.
                if (_openWindow != null)
                {
                    if (_openWindow.WindowState == WindowState.Minimized)
                        _openWindow.WindowState = WindowState.Normal;
                    _openWindow.Activate();
                    return 0;
                }

                Document document = NavApp.ActiveDocument;
                if (document == null || document.Models.Count == 0)
                {
                    MessageBox.Show(
                        "Open or append a model before using PIXMYD-Nav.",
                        "PIXMYD-Nav", MessageBoxButton.OK, MessageBoxImage.Information);
                    return 0;
                }

                var window = new MainWindow();
                _openWindow = window;
                window.Closed += (s, e) => _openWindow = null;

                // Parenting to the Navisworks main window keeps the tool on top
                // of the application and stops it being lost behind it. The
                // window stays open modelessly: placing points means clicking
                // in the viewport while the list watches, which a modal dialog
                // would block.
                try
                {
                    var helper = new System.Windows.Interop.WindowInteropHelper(window);
                    helper.Owner = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                }
                catch (Exception)
                {
                    // A parentless tool window still works; never block the export on this.
                }

                window.Show();
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "PIXMYD-Nav failed to start:" + Environment.NewLine + Environment.NewLine +
                    ex.Message + Environment.NewLine + Environment.NewLine + ex.StackTrace,
                    "PIXMYD-Nav", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }

        public override CommandState CanExecute()
        {
            return new CommandState(true);
        }
    }
}
