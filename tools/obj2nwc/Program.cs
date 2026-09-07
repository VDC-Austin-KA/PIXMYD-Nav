using System;
using System.IO;
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
            if (args.Length < 2 || args.Length > 3)
            {
                Console.WriteLine("usage: obj2nwc <input.obj> <output.nwc> [node name]");
                return BadArguments;
            }

            string input = args[0];
            string output = args[1];
            string name = args.Length == 3 ? args[2] : "";

            try
            {
                ObjReader.Result read = ObjReader.ReadFile(input);
                if (!read.Ok)
                {
                    Console.WriteLine(read.Message);
                    return CouldNotRead;
                }
                if (!string.IsNullOrWhiteSpace(name)) read.Mesh.Name = name;

                NwcWriter.Result written = NwcWriter.Write(read.Mesh, output);
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
