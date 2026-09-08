using System;
using System.Globalization;
using System.IO;
using PIXMYD_Nav.Core.Capture;
using PIXMYD_Nav.Core.Nwc;

namespace PIXMYD_Nav.Obj2Nwc
{
    /// <summary>
    /// Reads an OBJ and writes an NWC. That is the whole program.
    ///
    /// It exists as a program rather than as a method because nwcreate cannot
    /// be run inside Navisworks -- see Obj2Nwc.csproj for which build fails
    /// how. The plugin runs this and reads what comes back.
    ///
    /// ## The contract with the plugin
    ///
    /// Exit code 0 means the NWC is on disk at the path given. Anything else
    /// means it is not, and the last line of standard output says why in a
    /// sentence fit to put in a dialog. Nothing is written to standard error
    /// that the plugin needs; a crash here is a non-zero exit and an empty
    /// message, which the plugin reports as such rather than pretending.
    /// </summary>
    internal static class Program
    {
        private const int Ok = 0;
        private const int BadArguments = 2;
        private const int CouldNotRead = 3;
        private const int CouldNotWrite = 4;
        private const int Unexpected = 5;

        private static int Main(string[] args)
        {
            if (args.Length < 2 || args.Length > 4)
            {
                Console.WriteLine(
                    "usage: obj2nwc <input.obj> <output.nwc> [node name] [document up axis: Z|Y]");
                return BadArguments;
            }

            string input = args[0];
            string output = args[1];
            string name = args.Length >= 3 ? args[2] : "";
            // Z unless told otherwise: a Navisworks document is Z-up, and the
            // capture is ARKit's Y-up. See the turn in NwcMesh for why the
            // converter has to know.
            string upAxis = args.Length >= 4 ? args[3] : "Z";

            try
            {
                ObjReader.Result read = ObjReader.ReadFile(input);
                if (!read.Ok)
                {
                    Console.WriteLine(read.Message);
                    return CouldNotRead;
                }
                if (!string.IsNullOrWhiteSpace(name)) read.Mesh.Name = name;

                // The OBJ is in the capture's own frame, which is ARKit's:
                // Y-up. An NWC carries no up-axis declaration for a reader to
                // act on, so the turn into the document's frame happens here
                // or it does not happen at all.
                if (!string.Equals(upAxis.Trim(), "Y", StringComparison.OrdinalIgnoreCase))
                    read.Mesh.TurnYUpToZUp();

                NwcWriter.Result written = NwcWriter.Write(read.Mesh, output);

                // The scan's grid, for the plugin to line up with the model's.
                // Reported before the message, because the plugin reads the
                // last line as the sentence to show a person -- this is a
                // number for the machine and has no business being it.
                GridBearing.Result grid = GridBearing.Estimate(
                    read.Mesh.Vertices, read.Mesh.Triangles);
                if (written.Ok && grid.Found)
                    Console.WriteLine("bearing: "
                        + grid.Degrees.ToString("0.###", CultureInfo.InvariantCulture) + " "
                        + grid.Share.ToString("0.###", CultureInfo.InvariantCulture));

                Console.WriteLine(written.Message);
                return written.Ok ? Ok : CouldNotWrite;
            }
            catch (Exception ex)
            {
                // A message rather than a stack trace, because the plugin puts
                // the last line in front of a person. The trace is on standard
                // error for whoever runs this by hand.
                Console.WriteLine("The converter failed: " + ex.Message);
                Console.Error.WriteLine(ex.ToString());
                return Unexpected;
            }
        }
    }
}
