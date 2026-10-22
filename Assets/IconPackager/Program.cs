using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

internal static class IconPackager
{
    static readonly int[] Sizes = [16, 24, 32, 48, 256];
    static int Main(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0] == "--verify-pe") { VerifyPe(args[1]); return 0; }
            if (args.Length == 2 && args[0] == "--verify-ico") { VerifyIco(args[1]); return 0; }
            if (args.Length != 2) { Console.Error.WriteLine("Usage: IconPackager <master.png> <output-directory> | --verify-pe <exe> | --verify-ico <ico>"); return 2; }
            Convert(args[0], args[1]); return 0;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidDataException or ExternalException)
        {
            Console.Error.WriteLine("FAIL " + ex.Message); return 1;
        }
    }
    static void Convert(string master, string output)
    {
        using var source = Image.FromFile(master);
        var frames = new List<(int Size, byte[] Png)>();
        foreach (int size in Sizes)
        {
            using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent); graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.CompositingQuality = CompositingQuality.HighQuality; graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                float scale = Math.Min((float)size / source.Width, (float)size / source.Height);
                float width = source.Width * scale, height = source.Height * scale;
                using var attributes = new ImageAttributes(); attributes.SetWrapMode(WrapMode.TileFlipXY);
                graphics.DrawImage(source, new Rectangle((int)Math.Round((size-width)/2), (int)Math.Round((size-height)/2), (int)Math.Round(width), (int)Math.Round(height)), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
            }
            var alpha = CountAlpha(bitmap);
            if (alpha.Transparent == 0 || alpha.Opaque + alpha.Partial == 0) throw new InvalidDataException("Master must contain real transparency and visible artwork.");
            using var memory = new MemoryStream(); bitmap.Save(memory, ImageFormat.Png); frames.Add((size, memory.ToArray()));
        }
        using var ico = new MemoryStream(); using (var writer = new BinaryWriter(ico, System.Text.Encoding.UTF8, true))
        {
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)frames.Count);
            int offset = 6 + frames.Count * 16;
            foreach (var frame in frames)
            {
                writer.Write((byte)(frame.Size == 256 ? 0 : frame.Size)); writer.Write((byte)(frame.Size == 256 ? 0 : frame.Size)); writer.Write((byte)0); writer.Write((byte)0);
                writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(frame.Png.Length); writer.Write(offset); offset += frame.Png.Length;
            }
            foreach (var frame in frames) writer.Write(frame.Png);
        }
        Directory.CreateDirectory(output);
        WriteNew(Path.Combine(output, "AppIcon.ico"), ico.ToArray());
        foreach (var frame in frames) WriteNew(Path.Combine(output, $"AppIcon-{frame.Size}.png"), frame.Png);
        using var preview = new Bitmap(670, 400, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(preview))
        {
            graphics.Clear(Color.FromArgb(246, 248, 251)); using var dark = new SolidBrush(Color.FromArgb(29, 37, 48)); graphics.FillRectangle(dark, 335, 0, 335, 400);
            using var font = new Font("Segoe UI", 10); using var lightText = new SolidBrush(Color.FromArgb(36, 48, 65)); using var darkText = new SolidBrush(Color.FromArgb(226, 234, 244));
            graphics.DrawString("Fundo claro", font, lightText, 12, 8); graphics.DrawString("Fundo escuro", font, darkText, 347, 8);
            foreach (int pane in new[] { 0, 335 })
            {
                int x = pane + 12;
                foreach (var frame in frames.Where(f => f.Size < 256))
                {
                    using var memory = new MemoryStream(frame.Png); using var image = Image.FromStream(memory);
                    graphics.DrawImageUnscaled(image, x, 46); graphics.DrawString(frame.Size.ToString(), font, pane == 0 ? lightText : darkText, x, 99); x += frame.Size + 20;
                }
                using var largeMemory = new MemoryStream(frames[^1].Png); using var large = Image.FromStream(largeMemory); graphics.DrawImageUnscaled(large, pane + 40, 126);
            }
        }
        using var previewMemory = new MemoryStream(); preview.Save(previewMemory, ImageFormat.Png); WriteNew(Path.Combine(output, "AppIcon-preview.png"), previewMemory.ToArray());
        VerifyIco(Path.Combine(output, "AppIcon.ico")); Console.WriteLine("Preview=AppIcon-preview.png");
    }
    static void WriteNew(string path, byte[] bytes) { using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); file.Write(bytes); }
    static (int Transparent, int Partial, int Opaque) CountAlpha(Bitmap bitmap)
    {
        int transparent = 0, partial = 0, opaque = 0;
        for (int y = 0; y < bitmap.Height; y++) for (int x = 0; x < bitmap.Width; x++) { byte alpha = bitmap.GetPixel(x,y).A; if (alpha == 0) transparent++; else if (alpha == 255) opaque++; else partial++; }
        return (transparent, partial, opaque);
    }
    static void VerifyIco(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path)); if (reader.ReadUInt16() != 0 || reader.ReadUInt16() != 1 || reader.ReadUInt16() != Sizes.Length) throw new InvalidDataException("Invalid ICO header.");
        var entries = new List<(int Size, int Bytes, int Offset)>();
        foreach (int expected in Sizes)
        {
            int width = reader.ReadByte(), height = reader.ReadByte(); width = width == 0 ? 256 : width; height = height == 0 ? 256 : height;
            reader.ReadByte(); reader.ReadByte(); if (reader.ReadUInt16() != 1 || reader.ReadUInt16() != 32 || width != expected || height != expected) throw new InvalidDataException("Invalid ICO frame descriptor.");
            entries.Add((width, reader.ReadInt32(), reader.ReadInt32()));
        }
        foreach (var entry in entries)
        {
            reader.BaseStream.Position = entry.Offset; byte[] bytes = reader.ReadBytes(entry.Bytes); if (bytes.Length != entry.Bytes) throw new InvalidDataException("Truncated ICO frame.");
            using var memory = new MemoryStream(bytes); using var bitmap = new Bitmap(memory); if (bitmap.Width != entry.Size || bitmap.Height != entry.Size) throw new InvalidDataException("ICO frame size mismatch.");
            var alpha = CountAlpha(bitmap); if (alpha.Transparent == 0 || alpha.Opaque + alpha.Partial == 0) throw new InvalidDataException("ICO frame lost transparency or artwork.");
            Console.WriteLine($"ICO frame={entry.Size}x{entry.Size} bpp=32 transparent={alpha.Transparent} partial={alpha.Partial} opaque={alpha.Opaque}");
        }
        Console.WriteLine("ICO validation=PASS frames=5 realAlpha=True");
    }
    delegate bool EnumNames(IntPtr module, IntPtr type, IntPtr name, IntPtr parameter);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
    [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr module);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool EnumResourceNames(IntPtr module, IntPtr type, EnumNames callback, IntPtr parameter);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);
    [DllImport("kernel32.dll")] static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")] static extern IntPtr LockResource(IntPtr resource);
    [DllImport("kernel32.dll")] static extern uint SizeofResource(IntPtr module, IntPtr resource);
    static byte[] Resource(IntPtr module, IntPtr name, int type)
    {
        var resource = FindResource(module, name, (IntPtr)type); if (resource == IntPtr.Zero) throw new InvalidDataException("PE icon resource missing.");
        int size = checked((int)SizeofResource(module, resource)); var data = LockResource(LoadResource(module, resource)); if (data == IntPtr.Zero) throw new InvalidDataException("PE icon resource inaccessible.");
        byte[] bytes = new byte[size]; Marshal.Copy(data, bytes, 0, size); return bytes;
    }
    static void VerifyPe(string path)
    {
        var module = LoadLibraryEx(Path.GetFullPath(path), IntPtr.Zero, 0x2 | 0x20); if (module == IntPtr.Zero) throw new InvalidDataException("PE could not be opened as data.");
        try
        {
            int icons = 0, groups = 0;
            EnumNames countIcons = (_, _, _, _) => { icons++; return true; };
            if (!EnumResourceNames(module, (IntPtr)3, countIcons, IntPtr.Zero)) throw new InvalidDataException("RT_ICON missing.");
            EnumNames readGroup = (handle, _, name, _) =>
            {
                groups++; using var reader = new BinaryReader(new MemoryStream(Resource(handle,name,14))); if (reader.ReadUInt16() != 0 || reader.ReadUInt16() != 1 || reader.ReadUInt16() != Sizes.Length) throw new InvalidDataException("RT_GROUP_ICON frame count mismatch.");
                foreach (int expected in Sizes)
                {
                    int width=reader.ReadByte(), height=reader.ReadByte(); width=width==0?256:width; height=height==0?256:height; reader.ReadByte(); reader.ReadByte(); if (reader.ReadUInt16()!=1 || reader.ReadUInt16()!=32) throw new InvalidDataException("PE icon format mismatch."); reader.ReadUInt32(); int id=reader.ReadUInt16();
                    using var memory = new MemoryStream(Resource(handle,(IntPtr)id,3)); using var bitmap = new Bitmap(memory); var alpha=CountAlpha(bitmap); if (width!=expected || height!=expected || bitmap.Width!=expected || bitmap.Height!=expected || alpha.Transparent==0) throw new InvalidDataException("PE icon size/alpha mismatch."); Console.WriteLine($"PE frame={expected}x{expected} alpha=True");
                }
                return true;
            };
            if (!EnumResourceNames(module,(IntPtr)14,readGroup,IntPtr.Zero) || icons < 5 || groups < 1) throw new InvalidDataException("PE icon resources incomplete.");
            Console.WriteLine($"PE validation=PASS RT_ICON={icons} RT_GROUP_ICON={groups}");
        }
        finally { FreeLibrary(module); }
    }
}