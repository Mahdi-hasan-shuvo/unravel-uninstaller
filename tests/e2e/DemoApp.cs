// Minimal windowed program used as the "installed application" in the end-to-end tests.
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

[assembly: AssemblyTitle("Contoso Demo App")]
[assembly: AssemblyDescription("Contoso Demo App")]
[assembly: AssemblyCompany("Contoso Test Labs")]
[assembly: AssemblyProduct("Contoso Demo App")]

static class DemoApp
{
    static void Main()
    {
        var f = new Form { Text = "Contoso Demo App", Width = 420, Height = 220, StartPosition = FormStartPosition.CenterScreen };
        f.Controls.Add(new Label { Text = "Contoso Demo App – test program for Unravel.", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter });
        Application.Run(f);
    }
}
