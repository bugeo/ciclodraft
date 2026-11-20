using Avalonia;

namespace CicloDraft;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--photo-test") return SelfTest.RunPhoto(args.Skip(1).ToArray());
        if (args.Length > 0 && args[0] == "--project-polys")
        {
            var dto = CicloDraft.IO.ProjectFile.Read(args[2]);
            var d = CicloDraft.IO.GpkgReader.Load(args[1], new CicloDraft.Geometry.V2(dto.Origin![0], dto.Origin[1]));
            var pr = CicloDraft.IO.ProjectFile.ToProject(dto, d.Origin);
            var polys = pr.Elements.Where(e => e.Polygon.Count >= 3).Select(e => e.Polygon.Select(v => new[] { v.X + d.Origin.X, v.Y + d.Origin.Y }).ToList()).ToList();
            File.WriteAllText(args[3], System.Text.Json.JsonSerializer.Serialize(polys));
            return 0;
        }
        if (args.Length > 0 && args[0] == "--layers")
        {
            var d = CicloDraft.IO.GpkgReader.Load(args[1]);
            foreach (var l in d.Layers) System.Console.WriteLine($"{l.Name} | {(CicloDraft.IO.LayerRole)l.Role} | {l.FeatureCount}");
            return 0;
        }
        if (args.Length > 0 && args[0] == "--textures")
        {
            Directory.CreateDirectory(args[1]);
            void W(string n, CicloDraft.IO.Tex t) => File.WriteAllBytes(Path.Combine(args[1], n + ".png"), t.Png());
            W("asfalto", CicloDraft.IO.TextureFactory.Asphalt()); W("pietra", CicloDraft.IO.TextureFactory.StoneSlabs("#BFB8AA", 3));
            W("granito", CicloDraft.IO.TextureFactory.Granite()); W("resina", CicloDraft.IO.TextureFactory.Resin("#B5462F", 4));
            W("vernice", CicloDraft.IO.TextureFactory.Paint()); W("erba", CicloDraft.IO.TextureFactory.Grass()); W("coppi", CicloDraft.IO.TextureFactory.RoofTiles());
            W("bici", CicloDraft.IO.TextureFactory.BikeSymbol());
            for (int i = 0; i < 2; i++) { W($"piano_{i}", CicloDraft.IO.TextureFactory.FacadeFloor(CicloDraft.IO.TextureFactory.Palettes[i], i)); W($"terra_{i}", CicloDraft.IO.TextureFactory.FacadeGround(CicloDraft.IO.TextureFactory.Palettes[i], i)); }
            W("terra_2", CicloDraft.IO.TextureFactory.FacadeGround(CicloDraft.IO.TextureFactory.Palettes[2], 2));
            return 0;
        }
        if (args.Length > 0 && args[0] == "--export3d-test") return SelfTest.RunExport3D(args.Skip(1).ToArray());
        if (args.Length > 0 && args[0] == "--clip-test") return SelfTest.RunClip(args.Skip(1).ToArray());
        if (args.Length > 0 && args[0] == "--selftest-disegno") return SelfTest.RunDraw(args.Skip(1).ToArray());
        if (args.Length > 0 && args[0] == "--selftest-anello") return SelfTest.RunRing(args.Skip(1).ToArray());
        if (args.Length > 0 && args[0] == "--selftest-curve") return SelfTest.RunCurve(args.Skip(1).ToArray());
        if (args.Length > 0 && args[0] == "--selftest") return SelfTest.Run(args.Skip(1).ToArray());
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
