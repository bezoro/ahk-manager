namespace Bezoro.AhkManager;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        string data = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bezoro", "AhkManager"); string? render = null; bool small = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--data-dir" && i + 1 < args.Length) data = System.IO.Path.GetFullPath(args[++i]);
            else if (args[i] == "--render-fixture" && i + 1 < args.Length) render = System.IO.Path.GetFullPath(args[++i]);
            else if (args[i] == "--render-small") small = true;
            else { MessageBox.Show("Argumento inválido. Use --data-dir <pasta>.", "AHK Manager"); return 2; }
        }
        if (render is not null && !args.Contains("--data-dir") || small && render is null) return 2;
        try
        {
            using var store = new CatalogStore(data);
            using var appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            using var form = new MainForm(store, render is not null);
            if (appIcon is not null) form.Icon = appIcon;
            if (render is not null)
            {
                if (small) form.Size = new(1000, 720);
                form.CreateControl(); form.Show(); Application.DoEvents();
                using var bitmap = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, form.ClientRectangle); bitmap.Save(render, System.Drawing.Imaging.ImageFormat.Png); form.Hide(); return 0;
            }
            Application.Run(form); return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show("Não foi possível abrir o catálogo. " + ex.Message, "AHK Manager", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1;
        }
    }
}
