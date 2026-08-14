using System;
using System.Windows;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Plugins;
using NavApp = Autodesk.Navisworks.Api.Application;

namespace PIXMY4D_Nav
{
    /// <summary>
    /// PIXMY4D-Nav entry point — appears on the Add-Ins ribbon tab.
    ///
    /// One ribbon button opens one window with three tools: Points, Field Marker
    /// Export, AR Model Export. It is the bridge between the Navisworks model and
    /// the PIXMYD phone app.
    ///
    /// Only registration is wired up so far — the pure-logic layers (point set /
    /// JSON writer, level-name normalisation, QR encoder, marker page HTML) are
    /// implemented and offline-tested under Core/. The Navisworks-touching tool
    /// windows (viewpoint/image capture, AR bounding-box export, ribbon tab) are
    /// separate, deferred work — see docs/work-orders/pixmy4d-nav.md tasks P3/P4.
    /// </summary>
    [Plugin("PIXMY4D-Nav",
        "ACLP_VDC",
        ToolTip = "PIXMY4D-Nav: Points, Field Marker Export, AR Model Export",
        DisplayName = "PIXMY4D-Nav")]
    [AddInPlugin(AddInLocation.AddIn)]
    public class PluginMain : AddInPlugin
    {
        public override int Execute(params string[] parameters)
        {
            try
            {
                Document document = NavApp.ActiveDocument;
                if (document == null || document.Models.Count == 0)
                {
                    MessageBox.Show(
                        "Open or append a model before using PIXMY4D-Nav.",
                        "PIXMY4D-Nav", MessageBoxButton.OK, MessageBoxImage.Information);
                    return 0;
                }

                MessageBox.Show(
                    "PIXMY4D-Nav tool windows (Points, Field Marker Export, AR Model Export) " +
                    "are not yet wired up. The pure-logic layers behind them are implemented " +
                    "and covered by tools/writer-tests.",
                    "PIXMY4D-Nav", MessageBoxButton.OK, MessageBoxImage.Information);
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "PIXMY4D-Nav failed to start:" + Environment.NewLine + Environment.NewLine +
                    ex.Message + Environment.NewLine + Environment.NewLine + ex.StackTrace,
                    "PIXMY4D-Nav", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }

        public override CommandState CanExecute()
        {
            return new CommandState(true);
        }
    }
}
